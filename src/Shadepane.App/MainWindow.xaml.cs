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
    private const string ProductName = "Shadepane";

    private readonly ExtensionHost _extensions = new();
    private readonly Dictionary<string, ExtensionPopupWindow> _popups = [];
    private readonly List<BrowserTab> _tabs = [];
    private readonly Dictionary<BrowserTab, Border> _tabHeaders = [];
    private CoreWebView2Environment? _environment;
    private CoreWebView2Profile? _profile;
    private BrowserTab? _active;
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
        ContentHost.Visibility = Visibility.Collapsed;
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
            _environment = await BrowserEnvironment.GetAsync();

            stage = "create-first-tab";
            var first = await CreateTabAsync(activate: true);
            if (_closed || first?.Core is null)
                return;
            _profile = first.Core.Profile;
            AppServices.Audit.Write("webview-ready", new { runtime = _environment.BrowserVersionString });

            stage = "extensions-sync";
            await _extensions.SyncAsync(_profile);
            if (_closed)
                return;
            RebuildExtensionBar();

            stage = "navigate-home";
            AddressBox.IsEnabled = ReloadButton.IsEnabled = AddExtensionButton.IsEnabled = NewTabButton.IsEnabled = true;
            NavigateHome(first);
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

    // ---------- Вкладки ----------

    /// <summary>Создаёт и инициализирует вкладку. null — окно закрылось или вкладку закрыли во время создания.</summary>
    private async Task<BrowserTab?> CreateTabAsync(bool activate, int? insertAt = null)
    {
        if (_environment is null || _closed)
            return null;

        var tab = new BrowserTab();
        tab.Changed += OnTabChanged;
        tab.CloseRequested += t => Dispatcher.BeginInvoke(() => CloseTab(t)); // не уничтожаем WebView2 внутри его события
        tab.Status += (t, message) => { if (t == _active) SetStatus(message); };

        var index = Math.Clamp(insertAt ?? _tabs.Count, 0, _tabs.Count);
        _tabs.Insert(index, tab);
        ContentHost.Children.Add(tab.View);
        var header = CreateTabHeader(tab);
        _tabHeaders[tab] = header;
        TabStrip.Children.Insert(index, header);
        if (activate || _active is null)
            ActivateTab(tab);

        try
        {
            await tab.InitializeAsync(_environment, OnNewWindowRequested);
        }
        catch
        {
            // Не оставляем вкладку без движка; окно не закрываем, даже если вкладка была единственной —
            // о сбое сообщит вызывающий (для первой вкладки это сообщение об ошибке запуска).
            RemoveTab(tab);
            throw;
        }
        if (tab.IsClosed || _closed)
            return null;
        OnTabChanged(tab);
        return tab;
    }

    /// <summary>
    /// window.open / target=_blank / Ctrl+клик → новая вкладка. Через e.NewWindow, чтобы сохранить
    /// window.opener (без него не работают окна входа). Ctrl при клике — вкладка в фоне, как в Chrome.
    /// </summary>
    private async void OnNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var background = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var insertAt = _active is null ? (int?)null : _tabs.IndexOf(_active) + 1;
            var tab = await CreateTabAsync(activate: !background, insertAt);
            if (tab?.Core is not null)
                e.NewWindow = tab.Core;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            e.Handled = true;
            AppServices.Audit.Write("new-tab-failed", Diagnostics.Details(ex));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task OpenNewTabAsync(string? url = null)
    {
        try
        {
            var tab = await CreateTabAsync(activate: true);
            if (tab is null)
                return;
            if (url is null)
            {
                NavigateHome(tab);
                FocusAddressBox();
            }
            else
            {
                tab.Navigate(url);
            }
        }
        catch (Exception ex) when (!_closed)
        {
            AppServices.Audit.Write("new-tab-failed", Diagnostics.Details(ex));
            SetStatus($"Не удалось открыть вкладку: {Diagnostics.Describe(ex)}");
        }
    }

    private void OpenInNewTab(Uri uri)
    {
        _ = OpenNewTabAsync(uri.AbsoluteUri);
        Activate();
    }

    private void ActivateTab(BrowserTab tab)
    {
        if (tab.IsClosed)
            return;
        _active = tab;
        foreach (var t in _tabs)
            t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Hidden;
        foreach (var (t, header) in _tabHeaders)
            StyleTabHeader(header, t == tab);
        RefreshChrome();
        if (_tabHeaders.TryGetValue(tab, out var active))
            active.BringIntoView();
    }

    /// <summary>Убирает вкладку из окна и уничтожает её WebView2. Возвращает её прежний индекс или -1.</summary>
    private int RemoveTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
            return -1;
        _tabs.RemoveAt(index);
        if (_tabHeaders.Remove(tab, out var header))
            TabStrip.Children.Remove(header);
        ContentHost.Children.Remove(tab.View);
        tab.Close();
        if (_active == tab)
            _active = null;
        return index;
    }

    private void CloseTab(BrowserTab tab)
    {
        var wasActive = _active == tab;
        var index = RemoveTab(tab);
        if (index < 0)
            return;

        if (_tabs.Count == 0)
        {
            Close(); // как в Chrome: закрыли последнюю вкладку — закрыли окно
            return;
        }
        if (wasActive)
            ActivateTab(_tabs[Math.Clamp(index, 0, _tabs.Count - 1)]);
    }

    private void SelectRelativeTab(int delta)
    {
        if (_active is null || _tabs.Count < 2)
            return;
        var index = (_tabs.IndexOf(_active) + delta + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[index]);
    }

    private void SelectTabByNumber(int number)
    {
        // Ctrl+1..8 — вкладка по номеру, Ctrl+9 — последняя (как в Chrome).
        if (_tabs.Count == 0)
            return;
        var index = number == 9 ? _tabs.Count - 1 : number - 1;
        if (index >= 0 && index < _tabs.Count)
            ActivateTab(_tabs[index]);
    }

    private Border CreateTabHeader(BrowserTab tab)
    {
        var title = new TextBlock
        {
            Text = tab.Title,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "✕",
            Width = 20,
            Height = 20,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ToolTip = "Закрыть вкладку (Ctrl+W)",
            Focusable = false,
        };
        close.Click += (_, _) => CloseTab(tab);
        DockPanel.SetDock(close, Dock.Right);

        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(close);
        panel.Children.Add(title);

        var header = new Border
        {
            Child = panel,
            Padding = new Thickness(10, 4, 4, 4),
            Margin = new Thickness(0, 0, 2, 0),
            BorderThickness = new Thickness(1, 1, 1, 0),
            BorderBrush = SystemColors.ActiveBorderBrush,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Tag = title,
            Cursor = Cursors.Hand,
        };
        header.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button) ActivateTab(tab); };
        header.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        return header;
    }

    private static void StyleTabHeader(Border header, bool isActive)
    {
        header.Background = isActive ? SystemColors.WindowBrush : SystemColors.ControlBrush;
        if (header.Tag is TextBlock title)
            title.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void OnTabChanged(BrowserTab tab)
    {
        if (_closed || tab.IsClosed)
            return;
        if (_tabHeaders.TryGetValue(tab, out var header) && header.Tag is TextBlock title)
        {
            title.Text = tab.Title;
            header.ToolTip = string.IsNullOrEmpty(tab.Url) ? tab.Title : $"{tab.Title}\n{tab.Url}";
        }
        if (tab == _active)
            RefreshChrome();
    }

    /// <summary>Адресная строка, кнопки и заголовок окна — по активной вкладке.</summary>
    private void RefreshChrome()
    {
        var tab = _active;
        var core = tab?.Core;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        // Пока пользователь печатает в адресной строке, не затираем введённое.
        if (!AddressBox.IsKeyboardFocusWithin)
            AddressBox.Text = tab?.Url == UrlPolicy.AboutBlank ? "" : tab?.Url ?? "";
        Title = tab is null ? ProductName : $"{tab.Title} — {ProductName}";
    }

    // ---------- Навигация ----------

    private void NavigateHome(BrowserTab tab)
    {
        var home = AppServices.Policy.HomeUrl;
        if (home == UrlPolicy.AboutBlank)
            tab.Navigate(UrlPolicy.AboutBlank);
        else if (UrlPolicy.TryNormalizeUserInput(home, out var uri, out _))
            tab.Navigate(uri!.AbsoluteUri);
    }

    private void NavigateFromInput(string input)
    {
        if (_active is null)
            return;
        if (UrlPolicy.TryNormalizeUserInput(input, out var uri, out var error))
        {
            _active.Navigate(uri!.AbsoluteUri);
            _active.View.Focus();
        }
        else
        {
            SetStatus($"Адрес не открыт: {error}");
        }
    }

    private void FocusAddressBox()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

#if DEBUG
    /// <summary>
    /// Симуляция атаки для проверки контрольного цикла: Ctrl+Alt+Shift+D снимает флаг защиты
    /// с этого окна — так же, как сделал бы код, внедрённый в процесс. Только в Debug-сборке.
    /// </summary>
    private bool HandleDebugKeys(KeyEventArgs e)
    {
        if (e.Key == Key.D && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.NativeMethods.SetWindowDisplayAffinity(hwnd, Native.NativeMethods.WDA_NONE);
            SetStatus("DEBUG: флаг защиты снят — контрольный цикл должен сработать в течение ~200 мс");
            return true;
        }
        return false;
    }
#endif

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
#if DEBUG
        if (HandleDebugKeys(e))
        {
            e.Handled = true;
            return;
        }
#endif
        base.OnPreviewKeyDown(e);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                NavigateFromInput(AddressBox.Text);
                break;
            case Key.Escape:
                // Отменить ввод: вернуть адрес активной вкладки и отдать фокус странице.
                e.Handled = true;
                AddressBox.Text = _active?.Url == UrlPolicy.AboutBlank ? "" : _active?.Url ?? "";
                _active?.View.Focus();
                break;
        }
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        Dispatcher.BeginInvoke(AddressBox.SelectAll); // после клика мышью выделение иначе сбрасывается

    private void NewTab_Click(object sender, RoutedEventArgs e) => _ = OpenNewTabAsync();
    private void Back_Click(object sender, RoutedEventArgs e) => _active?.Core?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _active?.Core?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _active?.Core?.Reload();

    // ---------- Расширения ----------

    private async void AddExtension_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _profile is null)
            return; // защита от двойного нажатия
        _busy = true;
        AddExtensionButton.IsEnabled = false;
        try
        {
            var dialog = new OpenFolderDialog { Title = "Папка распакованного расширения (с manifest.json)" };
            if (dialog.ShowDialog(this) != true)
                return; // отмена

            var (added, validation) = await _extensions.AddFromFolderAsync(_profile, dialog.FolderName);
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
        if (_busy || _profile is null)
            return;
        var answer = MessageBox.Show(this, $"Удалить расширение «{extension.Name}»?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        _busy = true;
        try
        {
            if (_popups.TryGetValue(extension.Id, out var popup))
                popup.Close();
            await _extensions.RemoveAsync(_profile, extension.Id);
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

        var popup = new ExtensionPopupWindow(extension, UrlPolicy.BuildExtensionUrl(extension.Id, urlPath!), OpenInNewTab)
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
}
