using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Shadepane.App.Browser;
using Shadepane.App.Protection;
using Shadepane.Core.Extensions;
using Shadepane.Core.Navigation;

namespace Shadepane.App;

public partial class MainWindow : ProtectedWindow, IContentShield
{
    private readonly ExtensionHost _extensions = new();
    private readonly Dictionary<string, ExtensionPopupWindow> _popups = [];
    private CoreWebView2? _core;
    private bool _busy;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _closed = true;
    }

    public void HideContent()
    {
        WebView.Visibility = Visibility.Collapsed;
        HiddenNotice.Visibility = Visibility.Visible;
    }

    private async Task InitializeAsync()
    {
        // Шаг запуска — чтобы по сообщению об ошибке было понятно, что именно не сработало.
        var stage = "runtime-version";
        try
        {
            AppServices.Audit.Write("webview-runtime", new { available = CoreWebView2Environment.GetAvailableBrowserVersionString() });

            stage = "create-environment";
            var environment = await BrowserEnvironment.GetAsync();

            stage = "create-controller";
            await WebView.EnsureCoreWebView2Async(environment);
            if (_closed)
                return;

            _core = WebView.CoreWebView2;
            AppServices.Audit.Write("webview-ready", new { runtime = environment.BrowserVersionString });

            stage = "hardening";
            await WebViewHardening.ApplyAsync(_core, "main", OpenInMainView);
            WebViewHardening.BlockLeakShortcuts(WebView, "main");
            _core.NavigationStarting += OnNavigationStarting;
            _core.SourceChanged += (_, _) => AddressBox.Text = _core.Source;
            _core.HistoryChanged += (_, _) =>
            {
                BackButton.IsEnabled = _core.CanGoBack;
                ForwardButton.IsEnabled = _core.CanGoForward;
            };

            stage = "extensions-sync";
            await _extensions.SyncAsync(_core.Profile);
            if (_closed)
                return;
            RebuildExtensionBar();

            if (_extensions.UnremovableExtensions.Count > 0)
                SetStatus($"Встроенные расширения движка, которые нельзя удалить или отключить: {string.Join(", ", _extensions.UnremovableExtensions)} (см. audit.log)");

            stage = "navigate-home";

            AddressBox.IsEnabled = ReloadButton.IsEnabled = AddExtensionButton.IsEnabled = true;
            var home = AppServices.Policy.HomeUrl;
            if (home == UrlPolicy.AboutBlank)
                _core.Navigate(UrlPolicy.AboutBlank);
            else
                NavigateFromInput(home);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Не найден WebView2 Runtime. Установите его: https://developer.microsoft.com/microsoft-edge/webview2/",
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
        catch (Exception ex) when (!_closed)
        {
            AppServices.Audit.Write("webview-init-failed", Diagnostics.Details(ex, stage));
            MessageBox.Show(this, $"Не удалось запустить движок браузера.\n\nШаг: {stage}\n{Diagnostics.Describe(ex)}\n\nПодробности: {AppServices.Audit.FilePath}",
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    // ---------- Навигация ----------

    private void NavigateFromInput(string input)
    {
        if (_core is null)
            return;
        if (UrlPolicy.TryNormalizeUserInput(input, out var uri, out var error))
            _core.Navigate(uri!.AbsoluteUri);
        else
            SetStatus($"Адрес не открыт: {error}");
    }

    private void OpenInMainView(Uri uri)
    {
        _core?.Navigate(uri.AbsoluteUri);
        Activate();
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (UrlPolicy.IsAllowedTopLevelNavigation(e.Uri))
            return;
        e.Cancel = true;
        AppServices.Audit.Write("navigation-blocked", new { uri = e.Uri });
        SetStatus($"Переход заблокирован политикой: {Truncate(e.Uri, 120)}");
    }

#if DEBUG
    /// <summary>
    /// Симуляция атаки для проверки контрольного цикла: Ctrl+Alt+Shift+D снимает флаг защиты
    /// с этого окна — так же, как сделал бы код, внедрённый в процесс. Только в Debug-сборке.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.D && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.NativeMethods.SetWindowDisplayAffinity(hwnd, Native.NativeMethods.WDA_NONE);
            SetStatus("DEBUG: флаг защиты снят — контрольный цикл должен сработать в течение ~200 мс");
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
#endif

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        NavigateFromInput(AddressBox.Text);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _core?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _core?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _core?.Reload();

    // ---------- Расширения ----------

    private async void AddExtension_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _core is null)
            return; // защита от двойного нажатия
        _busy = true;
        AddExtensionButton.IsEnabled = false;
        try
        {
            var dialog = new OpenFolderDialog { Title = "Папка распакованного расширения (с manifest.json)" };
            if (dialog.ShowDialog(this) != true)
                return; // отмена

            var (added, validation) = await _extensions.AddFromFolderAsync(_core.Profile, dialog.FolderName);
            if (_closed)
                return;
            if (added is null)
            {
                var reasons = string.Join("\n", validation.Violations.Select(v => "• " + v.Message));
                MessageBox.Show(this, $"Расширение отклонено политикой:\n\n{reasons}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RebuildExtensionBar();
            SetStatus($"Расширение добавлено: {added.Name}");
        }
        catch (Exception ex) when (ex is ManifestException or ExtensionDirectoryException)
        {
            MessageBox.Show(this, $"Расширение не принято:\n\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (!_closed)
        {
            AppServices.Audit.Write("extension-add-failed", Diagnostics.Details(ex));
            MessageBox.Show(this, $"Не удалось добавить расширение:\n\n{Diagnostics.Describe(ex)}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            if (!_closed)
                AddExtensionButton.IsEnabled = true;
        }
    }

    private async Task RemoveExtensionAsync(ApprovedExtension extension)
    {
        if (_busy || _core is null)
            return;
        var answer = MessageBox.Show(this, $"Удалить расширение «{extension.Name}»?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        _busy = true;
        try
        {
            if (_popups.TryGetValue(extension.Id, out var popup))
                popup.Close();
            await _extensions.RemoveAsync(_core.Profile, extension.Id);
            if (_closed)
                return;
            RebuildExtensionBar();
            SetStatus($"Расширение удалено: {extension.Name}");
        }
        catch (Exception ex) when (!_closed)
        {
            AppServices.Audit.Write("extension-remove-failed", Diagnostics.Details(ex));
            MessageBox.Show(this, $"Не удалось удалить расширение:\n\n{Diagnostics.Describe(ex)}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void RebuildExtensionBar()
    {
        ExtensionBar.Children.Clear();
        foreach (var extension in _extensions.Approved)
        {
            var button = new Button
            {
                Width = 30,
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = extension.Name,
                Content = CreateIcon(extension),
            };
            button.Click += (_, _) => OpenPopup(extension, button);

            var remove = new MenuItem { Header = "Удалить расширение" };
            remove.Click += async (_, _) => await RemoveExtensionAsync(extension);
            button.ContextMenu = new ContextMenu { Items = { remove } };

            ExtensionBar.Children.Add(button);
        }
    }

    private static UIElement CreateIcon(ApprovedExtension extension)
    {
        if (extension.IconPath is not null &&
            ExtensionDirectory.TryResolveInside(extension.StagedPath, extension.IconPath, out var file, out _) &&
            file!.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // не держать файл открытым
                bitmap.DecodePixelWidth = 20;
                bitmap.UriSource = new Uri(file);
                bitmap.EndInit();
                bitmap.Freeze();
                return new Image { Source = bitmap, Width = 18, Height = 18 };
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
            {
                // битая иконка — показываем букву
            }
        }

        return new TextBlock
        {
            Text = extension.Name.Length > 0 ? extension.Name[..1].ToUpperInvariant() : "?",
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    private void OpenPopup(ApprovedExtension extension, FrameworkElement anchor)
    {
        if (_popups.TryGetValue(extension.Id, out var existing))
        {
            existing.Activate(); // повторный клик не открывает второе окно
            return;
        }

        if (extension.PopupPath is null ||
            !ExtensionDirectory.TryResolveInside(extension.StagedPath, extension.PopupPath, out _, out var urlPath))
        {
            SetStatus($"У «{extension.Name}» нет popup. Клик по иконке (action.onClicked) WebView2 не поддерживает.");
            return;
        }

        var popup = new ExtensionPopupWindow(extension, UrlPolicy.BuildExtensionUrl(extension.Id, urlPath!), OpenInMainView)
        {
            Owner = this,
        };
        PositionUnder(popup, anchor);
        _popups[extension.Id] = popup;
        popup.Closed += (_, _) => _popups.Remove(extension.Id);
        popup.Show();
    }

    private static void PositionUnder(Window popup, FrameworkElement anchor)
    {
        var source = PresentationSource.FromVisual(anchor);
        if (source?.CompositionTarget is null)
            return;
        // PointToScreen возвращает физические пиксели, а Left/Top — в DIP: переводим с учётом DPI.
        var devicePoint = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var dip = source.CompositionTarget.TransformFromDevice.Transform(devicePoint);
        popup.WindowStartupLocation = WindowStartupLocation.Manual;
        popup.Left = Math.Max(SystemParameters.VirtualScreenLeft, dip.X - popup.Width);
        popup.Top = dip.Y + 2;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static string Truncate(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "…";
}
