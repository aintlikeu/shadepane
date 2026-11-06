using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Shadepane.Core.Localization;

namespace Shadepane.Core.Extensions;

public sealed class CrxException(string message) : Exception(message);

/// <summary>
/// Пакет расширения Chrome (CRX3): заголовок с подписями и ZIP-архив. Формат — components/crx_file в Chromium:
/// "Cr24", версия 3, длина заголовка, заголовок CrxFileHeader (protobuf), архив.
/// Parse проверяет подписи так же, как Chrome: каждое доказательство в заголовке должно сойтись, и среди них
/// обязано быть доказательство ключа разработчика — того, из которого получен ID расширения.
/// Подпись Web Store (второй ключ, Google) не сверяется с известным ключом: пакет приходит по HTTPS с серверов
/// Google, и ID, ради которого пользователь открыл страницу, должен совпасть с ключом разработчика.
/// </summary>
public sealed class CrxPackage
{
    private const uint Magic = 0x34327243; // "Cr24" little-endian
    private const int MaxHeaderBytes = 256 * 1024;
    private static readonly byte[] SignaturePrefix = Encoding.ASCII.GetBytes("CRX3 SignedData\0");

    private CrxPackage(string id, byte[] publicKey, ReadOnlyMemory<byte> archive)
    {
        ExtensionId = id;
        PublicKey = publicKey;
        Archive = archive;
    }

    /// <summary>ID расширения (32 буквы a–p), полученный из ключа разработчика.</summary>
    public string ExtensionId { get; }

    /// <summary>Открытый ключ разработчика (SubjectPublicKeyInfo, DER) — значение "key" в manifest.json.</summary>
    public byte[] PublicKey { get; }

    /// <summary>ZIP-архив с файлами расширения.</summary>
    public ReadOnlyMemory<byte> Archive { get; }

    public static CrxPackage Parse(ReadOnlyMemory<byte> file)
    {
        var data = file.Span;
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
            throw new CrxException(Loc.T("это не пакет расширения Chrome (.crx)", "this is not a Chrome extension package (.crx)"));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (version != 3)
            throw new CrxException(Loc.T($"неподдерживаемая версия CRX: {version} (нужна 3)", $"unsupported CRX version: {version} (expected 3)"));
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (headerLength > MaxHeaderBytes || headerLength > data.Length - 12)
            throw new CrxException(Loc.T("повреждённый заголовок CRX", "corrupted CRX header"));

        var header = file.Slice(12, (int)headerLength);
        var archive = file[(12 + (int)headerLength)..];

        var proofs = new List<(byte[] Key, byte[] Signature, bool Rsa)>();
        byte[]? signedHeaderData = null;
        foreach (var (field, value) in Protobuf.Fields(header))
        {
            switch (field)
            {
                case 2: // sha256_with_rsa
                case 3: // sha256_with_ecdsa
                    byte[]? key = null, signature = null;
                    foreach (var (proofField, proofValue) in Protobuf.Fields(value))
                    {
                        if (proofField == 1) key = proofValue.ToArray();
                        else if (proofField == 2) signature = proofValue.ToArray();
                    }
                    if (key is null || signature is null)
                        throw new CrxException(Loc.T("повреждённая подпись CRX", "corrupted CRX signature"));
                    proofs.Add((key, signature, field == 2));
                    break;
                case 10000: // signed_header_data
                    signedHeaderData = value.ToArray();
                    break;
            }
        }

        byte[]? crxId = null;
        if (signedHeaderData is not null)
        {
            foreach (var (field, value) in Protobuf.Fields(signedHeaderData))
            {
                if (field == 1)
                    crxId = value.ToArray();
            }
        }
        if (crxId is not { Length: 16 })
            throw new CrxException(Loc.T("в пакете нет ID расширения", "the package has no extension ID"));
        if (proofs.Count == 0)
            throw new CrxException(Loc.T("пакет не подписан", "the package is not signed"));

        // Подписанное сообщение: префикс, длина signed_header_data (LE32), сама она и архив.
        var message = new byte[SignaturePrefix.Length + 4 + signedHeaderData!.Length + archive.Length];
        SignaturePrefix.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(SignaturePrefix.Length), (uint)signedHeaderData.Length);
        signedHeaderData.CopyTo(message, SignaturePrefix.Length + 4);
        archive.Span.CopyTo(message.AsSpan(SignaturePrefix.Length + 4 + signedHeaderData.Length));

        byte[]? developerKey = null;
        foreach (var (key, signature, rsa) in proofs)
        {
            if (!Verify(key, signature, rsa, message))
                throw new CrxException(Loc.T("подпись пакета не сходится — файл повреждён или подменён", "the package signature does not match: the file is damaged or tampered with"));
            if (SHA256.HashData(key).AsSpan(0, 16).SequenceEqual(crxId))
                developerKey = key;
        }
        if (developerKey is null)
            throw new CrxException(Loc.T("пакет не подписан ключом разработчика", "the package is not signed with the developer key"));

        return new CrxPackage(IdFromBytes(crxId), developerKey, archive);
    }

    /// <summary>ID расширения из ключа (как в Chrome): первые 16 байт SHA-256, каждая шестнадцатеричная цифра — буква a–p.</summary>
    public static string IdFromPublicKey(byte[] publicKey) => IdFromBytes(SHA256.HashData(publicKey).AsSpan(0, 16));

    private static string IdFromBytes(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[2 * i] = (char)('a' + (bytes[i] >> 4));
            chars[2 * i + 1] = (char)('a' + (bytes[i] & 0xF));
        }
        return new string(chars);
    }

    private static bool Verify(byte[] key, byte[] signature, bool rsa, byte[] message)
    {
        try
        {
            if (rsa)
            {
                using var r = RSA.Create();
                r.ImportSubjectPublicKeyInfo(key, out _);
                return r.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            using var e = ECDsa.Create();
            e.ImportSubjectPublicKeyInfo(key, out _);
            return e.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Минимальный разбор protobuf: только поля varint и length-delimited, остальное — ошибка формата.</summary>
    private static class Protobuf
    {
        public static IEnumerable<(long Field, ReadOnlyMemory<byte> Value)> Fields(ReadOnlyMemory<byte> buffer)
        {
            var position = 0;
            while (position < buffer.Length)
            {
                var tag = ReadVarint(buffer.Span, ref position);
                var field = (long)(tag >> 3);
                switch (tag & 7)
                {
                    case 0:
                        ReadVarint(buffer.Span, ref position);
                        break;
                    case 2:
                        var length = ReadVarint(buffer.Span, ref position);
                        if (length > (ulong)(buffer.Length - position))
                            throw Corrupted();
                        yield return (field, buffer.Slice(position, (int)length));
                        position += (int)length;
                        break;
                    default:
                        throw Corrupted();
                }
            }
        }

        private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int position)
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (position >= span.Length)
                    throw Corrupted();
                var b = span[position++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
            }
            throw Corrupted();
        }

        private static CrxException Corrupted() => new(Loc.T("повреждённый заголовок CRX", "corrupted CRX header"));
    }
}
