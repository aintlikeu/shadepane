using Shadepane.Core.Navigation;
using Shadepane.Core.Policy;
using Shadepane.Core.Settings;
using Shadepane.Core.Browsing;

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

public class SearchEngineChoiceTests
{
    [Fact]
    public void Default_IsDuckDuckGo_Unlocked()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Default, UserSettings.Default);
        Assert.Equal("duckduckgo", c.SearchEngine.Id);
        Assert.False(c.SearchLocked);
    }

    [Fact]
    public void User_Preset_AlsoDrivesSearchEngineHome()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Default, new UserSettings { SearchEngineId = "startpage" });
        Assert.Equal("startpage", c.SearchEngine.Id);
        Assert.Equal("https://www.startpage.com/", c.HomeUrl);
    }

    [Fact]
    public void User_UnknownPreset_FallsBackToDefault() =>
        Assert.Equal("duckduckgo", EffectiveConfig.Resolve(BrowserPolicy.Default, new UserSettings { SearchEngineId = "nope" }).SearchEngine.Id);

    [Fact]
    public void User_CustomTemplate()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Default,
            new UserSettings { SearchEngineId = "custom", CustomSearchUrl = "https://search.example/s?q={query}" });
        Assert.Equal("custom", c.SearchEngine.Id);
        Assert.Equal("https://search.example/s?q={query}", c.SearchEngine.SearchUrl);
        Assert.Equal("https://search.example/", c.HomeUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://search.example/?q={query}")]
    [InlineData("https://{query}/")]
    public void User_BadCustomTemplate_FallsBackToDefault(string? template) =>
        Assert.Equal("duckduckgo", EffectiveConfig.Resolve(BrowserPolicy.Default,
            new UserSettings { SearchEngineId = "custom", CustomSearchUrl = template }).SearchEngine.Id);

    [Fact]
    public void Policy_SearchUrl_WinsAndLocks_PresetIsRecognized()
    {
        var policy = BrowserPolicy.Parse("""{ "searchUrl": "https://www.bing.com/search?q={query}" }""");
        var c = EffectiveConfig.Resolve(policy, new UserSettings { SearchEngineId = "google" });
        Assert.Equal("bing", c.SearchEngine.Id);
        Assert.True(c.SearchLocked);
    }

    [Theory]
    [InlineData("""{ "searchUrl": "http://search.example/?q={query}" }""")]
    [InlineData("""{ "searchUrl": "https://search.example/" }""")]
    public void Policy_BadSearchUrl_IsRejected(string json) =>
        Assert.Throws<PolicyException>(() => BrowserPolicy.Parse(json));
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

public class ClipboardSettingTests
{
    [Fact]
    public void Default_Allowed_Unlocked()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Default, UserSettings.Default);
        Assert.True(c.AllowExtensionClipboardRead);
        Assert.False(c.ClipboardLocked);
    }

    [Fact]
    public void User_CanDisable() =>
        Assert.False(EffectiveConfig.Resolve(BrowserPolicy.Default, new UserSettings { AllowExtensionClipboardRead = false }).AllowExtensionClipboardRead);

    [Fact]
    public void Policy_WinsAndLocks()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Parse("""{ "allowExtensionClipboardRead": false }"""),
            new UserSettings { AllowExtensionClipboardRead = true });
        Assert.False(c.AllowExtensionClipboardRead);
        Assert.True(c.ClipboardLocked);
    }

    [Fact]
    public void ForExtensions_AppliesUserChoice_OnlyWhenPolicyIsSilent()
    {
        var silent = BrowserPolicy.Default;
        Assert.False(silent.ForExtensions(userAllowsClipboardRead: false).AllowExtensionClipboardRead);
        var strict = BrowserPolicy.Parse("""{ "allowExtensionClipboardRead": false }""");
        Assert.Same(strict, strict.ForExtensions(userAllowsClipboardRead: true));
    }
}

public class BrowserPolicyCopyTests
{
    // Новое свойство политики? Добавьте его в ForExtensions, в этот список и в JSON ниже.
    private static readonly string[] Expected =
    [
        "HomeUrl", "SearchUrl", "ProtectedHosts", "BlockedPermissions", "AllowedPermissions", "AllowBroadHostAccess",
        "AllowManifestV2", "AllowExtensionClipboardRead", "AllowDownloads", "BlockInRemoteSession",
    ];

    private static IEnumerable<System.Reflection.PropertyInfo> Settable() =>
        typeof(BrowserPolicy).GetProperties().Where(p => p.SetMethod is { IsPublic: true });

    [Fact]
    public void SettablePropertySet_IsKnown() =>
        Assert.Equal(Expected.Order(), Settable().Select(p => p.Name).Order());

    [Fact]
    public void ForExtensions_CopiesEveryProperty()
    {
        var source = BrowserPolicy.Parse("""
            {
              "homeUrl": "https://home.example/", "searchUrl": "https://s.example/?q={query}",
              "protectedHosts": ["*.corp.example"], "blockedPermissions": ["tabCapture"],
              "allowedPermissions": ["storage"], "allowBroadHostAccess": true, "allowManifestV2": true,
              "allowDownloads": false, "blockInRemoteSession": false
            }
            """);
        var copy = source.ForExtensions(userAllowsClipboardRead: false);
        foreach (var p in Settable().Where(p => p.Name != "AllowExtensionClipboardRead"))
            Assert.Equal(p.GetValue(source), p.GetValue(copy));
        Assert.False(copy.AllowExtensionClipboardRead);
    }
}

public class DownloadsSettingTests
{
    [Fact]
    public void Default_Allowed_Unlocked()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Default, UserSettings.Default);
        Assert.True(c.AllowDownloads);
        Assert.False(c.DownloadsLocked);
    }

    [Fact]
    public void User_CanDisable() =>
        Assert.False(EffectiveConfig.Resolve(BrowserPolicy.Default, new UserSettings { AllowDownloads = false }).AllowDownloads);

    [Fact]
    public void Policy_WinsAndLocks()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Parse("""{ "allowDownloads": false }"""), new UserSettings { AllowDownloads = true });
        Assert.False(c.AllowDownloads);
        Assert.True(c.DownloadsLocked);
    }
}

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1023, "1023 Б")]
    [InlineData(1536, "1,5 КБ")]
    [InlineData(10 * 1024 * 1024, "10 МБ")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 ГБ")]
    [InlineData(-1, "?")]
    public void Format(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Progress()
    {
        Assert.Equal("5 МБ из 10 МБ (50%)", ByteSize.Progress(5 * 1024 * 1024, 10 * 1024 * 1024));
        Assert.Equal("5 МБ", ByteSize.Progress(5 * 1024 * 1024, null));
        Assert.Equal("5 МБ", ByteSize.Progress(5 * 1024 * 1024, 0));
    }

    [Fact]
    public void CtrlJ_OpensDownloads() =>
        Assert.Equal(Shadepane.Core.Browsing.BrowserCommand.Downloads, Shadepane.Core.Browsing.BrowserShortcuts.Resolve("J", true, false, false)?.Command);
}
