using Shadepane.Core.Hosts;

namespace Shadepane.Core.Tests;

public class HostPatternTests
{
    [Theory]
    [InlineData("crm.corp.example", "crm.corp.example", true)]
    [InlineData("crm.corp.example", "mail.corp.example", false)]
    [InlineData("*.corp.example", "crm.corp.example", true)]
    [InlineData("*.corp.example", "corp.example", true)]
    [InlineData("*.corp.example", "evilcorp.example", false)]   // суффикс без точки — другой домен
    [InlineData("*.example", "*.corp.example", true)]           // родительский wildcard накрывает защищённый
    [InlineData("*.a.corp.example", "*.corp.example", true)]    // вложенный wildcard
    [InlineData("*.other.example", "*.corp.example", false)]
    [InlineData("*", "crm.corp.example", true)]
    [InlineData("CRM.Corp.Example.", "crm.corp.example", true)] // регистр и завершающая точка
    public void Overlaps_IsCorrectAndSymmetric(string a, string b, bool expected)
    {
        var pa = HostPattern.Parse(a);
        var pb = HostPattern.Parse(b);
        Assert.Equal(expected, pa.Overlaps(pb));
        Assert.Equal(expected, pb.Overlaps(pa));
    }

    [Fact]
    public void Idn_IsNormalizedToPunycode()
    {
        var unicode = HostPattern.Parse("*.пример.рф");
        var puny = HostPattern.Parse("crm.xn--e1afmkfd.xn--p1ai");
        Assert.True(unicode.Overlaps(puny));
    }

    [Theory]
    [InlineData("")]
    [InlineData("foo*.example")]
    [InlineData("*foo.example")]
    [InlineData("user@host.example")]
    [InlineData("host.example/path")]
    [InlineData("[not-ipv6]")]
    public void Invalid_IsRejected(string raw) => Assert.False(HostPattern.TryParse(raw, out _, out _));

    [Fact]
    public void Ipv6_IsNormalized() =>
        Assert.True(HostPattern.Parse("[0:0:0:0:0:0:0:1]").Overlaps(HostPattern.Parse("[::1]")));
}
