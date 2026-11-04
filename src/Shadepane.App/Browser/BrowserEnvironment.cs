using Microsoft.Web.WebView2.Core;

namespace Shadepane.App.Browser;

/// <summary>
/// Одно окружение WebView2 на всё приложение: главное окно и popup расширений должны работать
/// в одном профиле, иначе popup не увидит установленные расширения.
/// </summary>
internal static class BrowserEnvironment
{
    private static Task<CoreWebView2Environment>? _environment;

    public static Task<CoreWebView2Environment> GetAsync() => _environment ??= CreateAsync();

    private static Task<CoreWebView2Environment> CreateAsync()
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            // Без этого AddBrowserExtensionAsync возвращает ERROR_NOT_SUPPORTED.
            AreBrowserExtensionsEnabled = true,
            // Язык меню и диалогов WebView2 и заголовок Accept-Language — как у интерфейса браузера.
            Language = Shadepane.Core.Localization.Loc.IsEnglish ? "en-US" : "ru-RU",
        };
        return CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: AppPaths.WebViewData, options: options);
    }
}
