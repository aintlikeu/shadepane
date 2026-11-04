using System.Globalization;
using System.Net;
using Shadepane.Core.Localization;

namespace Shadepane.Core.Hosts;

public enum HostPatternKind
{
    /// <summary>Любой хост ("*").</summary>
    Any,
    /// <summary>Ровно этот хост ("crm.corp.example").</summary>
    Exact,
    /// <summary>Домен и все поддомены ("*.corp.example").</summary>
    DomainAndSubdomains,
}

/// <summary>
/// Шаблон хоста в нормализованном виде (нижний регистр, punycode, без завершающей точки).
/// Используется и для защищённых доменов из политики, и для хостов из match patterns расширений.
/// </summary>
public sealed record HostPattern(HostPatternKind Kind, string Domain)
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = false };
    private static readonly char[] ForbiddenHostChars = ['/', '\\', '@', ':', ' ', '?', '#', '%'];

    public static readonly HostPattern AnyHost = new(HostPatternKind.Any, "");

    public static bool TryParse(string? raw, out HostPattern? pattern, out string? error)
    {
        pattern = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = Loc.T("пустой хост", "empty host");
            return false;
        }

        var s = raw.Trim();
        if (s == "*")
        {
            pattern = AnyHost;
            return true;
        }

        var kind = HostPatternKind.Exact;
        if (s.StartsWith("*.", StringComparison.Ordinal))
        {
            kind = HostPatternKind.DomainAndSubdomains;
            s = s[2..];
        }

        if (s.Contains('*'))
        {
            error = Loc.T($"'*' допускается только как префикс '*.': {raw}", $"'*' is allowed only as the '*.' prefix: {raw}");
            return false;
        }

        if (!TryNormalizeHost(s, out var normalized, out error))
            return false;

        pattern = new HostPattern(kind, normalized!);
        return true;
    }

    public static HostPattern Parse(string raw) =>
        TryParse(raw, out var p, out var error) ? p! : throw new FormatException(error);

    /// <summary>
    /// Приводит хост к каноническому виду, чтобы "CRM.Corp.Example.", "crm.corp.example" и
    /// IDN-варианты сравнивались как один и тот же хост.
    /// </summary>
    public static bool TryNormalizeHost(string host, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        var h = host.Trim().TrimEnd('.');
        if (h.Length == 0)
        {
            error = Loc.T("пустой хост", "empty host");
            return false;
        }

        if (h.StartsWith('[') && h.EndsWith(']'))
        {
            if (!IPAddress.TryParse(h[1..^1], out var ip6))
            {
                error = Loc.T($"некорректный IPv6-адрес: {host}", $"invalid IPv6 address: {host}");
                return false;
            }
            normalized = "[" + ip6 + "]";
            return true;
        }

        if (h.IndexOfAny(ForbiddenHostChars) >= 0)
        {
            error = Loc.T($"недопустимые символы в хосте: {host}", $"invalid characters in host: {host}");
            return false;
        }

        try
        {
            normalized = Idn.GetAscii(h.ToLowerInvariant()).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            error = Loc.T($"некорректное имя хоста: {host}", $"invalid host name: {host}");
            return false;
        }
        return true;
    }

    // Частые публичные суффиксы второго уровня вида "co.uk", "com.au", "org.ru". Полный Public Suffix List
    // (в т.ч. частные суффиксы вроде github.io) не встроен — это эвристика, см. IsPublicSuffixWildcard.
    private static readonly HashSet<string> SecondLevelSuffixLabels = new(StringComparer.Ordinal)
    {
        "co", "com", "net", "org", "gov", "edu", "ac", "gob", "mil", "or", "ne", "go", "nic", "ltd", "plc", "sch",
    };

    /// <summary>
    /// "*.com", "*.рф", "*.co.uk" — по сути доступ ко всем сайтам зоны, т.е. широкий доступ, а не к одному домену.
    /// </summary>
    public bool IsPublicSuffixWildcard
    {
        get
        {
            if (Kind != HostPatternKind.DomainAndSubdomains)
                return false;
            var labels = Domain.Split('.');
            return labels.Length == 1
                || (labels.Length == 2 && labels[1].Length == 2 && SecondLevelSuffixLabels.Contains(labels[0]));
        }
    }

    /// <summary>
    /// Есть ли хотя бы один хост, который подходит под оба шаблона.
    /// Отношение симметрично: a.Overlaps(b) == b.Overlaps(a).
    /// </summary>
    public bool Overlaps(HostPattern other)
    {
        if (Kind == HostPatternKind.Any || other.Kind == HostPatternKind.Any)
            return true;

        return (Kind, other.Kind) switch
        {
            (HostPatternKind.Exact, HostPatternKind.Exact) => Domain == other.Domain,
            (HostPatternKind.Exact, HostPatternKind.DomainAndSubdomains) => IsSameOrSubdomain(Domain, other.Domain),
            (HostPatternKind.DomainAndSubdomains, HostPatternKind.Exact) => IsSameOrSubdomain(other.Domain, Domain),
            // "*.a" и "*.b" пересекаются, если один домен вложен в другой ("*.com" и "*.corp.com").
            _ => IsSameOrSubdomain(Domain, other.Domain) || IsSameOrSubdomain(other.Domain, Domain),
        };
    }

    private static bool IsSameOrSubdomain(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    public override string ToString() => Kind switch
    {
        HostPatternKind.Any => "*",
        HostPatternKind.DomainAndSubdomains => "*." + Domain,
        _ => Domain,
    };
}
