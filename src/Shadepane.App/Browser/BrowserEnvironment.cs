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
#if SCREENSHOTS
        // Сборка для скриншотов: --treat-as-secure=http://wiki.example:8000 делает тестовый http-адрес
        // «защищённым контекстом» — иначе демо-страница не может запросить демонстрацию экрана (getDisplayMedia).
        var secure = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--treat-as-secure=", StringComparison.OrdinalIgnoreCase));
        if (secure is not null && Uri.TryCreate(secure["--treat-as-secure=".Length..], UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttp)
            options.AdditionalBrowserArguments = $"--unsafely-treat-insecure-origin-as-secure={origin.GetLeftPart(UriPartial.Authority)}";
#endif
        return CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: AppPaths.WebViewData, options: options);
    }
}
