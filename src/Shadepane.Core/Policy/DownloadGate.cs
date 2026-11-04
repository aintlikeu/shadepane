using Shadepane.Core.Navigation;

namespace Shadepane.Core.Policy;

/// <summary>
/// Можно ли начать загрузку. Режим защиты от утечек — заданы protectedHosts и администратор НЕ разрешил загрузки
/// явно (allowDownloads != true). В нём действует fail closed: блокируем, если вкладка в текущем документе
/// показывала содержимое с защищённого домена (страница, перенаправление, фрейм), если файл с защищённого
/// домена (в т.ч. blob: от него) или если источник файла не определить (data: и т.п.).
/// </summary>
public static class DownloadGate
{
    /// <param name="downloadsAllowed">Итоговое значение (политика или настройка пользователя).</param>
    /// <param name="fromExtensionPopup">Загрузку начало окно расширения.</param>
    /// <param name="tabShowedProtectedContent">В текущем документе вкладки было содержимое с защищённого домена.</param>
    /// <returns>null — можно; иначе причина отказа для журнала.</returns>
    public static string? BlockReason(BrowserPolicy policy, bool downloadsAllowed, bool downloadsLocked, bool fromExtensionPopup,
        bool tabShowedProtectedContent, string? pageUrl, string? fileUrl)
    {
        if (!downloadsAllowed)
            return downloadsLocked ? "policy" : "user-setting";
        if (fromExtensionPopup)
            return "extension-popup";

        var dlpMode = policy.ProtectedHostPatterns.Count > 0 && policy.AllowDownloads != true;
        if (!dlpMode)
            return null;
        if (tabShowedProtectedContent || policy.IsProtectedUrl(pageUrl) || policy.IsProtectedUrl(fileUrl))
            return "protected-host";
        if (!HasDeterminableWebSource(fileUrl))
            return "unknown-source";
        return null;
    }

    /// <summary>http(s) или blob:/filesystem: с http(s)-источником внутри.</summary>
    private static bool HasDeterminableWebSource(string? url)
    {
        if (url is null)
            return false;
        if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("filesystem:", StringComparison.OrdinalIgnoreCase))
            url = url[(url.IndexOf(':') + 1)..];
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && UrlPolicy.IsWebScheme(uri);
    }
}
