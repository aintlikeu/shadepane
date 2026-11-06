using Shadepane.Core.Extensions;
using Shadepane.Core.Policy;

namespace Shadepane.Core.Tests;

/// <summary>Окно «Дать права»: какие отказы пользователь может снять для одного расширения и какие — никогда.</summary>
public class PermissionGrantTests
{
    // Права и хосты как у блокировщика рекламы (AdGuard): метаданные + все сайты + content_scripts на всех сайтах.
    private static readonly string AdBlocker = Ext.Mv3("""
        "permissions": ["tabs", "webNavigation", "declarativeNetRequestFeedback", "storage", "scripting"],
        "host_permissions": ["<all_urls>"],
        "content_scripts": [{ "matches": ["http://*/*", "https://*/*"], "js": ["c.js"] }]
        """);

    private static ValidationResult Check(string json, BrowserPolicy policy, ExtensionGrants? grants = null) =>
        ExtensionValidator.Validate(ManifestReader.Parse(json), policy, grants);

    [Fact]
    public void NoPolicy_AdBlocker_CanBeGranted_AndPassesWithTheGrants()
    {
        var denied = Check(AdBlocker, BrowserPolicy.Default);
        Assert.False(denied.IsAllowed);
        Assert.True(denied.CanBeGrantedByUser);

        var needed = denied.GrantsNeeded;
        Assert.True(needed.AllSitesAccess);
        Assert.Equal(["tabs", "webNavigation", "declarativeNetRequestFeedback"], needed.Permissions);
        Assert.Equal(ExtensionGrants.AllSites, needed.Keys.First());

        Assert.True(Check(AdBlocker, BrowserPolicy.Default, needed).IsAllowed);
    }

    [Fact]
    public void PartialGrants_LeaveTheRestDenied()
    {
        var result = Check(AdBlocker, BrowserPolicy.Default, new ExtensionGrants(["tabs"], AllSitesAccess: false));
        Assert.False(result.IsAllowed);
        Assert.DoesNotContain(result.Violations, v => v.Grant == "tabs");
        Assert.Contains(result.Violations, v => v.Grant == ExtensionGrants.AllSites);
    }

    [Fact]
    public void AdminPolicy_WithoutOptIn_DisablesGrants()
    {
        // Политика администратора без allowExtensionPermissionGrants: выдача прав выключена.
        var admin = BrowserPolicy.Parse("{}");
        Assert.False(admin.PermissionGrantsAllowed);

        var result = Check(AdBlocker, admin);
        Assert.False(result.CanBeGrantedByUser);
        Assert.All(result.Violations, v => Assert.Null(v.Grant));
        // Выданные раньше права (например, до появления политики) больше не действуют.
        Assert.False(Check(AdBlocker, admin, new ExtensionGrants(["tabs", "webNavigation", "declarativeNetRequestFeedback"], true)).IsAllowed);
    }

    [Fact]
    public void AdminPolicy_CanOptIn()
    {
        var admin = BrowserPolicy.Parse("""{ "allowExtensionPermissionGrants": true }""");
        Assert.True(Check(AdBlocker, admin).CanBeGrantedByUser);
    }

    [Theory]
    [InlineData("tabCapture")]
    [InlineData("desktopCapture")]
    [InlineData("pageCapture")]
    [InlineData("debugger")]
    [InlineData("downloads")]
    public void CapturePermissions_AreNeverGrantable(string permission)
    {
        var json = Ext.Mv3($$""" "permissions": ["{{permission}}", "tabs"] """);
        var result = Check(json, BrowserPolicy.Default);
        Assert.False(result.CanBeGrantedByUser);
        Assert.Contains(result.Violations, v => v.Code == "blocked-permission" && v.Grant is null);
        // Даже если запись в approved-extensions.json подделали и «выдали» такое право.
        Assert.False(Check(json, BrowserPolicy.Default, new ExtensionGrants([permission, "tabs"], false)).IsAllowed);
    }

    [Fact]
    public void ProtectedHosts_ShowTheHardRefusalUpFront()
    {
        // Все сайты пересекаются с защищёнными доменами: выдача прав этого не снимет, и это видно сразу,
        // а не после того, как пользователь согласился.
        var policy = BrowserPolicy.Parse("""{ "protectedHosts": ["*.corp.example"], "allowExtensionPermissionGrants": true }""");
        var result = Check(AdBlocker, policy);
        Assert.False(result.CanBeGrantedByUser);
        Assert.Contains(result.Violations, v => v.Code == "protected-host" && v.Grant is null);
        Assert.False(Check(AdBlocker, policy, result.GrantsNeeded).IsAllowed);
    }

    [Fact]
    public void AllowlistMode_IsNotGrantable()
    {
        var policy = BrowserPolicy.Parse("""{ "allowedPermissions": ["storage"], "blockedPermissions": [], "allowExtensionPermissionGrants": true }""");
        var result = Check(Ext.Mv3(""" "permissions": ["tabs"] """), policy);
        Assert.False(result.CanBeGrantedByUser);
        Assert.Contains(result.Violations, v => v.Code == "permission-not-allowed");
    }

    [Fact]
    public void ActiveTab_IsGrantable_WithoutProtectedHosts()
    {
        var json = Ext.Mv3(""" "permissions": ["activeTab", "storage"] """);
        var result = Check(json, BrowserPolicy.Default);
        Assert.True(result.CanBeGrantedByUser);
        Assert.Equal(["activeTab"], result.GrantsNeeded.Permissions);
        Assert.True(Check(json, BrowserPolicy.Default, result.GrantsNeeded).IsAllowed);
    }

    [Fact]
    public void ActiveTab_IsNeverGrantable_WithProtectedHosts()
    {
        // activeTab даёт доступ к вкладке, по которой кликнули, — в том числе к защищённому домену.
        var policy = BrowserPolicy.Parse("""{ "protectedHosts": ["*.corp.example"], "allowExtensionPermissionGrants": true }""");
        var json = Ext.Mv3(""" "permissions": ["activeTab"] """);
        var result = Check(json, policy);
        Assert.False(result.CanBeGrantedByUser);
        Assert.Contains(result.Violations, v => v.Code == "blocked-permission" && v.Grant is null);
        Assert.False(Check(json, policy, new ExtensionGrants(["activeTab"], false)).IsAllowed);
    }

    [Fact]
    public void GrantsFromTheDialog_CarryTheCurrentWording()
    {
        var needed = Check(AdBlocker, BrowserPolicy.Default).GrantsNeeded;
        Assert.Equal(ExtensionGrants.CurrentWording, needed.Wording);
        Assert.Empty(needed.KeysToReconfirm);
    }

    [Fact]
    public void AllSitesGrantedUnderTheOldWording_NeedsReconfirmation_OtherPermissionsDoNot()
    {
        var old = new ExtensionGrants(["tabs", "webNavigation"], AllSitesAccess: true);
        Assert.Equal([ExtensionGrants.AllSites], old.KeysToReconfirm);
        Assert.Empty(new ExtensionGrants(["tabs"], AllSitesAccess: false).KeysToReconfirm);

        var without = old.Without(old.KeysToReconfirm);
        Assert.False(without.AllSitesAccess);
        Assert.Equal(["tabs", "webNavigation"], without.Permissions);
        // Без доступа ко всем сайтам блокировщик не проходит — значит, спрашивать заново нужно.
        Assert.False(Check(AdBlocker, BrowserPolicy.Default, without).IsAllowed);
    }

    [Fact]
    public void Describe_CoversEveryGrantablePermission()
    {
        foreach (var permission in BrowserPolicy.UserGrantablePermissions)
            Assert.NotEqual(permission, ExtensionGrants.Describe(permission));
        Assert.NotEqual(ExtensionGrants.AllSites, ExtensionGrants.Describe(ExtensionGrants.AllSites));
    }

    [Fact]
    public void Staging_StagesAndVerifiesWithGrants()
    {
        using var tmp = new TempDir();
        var src = Ext.Create(tmp.Sub("src"), AdBlocker, ("c.js", "1"));
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));

        Assert.Null(staging.Stage(src, BrowserPolicy.Default).Staged);

        var grants = new ExtensionGrants(["tabs", "webNavigation", "declarativeNetRequestFeedback"], true);
        var staged = staging.Stage(src, BrowserPolicy.Default, grants).Staged!;
        Assert.True(staging.Verify(staged.Path, staged.Hash, BrowserPolicy.Default, grants).IsAllowed);
        Assert.False(staging.Verify(staged.Path, staged.Hash, BrowserPolicy.Default).IsAllowed);
    }

    [Fact]
    public void Store_KeepsGrants_AndReadsOldFilesWithoutThem()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "approved.json");
        var store = new ApprovedExtensionStore(path);
        var grants = new ExtensionGrants(["tabs"], AllSitesAccess: true);
        store.Save([
            new ApprovedExtension("a", "A", "p", "h", null, null, DateTimeOffset.UnixEpoch, grants),
            new ApprovedExtension("b", "B", "p", "h", null, null, DateTimeOffset.UnixEpoch),
        ]);

        var loaded = store.Load();
        Assert.Equal(["tabs"], loaded[0].Grants!.Permissions);
        Assert.True(loaded[0].Grants!.AllSitesAccess);
        Assert.Null(loaded[1].Grants);
        Assert.DoesNotContain("isEmpty", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

        // Файл прежней версии — без поля grants.
        File.WriteAllText(path, """[{"id":"c","name":"C","stagedPath":"p","hash":"h","popupPath":null,"iconPath":null,"addedAt":"2026-11-01T00:00:00+00:00"}]""");
        Assert.Null(store.Load()[0].Grants);

        // Права, выданные до версий текста (без поля wording): все сайты нужно подтвердить заново.
        File.WriteAllText(path, """[{"id":"d","name":"D","stagedPath":"p","hash":"h","popupPath":null,"iconPath":null,"addedAt":"2026-11-01T00:00:00+00:00","grants":{"permissions":["tabs"],"allSitesAccess":true}}]""");
        var legacy = store.Load()[0].Grants!;
        Assert.Equal(0, legacy.Wording);
        Assert.Equal([ExtensionGrants.AllSites], legacy.KeysToReconfirm);

        store.Save([store.Load()[0] with { Grants = legacy with { Wording = ExtensionGrants.CurrentWording } }]);
        Assert.Empty(store.Load()[0].Grants!.KeysToReconfirm);
    }
}
