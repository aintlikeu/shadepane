using Shadepane.Core.Bookmarks;
using Shadepane.Core.Browsing;

namespace Shadepane.Core.Tests;

public class BookmarksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    private static BookmarkCollection With(params string[] urls)
    {
        var c = new BookmarkCollection();
        foreach (var u in urls)
            Assert.True(c.TryAdd(u, null, T0, out _, out _));
        return c;
    }

    [Fact]
    public void Add_OnlyWebPages_NoDuplicates()
    {
        var c = new BookmarkCollection();
        Assert.True(c.TryAdd("https://a.example/", "A", T0, out var a, out _));
        Assert.True(c.TryAdd("https://a.example/", "Другое имя", T0, out var again, out _));
        Assert.Same(a, again);
        Assert.Single(c.Items);
        foreach (var bad in new[] { "about:blank", "javascript:alert(1)", "file:///C:/x", "chrome-extension://x/p.html", "https://u@evil.example/", null })
            Assert.False(c.TryAdd(bad, "x", T0, out _, out _), bad);
    }

    [Fact]
    public void Add_EmptyTitle_UsesUrl() =>
        Assert.Equal("https://a.example/", With("https://a.example/").Items[0].Title);

    [Fact]
    public void Remove_And_FindByUrl()
    {
        var c = With("https://a.example/", "https://b.example/");
        var b = c.FindByUrl("https://b.example/")!;
        Assert.True(c.Remove(b.Id));
        Assert.Null(c.FindByUrl("https://b.example/"));
        Assert.False(c.Remove(b.Id));
    }

    [Fact]
    public void Update_NormalizesUrl_AndRejectsBadOrDuplicate()
    {
        var c = With("https://a.example/", "https://b.example/");
        var a = c.Items[0];
        Assert.True(c.TryUpdate(a.Id, " Новое ", "c.example/x", out _));
        Assert.Equal("https://c.example/x", c.Items[0].Url);
        Assert.Equal("Новое", c.Items[0].Title);
        Assert.False(c.TryUpdate(a.Id, "x", "javascript:alert(1)", out var e1));
        Assert.NotNull(e1);
        Assert.False(c.TryUpdate(a.Id, "x", "https://b.example/", out var e2)); // дубль
        Assert.NotNull(e2);
        Assert.False(c.TryUpdate("missing", "x", "https://d.example/", out _));
    }

    [Fact]
    public void Move_ReordersAndClamps()
    {
        var c = With("https://1.example/", "https://2.example/", "https://3.example/");
        var third = c.Items[2];
        Assert.True(c.Move(third.Id, 0));
        Assert.Equal(third, c.Items[0]);
        Assert.True(c.Move(third.Id, 99)); // к концу
        Assert.Equal(third, c.Items[^1]);
        Assert.False(c.Move(third.Id, 2)); // уже там
    }

    [Fact]
    public void Search_TitleOrUrl_CaseInsensitive()
    {
        var c = new BookmarkCollection();
        c.TryAdd("https://docs.example/", "Документация", T0, out _, out _);
        c.TryAdd("https://mail.example/", "Почта", T0, out _, out _);
        Assert.Single(c.Search("ДОКУМЕНТ"));
        Assert.Single(c.Search("mail"));
        Assert.Equal(2, c.Search(" ").Count);
    }

    [Fact]
    public void Load_FiltersInjectedBadEntries_AndDuplicates()
    {
        var c = new BookmarkCollection(
        [
            new("1", "ok", "https://ok.example/", T0),
            new("2", "bad", "javascript:alert(1)", T0),
            new("1", "dup id", "https://other.example/", T0),
            new("3", "dup url", "https://ok.example/", T0),
            new("", "no id", "https://x.example/", T0),
        ]);
        Assert.Single(c.Items);
    }

    [Fact]
    public void Store_RoundTrip_CorruptFileIsBackedUp()
    {
        using var tmp = new TempDir();
        var store = new BookmarkStore(Path.Combine(tmp.Path, "bookmarks.json"));
        var c = With("https://a.example/", "https://b.example/");
        store.Save(c);
        Assert.Equal(c.Items, store.Load(out var p0).Items);
        Assert.Null(p0);

        File.WriteAllText(store.FilePath, "{ broken");
        Assert.Empty(store.Load(out var problem).Items);
        Assert.NotNull(problem);
        Assert.Equal("{ broken", File.ReadAllText(store.FilePath + ".bak"));
    }

    [Theory]
    [InlineData("D", false, BrowserCommand.ToggleBookmark)]
    [InlineData("O", true, BrowserCommand.BookmarksManager)]
    [InlineData("B", true, BrowserCommand.ToggleBookmarksBar)]
    public void Shortcuts(string key, bool shift, BrowserCommand command) =>
        Assert.Equal(command, BrowserShortcuts.Resolve(key, ctrl: true, shift, alt: false)?.Command);
}
