using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Shadepane.App.Native;
using Shadepane.Core.Localization;

namespace Shadepane.App;

/// <summary>
/// Свой заголовок окна: вкладки на месте системного заголовка (WindowChrome в MainWindow.xaml) и свои кнопки
/// «Свернуть / Развернуть / Закрыть». То, что системный заголовок делал сам, здесь сделано явно:
/// раскладки Snap на кнопке «Развернуть» (Windows 11), отступ сверху у развёрнутого окна, скруглённые углы.
/// </summary>
public partial class MainWindow
{
    private const double MinTabWidth = 72;
    private const double MaxTabWidth = 220;
    /// <summary>Сколько пустого места заголовка оставить всегда, чтобы окно было за что перетащить.</summary>
    private const double MinDragSpace = 56;

    private bool _maximizeHot;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e); // защита от захвата, цвет заголовка (ProtectedWindow)
        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return;
        // Наш обработчик добавлен после WindowChrome и потому вызывается раньше его: HTMAXBUTTON успевает вернуться
        // до того, как WindowChrome ответит HTCLIENT. Если порядок окажется другим, кнопка всё равно работает как
        // обычная, только без раскладок Snap.
        source.AddHook(TitleBarHook);
        var round = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(source.Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        StateChanged += (_, _) => { UpdateMaximizeButton(); UpdateMaximizedInset(); };
        TitleBar.SizeChanged += (_, _) => UpdateTabWidths();
        UpdateMaximizeButton();
        UpdateMaximizedInset();
        UpdateTitleIcon();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateTitleIcon(); // окно перенесли на монитор с другим масштабом
        UpdateMaximizedInset();
    }

    /// <summary>
    /// Развёрнутое окно Windows сдвигает за края экрана на толщину рамки: слева, справа и снизу за экраном оказывается
    /// системная рамка, а сверху — начало нашего заголовка (рамки там нет, он рисуется до самого края окна).
    /// Опускаем содержимое на эту толщину, иначе верх вкладок и кнопок обрезан.
    /// </summary>
    private void UpdateMaximizedInset()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var top = 0.0;
        if (WindowState == WindowState.Maximized)
        {
            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            var pixels = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYSIZEFRAME, dpi)
                       + NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, dpi);
            top = dpi == 0 ? 8 : pixels * 96.0 / dpi;
        }
        RootPanel.Margin = new Thickness(0, top, 0, 0);
    }

    /// <summary>Иконка в заголовке — кадр .ico под текущий масштаб (16 px при 100%, 24 при 150%…), а не растянутый 16 px.</summary>
    private void UpdateTitleIcon()
    {
        var target = 16 * VisualTreeHelper.GetDpi(this).DpiScaleX;
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/Shadepane.ico", UriKind.Absolute),
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        TitleIcon.Source = decoder.Frames.OrderBy(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth >= target) ?? decoder.Frames[^1];
    }

    private IntPtr TitleBarHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                if (IsOverMaximizeButton(lParam))
                {
                    SetMaximizeHot(true);
                    handled = true;
                    return NativeMethods.HTMAXBUTTON; // Windows 11 показывает раскладки Snap при наведении
                }
                SetMaximizeHot(false);
                break;

            case NativeMethods.WM_NCLBUTTONDOWN:
            case NativeMethods.WM_NCLBUTTONDBLCLK:
                if (wParam == NativeMethods.HTMAXBUTTON)
                    handled = true; // иначе система нарисует свою классическую кнопку и начнёт её «нажимать»
                break;

            case NativeMethods.WM_NCLBUTTONUP:
                if (wParam == NativeMethods.HTMAXBUTTON)
                {
                    handled = true;
                    ToggleMaximize();
                }
                break;

            case NativeMethods.WM_NCMOUSEMOVE:
                if (wParam != NativeMethods.HTMAXBUTTON)
                    SetMaximizeHot(false);
                break;

            case NativeMethods.WM_NCMOUSELEAVE:
                SetMaximizeHot(false);
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible)
            return false;
        // Экранные координаты в физических пикселях; со знаком — на мониторе левее основного они отрицательные.
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        try
        {
            var point = MaximizeButton.PointFromScreen(new Point(x, y));
            return point.X >= 0 && point.Y >= 0 && point.X < MaximizeButton.ActualWidth && point.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false; // кнопка ещё не в визуальном дереве окна
        }
    }

    /// <summary>Подсветка «Развернуть» вручную: над ней мышь в неклиентской области, и WPF о наведении не знает.</summary>
    private void SetMaximizeHot(bool hot)
    {
        if (hot == _maximizeHot)
            return;
        _maximizeHot = hot;
        if (hot)
            MaximizeButton.SetResourceReference(BackgroundProperty, "Brush.Hover");
        else
            MaximizeButton.ClearValue(BackgroundProperty);
    }

    private void UpdateMaximizeButton()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = (Geometry)FindResource(maximized ? "Icon.Restore" : "Icon.Maximize");
        var name = Loc.T(maximized ? "Восстановить" : "Развернуть", maximized ? "Restore" : "Maximize");
        MaximizeButton.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, name);
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>
    /// Ширина вкладок, как в Chrome: пока места хватает — 220 px, дальше вкладки сужаются до 72 px, а за ними
    /// вкладки прокручиваются колесом мыши. Справа всегда остаётся место, за которое можно перетащить окно.
    /// </summary>
    private void UpdateTabWidths()
    {
        if (TitleBar.ActualWidth <= 0)
            return;
        var reserved = 0.0;
        foreach (UIElement child in TitleBar.Children)
        {
            if (child != TabScroller)
                reserved += child.DesiredSize.Width;
        }
        var available = Math.Max(0, TitleBar.ActualWidth - reserved - MinDragSpace);
        TabsColumn.MaxWidth = available;
        var count = Math.Max(1, _tabHeaders.Count);
        var width = Math.Clamp(Math.Floor(available / count) - 2, MinTabWidth, MaxTabWidth);
        foreach (var header in _tabHeaders.Values)
            header.Width = width;
    }

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Вкладка получает клики сама, а не отдаёт их заголовку окна (перетаскивание).</summary>
    private static void MarkInteractiveInTitleBar(FrameworkElement element) =>
        WindowChrome.SetIsHitTestVisibleInChrome(element, true);
}
