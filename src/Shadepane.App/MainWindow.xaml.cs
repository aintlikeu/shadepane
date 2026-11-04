using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Shadepane.App.Browser;
using Shadepane.App.Protection;
using Shadepane.Core.Browsing;
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
    private readonly HashSet<BrowserTab> _findSubscribed = [];
    private CoreWebView2Environment? _environment;
    private CoreWebView2Profile? _profile;
    private BrowserTab? _active;
    private bool _busy;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
        AppServices.Bookmarks.Changed += OnBookmarksChanged;
        AppServices.ConfigChanged += OnConfigChanged;
        AppServices.Downloads.Started += OnDownloadStarted;
        AppServices.Downloads.Finished += OnDownloadFinished;
        Closed += (_, _) =>
        {
            _closed = true;
            AppServices.Bookmarks.Changed -= OnBookmarksChanged;
            AppServices.ConfigChanged -= OnConfigChanged;
            AppServices.Downloads.Started -= OnDownloadStarted;
            AppServices.Downloads.Finished -= OnDownloadFinished;
        };
        RebuildBookmarksBar();
    }

    private void OnBookmarksChanged()
    {
        if (_closed)
            return;
        RefreshChrome();
        RebuildBookmarksBar();
    }

    private void OnDownloadStarted(DownloadItem item)
    {
        if (!_closed)
            SetStatus($"Загрузка: {item.FileName} — Ctrl+J, чтобы открыть список загрузок");
    }

    private void OnDownloadFinished(DownloadItem item)
    {
        if (_closed)
            return;
        if (item.IsCompleted)
            SetStatus($"Загружено: {item.FileName}");
        else
            Warn($"Загрузка не завершена: {item.FileName}");
    }

    // ---------- Обновления ----------

    private Shadepane.Core.Updates.UpdateCandidate? _update;
    private bool _updating;

    /// <summary>Через несколько секунд после запуска, если проверка включена и это не сборка разработчика.</summary>
    private async Task CheckForUpdatesInBackgroundAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (_closed || !AppServices.Config.CheckForUpdates || AppServices.Updates.Current.IsDevelopmentBuild)
            return;
        await CheckForUpdatesAsync(manual: false);
    }

    /// <summary>Проверка (фоновая или по кнопке «Проверить сейчас» в настройках).</summary>
    internal async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            var update = await AppServices.Updates.CheckAsync();
            if (_closed)
                return;
            _update = update;
            UpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            if (update is not null)
            {
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(Glyph("Update"));
                label.Children.Add(new TextBlock { Text = $"Обновить до {update.Version}", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                UpdateButton.Content = label;
                UpdateButton.ToolTip = $"Доступна версия {update.Version} (у вас {AppServices.Updates.Current})";
                AppServices.Audit.Write("update-available", new { current = AppServices.Updates.Current.ToString(), available = update.Version.ToString() });
            }
            if (manual)
                SetStatus(update is null ? $"Обновлений нет: у вас последняя версия ({AppServices.Updates.Current})" : $"Доступна версия {update.Version}");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            AppServices.Audit.Write("update-check-failed", new { error = ex.Message });
            if (manual && !_closed)
                Warn($"Не удалось проверить обновления: {ex.Message}");
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var update = _update;
        if (update is null || _updating)
            return; // защита от двойного нажатия
        if (!Updates.UpdateService.IsInstalled)
        {
            // zip-версия: тихой установкой обновлять нечего — открываем страницу релиза
            _ = OpenNewTabAsync(update.PageUrl);
            return;
        }
        if (MessageBox.Show(this, $"Загрузить и установить Shadepane {update.Version}? Браузер закроется и запустится заново.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _updating = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(SetStatus);
            var installer = await AppServices.Updates.DownloadAndVerifyAsync(update, progress);
            AppServices.Audit.Write("update-installing", new { version = update.Version.ToString(), installer });
            Updates.UpdateService.LaunchInstaller(installer);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException
                                       or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            AppServices.Audit.Write("update-failed", Diagnostics.Details(ex));
            if (!_closed)
                MessageBox.Show(this, $"Не удалось обновить браузер:\n\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updating = false;
            if (!_closed)
                UpdateButton.IsEnabled = true;
        }
    }

    private DownloadsWindow? _downloadsWindow;

    private void ShowDownloads()
    {
        if (_downloadsWindow is not null)
        {
            _downloadsWindow.Activate();
            return;
        }
        _downloadsWindow = new DownloadsWindow { Owner = this };
        _downloadsWindow.Closed += (_, _) => _downloadsWindow = null;
        _downloadsWindow.Show();
    }

    private void Downloads_Click(object sender, RoutedEventArgs e) => ShowDownloads();

    private void OnConfigChanged()
    {
        if (!_closed)
            RebuildBookmarksBar();
    }

    // ---------- Панель закладок ----------

    private void RebuildBookmarksBar()
    {
        BookmarksBar.Visibility = AppServices.Settings.ShowBookmarksBar ? Visibility.Visible : Visibility.Collapsed;
        BookmarksBarItems.Children.Clear();
        var items = AppServices.Bookmarks.Items;
        BookmarksBarEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BookmarksOverflowButton.IsEnabled = items.Count > 0;
        foreach (var bookmark in items.Take(200)) // дальше всё равно не видно — остальное через «»»
        {
            var button = new Button
            {
                Style = (Style)FindResource("ToolbarTextButton"),
                Content = new TextBlock { Text = bookmark.Title, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = $"{bookmark.Title}\n{bookmark.Url}",
                Margin = new Thickness(0, 0, 2, 0),
                Focusable = false,
            };
            var url = bookmark.Url;
            var id = bookmark.Id;
            button.Click += (_, _) => OpenBookmark(url, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            button.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) OpenBookmark(url, newTab: true); };
            button.ContextMenu = BookmarkContextMenu(url, id);
            BookmarksBarItems.Children.Add(button);
        }
    }

    private ContextMenu BookmarkContextMenu(string url, string id)
    {
        var openNew = new MenuItem { Header = "Открыть в новой вкладке" };
        openNew.Click += (_, _) => OpenBookmark(url, newTab: true);
        var manage = new MenuItem { Header = "Изменить…" };
        manage.Click += (_, _) => ShowBookmarksManager();
        var remove = new MenuItem { Header = "Удалить" };
        remove.Click += (_, _) => AppServices.Bookmarks.Remove(id);
        return new ContextMenu { Items = { openNew, manage, new Separator(), remove } };
    }

    private void OpenBookmark(string url, bool newTab)
    {
        if (newTab || _active is null)
            _ = OpenNewTabAsync(url);
        else
            _active.Navigate(url);
    }

    private void BookmarksOverflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BookmarksOverflowButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var bookmark in AppServices.Bookmarks.Items)
        {
            var url = bookmark.Url;
            var item = new MenuItem { Header = bookmark.Title.Length > 80 ? bookmark.Title[..80] + "…" : bookmark.Title, ToolTip = url };
            item.Click += (_, _) => OpenBookmark(url, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var manager = new MenuItem { Header = "Управление закладками… (Ctrl+Shift+O)" };
        manager.Click += (_, _) => ShowBookmarksManager();
        menu.Items.Add(manager);
        menu.IsOpen = true;
    }

    private bool ToggleBookmarksBar()
    {
        try
        {
            AppServices.UpdateSettings(AppServices.Settings with { ShowBookmarksBar = !AppServices.Settings.ShowBookmarksBar });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"Не удалось сохранить настройку: {ex.Message}");
        }
        return true;
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
            _ = CheckForUpdatesInBackgroundAsync();
            AddressBox.IsEnabled = ReloadButton.IsEnabled = HomeButton.IsEnabled = AddExtensionButton.IsEnabled = NewTabButton.IsEnabled = SettingsButton.IsEnabled = true;
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
        tab.Status += (t, message) => { if (t == _active) Warn(message); };
        tab.NavigationCompleted += (t, success) =>
        {
            if (!success)
                return;
            AppServices.History.RecordVisit(t.Url, t.Core?.DocumentTitle);
            if (t == _active && _noticeKind == NoticeKind.Warning)
                HideNotice(); // «переход заблокирован» и т. п. относится к прошлой странице
        };

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
    private async void OnNewWindowRequested(CoreWebView2 opener, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var background = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var insertAt = _active is null ? (int?)null : _tabs.IndexOf(_active) + 1;
            var tab = await CreateTabAsync(activate: !background, insertAt);
            if (tab?.Core is not null)
            {
                WebViewHardening.InheritProtection(opener, tab.Core);
                e.NewWindow = tab.Core;
            }
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
            Warn($"Не удалось открыть вкладку: {Diagnostics.Describe(ex)}");
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
        if (_active != tab)
        {
            CloseFind(focusPage: false); // поиск и уведомление относятся к странице, а не к окну
            HideNotice();
        }
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
        _findSubscribed.Remove(tab);
        if (_active == tab)
        {
            CloseFind(focusPage: false);
            _active = null;
        }
        tab.Close();
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

    /// <summary>Значок из Assets/Icons.xaml для кнопки, созданной в коде.</summary>
    private System.Windows.Shapes.Path Glyph(string name, bool small = false) => new()
    {
        Style = (Style)FindResource(small ? "SmallGlyph" : "Glyph"),
        Data = (Geometry)FindResource("Icon." + name),
    };

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
            Style = (Style)FindResource("TabCloseButton"),
            Content = Glyph("Close", small: true),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Закрыть вкладку (Ctrl+W)",
        };
        close.Click += (_, _) => CloseTab(tab);
        DockPanel.SetDock(close, Dock.Right);

        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(close);
        panel.Children.Add(title);

        var header = new Border
        {
            Child = panel,
            Width = 220,
            Height = 34,
            Padding = new Thickness(12, 0, 6, 0),
            Margin = new Thickness(0, 0, 2, 0),
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Tag = title,
        };
        header.MouseEnter += (_, _) => StyleTabHeader(header, tab == _active);
        header.MouseLeave += (_, _) => StyleTabHeader(header, tab == _active);
        header.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button) ActivateTab(tab); };
        header.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        return header;
    }

    /// <summary>
    /// Активная вкладка — цвета панели под ней (сливается с ней), остальные — прозрачные, текст приглушён;
    /// при наведении — лёгкая подсветка. Цвета через ресурсы темы, чтобы работала смена темы на лету.
    /// </summary>
    private static void StyleTabHeader(Border header, bool isActive)
    {
        if (isActive)
            header.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        else if (header.IsMouseOver)
            header.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");
        else
            header.Background = Brushes.Transparent;
        if (header.Tag is TextBlock title)
        {
            title.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            title.SetResourceReference(TextBlock.ForegroundProperty, isActive ? "Brush.Ink" : "Brush.Muted");
        }
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
        AppServices.History.UpdateTitle(tab.Url, tab.Core?.DocumentTitle); // заголовок часто приходит после навигации
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
            AddressBox.Text = ActiveAddress;
        UpdateAddressView();
        UpdateLoadingIndicator();
        Title = tab is null ? ProductName : $"{tab.Title} — {ProductName}";

        var bookmarked = AppServices.Bookmarks.FindByUrl(tab?.Url) is not null;
        BookmarkButton.IsEnabled = tab is not null && Shadepane.Core.Bookmarks.BookmarkCollection.IsBookmarkable(tab.Url);
        BookmarkGlyph.Data = (Geometry)FindResource(bookmarked ? "Icon.StarFilled" : "Icon.Star");
        BookmarkButton.SetResourceReference(ForegroundProperty, bookmarked ? "Brush.AccentText" : "Brush.Ink");
        BookmarkButton.ToolTip = bookmarked ? "Удалить из закладок (Ctrl+D)" : "Добавить в закладки (Ctrl+D)";
    }

    /// <summary>Адрес активной вкладки для адресной строки (пустая страница — пустая строка).</summary>
    private string ActiveAddress => _active?.Url == UrlPolicy.AboutBlank ? "" : _active?.Url ?? "";

    /// <summary>
    /// Без фокуса поверх поля ввода показан адрес с выделенным доменом, а текст самого поля прозрачен;
    /// в фокусе — обычное поле с полным адресом.
    /// </summary>
    private void UpdateAddressView()
    {
        var focused = AddressBox.IsKeyboardFocusWithin;
        AddressView.Visibility = focused ? Visibility.Collapsed : Visibility.Visible;
        if (focused)
        {
            AddressBox.SetResourceReference(ForegroundProperty, "Brush.Ink");
            return;
        }
        AddressBox.Foreground = Brushes.Transparent;
        AddressView.Inlines.Clear();
        var parts = AddressDisplay.Split(AddressBox.Text);
        if (parts.IsEmpty)
        {
            AddressView.Inlines.Add(Themed(new System.Windows.Documents.Run("Введите адрес или поисковый запрос"), "Brush.Muted"));
            return;
        }
        if (parts.Prefix.Length > 0)
            AddressView.Inlines.Add(Themed(new System.Windows.Documents.Run(parts.Prefix), "Brush.Muted"));
        if (parts.Host.Length > 0)
            AddressView.Inlines.Add(Themed(new System.Windows.Documents.Run(parts.Host), "Brush.Ink"));
        if (parts.Rest.Length > 0)
            AddressView.Inlines.Add(Themed(new System.Windows.Documents.Run(parts.Rest), parts.Host.Length > 0 ? "Brush.Muted" : "Brush.Ink"));
    }

    private static System.Windows.Documents.Run Themed(System.Windows.Documents.Run run, string brushKey)
    {
        run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brushKey);
        return run;
    }

    /// <summary>Звёздочка / Ctrl+D: добавить текущую страницу в закладки или убрать её оттуда.</summary>
    private bool ToggleBookmark()
    {
        var tab = _active;
        if (tab is null || !Shadepane.Core.Bookmarks.BookmarkCollection.IsBookmarkable(tab.Url))
            return false;
        var existing = AppServices.Bookmarks.FindByUrl(tab.Url);
        if (existing is not null)
        {
            AppServices.Bookmarks.Remove(existing.Id);
            SetStatus("Удалено из закладок");
        }
        else if (AppServices.Bookmarks.TryAdd(tab.Url, tab.Core?.DocumentTitle, out var error))
        {
            SetStatus("Добавлено в закладки");
        }
        else
        {
            Warn($"Не добавлено в закладки: {error}");
        }
        return true;
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e) => ToggleBookmark();

    private BookmarksWindow? _bookmarksWindow;

    private void ShowBookmarksManager()
    {
        if (_bookmarksWindow is not null)
        {
            _bookmarksWindow.Activate();
            return;
        }
        _bookmarksWindow = new BookmarksWindow(url => { _active?.Navigate(url); Activate(); }, url => _ = OpenNewTabAsync(url)) { Owner = this };
        _bookmarksWindow.Closed += (_, _) => _bookmarksWindow = null;
        _bookmarksWindow.Show();
    }

    // ---------- Навигация ----------

    /// <summary>Домашняя страница: из политики (если задана), иначе из настроек; уже проверена в EffectiveConfig.</summary>
    private static void NavigateHome(BrowserTab tab) => tab.Navigate(AppServices.Config.HomeUrl);

    /// <summary>Адрес — открыть; слова — искать в выбранном поисковике; опасные схемы — отказ.</summary>
    private void NavigateFromInput(string input)
    {
        if (_active is null)
            return;
        var result = AddressInput.Resolve(input, AppServices.SearchUrl);
        if (result.IsSuccess)
        {
            _active.Navigate(result.Uri!.AbsoluteUri);
            _active.View.Focus();
        }
        else
        {
            Warn($"Не открыто: {result.Error}");
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
            Warn("DEBUG: флаг защиты снят — контрольный цикл должен сработать в течение ~200 мс");
            return true;
        }
        return false;
    }
#endif

    /// <summary>
    /// Горячие клавиши браузера. Срабатывают и когда фокус на странице: WPF-контрол WebView2 пересылает
    /// клавиши-ускорители в PreviewKeyDown окна (туннельное событие идёт от окна к контролу).
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
#if DEBUG
        if (HandleDebugKeys(e))
        {
            e.Handled = true;
            return;
        }
#endif
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt+клавиша приходит как Key.System
        var modifiers = Keyboard.Modifiers;
        var shortcut = BrowserShortcuts.Resolve(key.ToString(),
            ctrl: modifiers.HasFlag(ModifierKeys.Control),
            shift: modifiers.HasFlag(ModifierKeys.Shift),
            alt: modifiers.HasFlag(ModifierKeys.Alt));
        if (shortcut is not null && TryExecute(shortcut))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <returns>false — команда сейчас неприменима, клавиша уходит дальше (в поле ввода или на страницу).</returns>
    private bool TryExecute(BrowserShortcut shortcut)
    {
        var tab = _active;
        var core = tab?.Core;
        switch (shortcut.Command)
        {
            case BrowserCommand.NewTab:
                if (!NewTabButton.IsEnabled)
                    return false;
                _ = OpenNewTabAsync();
                return true;
            case BrowserCommand.CloseTab:
                if (tab is null)
                    return false;
                CloseTab(tab);
                return true;
            case BrowserCommand.NextTab:
                SelectRelativeTab(+1);
                return true;
            case BrowserCommand.PreviousTab:
                SelectRelativeTab(-1);
                return true;
            case BrowserCommand.SelectTab:
                SelectTabByNumber(shortcut.TabNumber);
                return true;
            case BrowserCommand.FocusAddressBar:
                if (!AddressBox.IsEnabled)
                    return false;
                FocusAddressBox();
                return true;
            case BrowserCommand.Reload:
                core?.Reload();
                return core is not null;
            case BrowserCommand.Back:
                if (core?.CanGoBack != true)
                    return false;
                core.GoBack();
                return true;
            case BrowserCommand.Forward:
                if (core?.CanGoForward != true)
                    return false;
                core.GoForward();
                return true;
            case BrowserCommand.Home:
                if (tab is null)
                    return false;
                NavigateHome(tab);
                return true;
            case BrowserCommand.Find:
                OpenFind();
                return core is not null;
            case BrowserCommand.FindNext:
                FindStep(forward: true);
                return core is not null;
            case BrowserCommand.FindPrevious:
                FindStep(forward: false);
                return core is not null;
            case BrowserCommand.ZoomIn:
                return SetZoom(z => ZoomLevels.ZoomIn(z));
            case BrowserCommand.ZoomOut:
                return SetZoom(z => ZoomLevels.ZoomOut(z));
            case BrowserCommand.ZoomReset:
                return SetZoom(_ => ZoomLevels.Default);
            case BrowserCommand.History:
                ShowHistory();
                return true;
            case BrowserCommand.ToggleBookmark:
                return ToggleBookmark();
            case BrowserCommand.BookmarksManager:
                ShowBookmarksManager();
                return true;
            case BrowserCommand.ToggleBookmarksBar:
                return ToggleBookmarksBar();
            case BrowserCommand.Downloads:
                ShowDownloads();
                return true;
            case BrowserCommand.Stop:
                // Esc в адресной строке обрабатывает сама строка (отмена ввода).
                if (AddressBox.IsKeyboardFocusWithin)
                    return false;
                if (FindBar.Visibility == Visibility.Visible)
                {
                    CloseFind(focusPage: true);
                    return true;
                }
                if (tab?.IsLoading == true)
                {
                    core?.Stop();
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private bool SetZoom(Func<double, double> next)
    {
        if (_active is null)
            return false;
        var view = _active.View;
        view.ZoomFactor = next(view.ZoomFactor);
        SetStatus($"Масштаб: {ZoomLevels.Format(view.ZoomFactor)}");
        return true;
    }

    // ---------- Поиск на странице ----------

    private void OpenFind()
    {
        if (_active?.Core is null)
            return;
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0)
            _ = StartFindAsync();
    }

    private void CloseFind(bool focusPage)
    {
        if (FindBar.Visibility != Visibility.Visible)
            return;
        try { _active?.Core?.Find.Stop(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        FindBar.Visibility = Visibility.Collapsed;
        FindCount.Text = "";
        if (focusPage)
            _active?.View.Focus();
    }

    private async Task StartFindAsync()
    {
        var tab = _active;
        if (tab?.Core is null || _environment is null)
            return;
        var term = FindBox.Text;
        try
        {
            if (term.Length == 0)
            {
                tab.Core.Find.Stop();
                FindCount.Text = "";
                return;
            }
            SubscribeFindEvents(tab);
            var options = _environment.CreateFindOptions();
            options.FindTerm = term;
            options.SuppressDefaultFindDialog = true;
            options.ShouldHighlightAllMatches = true;
            await tab.Core.Find.StartAsync(options);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Warn($"Поиск на странице недоступен: {Diagnostics.Describe(ex)}");
        }
    }

    private void SubscribeFindEvents(BrowserTab tab)
    {
        if (tab.Core is null || !_findSubscribed.Add(tab))
            return;
        var find = tab.Core.Find;
        find.MatchCountChanged += (_, _) => { if (tab == _active) UpdateFindCount(tab); };
        find.ActiveMatchIndexChanged += (_, _) => { if (tab == _active) UpdateFindCount(tab); };
    }

    private void UpdateFindCount(BrowserTab tab)
    {
        if (FindBar.Visibility != Visibility.Visible || tab.Core is null)
            return;
        var find = tab.Core.Find;
        FindCount.Text = find.MatchCount == 0 ? "нет совпадений" : $"{Math.Max(find.ActiveMatchIndex, 0)} из {find.MatchCount}";
    }

    private void FindStep(bool forward)
    {
        var core = _active?.Core;
        if (core is null)
            return;
        if (FindBar.Visibility != Visibility.Visible || FindBox.Text.Length == 0)
        {
            OpenFind();
            return;
        }
        try
        {
            if (forward)
                core.Find.FindNext();
            else
                core.Find.FindPrevious();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _ = StartFindAsync(); // сессии поиска ещё нет — начинаем
        }
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => _ = StartFindAsync();

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        FindStep(forward: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindStep(forward: true);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindStep(forward: false);
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind(focusPage: true);

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
                AddressBox.Text = ActiveAddress;
                _active?.View.Focus();
                break;
        }
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateAddressView();
        Dispatcher.BeginInvoke(AddressBox.SelectAll); // после клика мышью выделение иначе сбрасывается
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Ушли из строки без Enter — недописанный ввод отбрасываем, как Esc. NewFocus == null — окно просто
        // потеряло активность (Alt+Tab): тогда ввод сохраняем, фокус вернётся в строку.
        if (e.NewFocus is not null)
            AddressBox.Text = ActiveAddress;
        UpdateAddressView();
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => _ = OpenNewTabAsync();
    private void Back_Click(object sender, RoutedEventArgs e) => _active?.Core?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _active?.Core?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        // Во время загрузки кнопка «Обновить» становится «Остановить», как в Edge и Chrome.
        if (_active?.IsLoading == true)
            _active.Core?.Stop();
        else
            _active?.Core?.Reload();
    }
    private void Home_Click(object sender, RoutedEventArgs e) { if (_active is not null) NavigateHome(_active); }

    private HistoryWindow? _historyWindow;

    private void ShowHistory()
    {
        if (_historyWindow is not null)
        {
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow(url => { _active?.Navigate(url); Activate(); }, url => _ = OpenNewTabAsync(url)) { Owner = this };
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    private void History_Click(object sender, RoutedEventArgs e) => ShowHistory();

    private SettingsWindow? _settingsWindow;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate(); // одно окно настроек
            return;
        }
        _settingsWindow = new SettingsWindow { Owner = this };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        if (_settingsWindow.ShowDialog() == true)
            SetStatus($"Настройки сохранены. Поиск: {AppServices.Config.SearchEngine.Name}.");
    }

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
                Style = (Style)FindResource("ToolbarButton"),
                Margin = new Thickness(2, 0, 0, 0),
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
            Warn($"У «{extension.Name}» нет popup. Клик по иконке (action.onClicked) WebView2 не поддерживает.");
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

    // ---------- Уведомления и полоска загрузки ----------

    private enum NoticeKind { Info, Warning }

    private static readonly TimeSpan InfoNoticeDuration = TimeSpan.FromSeconds(5);
    private DispatcherTimer? _noticeTimer;
    private NoticeKind? _noticeKind;

    /// <summary>Справка (закладка добавлена, масштаб, загрузка): исчезает сама через несколько секунд.</summary>
    private void SetStatus(string text) => ShowNotice(text, NoticeKind.Info);

    /// <summary>Предупреждение (переход заблокирован, ошибка): висит до «Закрыть», перехода или смены вкладки.</summary>
    private void Warn(string text) => ShowNotice(text, NoticeKind.Warning);

    private void ShowNotice(string text, NoticeKind kind)
    {
        if (_closed)
            return;
        var warning = kind == NoticeKind.Warning;
        NoticeText.Text = text;
        NoticeIcon.Data = (Geometry)FindResource(warning ? "Icon.Warning" : "Icon.Info");
        NoticeBar.SetResourceReference(Border.BackgroundProperty, warning ? "Brush.WarningBack" : "Brush.InfoBack");
        NoticeBar.SetResourceReference(Border.BorderBrushProperty, warning ? "Brush.WarningBorder" : "Brush.InfoBorder");
        NoticeBar.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, warning ? "Brush.WarningText" : "Brush.InfoText");
        NoticeBar.ToolTip = text; // длинный текст обрезается — целиком в подсказке
        NoticeBar.Visibility = Visibility.Visible;
        _noticeKind = kind;

        _noticeTimer ??= CreateNoticeTimer();
        _noticeTimer.Stop();
        if (!warning)
            _noticeTimer.Start();

        // Экранному диктору — новое уведомление (LiveSetting="Polite" в разметке).
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(NoticeText)
            ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private DispatcherTimer CreateNoticeTimer()
    {
        var timer = new DispatcherTimer { Interval = InfoNoticeDuration };
        timer.Tick += (_, _) => { if (_noticeKind == NoticeKind.Info) HideNotice(); };
        return timer;
    }

    private void HideNotice()
    {
        _noticeTimer?.Stop();
        _noticeKind = null;
        NoticeBar.Visibility = Visibility.Collapsed;
    }

    private void NoticeClose_Click(object sender, RoutedEventArgs e) => HideNotice();

    private bool _loadingShown;

    /// <summary>Полоска под панелью и «Обновить» ↔ «Остановить» — по состоянию активной вкладки.</summary>
    private void UpdateLoadingIndicator()
    {
        var loading = _active?.IsLoading == true;
        ReloadGlyph.Data = (Geometry)FindResource(loading ? "Icon.Close" : "Icon.Reload");
        ReloadButton.ToolTip = loading ? "Остановить (Esc)" : "Обновить (F5)";
        if (loading == _loadingShown)
            return;
        _loadingShown = loading;
        if (loading)
            StartLoadingAnimation();
        else
        {
            LoadingBarShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            LoadingBar.Visibility = Visibility.Hidden;
        }
    }

    private void StartLoadingAnimation()
    {
        var width = LoadingTrack.ActualWidth;
        LoadingBar.Visibility = Visibility.Visible;
        if (!SystemParameters.ClientAreaAnimation || width <= 0)
        {
            // Анимации в Windows выключены («Специальные возможности → Визуальные эффекты»): полоска без движения.
            LoadingBarShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            LoadingBar.Width = Math.Max(width, 0);
            LoadingBar.Opacity = 0.5;
            return;
        }
        LoadingBar.Opacity = 1;
        LoadingBar.Width = Math.Max(80, width * 0.25);
        var run = new System.Windows.Media.Animation.DoubleAnimation(-LoadingBar.Width, width, TimeSpan.FromSeconds(1.4))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
        };
        LoadingBarShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, run);
    }

    private void LoadingTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_loadingShown)
            StartLoadingAnimation(); // ширина пробега зависит от ширины окна
    }
}
