using Shadepane.Core.Extensions;

namespace Shadepane.Core.Tests;

public class ManifestReaderTests
{
    [Fact]
    public void DuplicateKey_TopLevel_IsRejected()
    {
        var ex = Assert.Throws<ManifestException>(() => ManifestReader.Parse(
            """{ "manifest_version": 3, "name": "x", "version": "1", "permissions": [], "permissions": ["tabCapture"] }"""));
        Assert.Contains("permissions", ex.Message);
    }

    [Fact]
    public void DuplicateKey_Nested_IsRejected() =>
        Assert.Throws<ManifestException>(() => ManifestReader.Parse(
            """{ "manifest_version": 3, "name": "x", "version": "1", "action": { "default_popup": "a.html", "default_popup": "b.html" } }"""));

    [Fact]
    public void CommentsAndTrailingCommas_AreAccepted()
    {
        var m = ManifestReader.Parse("""
            {
              // комментарий
              "manifest_version": 3, "name": "x", "version": "1",
              "permissions": ["storage",],
            }
            """);
        Assert.Equal(["storage"], m.ApiPermissions);
    }

    [Fact]
    public void Bom_IsAccepted()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "manifest.json"), Ext.Mv3(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal("Test", ManifestReader.ReadFromDirectory(tmp.Path).Name);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{ "name": "x", "version": "1" }""")]                                      // нет manifest_version
    [InlineData("""{ "manifest_version": 3, "version": "1" }""")]                            // нет name
    [InlineData("""{ "manifest_version": 3, "name": "x", "version": "1", "permissions": "tabCapture" }""")] // не массив
    [InlineData("""{ "manifest_version": 3, "name": "x", "version": "1", "permissions": [{"socket": []}] }""")]
    public void Malformed_IsRejected(string json) => Assert.Throws<ManifestException>(() => ManifestReader.Parse(json));

    [Fact]
    public void TooLarge_IsRejected()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "manifest.json"), Ext.Mv3() + new string(' ', (int)ManifestReader.MaxManifestBytes));
        Assert.Throws<ManifestException>(() => ManifestReader.ReadFromDirectory(tmp.Path));
    }

    [Fact]
    public void HostSources_AreCollectedFromAllPlaces()
    {
        var m = ManifestReader.Parse(Ext.Mv3("""
            "permissions": ["storage", "https://a.example/*"],
            "optional_permissions": ["<all_urls>"],
            "host_permissions": ["https://b.example/*"],
            "optional_host_permissions": ["https://c.example/*"],
            "content_scripts": [{ "matches": ["https://d.example/*"], "js": ["x.js"] }]
            """));
        Assert.Equal(["storage"], m.ApiPermissions);
        Assert.Equal(
            ["https://a.example/*", "<all_urls>", "https://b.example/*", "https://c.example/*", "https://d.example/*"],
            m.HostPermissions.Select(h => h.Pattern));
    }

    [Fact]
    public void PopupAndIcon_ArePicked()
    {
        var m = ManifestReader.Parse(Ext.Mv3("""
            "action": { "default_popup": "ui/popup.html", "default_icon": { "16": "i16.png", "48": "i48.png", "32": "i32.png" } }
            """));
        Assert.Equal("ui/popup.html", m.PopupPath);
        Assert.Equal("i32.png", m.IconPath);
    }

    [Fact]
    public void Mv2BrowserAction_PopupIsPicked()
    {
        var m = ManifestReader.Parse("""{ "manifest_version": 2, "name": "x", "version": "1", "browser_action": { "default_popup": "p.html" }, "icons": { "16": "s.png" } }""");
        Assert.Equal("p.html", m.PopupPath);
        Assert.Equal("s.png", m.IconPath);
    }
}
