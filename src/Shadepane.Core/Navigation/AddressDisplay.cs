namespace Shadepane.Core.Navigation;

/// <summary>Адрес для показа в адресной строке без фокуса: Prefix и Rest — приглушённо, Host — выделенно.</summary>
public sealed record AddressParts(string Prefix, string Host, string Rest)
{
    public static AddressParts Empty { get; } = new("", "", "");

    public bool IsEmpty => Prefix.Length == 0 && Host.Length == 0 && Rest.Length == 0;
}

/// <summary>
/// Как показать адрес, пока адресная строка не в фокусе (как в Edge и Chrome): домен выделен, чтобы подделку
/// вида «bank.example.evil.test» было видно сразу; «https://» скрыт, «http://» остаётся — это незащищённое соединение.
/// Части берутся из исходной строки, без перекодировки: в фокусе пользователь видит тот же текст целиком.
/// </summary>
public static class AddressDisplay
{
    public static AddressParts Split(string? url)
    {
        if (string.IsNullOrEmpty(url) || url == UrlPolicy.AboutBlank)
            return AddressParts.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !UrlPolicy.IsWebScheme(uri) || uri.Host.Length == 0)
            return new AddressParts("", "", url); // chrome-extension://, data: и прочее — без выделения
        if (uri.UserInfo.Length > 0)
            return new AddressParts("", "", url); // «user@host» — не выделяем то, что похоже на домен, но им не является

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return new AddressParts("", "", url);
        var hostStart = schemeEnd + 3;
        var hostEnd = url.IndexOfAny(['/', '?', '#'], hostStart);
        if (hostEnd < 0)
            hostEnd = url.Length;

        var prefix = uri.Scheme == Uri.UriSchemeHttps ? "" : url[..hostStart];
        var rest = url[hostEnd..];
        if (rest == "/")
            rest = ""; // корень сайта: «example.com», а не «example.com/»
        return new AddressParts(prefix, url[hostStart..hostEnd], rest);
    }
}
