using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.IO;

namespace Shadepane.App.Native;

internal static partial class NativeMethods
{
    /// <summary>Защиты нет.</summary>
    public const uint WDA_NONE = 0x00;
    /// <summary>В захвате окно чёрное (поведение до Windows 10 2004).</summary>
    public const uint WDA_MONITOR = 0x01;
    /// <summary>Окно полностью исключено из захвата (Windows 10 2004+).</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public const int SM_REMOTESESSION = 0x1000;

    /// <summary>
    /// Работает только для окна верхнего уровня, принадлежащего текущему процессу, и только при композиции DWM.
    /// Microsoft явно пишет, что это не DRM и не гарантия защиты.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    /// <summary>Итоговый путь открытого файла после разрешения всех ссылок (FILE_NAME_NORMALIZED | VOLUME_NAME_DOS).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);

    public static unsafe string GetFinalPath(SafeFileHandle handle)
    {
        const int capacity = 32768;
        var buffer = new char[capacity];
        fixed (char* ptr = buffer)
        {
            var length = GetFinalPathNameByHandle(handle, ptr, capacity, 0);
            if (length == 0 || length >= capacity)
                throw new IOException($"GetFinalPathNameByHandle: Win32 error {Marshal.GetLastPInvokeError()}");
            var path = new string(buffer, 0, (int)length);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
    }
}
