using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Shadepane.Core.Extensions;

namespace Shadepane.Core.Tests;

/// <summary>Собирает CRX3 так же, как Chromium (components/crx_file): для проверки разбора и подписей.</summary>
internal static class Crx
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("CRX3 SignedData\0");

    public static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }

    /// <param name="idKey">Ключ, из которого берётся crx_id (по умолчанию — первый подписывающий).</param>
    public static byte[] Build(byte[] archive, AsymmetricAlgorithm[] signers, AsymmetricAlgorithm? idKey = null, Func<byte[], byte[]>? tamperArchive = null)
    {
        var crxId = SHA256.HashData(Spki(idKey ?? signers[0]))[..16];
        var signedHeaderData = Field(1, crxId);
        var message = Prefix.Concat(Le32(signedHeaderData.Length)).Concat(signedHeaderData).Concat(archive).ToArray();

        var header = new List<byte>();
        foreach (var signer in signers)
        {
            var (field, signature) = signer switch
            {
                RSA rsa => (2, rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
                ECDsa ec => (3, ec.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
                _ => throw new ArgumentException("unsupported key"),
            };
            header.AddRange(Field(field, [.. Field(1, Spki(signer)), .. Field(2, signature)]));
        }
        header.AddRange(Field(10000, signedHeaderData));

        var body = tamperArchive?.Invoke(archive) ?? archive;
        return [.. "Cr24"u8.ToArray(), .. Le32(3), .. Le32(header.Count), .. header, .. body];
    }

    public static byte[] Spki(AsymmetricAlgorithm key) => key switch
    {
        RSA rsa => rsa.ExportSubjectPublicKeyInfo(),
        ECDsa ec => ec.ExportSubjectPublicKeyInfo(),
        _ => throw new ArgumentException("unsupported key"),
    };

    private static byte[] Le32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        return bytes;
    }

    private static byte[] Field(int number, byte[] value) => [.. Varint((ulong)(number << 3 | 2)), .. Varint((ulong)value.Length), .. value];

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
        } while (value != 0);
        return [.. bytes];
    }
}

public class CrxPackageTests
{
    private static readonly byte[] Archive = Crx.Zip(("manifest.json", """{"manifest_version":3,"name":"A","version":"1.0"}"""));

    [Fact]
    public void ValidRsaPackage_GivesIdKeyAndArchive()
    {
        using var rsa = RSA.Create(2048);
        var package = CrxPackage.Parse(Crx.Build(Archive, [rsa]));

        Assert.Equal(CrxPackage.IdFromPublicKey(Crx.Spki(rsa)), package.ExtensionId);
        Assert.True(WebStore.IsExtensionId(package.ExtensionId));
        Assert.Equal(Crx.Spki(rsa), package.PublicKey);
        Assert.Equal(Archive, package.Archive.ToArray());
    }

    [Fact]
    public void EcdsaDeveloperKey_PlusSecondSigner_IsAccepted()
    {
        // Как в магазине: подпись разработчика и вторая подпись (у магазина — ключ Google).
        using var developer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var store = RSA.Create(2048);
        var package = CrxPackage.Parse(Crx.Build(Archive, [store, developer], idKey: developer));
        Assert.Equal(CrxPackage.IdFromPublicKey(Crx.Spki(developer)), package.ExtensionId);
    }

    [Fact]
    public void TamperedArchive_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var file = Crx.Build(Archive, [rsa], tamperArchive: a => { var copy = (byte[])a.Clone(); copy[^10] ^= 0xFF; return copy; });
        Assert.Throws<CrxException>(() => CrxPackage.Parse(file));
    }

    [Fact]
    public void IdOfAnotherKey_WithoutItsSignature_IsRejected()
    {
        // Подписано своим ключом, а ID выдаёт себя за чужое расширение.
        using var attacker = RSA.Create(2048);
        using var victim = RSA.Create(2048);
        Assert.Throws<CrxException>(() => CrxPackage.Parse(Crx.Build(Archive, [attacker], idKey: victim)));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0, 0 })] // обычный ZIP
    [InlineData(new byte[] { 0x43, 0x72, 0x32, 0x34, 2, 0, 0, 0, 0, 0, 0, 0 })] // CRX2
    [InlineData(new byte[] { 0x43, 0x72, 0x32, 0x34, 3, 0, 0, 0, 0xFF, 0xFF, 0, 0 })] // заголовок длиннее файла
    [InlineData(new byte[] { 0x43, 0x72, 0x32, 0x34, 3, 0, 0, 0, 2, 0, 0, 0, 0x12, 0x7F })] // поле длиннее заголовка
    [InlineData(new byte[] { 0x43, 0x72, 0x32, 0x34, 3, 0, 0, 0, 0, 0, 0, 0 })] // без подписей и ID
    public void Garbage_IsRejected(byte[] file) => Assert.Throws<CrxException>(() => CrxPackage.Parse(file));
}

public class WebStoreTests
{
    private const string Id = "abcdefghijklmnopabcdefghijklmnop";

    [Theory]
    [InlineData("https://chromewebstore.google.com/detail/some-name/" + Id, Id)]
    [InlineData("https://chromewebstore.google.com/detail/" + Id + "?hl=ru", Id)]
    [InlineData("https://chromewebstore.google.com/detail/some-name/" + Id + "/reviews", Id)]
    [InlineData("https://chrome.google.com/webstore/detail/some-name/" + Id, Id)]
    [InlineData("https://chromewebstore.google.com/category/extensions", null)]
    [InlineData("https://chromewebstore.google.com/detail/some-name/ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP", null)]
    [InlineData("https://chromewebstore.google.com/detail/some-name/abcdefghijklmnopabcdefghijklmnoq", null)] // q вне a–p
    [InlineData("http://chromewebstore.google.com/detail/some-name/" + Id, null)]
    [InlineData("https://chromewebstore.google.com.evil.example/detail/x/" + Id, null)]
    [InlineData("https://evil.example/detail/x/" + Id, null)]
    [InlineData(null, null)]
    public void ExtensionIdFromPage(string? url, string? expected) => Assert.Equal(expected, WebStore.ExtensionIdFromPage(url));

    [Theory]
    [InlineData("https://clients2.google.com/service/update2/crx?response=redirect&x=id%3D" + Id + "%26uc", null, true, Id)]
    [InlineData("https://clients2.googleusercontent.com/crx/blobs/AbC/EXT_1_0_0_0.crx", null, true, null)]
    [InlineData("https://edgedl.me.gvt1.com/edgedl/release2/chrome_component/x/file.crx", null, true, null)]
    [InlineData("https://clients2.googleusercontent.com/some/file", "application/x-chrome-extension", true, null)]
    [InlineData("https://clients2.googleusercontent.com/some/file.zip", "application/zip", false, null)]
    [InlineData("https://evil.example/service/update2/crx?x=id%3D" + Id, null, false, null)]
    [InlineData("https://evil.example/ext.crx", "application/x-chrome-extension", false, null)]
    [InlineData("http://clients2.google.com/service/update2/crx", null, false, null)]
    [InlineData("https://clients2.google.com/service/update2/crx?x=id%3Dnot-an-id", null, true, null)]
    public void IsPackageDownload(string url, string? mime, bool expected, string? expectedId)
    {
        Assert.Equal(expected, WebStore.IsPackageDownload(url, mime, out var id));
        Assert.Equal(expectedId, id);
    }

    [Fact]
    public void PackageUrl_UsesEngineVersionAndId()
    {
        var url = WebStore.PackageUrl(Id, "156.0.4314.58 canary");
        Assert.StartsWith("https://clients2.google.com/service/update2/crx?", url);
        Assert.Contains("prodversion=156.0.4314.58&", url);
        Assert.Contains("x=id%3D" + Id + "%26", url);
        Assert.True(WebStore.IsPackageDownload(url, null, out var id));
        Assert.Equal(Id, id);
        Assert.Throws<ArgumentException>(() => WebStore.PackageUrl("../evil", "1.0"));
    }

    [Theory]
    [InlineData("https://clients2.googleusercontent.com/crx/blobs/x.crx", true)]
    [InlineData("https://clients2.google.com/service/update2/crx", true)]
    [InlineData("http://clients2.googleusercontent.com/crx/blobs/x.crx", false)]
    [InlineData("https://googleusercontent.com.evil.example/x.crx", false)]
    public void IsTrustedPackageSource(string url, bool expected) => Assert.Equal(expected, WebStore.IsTrustedPackageSource(new Uri(url)));

    [Fact]
    public void ExtractArchive_WritesFiles_SkipsMetadata()
    {
        using var temp = new TempDir();
        var target = temp.Sub("out");
        WebStore.ExtractArchive(Crx.Zip(
            ("manifest.json", "{}"),
            ("js/app.js", "1"),
            ("_metadata/verified_contents.json", "[]")), target);

        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(target, "js", "app.js")));
        Assert.False(Directory.Exists(Path.Combine(target, "_metadata")));
    }

    [Theory]
    [InlineData("../evil.js")]
    [InlineData("js/../../evil.js")]
    [InlineData("/etc/evil.js")]
    [InlineData("C:/evil.js")]
    [InlineData("..\\evil.js")]
    public void ExtractArchive_RejectsPathsOutside(string path)
    {
        using var temp = new TempDir();
        var target = temp.Sub("out");
        Assert.Throws<CrxException>(() => WebStore.ExtractArchive(Crx.Zip(("manifest.json", "{}"), (path, "x")), target));
        Assert.False(File.Exists(Path.Combine(temp.Path, "evil.js")));
    }

    [Fact]
    public void ExtractArchive_RejectsDuplicateEntries()
    {
        using var temp = new TempDir();
        Assert.Throws<CrxException>(() => WebStore.ExtractArchive(Crx.Zip(("a.js", "1"), ("a.js", "2")), temp.Sub("out")));
    }

    [Fact]
    public void ExtractArchive_RejectsCorruptedZip()
    {
        using var temp = new TempDir();
        Assert.Throws<CrxException>(() => WebStore.ExtractArchive(new byte[] { 1, 2, 3 }, temp.Sub("out")));
    }

    [Fact]
    public void WriteDeveloperKey_GivesStoreId()
    {
        using var temp = new TempDir();
        using var rsa = RSA.Create(2048);
        var dir = temp.Sub("ext");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """
            {
              // комментарии Chrome допускает
              "manifest_version": 3, "name": "A", "version": "1.0", "key": "old",
            }
            """);

        WebStore.WriteDeveloperKey(dir, Crx.Spki(rsa));

        var manifest = ManifestReader.ReadFromDirectory(dir);
        Assert.Equal("A", manifest.Name);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
        var key = Convert.FromBase64String(doc.RootElement.GetProperty("key").GetString()!);
        Assert.Equal(CrxPackage.IdFromPublicKey(Crx.Spki(rsa)), CrxPackage.IdFromPublicKey(key));
    }

    [Fact]
    public void DisplayName_ResolvesLocalizedName()
    {
        using var temp = new TempDir();
        var dir = temp.Sub("ext");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """{"name":"__MSG_appName__","default_locale":"de"}""");
        Directory.CreateDirectory(Path.Combine(dir, "_locales", "de"));
        File.WriteAllText(Path.Combine(dir, "_locales", "de", "messages.json"), """{"APPNAME":{"message":"Notizen"}}""");

        Assert.Equal("Notizen", WebStore.DisplayName(dir, "__MSG_appName__"));
        Assert.Equal("Plain", WebStore.DisplayName(dir, "Plain"));
        Assert.Equal("__MSG_missing__", WebStore.DisplayName(dir, "__MSG_missing__"));
    }
}
