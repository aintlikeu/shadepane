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

    /// <summary>Тёмный заголовок окна (Windows 10 2004+ и Windows 11).</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Скруглённые углы окна на Windows 11 (для окна со своим заголовком — явно).</summary>
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    // ---------- Свой заголовок окна (MainWindow, WindowChrome) ----------

    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCMOUSEMOVE = 0x00A0;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_NCLBUTTONUP = 0x00A2;
    public const int WM_NCLBUTTONDBLCLK = 0x00A3;
    public const int WM_NCMOUSELEAVE = 0x02A2;
    public const int HTMAXBUTTON = 9;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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
