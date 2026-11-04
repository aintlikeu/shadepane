using Shadepane.Core.Navigation;

namespace Shadepane.Core.Tests;

public class AddressDisplayTests
{
    [Theory]
    [InlineData("https://wiki.corp.example/product/releases?x=1#top", "", "wiki.corp.example", "/product/releases?x=1#top")]
    [InlineData("https://example.com/", "", "example.com", "")]
    [InlineData("https://example.com", "", "example.com", "")]
    [InlineData("https://example.com:8443/a", "", "example.com:8443", "/a")]
    [InlineData("http://localhost:8000/", "http://", "localhost:8000", "")]
    [InlineData("http://intranet/docs", "http://", "intranet", "/docs")]
    [InlineData("https://example.com?q=1", "", "example.com", "?q=1")]
    [InlineData("https://xn--e1afmkfd.xn--p1ai/", "", "xn--e1afmkfd.xn--p1ai", "")]
    [InlineData("https://[2001:db8::1]/x", "", "[2001:db8::1]", "/x")]
    public void WebAddress_IsSplitIntoHostAndRest(string url, string prefix, string host, string rest) =>
        Assert.Equal(new AddressParts(prefix, host, rest), AddressDisplay.Split(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank")]
    public void BlankPage_IsEmpty(string? url) => Assert.True(AddressDisplay.Split(url).IsEmpty);

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html")]
    [InlineData("data:text/html,hello")]
    [InlineData("edge://settings")]
    [InlineData("not a url")]
    public void NonWebAddress_IsShownWholeWithoutEmphasis(string url) =>
        Assert.Equal(new AddressParts("", "", url), AddressDisplay.Split(url));

    [Fact]
    public void UserInfo_IsNotEmphasizedAsHost()
    {
        // «bank.example@evil.test» — выделять «bank.example» нельзя: настоящий хост тут evil.test.
        const string url = "https://bank.example@evil.test/login";
        Assert.Equal(new AddressParts("", "", url), AddressDisplay.Split(url));
    }
}
