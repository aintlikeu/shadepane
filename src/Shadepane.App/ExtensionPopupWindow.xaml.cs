using System.ComponentModel;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Shadepane.App.Browser;
using Shadepane.App.Protection;
using Shadepane.Core.Extensions;
using Shadepane.Core.Navigation;

namespace Shadepane.App;

/// <summary>
/// Эмуляция popup расширения: у WebView2 нет панели расширений, поэтому открываем
/// chrome-extension://&lt;id&gt;/&lt;default_popup&gt; в своём защищённом окне в том же профиле.
/// </summary>
public partial class ExtensionPopupWindow : ProtectedWindow, IContentShield
{
    /// <summary>Как в Chrome: popup закрывается при потере фокуса. Для отладки можно выключить.</summary>
    private const bool CloseOnDeactivate = true;

    private readonly ApprovedExtension _extension;
    private readonly Uri _url;
    private readonly Action<Uri> _openInMainView;
    private bool _ready;
    private bool _closing;

    internal ExtensionPopupWindow(ApprovedExtension extension, Uri url, Action<Uri> openInMainView)
    {
        InitializeComponent();
        _extension = extension;
        _url = url;
        _openInMainView = openInMainView;
        Title = extension.Name;
        Loaded += async (_, _) => await InitializeAsync();
        Deactivated += (_, _) =>
        {
            if (CloseOnDeactivate && _ready && !_closing)
                Close();
        };
    }

    public void HideContent()
    {
        WebView.Visibility = Visibility.Collapsed;
        HiddenNotice.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Закрытие из обработчика события WebView2 уничтожило бы WebView2 посреди его же колбэка,
    /// поэтому откладываем до выхода из обработчика.
    /// </summary>
    private void CloseLater() => Dispatcher.BeginInvoke(() =>
    {
        if (!_closing)
            Close();
    });

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true; // повторный Close() во время закрытия бросает исключение
        base.OnClosing(e);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await BrowserEnvironment.GetAsync();
            await WebView.EnsureCoreWebView2Async(environment);
            if (_closing)
                return;

            var core = WebView.CoreWebView2;
            await WebViewHardening.ApplyAsync(core, $"popup:{_extension.Id}", uri =>
            {
                _openInMainView(uri);
                CloseLater();
            });

            WebViewHardening.BlockLeakShortcuts(WebView, $"popup:{_extension.Id}");

            core.NavigationStarting += (_, e) =>
            {
                if (UrlPolicy.IsAllowedExtensionPopupNavigation(e.Uri, _extension.Id))
                    return;
                e.Cancel = true;
                // Ссылка из popup на сайт — открываем во вкладке, как сделал бы Chrome.
                if (UrlPolicy.IsAllowedTopLevelNavigation(e.Uri) && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && UrlPolicy.IsWebScheme(uri))
                {
                    _openInMainView(uri);
                    CloseLater();
                }
                else
                {
                    AppServices.Audit.Write("popup-navigation-blocked", new { _extension.Id, uri = e.Uri });
                }
            };
            core.WindowCloseRequested += (_, _) => CloseLater(); // window.close() из popup

            core.Navigate(_url.AbsoluteUri);
            _ready = true;
        }
        catch (Exception ex) when (!_closing)
        {
            AppServices.Audit.Write("popup-failed", new { _extension.Id, error = Diagnostics.Details(ex) });
            MessageBox.Show(this, $"Не удалось открыть popup расширения:\n\n{Diagnostics.Describe(ex)}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }
}
