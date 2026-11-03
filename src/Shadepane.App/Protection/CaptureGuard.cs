using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    /// <summary>Вызывать из OnSourceInitialized: HWND уже есть, а первый кадр ещё не нарисован.</summary>
    public void Protect(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
        {
            Trip($"set-affinity-failed:win32={Marshal.GetLastPInvokeError()}");
            return;
        }

        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
    }

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
