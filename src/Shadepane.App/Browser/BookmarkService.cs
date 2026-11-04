using System.IO;
using Shadepane.Core.Bookmarks;

namespace Shadepane.App.Browser;

/// <summary>Закладки приложения: сохраняются сразу при каждом изменении (файл маленький, а закладки ценнее истории).</summary>
internal sealed class BookmarkService
{
    private readonly BookmarkStore _store;
    private readonly BookmarkCollection _bookmarks;

    public BookmarkService(string filePath)
    {
        _store = new BookmarkStore(filePath);
        _bookmarks = _store.Load(out var problem);
        if (problem is not null)
            AppServices.Audit.Write("bookmarks-unreadable", new { file = _store.FilePath, backup = _store.FilePath + ".bak", problem });
    }

    /// <summary>Закладки изменились (звёздочка, панель закладок, менеджер).</summary>
    public event Action? Changed;

    public IReadOnlyList<Bookmark> Items => _bookmarks.Items;

    public Bookmark? FindByUrl(string? url) => _bookmarks.FindByUrl(url);

    public IReadOnlyList<Bookmark> Search(string? query) => _bookmarks.Search(query);

    public bool TryAdd(string? url, string? title, out string? error)
    {
        var added = _bookmarks.TryAdd(url, title, DateTimeOffset.Now, out _, out error);
        if (added)
            Commit();
        return added;
    }

    public void Remove(string id)
    {
        if (_bookmarks.Remove(id))
            Commit();
    }

    public bool TryUpdate(string id, string? title, string? url, out string? error)
    {
        var updated = _bookmarks.TryUpdate(id, title, url, out error);
        if (updated)
            Commit();
        return updated;
    }

    public void Move(string id, int newIndex)
    {
        if (_bookmarks.Move(id, newIndex))
            Commit();
    }

    private void Commit()
    {
        try
        {
            _store.Save(_bookmarks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppServices.Audit.Write("bookmarks-save-failed", new { error = ex.Message });
        }
        Changed?.Invoke();
    }
}
