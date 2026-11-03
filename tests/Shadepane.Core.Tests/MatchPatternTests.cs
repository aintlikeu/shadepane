using Shadepane.Core.Hosts;

namespace Shadepane.Core.Tests;

public class MatchPatternTests
{
    [Theory]
    [InlineData("<all_urls>", true)]
    [InlineData("*://*/*", true)]
    [InlineData("https://*/*", true)]
    [InlineData("https://*.example.com/*", false)]
    [InlineData("*://*.com/*", true)]          // вся зона .com
    [InlineData("https://*.ru/*", true)]
    [InlineData("https://*.рф/*", true)]       // IDN-зона → xn--p1ai
    [InlineData("https://*.co.uk/*", true)]
    [InlineData("https://*.com.au/*", true)]
    [InlineData("https://*.example.co.uk/*", false)]
    [InlineData("https://*.corp.example/*", false)]
    [InlineData("https://com/*", false)]       // один хост "com", не зона
    public void Broad_IsDetected(string raw, bool broad)
    {
        Assert.True(MatchPattern.TryParse(raw, out var p, out _));
        Assert.Equal(broad, p!.IsBroad);
    }

    [Theory]
    [InlineData("https://crm.corp.example:8443/*", "crm.corp.example")]
    [InlineData("HTTPS://CRM.corp.example/*", "crm.corp.example")]
    [InlineData("wss://crm.corp.example/*", "crm.corp.example")]
    [InlineData("http://[::1]:8080/*", "[::1]")]
    public void Host_IsExtracted(string raw, string host)
    {
        Assert.True(MatchPattern.TryParse(raw, out var p, out _));
        Assert.True(p!.AppliesToWeb);
        Assert.Equal(host, p.Host!.Domain);
    }

    [Theory]
    [InlineData("file:///*")]
    [InlineData("chrome://favicon/*")]
    public void NonWebSchemes_DoNotApplyToWeb(string raw)
    {
        Assert.True(MatchPattern.TryParse(raw, out var p, out _));
        Assert.False(p!.AppliesToWeb);
    }

    [Theory]
    [InlineData("example.com")]            // нет схемы
    [InlineData("gopher://example.com/*")] // неизвестная схема — fail closed
    [InlineData("https://ex*ample.com/*")]
    [InlineData("https://user@example.com/*")]
    public void Invalid_IsRejected(string raw) => Assert.False(MatchPattern.TryParse(raw, out _, out _));
}
