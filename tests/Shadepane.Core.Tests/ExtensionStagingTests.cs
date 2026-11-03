using Shadepane.Core.Extensions;

namespace Shadepane.Core.Tests;

public class ExtensionStagingTests
{
    [Fact]
    public void Stage_Good_CopiesIntoHashNamedFolder()
    {
        using var tmp = new TempDir();
        var src = Ext.Create(tmp.Sub("src"), Ext.Mv3("""
            "permissions": ["storage"]
            """), ("popup.html", "<p>hi</p>"));
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));

        var result = staging.Stage(src, Ext.CorpPolicy);

        Assert.True(result.Validation.IsAllowed);
        var staged = result.Staged!;
        Assert.StartsWith(staging.Root, staged.Path);
        Assert.False(Path.GetFileName(staged.Path).StartsWith('_')); // ограничение WebView2
        Assert.Equal(ExtensionDirectory.ComputeHash(src), staged.Hash);
        Assert.Single(Directory.GetDirectories(staging.Root));      // временной папки не осталось
    }

    [Fact]
    public void Stage_Rejected_LeavesNothingBehind()
    {
        using var tmp = new TempDir();
        var src = Ext.Create(tmp.Sub("src"), Ext.Mv3("""
            "permissions": ["tabCapture"]
            """));
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));

        var result = staging.Stage(src, Ext.CorpPolicy);

        Assert.Null(result.Staged);
        Assert.False(result.Validation.IsAllowed);
        Assert.Empty(Directory.GetDirectories(staging.Root));
    }

    [Fact]
    public void Stage_MalformedManifest_ThrowsAndLeavesNothingBehind()
    {
        using var tmp = new TempDir();
        var src = Ext.Create(tmp.Sub("src"), "{ broken");
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));

        Assert.Throws<ManifestException>(() => staging.Stage(src, Ext.CorpPolicy));
        Assert.Empty(Directory.GetDirectories(staging.Root));
    }

    [Fact]
    public void Stage_SameContentTwice_ReusesFolder()
    {
        using var tmp = new TempDir();
        var src = Ext.Create(tmp.Sub("src"), Ext.Mv3());
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));

        var first = staging.Stage(src, Ext.CorpPolicy).Staged!;
        var second = staging.Stage(src, Ext.CorpPolicy).Staged!;

        Assert.Equal(first.Path, second.Path);
        Assert.Single(Directory.GetDirectories(staging.Root));
    }

    [Fact]
    public void Stage_FromStagingRoot_IsRejected()
    {
        using var tmp = new TempDir();
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));
        var inside = Ext.Create(Path.Combine(staging.Root, "x"), Ext.Mv3());
        Assert.Throws<ExtensionDirectoryException>(() => staging.Stage(inside, Ext.CorpPolicy));
    }

    [Fact]
    public void Verify_DetectsTampering()
    {
        using var tmp = new TempDir();
        var src = Ext.Create(tmp.Sub("src"), Ext.Mv3(), ("bg.js", "ok"));
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));
        var staged = staging.Stage(src, Ext.CorpPolicy).Staged!;

        Assert.True(staging.Verify(staged.Path, staged.Hash, Ext.CorpPolicy).IsAllowed);

        File.WriteAllText(Path.Combine(staged.Path, "bg.js"), "evil()");
        Assert.Contains(staging.Verify(staged.Path, staged.Hash, Ext.CorpPolicy).Violations, v => v.Code == "tampered");
    }

    [Fact]
    public void Verify_AppliesCurrentPolicy()
    {
        using var tmp = new TempDir();
        var src = Ext.Create(tmp.Sub("src"), Ext.Mv3("""
            "host_permissions": ["https://wiki.newcorp.example/*"]
            """));
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));
        var staged = staging.Stage(src, Ext.CorpPolicy).Staged!;

        // Администратор добавил домен в защищённые — ранее одобренное расширение должно отозваться.
        var stricter = new Policy.BrowserPolicy { ProtectedHosts = ["*.newcorp.example"] }.EnsureValid();
        Assert.Contains(staging.Verify(staged.Path, staged.Hash, stricter).Violations, v => v.Code == "protected-host");
    }

    [Fact]
    public void Verify_RejectsPathOutsideStaging()
    {
        using var tmp = new TempDir();
        var src = Ext.Create(tmp.Sub("src"), Ext.Mv3());
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));
        Assert.Contains(staging.Verify(src, ExtensionDirectory.ComputeHash(src), Ext.CorpPolicy).Violations, v => v.Code == "staging-path");
    }

    [Fact]
    public void DeleteUnreferenced_KeepsOnlyReferenced()
    {
        using var tmp = new TempDir();
        var staging = new ExtensionStaging(Path.Combine(tmp.Path, "staging"));
        var keep = staging.Stage(Ext.Create(tmp.Sub("a"), Ext.Mv3(), ("a", "1")), Ext.CorpPolicy).Staged!;
        staging.Stage(Ext.Create(tmp.Sub("b"), Ext.Mv3(), ("b", "2")), Ext.CorpPolicy);

        Assert.Empty(staging.DeleteUnreferenced([keep.Path]));
        Assert.Equal([keep.Path], Directory.GetDirectories(staging.Root));
    }
}
