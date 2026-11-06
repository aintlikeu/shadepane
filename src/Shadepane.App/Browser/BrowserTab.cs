using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Shadepane.Core.Browsing;
using Shadepane.Core.Navigation;
using Shadepane.Core.Localization;

namespace Shadepane.App.Browser;

/// <summary>
/// Одна вкладка: свой WebView2 в общем окружении и профиле. Все вкладки живут внутри главного окна,
/// поэтому защиту от захвата экрана получают от него (флаг стоит на окне верхнего уровня).
/// </summary>
internal sealed class BrowserTab
{
    private static int _nextId;

    public BrowserTab()
    {
        Id = Interlocked.Increment(ref _nextId);
        // Hidden, а не Collapsed: у скрытого HwndHost сохраняется окно, и фоновая вкладка
        // инициализируется так же, как видимая.
        View = new WebView2 { Visibility = Visibility.Hidden };
    }

    public int Id { get; }
    public WebView2 View { get; }
    public CoreWebView2? Core { get; private set; }
    public bool IsClosed { get; private set; }
    public bool IsLoading { get; private set; }

    public string Url => Core?.Source ?? "";

    public string Title
    {
        get
        {
            var title = Core?.DocumentTitle;
            if (!string.IsNullOrWhiteSpace(title))
                return title;
            var url = Url;
            return string.IsNullOrEmpty(url) || url == UrlPolicy.AboutBlank ? Loc.T("Новая вкладка", "New tab") : url;
        }
    }

    /// <summary>Изменились адрес, заголовок, история или состояние загрузки.</summary>
    public event Action<BrowserTab>? Changed;

    /// <summary>Страница вызвала window.close().</summary>
    public event Action<BrowserTab>? CloseRequested;

    /// <summary>Сообщение для строки состояния (например, переход заблокирован политикой).</summary>
    public event Action<BrowserTab, string>? Status;

    /// <summary>У страницы появился или сменился значок (для значков на панели закладок).</summary>
    public event Action<BrowserTab>? FaviconChanged;

    /// <summary>Завершилась навигация верхнего уровня; bool — успешно ли (для истории).</summary>
    public event Action<BrowserTab, bool>? NavigationCompleted;

    public async Task InitializeAsync(CoreWebView2Environment environment, WebViewHardening.NewWindowHandler onNewWindow)
    {
        await View.EnsureCoreWebView2Async(environment);
        if (IsClosed)
            return;

        var core = View.CoreWebView2;
        await WebViewHardening.ApplyAsync(core, $"tab:{Id}", onNewWindow);
        if (IsClosed)
            return;
        WebViewHardening.BlockLeakShortcuts(View, $"tab:{Id}");

        core.NavigationStarting += (_, e) =>
        {
            if (UrlPolicy.IsAllowedTopLevelNavigation(e.Uri))
            {
                IsLoading = true;
                Changed?.Invoke(this);
                return;
            }
            e.Cancel = true;
            AppServices.Audit.Write("navigation-blocked", new { tab = Id, uri = e.Uri });
            Status?.Invoke(this, Loc.T($"Переход заблокирован политикой: {Truncate(e.Uri, 120)}", $"Blocked by policy: {Truncate(e.Uri, 120)}"));
        };
        core.NavigationCompleted += (_, e) =>
        {
            IsLoading = false;
            _expectNavigationZoom = false;
            Changed?.Invoke(this);
            NavigationCompleted?.Invoke(this, e.IsSuccess);
        };
        core.SourceChanged += (_, _) =>
        {
            _lastSource = core.Source;
            Changed?.Invoke(this);
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                return;
            // Движок упал: объект CoreWebView2 закрыт, любое обращение к нему бросает исключение. Без Core вкладка
            // ведёт себя как ещё не готовая (все обращения уже проверяют Core на null), пока MainWindow её не пересоздаст.
            Core = null;
            IsLoading = false;
            Changed?.Invoke(this);
        };
        core.DocumentTitleChanged += (_, _) => Changed?.Invoke(this);
        core.HistoryChanged += (_, _) => Changed?.Invoke(this);
        core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this);
        core.FaviconChanged += (_, _) => FaviconChanged?.Invoke(this);

        // Масштаб сайта (SiteZoom). ContentLoading — новый документ: Source уже новый, страница ещё не нарисована.
        core.ContentLoading += (_, _) => OnDocumentCommitted(core.Source);
        View.ZoomFactorChanged += (_, _) => OnZoomFactorChanged();
        AppServices.SiteZoom.Changed += OnSiteZoomChanged;

        Core = core;
    }

    // ---------- Масштаб ----------

    /// <summary>Адрес документа, к сайту которого относится текущий масштаб (обновляется на ContentLoading).</summary>
    private string? _zoomUrl;
    /// <summary>Мы сами меняем ZoomFactor: это не жест пользователя, запоминать нечего.</summary>
    private bool _settingZoom;
    /// <summary>
    /// Новый документ загружается: WebView2 сбрасывает масштаб при переходе и сообщает об этом ZoomFactorChanged сразу
    /// после ContentLoading. Это событие — не выбор пользователя: возвращаем масштаб сайта вместо того, чтобы запомнить.
    /// </summary>
    private bool _expectNavigationZoom;

    /// <summary>Масштаб, выбранный в браузере (Ctrl+= / Ctrl+- / Ctrl+0, меню): страница и сайт целиком.</summary>
    public void SetZoom(double factor)
    {
        if (IsClosed)
            return;
        ApplyZoom(factor);
        _expectNavigationZoom = false;
        AppServices.SiteZoom.Set(_zoomUrl, View.ZoomFactor);
    }

    private void OnDocumentCommitted(string? url)
    {
        _zoomUrl = url;
        ApplyZoom(AppServices.SiteZoom.Get(url));
        _expectNavigationZoom = true;
    }

    private void OnZoomFactorChanged()
    {
        if (_settingZoom || IsClosed)
            return;
        if (_expectNavigationZoom)
        {
            _expectNavigationZoom = false;
            ApplyZoom(AppServices.SiteZoom.Get(_zoomUrl));
            return;
        }
        // Пользователь изменил масштаб в самой странице (Ctrl+колесо): запоминаем для сайта.
        AppServices.SiteZoom.Set(_zoomUrl, View.ZoomFactor);
    }

    /// <summary>Масштаб сайта изменили в другой вкладке (или в этой): та же страница сайта подстраивается.</summary>
    private void OnSiteZoomChanged(string host, double factor)
    {
        if (!IsClosed && SiteZoom.KeyOf(_zoomUrl) == host)
            ApplyZoom(factor);
    }

    private void ApplyZoom(double factor)
    {
        if (SiteZoom.SameZoom(View.ZoomFactor, factor))
            return;
        _settingZoom = true;
        try
        {
            View.ZoomFactor = factor;
        }
        finally
        {
            _settingZoom = false;
        }
    }

    public void Navigate(string url)
    {
        if (Core is null || IsClosed)
            return;
        _requestedUrl = url;
        Core.Navigate(url);
    }

    private string? _requestedUrl;
    /// <summary>Последний адрес документа, прочитанный, пока движок был жив, — для восстановления после сбоя.</summary>
    private string? _lastSource;

    /// <summary>Адрес, который открыть заново после сбоя движка (Core уже недоступен).</summary>
    public string? RecoveryUrl =>
        Shadepane.Core.Browsing.SessionState.IsRestorable(_lastSource) ? _lastSource : _requestedUrl;

    /// <summary>
    /// Адрес для сохранения сеанса: открытый документ, а пока он ещё не загрузился (вкладку только что восстановили
    /// или открыли) — запрошенный адрес, чтобы быстрый перезапуск не терял вкладки.
    /// </summary>
    public string? SessionUrl =>
        Shadepane.Core.Browsing.SessionState.IsRestorable(Core?.Source) ? Core!.Source : _requestedUrl;

    public void Close()
    {
        if (IsClosed)
            return;
        IsClosed = true;
        AppServices.SiteZoom.Changed -= OnSiteZoomChanged;
        View.Dispose();
    }

    private static string Truncate(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "…";
}
