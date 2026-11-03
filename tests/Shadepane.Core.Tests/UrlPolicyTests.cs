using Shadepane.Core.Navigation;

namespace Shadepane.Core.Tests;

public class UrlPolicyTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  https://example.com/a?b=1  ", "https://example.com/a?b=1")]
    [InlineData("http://intranet", "http://intranet/")]
    [InlineData("localhost:3000", "https://localhost:3000/")]
    public void UserInput_IsNormalized(string input, string expected)
    {
        Assert.True(UrlPolicy.TryNormalizeUserInput(input, out var uri, out var error), error);
        Assert.Equal(expected, uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("chrome-extension://abc/popup.html")]
    [InlineData("ftp://example.com")]
    [InlineData("https://corp.example@evil.example")]
    [InlineData("mailto:a@b.example")]
    [InlineData("exa mple.com")]
    [InlineData("https://")]
    public void UserInput_Dangerous_IsRejected(string input) =>
        Assert.False(UrlPolicy.TryNormalizeUserInput(input, out _, out _));

    [Fact]
    public void UserInput_TooLong_IsRejected() =>
        Assert.False(UrlPolicy.TryNormalizeUserInput("example.com/" + new string('a', UrlPolicy.MaxInputLength), out _, out _));

    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("about:blank", true)]
    [InlineData("data:text/html,x", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://a@b.example/", false)]
    [InlineData(null, false)]
    public void TopLevelNavigation(string? url, bool allowed) =>
        Assert.Equal(allowed, UrlPolicy.IsAllowedTopLevelNavigation(url));

    [Theory]
    [InlineData("chrome-extension://abcdef/popup.html", true)]
    [InlineData("chrome-extension://ABCDEF/popup.html", true)]
    [InlineData("chrome-extension://other/popup.html", false)]
    [InlineData("https://abcdef/popup.html", false)]
    public void PopupNavigation(string url, bool allowed) =>
        Assert.Equal(allowed, UrlPolicy.IsAllowedExtensionPopupNavigation(url, "abcdef"));

    [Fact]
    public void BuildExtensionUrl_ValidatesId()
    {
        Assert.Equal("chrome-extension://abcdef/ui/popup.html?x=1", UrlPolicy.BuildExtensionUrl("abcdef", "/ui/popup.html?x=1").ToString());
        Assert.Throws<ArgumentException>(() => UrlPolicy.BuildExtensionUrl("evil.com/x", "p.html"));
    }
}
