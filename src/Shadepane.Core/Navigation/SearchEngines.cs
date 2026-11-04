using Shadepane.Core.Localization;

namespace Shadepane.Core.Navigation;

/// <param name="SearchUrl">Шаблон адреса поиска: {query} заменяется закодированным запросом.</param>
/// <param name="HomeUrl">Главная страница поисковика — домашняя страница по умолчанию.</param>
public sealed record SearchEngine(string Id, string Name, string SearchUrl, string HomeUrl);

public static class SearchEngines
{
    public const string QueryPlaceholder = "{query}";
    public const string CustomId = "custom";
    public const int MaxTemplateLength = 2048;

    // Адреса проверены запросом 03.11.2026 (Brave ответил 429 на автоматический запрос — защита от ботов,
    // путь существует). Startpage: шаблон из их OpenSearch-описания (sp/search?query=).
    public static SearchEngine Startpage { get; } = new("startpage", "Startpage", "https://www.startpage.com/sp/search?query={query}", "https://www.startpage.com/");
    public static SearchEngine DuckDuckGo { get; } = new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={query}", "https://duckduckgo.com/");

    public static IReadOnlyList<SearchEngine> All { get; } =
    [
        Startpage,
        DuckDuckGo,
        new("brave", "Brave Search", "https://search.brave.com/search?q={query}", "https://search.brave.com/"),
        new("google", "Google", "https://www.google.com/search?q={query}", "https://www.google.com/"),
        new("bing", "Bing", "https://www.bing.com/search?q={query}", "https://www.bing.com/"),
    ];

    /// <summary>По умолчанию — приватный поисковик: Startpage (выдача Google без слежки).</summary>
    public static SearchEngine Default => Startpage;

    public static SearchEngine? Find(string? id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Свой шаблон: только https, с {query}; после подстановки — допустимый адрес верхнего уровня.</summary>
    public static bool TryValidateTemplate(string? template, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(template))
        {
            error = Loc.T("шаблон поиска пуст", "the search template is empty");
            return false;
        }
        if (template.Length > MaxTemplateLength)
        {
            error = Loc.T("шаблон поиска слишком длинный", "the search template is too long");
            return false;
        }
        if (!template.Contains(QueryPlaceholder, StringComparison.Ordinal))
        {
            error = Loc.T($"в шаблоне нет {QueryPlaceholder}", $"the template has no {QueryPlaceholder}");
            return false;
        }
        if (!template.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = Loc.T("шаблон поиска должен начинаться с https:// (запросы не должны уходить открытым текстом)", "the search template must start with https:// (queries must not be sent in clear text)");
            return false;
        }

        if (!TrySample(template, "test", out var first) || !TrySample(template, "other", out var second))
        {
            error = Loc.T("шаблон поиска — некорректный адрес", "the search template is not a valid address");
            return false;
        }
        // Запрос не должен попадать в имя хоста: иначе текст запроса решал бы, на какой сайт уйти.
        if (!string.Equals(first!.IdnHost, second!.IdnHost, StringComparison.OrdinalIgnoreCase) || first.Port != second.Port)
        {
            error = Loc.T($"{QueryPlaceholder} не может быть частью имени сайта — только пути или параметров", $"{QueryPlaceholder} cannot be part of the site name, only of the path or parameters");
            return false;
        }
        return true;
    }

    private static bool TrySample(string template, string query, out Uri? uri)
    {
        var sample = template.Replace(QueryPlaceholder, query, StringComparison.Ordinal);
        return Uri.TryCreate(sample, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
               !string.IsNullOrEmpty(uri.Host) && UrlPolicy.IsAllowedTopLevelNavigation(uri.AbsoluteUri);
    }

    public static Uri BuildSearchUri(string template, string query) =>
        new(template.Replace(QueryPlaceholder, Uri.EscapeDataString(query), StringComparison.Ordinal));
}
