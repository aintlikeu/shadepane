using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shadepane.App.Native;
using Shadepane.Core.Audit;
using Shadepane.Core.Policy;

namespace Shadepane.App.Protection;

/// <summary>
/// Ставит WDA_EXCLUDEFROMCAPTURE на окна браузера и следит, чтобы флаг не сняли
/// (типичный обход — внедрить код в процесс и вызвать SetWindowDisplayAffinity(hwnd, WDA_NONE)).
/// Это не предотвращает обход, а сужает окно атаки до одного интервала проверки и оставляет след в аудите.
/// </summary>
internal sealed class CaptureGuard
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(200);

    private readonly BrowserPolicy _policy;
    private readonly AuditLog _audit;
    private readonly HashSet<Window> _windows = [];
    private readonly DispatcherTimer _timer;
    private bool _tripped;

    /// <summary>Защита нарушена; аргумент — причина. Вызывается один раз.</summary>
    public event Action<string>? Tripped;

    public CaptureGuard(BrowserPolicy policy, AuditLog audit)
    {
        _policy = policy;
        _audit = audit;
        _timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = CheckInterval };
        _timer.Tick += (_, _) => Check();
    }

    public static bool IsRemoteSession => NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;

#if SCREENSHOTS
    // Сборка для скриншотов (Shadepane.App.csproj, ScreenshotBuild): окна не защищаются и не проверяются.
    public void Start() => _audit.Write("screenshot-build", new { warning = "screen capture protection is OFF" });
#else
    public void Start() => _timer.Start();
#endif

    public void Stop() => _timer.Stop();

    /// <summary>Вызывать из OnSourceInitialized: HWND уже есть, а первый кадр ещё не нарисован.</summary>
    public void Protect(Window window)
    {
#if !SCREENSHOTS
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
        {
            Trip($"set-affinity-failed:win32={Marshal.GetLastPInvokeError()}");
            return;
        }

        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
#endif
    }

    /// <summary>Итог защиты всплывающего окна. <see cref="Layered"/> — окно прозрачное (WS_EX_LAYERED).</summary>
    public readonly record struct TransientProtection(bool Protected, bool Layered);

    /// <summary>
    /// Всплывающее окно WPF (меню, подсказка, панель): у него свой HWND верхнего уровня, флаг главного окна на него
    /// не распространяется, поэтому ставим тот же флаг при каждом открытии. Если Windows отказала, пробуем
    /// WDA_MONITOR: содержимое так же не попадает в снимок, только на его месте чёрный прямоугольник.
    /// Не вышло — Protected = false, и вызывающий закрывает окно (fail closed). На Windows 10 21H2 Windows отказывает
    /// так (ошибка 8) прозрачным окнам меню и подсказок; что делать с меню — решает App.ProtectTransientWindows.
    /// Контрольный цикл такие окна не проверяет: они живут секунды, а содержимого страниц в них нет.
    /// </summary>
    public TransientProtection ProtectTransient(Visual content, string kind)
    {
#if SCREENSHOTS
        return new(true, false);
#else
        if (PresentationSource.FromVisual(content) is not HwndSource source || source.Handle == IntPtr.Zero)
        {
            _audit.Write("popup-protect-failed", new { kind, reason = "no-hwnd" });
            return new(false, false);
        }
        var hwnd = source.Handle;
        var layered = IsLayered(hwnd);
        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
            return new(true, layered);
        var win32 = Marshal.GetLastPInvokeError();
        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_MONITOR))
        {
            _audit.Write("popup-protect-fallback", new { kind, mode = "monitor", win32, layered });
            return new(true, layered);
        }
        var monitorWin32 = Marshal.GetLastPInvokeError();
        _audit.Write("popup-protect-failed", new { kind, win32, monitorWin32, layered });
        return new(false, layered);
#endif
    }

    private static bool IsLayered(IntPtr hwnd) =>
        ((long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_LAYERED) != 0;

    private void Check()
    {
        if (_tripped)
            return;

        if (_policy.BlockInRemoteSession && IsRemoteSession)
        {
            // Покрывает и случай, когда к уже открытой сессии подключились по RDP.
            Trip("remote-session");
            return;
        }

        // Снимок: обработчик Tripped закрывает окна, а это меняет _windows.
        foreach (var window in _windows.ToArray())
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                continue;
            if (!NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity) ||
                affinity is not (NativeMethods.WDA_EXCLUDEFROMCAPTURE or NativeMethods.WDA_MONITOR))
            {
                Trip($"affinity-changed:0x{affinity:X2}:{window.GetType().Name}");
                return;
            }
        }
    }

    private void Trip(string reason)
    {
        if (_tripped)
            return;
        _tripped = true;
        _timer.Stop();
        _audit.Write("capture-guard-tripped", new { reason });
        Tripped?.Invoke(reason);
    }
}
