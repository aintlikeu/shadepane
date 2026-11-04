using Shadepane.Core.Navigation;
using Shadepane.Core.Policy;
using Shadepane.Core.Settings;

namespace Shadepane.Core.Tests;

public class EffectiveConfigTests
{
    private static readonly BrowserPolicy NoPolicy = BrowserPolicy.Default;

    [Fact]
    public void Default_HomeIsSearchEngineHome_Unlocked()
    {
        var c = EffectiveConfig.Resolve(NoPolicy, UserSettings.Default);
        Assert.Equal(SearchEngines.Default.HomeUrl, c.HomeUrl);
        Assert.False(c.HomeLocked);
    }

    [Fact]
    public void User_BlankHome() =>
        Assert.Equal("about:blank", EffectiveConfig.Resolve(NoPolicy, new UserSettings { HomePage = HomePageKind.Blank }).HomeUrl);

    [Fact]
    public void User_CustomHome_IsNormalized() =>
        Assert.Equal("https://example.com/", EffectiveConfig.Resolve(NoPolicy,
            new UserSettings { HomePage = HomePageKind.Custom, CustomHomeUrl = "example.com" }).HomeUrl);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/x")]
    [InlineData(null)]
    public void User_BadCustomHome_FallsBackToSearchEngineHome(string? url) =>
        Assert.Equal(SearchEngines.Default.HomeUrl, EffectiveConfig.Resolve(NoPolicy,
            new UserSettings { HomePage = HomePageKind.Custom, CustomHomeUrl = url }).HomeUrl);

    [Fact]
    public void Policy_HomeWins_AndLocks()
    {
        var policy = BrowserPolicy.Parse("""{ "homeUrl": "https://intranet.corp.example" }""");
        var c = EffectiveConfig.Resolve(policy, new UserSettings { HomePage = HomePageKind.Blank });
        Assert.Equal("https://intranet.corp.example/", c.HomeUrl);
        Assert.True(c.HomeLocked);
    }

    [Fact]
    public void Policy_WithoutHome_DoesNotLock() =>
        Assert.False(EffectiveConfig.Resolve(BrowserPolicy.Parse("{}"), UserSettings.Default).HomeLocked);
}

public class UserSettingsStoreTests
{
    [Fact]
    public void RoundTrip_AndMissingFileGivesDefaults()
    {
        using var tmp = new TempDir();
        var store = new UserSettingsStore(Path.Combine(tmp.Path, "sub", "settings.json"));
        Assert.Equal(UserSettings.Default, store.Load(out var p0));
        Assert.Null(p0);

        var s = new UserSettings { HomePage = HomePageKind.Custom, CustomHomeUrl = "https://example.com/" };
        store.Save(s);
        Assert.Equal(s, store.Load(out var p1));
        Assert.Null(p1);
        Assert.Contains("\"custom\"", File.ReadAllText(store.FilePath)); // enum строкой — файл читаем человеком
    }

    [Fact]
    public void CorruptFile_GivesDefaults_WithProblem()
    {
        using var tmp = new TempDir();
        var store = new UserSettingsStore(Path.Combine(tmp.Path, "settings.json"));
        File.WriteAllText(store.FilePath, "{ broken");
        Assert.Equal(UserSettings.Default, store.Load(out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void UnknownFields_AreIgnored() // файл от более новой версии не должен ломать старую
    {
        using var tmp = new TempDir();
        var store = new UserSettingsStore(Path.Combine(tmp.Path, "settings.json"));
        File.WriteAllText(store.FilePath, """{ "homePage": "blank", "futureOption": 42 }""");
        Assert.Equal(HomePageKind.Blank, store.Load(out _).HomePage);
    }
}
