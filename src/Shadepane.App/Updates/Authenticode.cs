using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Shadepane.App.Updates;

/// <summary>
/// Проверка подписи Authenticode (WinVerifyTrust) и издателя файла. Используется автообновлением: если сам браузер
/// подписан, установщик обновления обязан иметь действительную подпись того же издателя.
/// </summary>
internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>Издатель (Subject сертификата подписи) или null, если файл не подписан.</summary>
    public static string? SignerSubject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // нужен именно сертификат из подписи файла, а не из файла сертификата
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Подпись есть, цепочка доверена, файл после подписи не менялся.</summary>
    public static bool IsValidlySigned(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,           // WTD_UI_NONE
                fdwRevocationChecks = 0,  // WTD_REVOKE_NONE (без сетевой проверки отзыва)
                dwUnionChoice = 1,        // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,        // WTD_STATEACTION_VERIFY
            };
            var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            data.dwStateAction = 2;       // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WinTrustData pWVTData);
}
