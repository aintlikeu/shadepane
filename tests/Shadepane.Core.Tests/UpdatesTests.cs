using Shadepane.Core.Updates;

namespace Shadepane.Core.Tests;

public class UpdatesTests
{
    private static string ReleaseJson(string tag, bool draft = false, bool pre = false, string? installerUrl = null, string? sumsUrl = null) => $$"""
        {
          "tag_name": "{{tag}}", "html_url": "https://github.com/aintlikeu/shadepane/releases/tag/{{tag}}",
          "draft": {{draft.ToString().ToLowerInvariant()}}, "prerelease": {{pre.ToString().ToLowerInvariant()}},
          "assets": [
            { "name": "Shadepane-{{tag.TrimStart('v')}}-win-x64-setup.exe", "size": 1000,
              "browser_download_url": "{{installerUrl ?? $"https://github.com/aintlikeu/shadepane/releases/download/{tag}/Shadepane-{tag.TrimStart('v')}-win-x64-setup.exe"}}" },
            { "name": "SHA256SUMS.txt", "size": 200,
              "browser_download_url": "{{sumsUrl ?? $"https://github.com/aintlikeu/shadepane/releases/download/{tag}/SHA256SUMS.txt"}}" },
            { "name": "Shadepane-{{tag.TrimStart('v')}}-win-x64.zip", "size": 5000,
              "browser_download_url": "https://github.com/aintlikeu/shadepane/releases/download/{{tag}}/Shadepane-{{tag.TrimStart('v')}}-win-x64.zip" }
          ]
        }
        """;

    private static readonly ReleaseVersion V010 = new(0, 1, 0);

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("0.10.0", 0, 10, 0)]
    public void Version_Parse(string text, int a, int b, int c) => Assert.Equal(new ReleaseVersion(a, b, c), ReleaseVersion.TryParse(text));

    [Theory]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-beta")]
    [InlineData("latest")]
    [InlineData(null)]
    public void Version_Invalid(string? text) => Assert.Null(ReleaseVersion.TryParse(text));

    [Fact]
    public void Version_FromAssembly_IgnoresBuildMetadata() =>
        Assert.Equal(new ReleaseVersion(0, 2, 0), ReleaseVersion.FromAssembly("0.2.0+5251d52abc"));

    [Fact]
    public void Version_Ordering()
    {
        Assert.True(new ReleaseVersion(0, 10, 0).CompareTo(new ReleaseVersion(0, 9, 9)) > 0);
        Assert.True(new ReleaseVersion(0, 0, 0).IsDevelopmentBuild);
    }

    [Fact]
    public void Select_NewerRelease_WithOwnAssets()
    {
        var c = UpdatePlanner.Select(ReleaseInfo.TryParse(ReleaseJson("v0.2.0")), V010);
        Assert.NotNull(c);
        Assert.Equal(new ReleaseVersion(0, 2, 0), c.Version);
        Assert.Equal("Shadepane-0.2.0-win-x64-setup.exe", c.Installer.Name);
        Assert.Equal("SHA256SUMS.txt", c.Checksums.Name);
    }

    [Theory]
    [InlineData("v0.1.0")]   // та же версия
    [InlineData("v0.0.9")]   // старее
    public void Select_NotNewer_IsNull(string tag) => Assert.Null(UpdatePlanner.Select(ReleaseInfo.TryParse(ReleaseJson(tag)), V010));

    [Fact]
    public void Select_DraftOrPrerelease_IsNull()
    {
        Assert.Null(UpdatePlanner.Select(ReleaseInfo.TryParse(ReleaseJson("v0.2.0", draft: true)), V010));
        Assert.Null(UpdatePlanner.Select(ReleaseInfo.TryParse(ReleaseJson("v0.2.0", pre: true)), V010));
    }

    [Theory]
    [InlineData("https://evil.example/aintlikeu/shadepane/releases/download/v0.2.0/Shadepane-0.2.0-win-x64-setup.exe")]
    [InlineData("http://github.com/aintlikeu/shadepane/releases/download/v0.2.0/Shadepane-0.2.0-win-x64-setup.exe")]
    [InlineData("https://github.com/someone/shadepane/releases/download/v0.2.0/Shadepane-0.2.0-win-x64-setup.exe")]
    [InlineData("https://github.com/aintlikeu/shadepane/releases/download/v9.9.9/Shadepane-0.2.0-win-x64-setup.exe")]
    [InlineData("https://github.com:8443/aintlikeu/shadepane/releases/download/v0.2.0/Shadepane-0.2.0-win-x64-setup.exe")]
    [InlineData("https://user@github.com/aintlikeu/shadepane/releases/download/v0.2.0/Shadepane-0.2.0-win-x64-setup.exe")]
    [InlineData("https://github.com/aintlikeu/shadepane/releases/download/v0.2.0/Shadepane-0.2.0-win-x64-setup.exe?x=1")]
    public void Select_ForeignInstallerUrl_IsNull(string url) =>
        Assert.Null(UpdatePlanner.Select(ReleaseInfo.TryParse(ReleaseJson("v0.2.0", installerUrl: url)), V010));

    [Fact]
    public void Select_ForeignChecksumsUrl_IsNull() =>
        Assert.Null(UpdatePlanner.Select(ReleaseInfo.TryParse(ReleaseJson("v0.2.0",
            sumsUrl: "https://gist.githubusercontent.com/x/SHA256SUMS.txt")), V010));

    [Fact]
    public void Select_MissingAssets_IsNull()
    {
        var json = """{ "tag_name": "v0.2.0", "html_url": "x", "draft": false, "prerelease": false, "assets": [] }""";
        Assert.Null(UpdatePlanner.Select(ReleaseInfo.TryParse(json), V010));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public void ReleaseInfo_Garbage_IsNull(string json) => Assert.Null(ReleaseInfo.TryParse(json));

    [Fact]
    public void Sha256Sums_ParseAndVerify()
    {
        var data = "hello"u8.ToArray();
        var hash = Sha256Sums.Compute(new MemoryStream(data));
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);

        var sums = Sha256Sums.Parse($"{hash.ToUpperInvariant()}  Shadepane-0.2.0-win-x64-setup.exe\r\n" +
                                    $"{new string('0', 64)} *Shadepane-0.2.0-win-x64.zip\n garbage line\n");
        Assert.Equal(hash, sums["Shadepane-0.2.0-win-x64-setup.exe"]);
        Assert.True(sums.ContainsKey("Shadepane-0.2.0-win-x64.zip"));
        Assert.Equal(2, sums.Count);
        Assert.True(Sha256Sums.Matches(new MemoryStream(data), sums["Shadepane-0.2.0-win-x64-setup.exe"]));
        Assert.False(Sha256Sums.Matches(new MemoryStream("tampered"u8.ToArray()), hash));
    }
}
