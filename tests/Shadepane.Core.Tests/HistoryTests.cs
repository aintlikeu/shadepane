using Shadepane.Core.Browsing;
using Shadepane.Core.History;

namespace Shadepane.Core.Tests;

public class BrowsingHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Add_RecordsOnlyWebPages()
    {
        var h = new BrowsingHistory();
        Assert.True(h.Add("https://example.com/", "Example", T0));
        Assert.False(h.Add("about:blank", null, T0));
        Assert.False(h.Add("chrome-extension://abc/popup.html", null, T0));
        Assert.False(h.Add("data:text/html,x", null, T0));
        Assert.False(h.Add("https://user@evil.example/", null, T0));
        Assert.False(h.Add(null, null, T0));
        Assert.Equal(1, h.Count);
    }

    [Fact]
    public void Add_SameUrlTwiceInARow_UpdatesInsteadOfDuplicating()
    {
        var h = new BrowsingHistory();
        h.Add("https://a.example/", "A", T0);
        h.Add("https://a.example/", "A2", T0.AddMinutes(1));
        Assert.Equal(1, h.Count);
        Assert.Equal("A2", h.Search(null)[0].Title);
        h.Add("https://b.example/", "B", T0.AddMinutes(2));
        h.Add("https://a.example/", "A", T0.AddMinutes(3)); // не подряд — новая запись
        Assert.Equal(3, h.Count);
    }

    [Fact]
    public void Search_NewestFirst_MatchesTitleAndUrl_CaseInsensitive()
    {
        var h = new BrowsingHistory();
        h.Add("https://docs.example/guide", "Руководство", T0);
        h.Add("https://news.example/", "Новости", T0.AddMinutes(1));
        Assert.Equal("https://news.example/", h.Search(null)[0].Url);
        Assert.Single(h.Search("РУКОВОД"));
        Assert.Single(h.Search("docs.EXAMPLE"));
        Assert.Empty(h.Search("nothing"));
    }

    [Fact]
    public void UpdateTitle_UpdatesLatestEntryWithThatUrl()
    {
        var h = new BrowsingHistory();
        h.Add("https://a.example/", null, T0); // заголовка ещё нет — пока адрес
        Assert.Equal("https://a.example/", h.Search(null)[0].Title);
        Assert.True(h.UpdateTitle("https://a.example/", "  Настоящий заголовок "));
        Assert.Equal("Настоящий заголовок", h.Search(null)[0].Title);
        Assert.False(h.UpdateTitle("https://a.example/", "Настоящий заголовок")); // без изменений
        Assert.False(h.UpdateTitle("https://other.example/", "X"));
    }

    [Fact]
    public void Trim_KeepsNewestMaxEntries()
    {
        var h = new BrowsingHistory();
        for (var i = 0; i < BrowsingHistory.MaxEntries + 10; i++)
            h.Add($"https://site{i}.example/", null, T0.AddSeconds(i));
        Assert.Equal(BrowsingHistory.MaxEntries, h.Count);
        Assert.Equal($"https://site{BrowsingHistory.MaxEntries + 9}.example/", h.Search(null)[0].Url);
        Assert.Empty(h.Search("site0.example"));
    }

    [Fact]
    public void LongTitle_IsTruncated()
    {
        var h = new BrowsingHistory();
        h.Add("https://a.example/", new string('x', 10_000), T0);
        Assert.Equal(BrowsingHistory.MaxTitleLength, h.Search(null)[0].Title.Length);
    }

    [Fact]
    public void Remove_And_Clear_MarkDirty()
    {
        var h = new BrowsingHistory();
        h.Add("https://a.example/", "A", T0);
        h.Add("https://b.example/", "B", T0.AddMinutes(1));
        h.MarkSaved();
        Assert.True(h.Remove(h.Search("a.example")[0]));
        Assert.True(h.IsDirty);
        h.MarkSaved();
        h.Clear();
        Assert.True(h.IsDirty);
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void Store_RoundTrip_FiltersBadEntries_AndSurvivesCorruption()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(Path.Combine(tmp.Path, "history.json"));
        var h = new BrowsingHistory();
        h.Add("https://a.example/", "A", T0);
        store.Save(h);
        Assert.False(h.IsDirty);
        Assert.Equal(h.Snapshot(), store.Load(out var p0).Snapshot());
        Assert.Null(p0);

        // запись, подложенная в файл вручную, проходит те же правила
        File.WriteAllText(store.FilePath, """[{"url":"javascript:alert(1)","title":"x","visitedAt":"2026-11-04T10:00:00+00:00"},{"url":"https://ok.example/","title":"ok","visitedAt":"2026-11-04T10:00:00+00:00"}]""");
        Assert.Equal(1, store.Load(out _).Count);

        File.WriteAllText(store.FilePath, "{ broken");
        Assert.Equal(0, store.Load(out var problem).Count);
        Assert.NotNull(problem);
    }

    [Fact]
    public void CtrlH_OpensHistory() =>
        Assert.Equal(BrowserCommand.History, BrowserShortcuts.Resolve("H", ctrl: true, shift: false, alt: false)?.Command);
}
