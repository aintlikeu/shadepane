using Shadepane.Core.Browsing;
using Shadepane.Core.Policy;
using Shadepane.Core.Settings;

namespace Shadepane.Core.Tests;

public class SessionTests
{
    [Fact]
    public void FromTabs_KeepsOnlyWebPages_AndMapsActiveTab()
    {
        string?[] tabs = ["https://a.example/", "about:blank", "chrome-extension://abcdefghijklmnopabcdefghijklmnop/p.html",
                          "http://b.example/x", null, "javascript:alert(1)", "file:///C:/x"];
        var s = SessionState.FromTabs(tabs, activeIndex: 3);
        Assert.Equal(["https://a.example/", "http://b.example/x"], s.Urls);
        Assert.Equal(1, s.ActiveIndex);
    }

    [Fact]
    public void FromTabs_ActiveTabNotSaved_FallsBackToNearestOnTheLeft()
    {
        var s = SessionState.FromTabs(["https://a.example/", "https://b.example/", "about:blank"], activeIndex: 2);
        Assert.Equal(1, s.ActiveIndex);
        Assert.Equal(0, SessionState.FromTabs(["about:blank", "https://a.example/"], activeIndex: 0).ActiveIndex);
    }

    [Fact]
    public void FromTabs_NothingToSave_IsEmpty() =>
        Assert.True(SessionState.FromTabs(["about:blank", null], 0).IsEmpty);

    [Fact]
    public void FromTabs_CapsTabCount()
    {
        var many = Enumerable.Range(0, 80).Select(i => (string?)$"https://site{i}.example/").ToList();
        Assert.Equal(SessionState.MaxTabs, SessionState.FromTabs(many, 79).Urls.Count);
    }

    [Fact]
    public void Store_RoundTrip_EmptyDeletesFile_CorruptGivesEmpty()
    {
        using var tmp = new TempDir();
        var store = new SessionStore(Path.Combine(tmp.Path, "session.json"));
        Assert.True(store.Load(out var p0).IsEmpty);
        Assert.Null(p0);

        store.Save(SessionState.FromTabs(["https://a.example/", "https://b.example/"], 1));
        var loaded = store.Load(out _);
        Assert.Equal(["https://a.example/", "https://b.example/"], loaded.Urls);
        Assert.Equal(1, loaded.ActiveIndex);

        store.Save(SessionState.Empty);
        Assert.False(File.Exists(store.FilePath));

        File.WriteAllText(store.FilePath, "{ broken");
        Assert.True(store.Load(out var problem).IsEmpty);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Store_Load_RevalidatesUrls_FromAnEditedFile()
    {
        using var tmp = new TempDir();
        var store = new SessionStore(Path.Combine(tmp.Path, "session.json"));
        File.WriteAllText(store.FilePath, """{ "urls": ["javascript:alert(1)", "https://ok.example/"], "activeIndex": 9 }""");
        var s = store.Load(out _);
        Assert.Equal(["https://ok.example/"], s.Urls);
        Assert.Equal(0, s.ActiveIndex);
    }

    [Fact]
    public void Setting_DefaultsOff_PolicyWinsAndLocks()
    {
        var def = EffectiveConfig.Resolve(BrowserPolicy.Default, UserSettings.Default);
        Assert.False(def.RestoreSession);
        Assert.False(def.RestoreSessionLocked);
        Assert.True(EffectiveConfig.Resolve(BrowserPolicy.Default, new UserSettings { RestoreSession = true }).RestoreSession);

        var off = EffectiveConfig.Resolve(BrowserPolicy.Parse("""{ "restoreSession": false }"""), new UserSettings { RestoreSession = true });
        Assert.False(off.RestoreSession);
        Assert.True(off.RestoreSessionLocked);
    }
}
