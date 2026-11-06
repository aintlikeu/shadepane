using Microsoft.Web.WebView2.Core;

namespace Shadepane.App.Browser;

/// <summary>
/// Одно окружение WebView2 на всё приложение: главное окно и popup расширений должны работать
/// в одном профиле, иначе popup не увидит установленные расширения.
/// </summary>
internal static class BrowserEnvironment
{
    private static Task<CoreWebView2Environment>? _environment;
    /// <summary>Завершился процесс браузера текущего окружения (и все дочерние) — папку данных можно открыть заново.</summary>
    private static TaskCompletionSource? _browserExited;

    public static Task<CoreWebView2Environment> GetAsync() => _environment ??= CreateAsync();

    /// <summary>
    /// После сбоя процесса браузера (BrowserProcessExited) старое окружение непригодно: забываем его и ждём, пока процесс
    /// браузера завершится полностью (иначе новое окружение на той же папке данных может не создаться). Порядок событий
    /// ProcessFailed и BrowserProcessExited не гарантирован, поэтому ждём с тайм-аутом.
    /// </summary>
    public static async Task ResetAfterCrashAsync(TimeSpan timeout)
    {
        var exited = _browserExited;
        _environment = null;
        _browserExited = null;
        if (exited is not null)
            await Task.WhenAny(exited.Task, Task.Delay(timeout));
    }

    private static async Task<CoreWebView2Environment> CreateAsync()
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
        var environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: AppPaths.WebViewData, options: options);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, e) =>
        {
            AppServices.Audit.Write("browser-process-exited", new { kind = e.BrowserProcessExitKind.ToString(), pid = e.BrowserProcessId });
            exited.TrySetResult();
        };
        _browserExited = exited;
        return environment;
    }
}
