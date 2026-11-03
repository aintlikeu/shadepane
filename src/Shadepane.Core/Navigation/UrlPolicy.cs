using System.Text.RegularExpressions;

namespace Shadepane.Core.Navigation;

/// <summary>Единая проверка адресов: ввод пользователя, навигация вкладки, навигация popup расширения.</summary>
public static partial class UrlPolicy
{
    public const int MaxInputLength = 8192;
    public const string AboutBlank = "about:blank";

    [GeneratedRegex("^[a-z0-9]{1,64}$")]
    private static partial Regex ExtensionIdRegex();

    /// <summary>
    /// "example.com" → https://example.com/. Разрешены только http/https без логина в URL.
    /// javascript:, data:, file:, chrome-extension: и т.п. из адресной строки не открываются.
    /// </summary>
    public static bool TryNormalizeUserInput(string? input, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        var s = input?.Trim() ?? "";

        if (s.Length == 0)
        {
            error = "введите адрес";
            return false;
        }
        if (s.Length > MaxInputLength)
        {
            error = "адрес слишком длинный";
            return false;
        }
        if (s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            error = "адрес не должен содержать пробелов и управляющих символов";
            return false;
        }

        // Без "://" считаем вводом хоста. Так "localhost:3000" не путается со схемой "localhost:",
        // а "javascript:alert(1)" превращается в некорректный https-адрес и отклоняется.
        var candidate = s.Contains("://", StringComparison.Ordinal) ? s : "https://" + s;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
        {
            error = "некорректный адрес";
            return false;
        }
        if (!IsWebScheme(parsed))
        {
            error = $"схема '{parsed.Scheme}' запрещена: разрешены только http и https";
            return false;
        }
        if (string.IsNullOrEmpty(parsed.Host))
        {
            error = "в адресе нет хоста";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            // https://corp.example@evil.example — классическая маскировка фишинга.
            error = "адреса с логином в URL (user@host) запрещены";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>Навигация основной вкладки (ссылки, редиректы, window.open).</summary>
    public static bool IsAllowedTopLevelNavigation(string? url)
    {
        if (url == AboutBlank)
            return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && IsWebScheme(u) && string.IsNullOrEmpty(u.UserInfo);
    }

    /// <summary>Popup может ходить только по страницам своего же расширения.</summary>
    public static bool IsAllowedExtensionPopupNavigation(string? url, string extensionId) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == "chrome-extension"
        && string.Equals(u.Host, extensionId, StringComparison.OrdinalIgnoreCase);

    public static Uri BuildExtensionUrl(string extensionId, string relativeUrlPath)
    {
        if (!ExtensionIdRegex().IsMatch(extensionId))
            throw new ArgumentException($"некорректный ID расширения: {extensionId}", nameof(extensionId));
        return new Uri($"chrome-extension://{extensionId}/{relativeUrlPath.TrimStart('/')}");
    }

    public static bool IsWebScheme(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
}
