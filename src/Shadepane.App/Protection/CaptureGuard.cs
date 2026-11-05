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

    private static readonly TimeSpan TransientRetryInterval = TimeSpan.FromMilliseconds(30);
    private const int TransientRetries = 10;

    /// <summary>Всплывающие окна, которые ждут защиты (содержимое пока невидимо).</summary>
    private readonly Dictionary<UIElement, PendingTransient> _pending = [];

    private sealed record PendingTransient(DispatcherTimer Timer, object OriginalOpacity, object OriginalHitTest);

    /// <summary>
    /// Всплывающее окно WPF (меню, подсказка, панель): у него свой HWND верхнего уровня, флаг главного окна на него
    /// не распространяется, поэтому ставим тот же флаг при каждом открытии.
    /// Если Windows отказала (на Windows 10 21H2 у меню и подсказок встречается ошибка 8; их окна прозрачные, и Opened
    /// приходит раньше первого кадра — что из этого причина, пока не установлено), по очереди:
    /// 1) WDA_MONITOR — содержимое так же не попадает в снимок, только на его месте чёрный прямоугольник;
    /// 2) несколько повторов в течение ~300 мс, пока содержимое невидимо (Opacity 0): в снимок попадать нечему.
    /// Не вышло и так — <paramref name="onFailed"/>: вызывающий закрывает окно (fail closed).
    /// Контрольный цикл такие окна не проверяет: они живут секунды, а содержимого страниц в них нет.
    /// </summary>
    public void ProtectTransient(UIElement content, string kind, Action onFailed)
    {
#if !SCREENSHOTS
        CancelPending(content); // окно открыли заново, пока шли повторы для прошлого открытия

        if (PresentationSource.FromVisual(content) is not HwndSource source || source.Handle == IntPtr.Zero)
        {
            _audit.Write("popup-protect-failed", new { kind, reason = "no-hwnd" });
            onFailed();
            return;
        }
        var hwnd = source.Handle;
        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
            return;
        var firstError = Marshal.GetLastPInvokeError();
        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_MONITOR))
        {
            _audit.Write("popup-protect-fallback", new { kind, mode = "monitor", firstError, layered = IsLayered(hwnd) });
            return;
        }
        var monitorError = Marshal.GetLastPInvokeError();

        // Пока флага нет, окно не должно ничего показывать, а по невидимым пунктам нельзя щёлкнуть.
        var original = content.ReadLocalValue(UIElement.OpacityProperty);
        var originalHitTest = content.ReadLocalValue(UIElement.IsHitTestVisibleProperty);
        content.Opacity = 0;
        content.IsHitTestVisible = false;
        var attempts = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TransientRetryInterval };
        _pending[content] = new PendingTransient(timer, original, originalHitTest);
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (PresentationSource.FromVisual(content) is not HwndSource current || current.IsDisposed || current.Handle != hwnd)
            {
                CancelPending(content); // окно уже закрыли
                return;
            }
            string? mode = NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE) ? "exclude-retry"
                : NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_MONITOR) ? "monitor-retry"
                : null;
            if (mode is not null)
            {
                CancelPending(content);
                _audit.Write("popup-protect-fallback", new { kind, mode, attempts, firstError, monitorError, layered = IsLayered(hwnd) });
                return;
            }
            if (attempts < TransientRetries)
                return;
            var lastError = Marshal.GetLastPInvokeError();
            _audit.Write("popup-protect-failed", new { kind, win32 = firstError, monitorError, lastError, attempts, layered = IsLayered(hwnd) });
            onFailed();             // сначала закрыть окно,
            CancelPending(content); // потом вернуть видимость: иначе незащищённое меню успеет мелькнуть
        };
        timer.Start();
#endif
    }

    private void CancelPending(UIElement content)
    {
        if (!_pending.Remove(content, out var pending))
            return;
        pending.Timer.Stop();
        Restore(content, UIElement.OpacityProperty, pending.OriginalOpacity);
        Restore(content, UIElement.IsHitTestVisibleProperty, pending.OriginalHitTest);
    }

    private static void Restore(UIElement content, DependencyProperty property, object localValue)
    {
        if (localValue == DependencyProperty.UnsetValue)
            content.ClearValue(property);
        else
            content.SetValue(property, localValue);
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
