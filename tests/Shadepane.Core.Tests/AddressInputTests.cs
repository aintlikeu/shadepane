using Shadepane.Core.Navigation;

namespace Shadepane.Core.Tests;

public class AddressInputTests
{
    private const string Ddg = "https://duckduckgo.com/?q={query}";

    private static AddressResolution R(string input) => AddressInput.Resolve(input, Ddg);

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("example.com/path?x=1", "https://example.com/path?x=1")]
    [InlineData("http://intranet", "http://intranet/")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("localhost", "https://localhost/")]
    [InlineData("localhost:3000", "https://localhost:3000/")]
    [InlineData("example.com:8080/a", "https://example.com:8080/a")]
    [InlineData("192.168.1.10", "https://192.168.1.10/")]
    [InlineData("about:blank", "about:blank")]
    public void Address_IsNavigated(string input, string expected)
    {
        var r = R(input);
        Assert.True(r.IsSuccess, r.Error);
        Assert.False(r.IsSearch);
        Assert.Equal(expected, r.Uri!.AbsoluteUri);
    }

    [Fact]
    public void IdnDomain_IsNavigated()
    {
        // AbsoluteUri оставляет кириллицу, punycode — в IdnHost; WebView2 принимает оба вида.
        var r = R("пример.рф");
        Assert.True(r.IsSuccess, r.Error);
        Assert.False(r.IsSearch);
        Assert.Equal("xn--e1afmkfd.xn--p1ai", r.Uri!.IdnHost);
    }

    [Theory]
    [InlineData("погода москва")]
    [InlineData("shadepane")]            // одно слово
    [InlineData("3.14")]                 // число, не домен
    [InlineData("v1.2")]
    [InlineData("C# records")]
    [InlineData("user@example.com")]     // адрес почты — искать, а не открывать user@host
    [InlineData("what is 2+2?")]
    [InlineData("myhost:8080")]          // хост без зоны — запрос
    public void Query_IsSearched(string input)
    {
        var r = R(input);
        Assert.True(r.IsSuccess, r.Error);
        Assert.True(r.IsSearch);
        Assert.StartsWith("https://duckduckgo.com/?q=", r.Uri!.AbsoluteUri);
    }

    [Fact]
    public void Query_IsEncoded()
    {
        var r = R("a&b=c #d");
        Assert.Equal("https://duckduckgo.com/?q=a%26b%3Dc%20%23d", r.Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("vbscript:msgbox")]
    [InlineData("chrome-extension://abc/x.html")]
    [InlineData("mailto:a@b.example")]
    [InlineData("view-source:https://example.com")]
    [InlineData("https://corp.example@evil.example")]
    [InlineData("")]
    [InlineData("   ")]
    public void Dangerous_IsRejected(string input)
    {
        var r = R(input);
        Assert.False(r.IsSuccess);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void ControlCharacters_AreRejected() => Assert.False(R("example.com\u0000").IsSuccess);
}

public class SearchEnginesTests
{
    [Fact]
    public void Default_IsStartpage() => Assert.Equal("startpage", SearchEngines.Default.Id);

    [Fact]
    public void AllPresets_AreValidTemplates_WithDistinctIds()
    {
        Assert.Equal(SearchEngines.All.Count, SearchEngines.All.Select(e => e.Id).Distinct().Count());
        foreach (var engine in SearchEngines.All)
        {
            Assert.True(SearchEngines.TryValidateTemplate(engine.SearchUrl, out var error), $"{engine.Id}: {error}");
            Assert.True(UrlPolicy.TryNormalizeUserInput(engine.HomeUrl, out _, out _), engine.Id);
        }
    }

    [Theory]
    [InlineData("https://search.example/?q={query}")]
    [InlineData("https://search.example/s/{query}?lang=ru")]
    public void CustomTemplate_Valid(string template) => Assert.True(SearchEngines.TryValidateTemplate(template, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://search.example/?q=")]            // нет {query}
    [InlineData("http://search.example/?q={query}")]      // не https
    [InlineData("javascript:alert('{query}')")]
    [InlineData("https://user@search.example/?q={query}")]
    [InlineData("https://{query}")]                        // хост из запроса
    [InlineData("https://{query}.search.example/")]       // поддомен из запроса
    [InlineData("https://search.example:{query}/")]       // порт из запроса
    public void CustomTemplate_Invalid(string? template) => Assert.False(SearchEngines.TryValidateTemplate(template, out _));

    [Fact]
    public void Find_IsCaseInsensitive() => Assert.Equal("startpage", SearchEngines.Find("StartPage")?.Id);
}
