using System.Net;
using System.Text.RegularExpressions;
using Shadepane.Core.Localization;

namespace Shadepane.Core.Navigation;

/// <param name="Uri">Куда перейти (null при ошибке).</param>
/// <param name="IsSearch">Ввод распознан как поисковый запрос.</param>
public sealed record AddressResolution(Uri? Uri, bool IsSearch, string? Error)
{
    public bool IsSuccess => Uri is not null;
}

/// <summary>Что ввели в адресную строку: адрес или поисковый запрос.</summary>
public static partial class AddressInput
{
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.\-]*):(.*)$", RegexOptions.Singleline)]
    private static partial Regex SchemePrefix();

    [GeneratedRegex(@"^\d+(?:[/?#].*)?$", RegexOptions.Singleline)]
    private static partial Regex PortAndRest();

    /// <param name="searchTemplate">Шаблон поисковика с {query} (уже проверенный).</param>
    public static AddressResolution Resolve(string? input, string searchTemplate)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0)
            return Fail(Loc.T("введите адрес или поисковый запрос", "enter an address or a search query"));
        if (s.Length > UrlPolicy.MaxInputLength)
            return Fail(Loc.T("слишком длинный ввод", "the input is too long"));
        if (s.Any(char.IsControl))
            return Fail(Loc.T("ввод содержит управляющие символы", "the input contains control characters"));

        if (s == UrlPolicy.AboutBlank)
            return new(new Uri(UrlPolicy.AboutBlank), false, null);

        // Явный адрес со схемой — только через общую проверку (http/https, без user@host).
        if (s.Contains("://", StringComparison.Ordinal))
        {
            return UrlPolicy.TryNormalizeUserInput(s, out var explicitUri, out var error)
                ? new(explicitUri, false, null)
                : Fail(error!);
        }

        if (s.Any(char.IsWhiteSpace))
            return Search(s, searchTemplate);

        // "javascript:alert(1)", "data:…", "file:…", "mailto:…" — не адрес и не запрос, а попытка сменить схему.
        var scheme = SchemePrefix().Match(s);
        if (scheme.Success && IsSchemeRatherThanHost(scheme.Groups[1].Value, scheme.Groups[2].Value))
            return Fail(Loc.T($"схема «{scheme.Groups[1].Value.ToLowerInvariant()}:» не поддерживается — разрешены только http и https", $"the \"{scheme.Groups[1].Value.ToLowerInvariant()}:\" scheme is not supported: only http and https are allowed"));

        if (LooksLikeHost(s) && UrlPolicy.TryNormalizeUserInput(s, out var uri, out _))
            return new(uri, false, null);

        return Search(s, searchTemplate);
    }

    private static AddressResolution Search(string query, string template) =>
        new(SearchEngines.BuildSearchUri(template, query), true, null);

    private static AddressResolution Fail(string error) => new(null, false, error);

    /// <summary>"localhost:3000", "example.com:8080" — хост с портом; "javascript:…" — схема.</summary>
    private static bool IsSchemeRatherThanHost(string prefix, string rest) =>
        !prefix.Contains('.') &&
        !prefix.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        !PortAndRest().IsMatch(rest);

    /// <summary>
    /// Похоже на адрес без схемы: localhost, IP или домен с буквенной зоной ("example.com", "пример.рф").
    /// "3.14", "v1.2" и одиночные слова считаются запросом.
    /// </summary>
    private static bool LooksLikeHost(string s)
    {
        var end = s.IndexOfAny(['/', '?', '#']);
        var hostPort = end >= 0 ? s[..end] : s;
        if (hostPort.Contains('@'))
            return false;

        var host = hostPort;
        if (host.StartsWith('['))
            return host.Contains(']') && IPAddress.TryParse(host[1..host.IndexOf(']')], out _);
        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!host[(colon + 1)..].All(char.IsAsciiDigit))
                return false;
            host = host[..colon];
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && host.Count(c => c == '.') == 3)
            return true;

        var labels = host.TrimEnd('.').Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length == 0))
            return false;
        var tld = labels[^1];
        return tld.Length >= 2 && (char.IsLetter(tld[0]) || tld.StartsWith("xn--", StringComparison.OrdinalIgnoreCase));
    }
}
