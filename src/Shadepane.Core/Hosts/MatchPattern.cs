namespace Shadepane.Core.Hosts;

/// <summary>
/// Разбор Chrome match pattern ("https://*.example.com/*", "&lt;all_urls&gt;") в объёме,
/// нужном для проверки политики: применяется ли шаблон к вебу и к каким хостам.
/// Путь шаблона игнорируется намеренно: доступ к любому пути домена — уже доступ к домену.
/// </summary>
public sealed record MatchPattern(string Raw, bool AppliesToWeb, HostPattern? Host)
{
    private static readonly HashSet<string> WebSchemes = new(StringComparer.Ordinal) { "http", "https", "ws", "wss", "*" };

    // Схемы, которые не дают доступа к http(s)-страницам. Их пропускаем, а не отклоняем,
    // чтобы не ломать легитимные расширения (например, "file:///*").
    private static readonly HashSet<string> NonWebSchemes = new(StringComparer.Ordinal)
    {
        "file", "ftp", "chrome", "chrome-extension", "edge", "urn", "data", "filesystem",
    };

    public const string AllUrls = "<all_urls>";

    public bool IsBroad => AppliesToWeb && Host is { Kind: HostPatternKind.Any };

    public static bool TryParse(string? raw, out MatchPattern? pattern, out string? error)
    {
        pattern = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "пустой шаблон";
            return false;
        }

        if (raw == AllUrls)
        {
            pattern = new MatchPattern(raw, true, HostPattern.AnyHost);
            return true;
        }

        var sep = raw.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0)
        {
            error = "это не match pattern (ожидается scheme://host/path)";
            return false;
        }

        var scheme = raw[..sep].ToLowerInvariant();
        if (NonWebSchemes.Contains(scheme))
        {
            pattern = new MatchPattern(raw, false, null);
            return true;
        }
        if (!WebSchemes.Contains(scheme))
        {
            error = $"неизвестная схема '{scheme}'";
            return false;
        }

        var rest = raw[(sep + 3)..];
        var slash = rest.IndexOf('/');
        var hostPort = slash >= 0 ? rest[..slash] : rest;

        if (!TryStripPort(hostPort, out var host, out error))
            return false;
        if (!HostPattern.TryParse(host, out var hostPattern, out error))
            return false;

        pattern = new MatchPattern(raw, true, hostPattern);
        return true;
    }

    private static bool TryStripPort(string hostPort, out string host, out string? error)
    {
        error = null;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close < 0)
            {
                host = "";
                error = "незакрытая скобка IPv6";
                return false;
            }
            host = hostPort[..(close + 1)];
            return true;
        }

        var colon = hostPort.LastIndexOf(':');
        host = colon >= 0 ? hostPort[..colon] : hostPort;
        return true;
    }
}
