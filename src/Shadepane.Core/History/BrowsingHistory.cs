using System.Text.Json;
using Shadepane.Core.Navigation;

namespace Shadepane.Core.History;

public sealed record HistoryEntry(string Url, string Title, DateTimeOffset VisitedAt);

/// <summary>История посещений: только http(s), без повторов подряд, не больше MaxEntries (старые вытесняются).</summary>
public sealed class BrowsingHistory
{
    public const int MaxEntries = 5000;
    public const int MaxTitleLength = 500;

    private readonly List<HistoryEntry> _entries = []; // от старых к новым

    public BrowsingHistory(IEnumerable<HistoryEntry>? entries = null)
    {
        foreach (var e in entries ?? [])
        {
            if (e is not null && IsRecordable(e.Url))
                _entries.Add(e with { Title = Clean(e.Title, e.Url) });
        }
        _entries.Sort((a, b) => a.VisitedAt.CompareTo(b.VisitedAt));
        Trim();
    }

    public int Count => _entries.Count;

    /// <summary>Есть несохранённые изменения.</summary>
    public bool IsDirty { get; private set; }

    public IReadOnlyList<HistoryEntry> Snapshot() => [.. _entries];

    public void MarkSaved() => IsDirty = false;

    /// <returns>true — история изменилась.</returns>
    public bool Add(string? url, string? title, DateTimeOffset visitedAt)
    {
        if (!IsRecordable(url))
            return false;
        var entry = new HistoryEntry(url!, Clean(title, url!), visitedAt);
        // Обновление страницы и повторный переход на тот же адрес не плодят записи.
        if (_entries.Count > 0 && _entries[^1].Url == url)
            _entries[^1] = entry;
        else
            _entries.Add(entry);
        Trim();
        IsDirty = true;
        return true;
    }

    /// <summary>Заголовок страницы часто приходит после завершения навигации — обновляем последнюю запись с этим адресом.</summary>
    public bool UpdateTitle(string? url, string? title)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrWhiteSpace(title))
            return false;
        for (var i = _entries.Count - 1; i >= Math.Max(0, _entries.Count - 20); i--)
        {
            if (_entries[i].Url != url)
                continue;
            var cleaned = Clean(title, url);
            if (_entries[i].Title == cleaned)
                return false;
            _entries[i] = _entries[i] with { Title = cleaned };
            IsDirty = true;
            return true;
        }
        return false;
    }

    /// <summary>Новые сначала; query ищется в заголовке и адресе без учёта регистра.</summary>
    public IReadOnlyList<HistoryEntry> Search(string? query, int limit = 500)
    {
        var q = query?.Trim() ?? "";
        IEnumerable<HistoryEntry> result = Enumerable.Reverse(_entries);
        if (q.Length > 0)
        {
            result = result.Where(e => e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                       e.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        return [.. result.Take(limit)];
    }

    public bool Remove(HistoryEntry entry)
    {
        var removed = _entries.Remove(entry);
        IsDirty |= removed;
        return removed;
    }

    public void Clear()
    {
        if (_entries.Count == 0)
            return;
        _entries.Clear();
        IsDirty = true;
    }

    /// <summary>about:blank, chrome-extension://, data: и адреса с логином не записываем.</summary>
    public static bool IsRecordable(string? url) =>
        !string.IsNullOrEmpty(url) && url != UrlPolicy.AboutBlank && UrlPolicy.IsAllowedTopLevelNavigation(url);

    private void Trim()
    {
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
    }

    private static string Clean(string? title, string url)
    {
        var t = string.IsNullOrWhiteSpace(title) ? url : title.Trim();
        return t.Length > MaxTitleLength ? t[..MaxTitleLength] : t;
    }
}

/// <summary>Файл истории. Повреждённый файл не мешает запуску: история начинается заново (о проблеме сообщает problem).</summary>
public sealed class HistoryStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public BrowsingHistory Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(FilePath))
            return new BrowsingHistory();
        try
        {
            return new BrowsingHistory(JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath), JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = ex.Message;
            return new BrowsingHistory();
        }
    }

    public void Save(BrowsingHistory history)
    {
        IO.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(history.Snapshot(), JsonOptions));
        history.MarkSaved();
    }
}
