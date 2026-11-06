using Shadepane.Core.Extensions;
using Shadepane.Core.Policy;

namespace Shadepane.Core.Tests;

public class ExtensionValidatorTests
{
    private static ValidationResult Check(string json, BrowserPolicy? policy = null) =>
        ExtensionValidator.Validate(ManifestReader.Parse(json), policy ?? Ext.CorpPolicy);

    private static void AssertDenied(ValidationResult r, string code) =>
        Assert.Contains(r.Violations, v => v.Code == code);

    [Fact]
    public void HarmlessExtension_IsAllowed() =>
        Assert.True(Check(Ext.Mv3("""
            "permissions": ["storage", "alarms"],
            "host_permissions": ["https://api.public.example/*"],
            "content_scripts": [{ "matches": ["https://news.example/*"], "js": ["c.js"] }]
            """)).IsAllowed);

    [Theory]
    [InlineData("""
        "permissions": ["tabCapture"]
        """)]
    [InlineData("""
        "optional_permissions": ["desktopCapture"]
        """)] // опциональные права запрашиваются уже во время работы
    [InlineData("""
        "permissions": ["TabCapture"]
        """)] // регистр не помогает обойти
    [InlineData("""
        "permissions": ["activeTab"]
        """)]
    public void BlockedPermissions_AreDenied(string extra) => AssertDenied(Check(Ext.Mv3(extra)), "blocked-permission");

    [Theory]
    [InlineData("tabs")]
    [InlineData("history")]
    [InlineData("webNavigation")]
    [InlineData("declarativeNetRequestFeedback")]
    [InlineData("topSites")]
    [InlineData("sessions")]
    [InlineData("proxy")]
    [InlineData("downloads")]
    public void MetadataAndBypassPermissions_AreDeniedByDefault(string permission) =>
        AssertDenied(Check(Ext.Mv3($$""" "permissions": ["{{permission}}"] """)), "blocked-permission");

    // Типичный набор прав расширения-помощника, которое работает через буфер обмена.
    private static string ClipboardHelperManifest => Ext.Mv3("""
        "permissions": ["clipboardRead", "storage", "offscreen", "alarms", "scripting"],
        "host_permissions": ["https://helper.example/*", "https://www.helper.example/*"]
        """);

    [Fact]
    public void ClipboardRead_IsAllowedByDefault() =>
        Assert.True(Check(ClipboardHelperManifest).IsAllowed);

    [Theory]
    [InlineData("""
        "permissions": ["clipboardRead"]
        """)]
    [InlineData("""
        "optional_permissions": ["ClipboardRead"]
        """)]
    public void ClipboardRead_IsDenied_WhenOptionIsOff(string extra) =>
        AssertDenied(Check(Ext.Mv3(extra), new BrowserPolicy { AllowExtensionClipboardRead = false }.EnsureValid()), "clipboard-read-disabled");

    [Fact]
    public void ClipboardRead_OptionWorksInAllowlistMode()
    {
        var policy = new BrowserPolicy { AllowedPermissions = ["storage", "offscreen", "alarms", "scripting"] }.EnsureValid();
        Assert.True(Check(ClipboardHelperManifest, policy).IsAllowed);
    }

    [Fact]
    public void ClipboardRead_OptionDoesNotLiftHostRestrictions()
    {
        var policy = new BrowserPolicy { ProtectedHosts = ["helper.example"] }.EnsureValid();
        AssertDenied(Check(ClipboardHelperManifest, policy), "protected-host");
    }

    [Fact]
    public void Allowlist_DeniesEverythingNotListed()
    {
        var policy = new BrowserPolicy { AllowedPermissions = ["storage"] }.EnsureValid();
        Assert.True(Check(Ext.Mv3("""
            "permissions": ["storage"]
            """), policy).IsAllowed);
        AssertDenied(Check(Ext.Mv3("""
            "permissions": ["storage", "alarms"]
            """), policy), "permission-not-allowed");
        AssertDenied(Check(Ext.Mv3("""
            "optional_permissions": ["Alarms"]
            """), policy), "permission-not-allowed");
    }

    [Fact]
    public void Allowlist_BlockedPermission_IsReportedOnceAsBlocked()
    {
        var policy = new BrowserPolicy { AllowedPermissions = ["storage"] }.EnsureValid();
        var result = Check(Ext.Mv3("""
            "permissions": ["tabCapture"]
            """), policy);
        Assert.Single(result.Violations);
        AssertDenied(result, "blocked-permission");
    }

    [Theory]
    [InlineData("""
        "host_permissions": ["<all_urls>"]
        """)]
    [InlineData("""
        "optional_host_permissions": ["*://*/*"]
        """)]
    [InlineData("""
        "content_scripts": [{ "matches": ["<all_urls>"], "js": ["c.js"] }]
        """)]
    [InlineData("""
        "content_scripts": [{ "matches": ["*://*.com/*"], "js": ["c.js"] }]
        """)]
    [InlineData("""
        "host_permissions": ["*://*.example/*"]
        """)] // зона над всеми защищёнными доменами — отклоняется как широкий доступ
    public void BroadHostAccess_IsDenied(string extra) => AssertDenied(Check(Ext.Mv3(extra)), "broad-host-access");

    [Fact]
    public void PublicSuffixWildcard_IsDenied_EvenWithoutProtectedHosts() =>
        AssertDenied(Check(Ext.Mv3("""
            "host_permissions": ["https://*.co.uk/*"]
            """), BrowserPolicy.Default), "broad-host-access");

    [Theory]
    [InlineData("https://crm.corp.example/*")]
    [InlineData("https://corp.example/*")]
    [InlineData("*://*.partner.example/*")]  // родительский wildcard над crm.partner.example
    [InlineData("https://CRM.PARTNER.EXAMPLE:443/*")]
    public void ProtectedHosts_AreDenied(string pattern) =>
        AssertDenied(Check(Ext.Mv3($$""" "host_permissions": ["{{pattern}}"] """)), "protected-host");

    [Fact]
    public void ContentScriptOnProtectedHost_IsDenied() =>
        AssertDenied(Check(Ext.Mv3("""
            "content_scripts": [{ "matches": ["https://wiki.corp.example/*"], "js": ["steal.js"] }]
            """)), "protected-host");

    [Fact]
    public void BroadAccessAllowedByPolicy_StillDeniedOnProtectedHosts()
    {
        var policy = new BrowserPolicy { ProtectedHosts = ["*.corp.example"], AllowBroadHostAccess = true }.EnsureValid();
        AssertDenied(Check(Ext.Mv3("""
            "host_permissions": ["<all_urls>"]
            """), policy), "protected-host");
    }

    [Fact]
    public void Mv2HostInPermissions_IsChecked() =>
        AssertDenied(Check("""{ "manifest_version": 2, "name": "x", "version": "1", "permissions": ["https://crm.corp.example/*"] }""",
            new BrowserPolicy { ProtectedHosts = ["*.corp.example"], AllowManifestV2 = true }.EnsureValid()), "protected-host");

    [Fact]
    public void Mv2_IsDeniedByDefault() =>
        AssertDenied(Check("""{ "manifest_version": 2, "name": "x", "version": "1" }"""), "manifest-v2");

    [Fact]
    public void UnparseableHostPattern_IsDenied() =>
        AssertDenied(Check(Ext.Mv3("""
            "host_permissions": ["gopher://x.example/*"]
            """)), "invalid-host-pattern");

    [Fact]
    public void FileScheme_IsIgnored() =>
        Assert.True(Check(Ext.Mv3("""
            "host_permissions": ["file:///*"]
            """)).IsAllowed);

    [Fact]
    public void SampleEvilCapture_IsDenied_AndSpikePopup_IsAllowed()
    {
        var evil = ManifestReader.ReadFromDirectory(Path.Combine(Ext.SamplesDir, "extensions", "evil-capture"));
        var evilResult = ExtensionValidator.Validate(evil, Ext.CorpPolicy);
        AssertDenied(evilResult, "blocked-permission");
        AssertDenied(evilResult, "broad-host-access");

        var good = ManifestReader.ReadFromDirectory(Path.Combine(Ext.SamplesDir, "extensions", "spike-popup"));
        Assert.True(ExtensionValidator.Validate(good, Ext.CorpPolicy).IsAllowed);
    }

    [Fact]
    public void CaptureProbes_AreInstallableThroughTheGrantDialog()
    {
        // Стенд (docs/TESTING.md): обе пробы ставятся через окно «Дать права», каждая — с одним пунктом.
        // Если это изменится, строки в TESTING.md перестанут совпадать с поведением.
        var allSites = ManifestReader.ReadFromDirectory(Path.Combine(Ext.SamplesDir, "extensions", "capture-probe-allsites"));
        var denied = ExtensionValidator.Validate(allSites, BrowserPolicy.Default);
        Assert.True(denied.CanBeGrantedByUser);
        Assert.Equal([ExtensionGrants.AllSites], denied.GrantsNeeded.Keys);

        var activeTab = ManifestReader.ReadFromDirectory(Path.Combine(Ext.SamplesDir, "extensions", "capture-probe-activetab"));
        var activeTabResult = ExtensionValidator.Validate(activeTab, BrowserPolicy.Default);
        Assert.True(activeTabResult.CanBeGrantedByUser);
        Assert.Equal([BrowserPolicy.ActiveTabPermission], activeTabResult.GrantsNeeded.Keys);

        var permissive = BrowserPolicy.Parse(File.ReadAllText(Path.Combine(Ext.SamplesDir, "policies", "spike-permissive.json")));
        Assert.True(ExtensionValidator.Validate(activeTab, permissive).IsAllowed);
        Assert.True(ExtensionValidator.Validate(allSites, permissive).IsAllowed);
    }
}
