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
    public void Default_IsStartpage_Unlocked()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Default, UserSettings.Default);
        Assert.Equal("startpage", c.SearchEngine.Id);
        Assert.Equal("https://www.startpage.com/", c.HomeUrl);
        Assert.False(c.SearchLocked);
    }

    [Fact]
    public void User_Preset_AlsoDrivesSearchEngineHome()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Default, new UserSettings { SearchEngineId = "duckduckgo" });
        Assert.Equal("duckduckgo", c.SearchEngine.Id);
        Assert.Equal("https://duckduckgo.com/", c.HomeUrl);
    }

    [Fact]
    public void User_UnknownPreset_FallsBackToDefault() =>
        Assert.Equal("startpage", EffectiveConfig.Resolve(BrowserPolicy.Default, new UserSettings { SearchEngineId = "nope" }).SearchEngine.Id);

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
        Assert.Equal("startpage", EffectiveConfig.Resolve(BrowserPolicy.Default,
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
        "AllowManifestV2", "AllowExtensionClipboardRead", "AllowDownloads", "CheckForUpdates", "BlockInRemoteSession",
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
              "allowDownloads": false, "checkForUpdates": false, "blockInRemoteSession": false
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

public class ProtectedUrlTests
{
    private static readonly BrowserPolicy Policy = new BrowserPolicy { ProtectedHosts = ["*.corp.example", "crm.partner.example"] }.EnsureValid();

    [Theory]
    [InlineData("https://wiki.corp.example/doc.pdf", true)]
    [InlineData("https://corp.example/", true)]
    [InlineData("http://CRM.partner.example:8443/export", true)]
    [InlineData("https://partner.example/", false)]
    [InlineData("https://evilcorp.example/", false)]
    [InlineData("about:blank", false)]
    [InlineData("data:text/plain,x", false)]
    [InlineData("blob:https://wiki.corp.example/6f1c", true)]       // blob от защищённого источника
    [InlineData("filesystem:https://corp.example/temporary/a", true)]
    [InlineData("blob:https://public.example/6f1c", false)]
    [InlineData(null, false)]
    public void IsProtectedUrl(string? url, bool expected) => Assert.Equal(expected, Policy.IsProtectedUrl(url));

    [Fact]
    public void NoProtectedHosts_NothingIsProtected() =>
        Assert.False(BrowserPolicy.Default.IsProtectedUrl("https://corp.example/"));
}

public class DownloadGateTests
{
    private static readonly BrowserPolicy Dlp = new BrowserPolicy { ProtectedHosts = ["*.corp.example"] }.EnsureValid();
    private static readonly BrowserPolicy DlpAllowed = new BrowserPolicy { ProtectedHosts = ["*.corp.example"], AllowDownloads = true }.EnsureValid();
    private const string Public = "https://public.example/file.zip";

    private static string? Gate(BrowserPolicy policy, string? page, string? file, bool protectedSeen = false, bool popup = false, bool allowed = true) =>
        DownloadGate.BlockReason(policy, allowed, downloadsLocked: false, popup, protectedSeen, page, file);

    [Fact]
    public void Disabled_IsBlocked() => Assert.Equal("user-setting", Gate(BrowserPolicy.Default, null, Public, allowed: false));

    [Fact]
    public void ExtensionPopup_IsBlocked() => Assert.Equal("extension-popup", Gate(BrowserPolicy.Default, null, Public, popup: true));

    [Fact]
    public void NoProtectedHosts_AnythingGoes()
    {
        Assert.Null(Gate(BrowserPolicy.Default, "https://corp.example/", "data:text/plain,x", protectedSeen: true));
    }

    [Theory]
    [InlineData("https://wiki.corp.example/page", Public)]             // страница защищённая
    [InlineData("https://public.example/", "https://corp.example/x")]  // файл защищённый
    [InlineData("https://public.example/", "blob:https://wiki.corp.example/id")] // blob от защищённого фрейма
    public void DlpMode_ProtectedPageOrFile_IsBlocked(string page, string file) =>
        Assert.Equal("protected-host", Gate(Dlp, page, file));

    [Fact]
    public void DlpMode_TabShowedProtectedContent_IsBlocked() => // перенаправление на защищённый домен или защищённый фрейм
        Assert.Equal("protected-host", Gate(Dlp, "https://public.example/", Public, protectedSeen: true));

    [Theory]
    [InlineData("data:text/plain,secret")]
    [InlineData("about:blank")]
    [InlineData(null)]
    public void DlpMode_UnknownSource_IsBlocked(string? file) =>
        Assert.Equal("unknown-source", Gate(Dlp, "https://public.example/", file));

    [Theory]
    [InlineData(Public)]
    [InlineData("blob:https://public.example/id")]
    public void DlpMode_PublicDownload_IsAllowed(string file) => Assert.Null(Gate(Dlp, "https://public.example/", file));

    [Fact]
    public void ExplicitAllowDownloads_LiftsDlpRestrictions() =>
        Assert.Null(Gate(DlpAllowed, "https://wiki.corp.example/", "data:text/plain,x", protectedSeen: true));
}

public class UpdatesSettingTests
{
    [Fact]
    public void Default_On_Unlocked()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Default, UserSettings.Default);
        Assert.True(c.CheckForUpdates);
        Assert.False(c.UpdatesLocked);
    }

    [Fact]
    public void Policy_WinsAndLocks()
    {
        var c = EffectiveConfig.Resolve(BrowserPolicy.Parse("""{ "checkForUpdates": false }"""), new UserSettings { CheckForUpdates = true });
        Assert.False(c.CheckForUpdates);
        Assert.True(c.UpdatesLocked);
    }
}

public class ThemeSettingTests
{
    [Theory]
    [InlineData(AppTheme.System, true, false)]
    [InlineData(AppTheme.System, false, true)]
    [InlineData(AppTheme.Light, false, false)]
    [InlineData(AppTheme.Dark, true, true)]
    public void UseDark_FollowsChoiceOrSystem(AppTheme theme, bool systemLight, bool expectedDark) =>
        Assert.Equal(expectedDark, ThemeChoice.UseDark(theme, systemLight));

    [Fact]
    public void Theme_DefaultsToSystem_AndRoundTripsAsString()
    {
        using var tmp = new TempDir();
        var store = new UserSettingsStore(Path.Combine(tmp.Path, "settings.json"));
        File.WriteAllText(store.FilePath, """{ "homePage": "blank" }"""); // файл старой версии, без темы
        Assert.Equal(AppTheme.System, store.Load(out _).Theme);

        store.Save(UserSettings.Default with { Theme = AppTheme.Dark });
        Assert.Contains("\"dark\"", File.ReadAllText(store.FilePath));
        Assert.Equal(AppTheme.Dark, store.Load(out _).Theme);
    }
}

public class LanguageSettingTests
{
    [Theory]
    [InlineData(AppLanguage.System, "ru", Localization.UiLanguage.Russian)]
    [InlineData(AppLanguage.System, "RU", Localization.UiLanguage.Russian)]
    [InlineData(AppLanguage.System, "en", Localization.UiLanguage.English)]
    [InlineData(AppLanguage.System, "de", Localization.UiLanguage.English)]
    [InlineData(AppLanguage.System, null, Localization.UiLanguage.English)]
    [InlineData(AppLanguage.Russian, "en", Localization.UiLanguage.Russian)]
    [InlineData(AppLanguage.English, "ru", Localization.UiLanguage.English)]
    public void Resolve_FollowsChoiceOrWindows(AppLanguage choice, string? windows, Localization.UiLanguage expected) =>
        Assert.Equal(expected, LanguageChoice.Resolve(choice, windows));

    [Fact]
    public void Language_DefaultsToSystem_AndRoundTripsAsString()
    {
        using var tmp = new TempDir();
        var store = new UserSettingsStore(Path.Combine(tmp.Path, "settings.json"));
        store.Save(UserSettings.Default with { Language = AppLanguage.English });
        Assert.Contains("\"english\"", File.ReadAllText(store.FilePath));
        Assert.Equal(AppLanguage.English, store.Load(out _).Language);
        File.WriteAllText(store.FilePath, "{}");
        Assert.Equal(AppLanguage.System, store.Load(out _).Language);
    }

    [Fact]
    public void Pick_ChoosesByLanguage()
    {
        Assert.Equal("Сохранить", Localization.Loc.Pick(Localization.UiLanguage.Russian, "Сохранить", "Save"));
        Assert.Equal("Save", Localization.Loc.Pick(Localization.UiLanguage.English, "Сохранить", "Save"));
    }
}
