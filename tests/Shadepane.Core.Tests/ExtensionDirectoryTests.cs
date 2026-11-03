using Shadepane.Core.Extensions;

namespace Shadepane.Core.Tests;

public class ExtensionDirectoryTests
{
    [Fact]
    public void Hash_IsStable_AndSensitiveToContentAndNames()
    {
        using var tmp = new TempDir();
        var a = Ext.Create(tmp.Sub("a"), Ext.Mv3(), ("js/x.js", "1"));
        var b = Ext.Create(tmp.Sub("b"), Ext.Mv3(), ("js/x.js", "1"));
        Assert.Equal(ExtensionDirectory.ComputeHash(a), ExtensionDirectory.ComputeHash(b));

        File.WriteAllText(Path.Combine(b, "js", "x.js"), "2");
        Assert.NotEqual(ExtensionDirectory.ComputeHash(a), ExtensionDirectory.ComputeHash(b));

        var c = Ext.Create(tmp.Sub("c"), Ext.Mv3(), ("js/y.js", "1")); // то же содержимое, другое имя
        Assert.NotEqual(ExtensionDirectory.ComputeHash(a), ExtensionDirectory.ComputeHash(c));
    }

    [Fact]
    public void Hash_DistinguishesMovedBytesBetweenFiles()
    {
        using var tmp = new TempDir();
        var a = Ext.Create(tmp.Sub("a"), Ext.Mv3(), ("a", "xy"), ("b", ""));
        var b = Ext.Create(tmp.Sub("b"), Ext.Mv3(), ("a", "x"), ("b", "y"));
        Assert.NotEqual(ExtensionDirectory.ComputeHash(a), ExtensionDirectory.ComputeHash(b));
    }

    [SymlinkFact]
    public void SymlinkedFile_IsRejected()
    {
        using var tmp = new TempDir();
        var secret = Path.Combine(tmp.Path, "secret.txt");
        File.WriteAllText(secret, "s");
        var ext = Ext.Create(tmp.Sub("ext"), Ext.Mv3());
        File.CreateSymbolicLink(Path.Combine(ext, "link.js"), secret);
        Assert.Throws<ExtensionDirectoryException>(() => ExtensionDirectory.ListFiles(ext));
    }

    [SymlinkFact]
    public void SymlinkedDirectory_IsRejected()
    {
        using var tmp = new TempDir();
        var outside = tmp.Sub("outside");
        var ext = Ext.Create(tmp.Sub("ext"), Ext.Mv3());
        Directory.CreateSymbolicLink(Path.Combine(ext, "lib"), outside);
        Assert.Throws<ExtensionDirectoryException>(() => ExtensionDirectory.ListFiles(ext));
    }

    [WindowsFact]
    public void JunctionInsideExtension_IsRejected()
    {
        using var tmp = new TempDir();
        var outside = tmp.Sub("outside");
        var ext = Ext.Create(tmp.Sub("ext"), Ext.Mv3());
        LinkSupport.CreateJunction(Path.Combine(ext, "lib"), outside);
        Assert.Throws<ExtensionDirectoryException>(() => ExtensionDirectory.ListFiles(ext));
    }

    [WindowsFact]
    public void ExtensionRootIsJunction_IsRejected()
    {
        using var tmp = new TempDir();
        var real = Ext.Create(tmp.Sub("real"), Ext.Mv3());
        var link = Path.Combine(tmp.Path, "ext-link");
        LinkSupport.CreateJunction(link, real);
        Assert.Throws<ExtensionDirectoryException>(() => ExtensionDirectory.ListFiles(link));
    }

    [Fact]
    public void CopyTree_IntoItself_IsRejected()
    {
        using var tmp = new TempDir();
        var ext = Ext.Create(tmp.Sub("ext"), Ext.Mv3());
        Assert.Throws<ExtensionDirectoryException>(() => ExtensionDirectory.CopyTree(ext, Path.Combine(ext, "copy")));
    }

    [Theory]
    [InlineData("popup.html", "popup.html")]
    [InlineData("/popup.html", "popup.html")]
    [InlineData("ui/popup.html?tab=2#x", "ui/popup.html?tab=2#x")]
    public void ResolveInside_AcceptsValidPaths(string input, string expectedUrlPath)
    {
        using var tmp = new TempDir();
        var ext = Ext.Create(tmp.Sub("ext"), Ext.Mv3(), ("popup.html", ""), ("ui/popup.html", ""));
        Assert.True(ExtensionDirectory.TryResolveInside(ext, input, out var full, out var url));
        Assert.True(File.Exists(full));
        Assert.Equal(expectedUrlPath, url);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("ui/../../secret.txt")]
    [InlineData("ui\\popup.html")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("missing.html")]
    [InlineData("")]
    [InlineData("ui//popup.html")]
    public void ResolveInside_RejectsEscapesAndGarbage(string input)
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "secret.txt"), "s");
        var ext = Ext.Create(tmp.Sub("ext"), Ext.Mv3(), ("ui/popup.html", ""));
        Assert.False(ExtensionDirectory.TryResolveInside(ext, input, out _, out _));
    }

    [Fact]
    public void ResolveInside_RejectsAbsolutePathOutsideRoot()
    {
        using var tmp = new TempDir();
        var secret = Path.Combine(tmp.Path, "secret.txt");
        File.WriteAllText(secret, "s");
        var ext = Ext.Create(tmp.Sub("ext"), Ext.Mv3());
        Assert.False(ExtensionDirectory.TryResolveInside(ext, secret, out _, out _));
    }
}
