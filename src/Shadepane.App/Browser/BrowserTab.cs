using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
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
            Changed?.Invoke(this);
            NavigationCompleted?.Invoke(this, e.IsSuccess);
        };
        core.SourceChanged += (_, _) => Changed?.Invoke(this);
        core.DocumentTitleChanged += (_, _) => Changed?.Invoke(this);
        core.HistoryChanged += (_, _) => Changed?.Invoke(this);
        core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this);
        core.FaviconChanged += (_, _) => FaviconChanged?.Invoke(this);

        Core = core;
    }

    public void Navigate(string url)
    {
        if (Core is null || IsClosed)
            return;
        _requestedUrl = url;
        Core.Navigate(url);
    }

    private string? _requestedUrl;

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
        View.Dispose();
    }

    private static string Truncate(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "…";
}
