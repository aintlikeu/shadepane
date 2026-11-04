using System.Security.Cryptography;
using System.Text;
using Shadepane.Core.Navigation;

namespace Shadepane.Core.Bookmarks;

/// <summary>
/// Имя файла значка сайта в кэше (папка favicons): одно на сайт — хост и нестандартный порт, без схемы и пути,
/// чтобы у http:// и https:// одного сайта и у разных страниц сайта был один значок. Имя — хеш, а не сам хост:
/// в имени файла не должно быть символов, недопустимых в Windows, и по списку файлов не читаются адреса.
/// </summary>
public static class FaviconKey
{
    /// <returns>null — адрес не http(s) или без хоста: значка у него нет.</returns>
    public static string? FileNameFor(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !UrlPolicy.IsWebScheme(uri) || uri.IdnHost.Length == 0)
            return null;
        var site = uri.IsDefaultPort ? uri.IdnHost.ToLowerInvariant() : $"{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(site));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + ".png";
    }

    /// <summary>Один и тот же сайт для значка (см. FileNameFor).</summary>
    public static bool SameSite(string? a, string? b) =>
        FileNameFor(a) is { } x && x == FileNameFor(b);
}
