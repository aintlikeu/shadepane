using System.Text.Json;
using Shadepane.Core.Navigation;
using Shadepane.Core.Localization;

namespace Shadepane.Core.Bookmarks;

public sealed record Bookmark(string Id, string Title, string Url, DateTimeOffset AddedAt);

/// <summary>
/// Закладки в пользовательском порядке (в нём же их показывает панель закладок).
/// Только http(s)-адреса без логина в URL; один адрес — одна закладка.
/// </summary>
public sealed class BookmarkCollection
{
    public const int MaxBookmarks = 10_000;
    public const int MaxTitleLength = 300;

    private readonly List<Bookmark> _items = [];

    public BookmarkCollection(IEnumerable<Bookmark>? items = null)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in items ?? [])
        {
            // Подложенные в файл записи проходят те же правила, что и добавленные из браузера.
            if (b is null || string.IsNullOrEmpty(b.Id) || !IsBookmarkable(b.Url) || !ids.Add(b.Id) || !urls.Add(b.Url))
                continue;
            if (_items.Count >= MaxBookmarks)
                break;
            _items.Add(b with { Title = CleanTitle(b.Title, b.Url) });
        }
    }

    public IReadOnlyList<Bookmark> Items => _items;

    public Bookmark? FindByUrl(string? url) => _items.FirstOrDefault(b => b.Url == url);

    public bool TryAdd(string? url, string? title, DateTimeOffset now, out Bookmark? bookmark, out string? error)
    {
        bookmark = null;
        error = null;
        if (!IsBookmarkable(url))
        {
            error = Loc.T("в закладки можно добавить только страницы http:// и https://", "only http:// and https:// pages can be bookmarked");
            return false;
        }
        var existing = FindByUrl(url);
        if (existing is not null)
        {
            bookmark = existing; // повторное добавление — не дубль
            return true;
        }
        if (_items.Count >= MaxBookmarks)
        {
            error = Loc.T($"закладок больше {MaxBookmarks}", $"more than {MaxBookmarks} bookmarks");
            return false;
        }
        bookmark = new Bookmark(Guid.NewGuid().ToString("N"), CleanTitle(title, url!), url!, now);
        _items.Add(bookmark);
        return true;
    }

    public bool Remove(string id) => _items.RemoveAll(b => b.Id == id) > 0;

    /// <summary>Правка из менеджера: адрес вводит человек, поэтому нормализуем его так же, как в адресной строке.</summary>
    public bool TryUpdate(string id, string? title, string? url, out string? error)
    {
        error = null;
        var index = _items.FindIndex(b => b.Id == id);
        if (index < 0)
        {
            error = Loc.T("закладка не найдена", "bookmark not found");
            return false;
        }
        if (!UrlPolicy.TryNormalizeUserInput(url, out var uri, out var urlError))
        {
            error = urlError;
            return false;
        }
        var normalized = uri!.AbsoluteUri;
        if (_items.Any(b => b.Id != id && b.Url == normalized))
        {
            error = Loc.T("закладка с таким адресом уже есть", "a bookmark with this address already exists");
            return false;
        }
        _items[index] = _items[index] with { Url = normalized, Title = CleanTitle(title, normalized) };
        return true;
    }

    /// <summary>Переставить закладку на позицию newIndex (с ограничением по краям).</summary>
    /// <summary>
    /// Перетаскивание: закладку с позиции <paramref name="fromIndex"/> бросили перед закладкой на позиции
    /// <paramref name="insertBefore"/> (Count — в конец). Возвращает итоговую позицию для Move: после удаления
    /// перетаскиваемой закладки всё, что правее неё, сдвигается на одну позицию влево.
    /// </summary>
    public static int DropIndex(int fromIndex, int insertBefore) =>
        insertBefore > fromIndex ? insertBefore - 1 : insertBefore;

    public bool Move(string id, int newIndex)
    {
        var index = _items.FindIndex(b => b.Id == id);
        if (index < 0)
            return false;
        newIndex = Math.Clamp(newIndex, 0, _items.Count - 1);
        if (newIndex == index)
            return false;
        var item = _items[index];
        _items.RemoveAt(index);
        _items.Insert(newIndex, item);
        return true;
    }

    public IReadOnlyList<Bookmark> Search(string? query)
    {
        var q = query?.Trim() ?? "";
        return q.Length == 0
            ? [.. _items]
            : [.. _items.Where(b => b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(q, StringComparison.OrdinalIgnoreCase))];
    }

    public static bool IsBookmarkable(string? url) =>
        !string.IsNullOrEmpty(url) && url != UrlPolicy.AboutBlank && UrlPolicy.IsAllowedTopLevelNavigation(url);

    private static string CleanTitle(string? title, string url)
    {
        var t = string.IsNullOrWhiteSpace(title) ? url : title.Trim();
        return t.Length > MaxTitleLength ? t[..MaxTitleLength] : t;
    }
}

public sealed class BookmarkStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public string FilePath { get; } = Path.GetFullPath(filePath);

    /// <summary>Повреждённый файл не перезаписывается молча: problem сообщает вызывающему, а файл копируется в .bak.</summary>
    public BookmarkCollection Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(FilePath))
            return new BookmarkCollection();
        try
        {
            return new BookmarkCollection(JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(FilePath), JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = ex.Message;
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch (IOException) { } // не теряем закладки при сбое
            return new BookmarkCollection();
        }
    }

    public void Save(BookmarkCollection bookmarks) =>
        IO.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(bookmarks.Items, JsonOptions));
}
