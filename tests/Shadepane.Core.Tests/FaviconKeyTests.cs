using Shadepane.Core.Bookmarks;

namespace Shadepane.Core.Tests;

public class FaviconKeyTests
{
    [Theory]
    [InlineData("https://wiki.corp.example/a/b?x=1", "http://wiki.corp.example/")]   // схема и путь не важны
    [InlineData("https://Wiki.Corp.Example/", "https://wiki.corp.example/other")]    // регистр хоста не важен
    [InlineData("https://пример.рф/", "https://xn--e1afmkfd.xn--p1ai/x")]          // IDN и punycode — один сайт
    [InlineData("https://example.com:443/", "https://example.com/")]                 // стандартный порт
    public void SameSite(string a, string b) => Assert.True(FaviconKey.SameSite(a, b));

    [Theory]
    [InlineData("https://example.com/", "https://www.example.com/")]
    [InlineData("https://example.com/", "https://example.com:8443/")]
    [InlineData("https://bank.example/", "https://bank.example.evil.test/")]
    public void DifferentSites(string a, string b) => Assert.False(FaviconKey.SameSite(a, b));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank")]
    [InlineData("data:text/html,x")]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html")]
    public void NonWeb_HasNoIcon(string? url) => Assert.Null(FaviconKey.FileNameFor(url));

    [Fact]
    public void FileName_IsHexPng_AndHidesTheHost()
    {
        var name = FaviconKey.FileNameFor("https://wiki.corp.example/")!;
        Assert.Matches("^[0-9a-f]{32}\\.png$", name);
        Assert.DoesNotContain("corp", name);
    }
}
