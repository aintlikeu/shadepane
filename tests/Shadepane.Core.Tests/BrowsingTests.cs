using Shadepane.Core.Browsing;

namespace Shadepane.Core.Tests;

public class ZoomLevelsTests
{
    [Theory]
    [InlineData(1.0, 1.1)]
    [InlineData(1.05, 1.1)]   // между ступенями — к следующей
    [InlineData(4.9, 5.0)]
    [InlineData(5.0, 5.0)]    // предел
    [InlineData(0.1, 0.25)]
    public void ZoomIn(double current, double expected) => Assert.Equal(expected, ZoomLevels.ZoomIn(current));

    [Theory]
    [InlineData(1.0, 0.9)]
    [InlineData(1.05, 1.0)]
    [InlineData(0.25, 0.25)]  // предел
    [InlineData(9.0, 5.0)]
    public void ZoomOut(double current, double expected) => Assert.Equal(expected, ZoomLevels.ZoomOut(current));

    [Fact]
    public void Format() => Assert.Equal("125%", ZoomLevels.Format(1.25));
}

public class SiteZoomTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1", "example.com")]
    [InlineData("http://Example.COM:8080/", "example.com")]      // регистр и порт не важны
    [InlineData("https://docs.example.com/", "docs.example.com")] // поддомен — отдельный сайт
    [InlineData("https://пример.рф/", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("about:blank", null)]
    [InlineData("file:///C:/a.html", null)]
    [InlineData("chrome-extension://abc/popup.html", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void KeyOf(string? url, string? expected) => Assert.Equal(expected, SiteZoom.KeyOf(url));

    [Fact]
    public void RemembersPerSite_AcrossPages()
    {
        var zoom = new SiteZoom();
        Assert.True(zoom.Set("https://example.com/first", 1.5));
        Assert.Equal(1.5, zoom.Get("https://example.com/second#x"));
        Assert.Equal(ZoomLevels.Default, zoom.Get("https://other.example/"));
        Assert.Equal(ZoomLevels.Default, zoom.Get("https://docs.example.com/"));
    }

    [Fact]
    public void Changed_OnlyWhenValueChanges()
    {
        var zoom = new SiteZoom();
        var events = new List<(string, double)>();
        zoom.Changed += (host, factor) => events.Add((host, factor));

        zoom.Set("https://example.com/", 1.25);
        zoom.Set("https://example.com/other", 1.25); // тот же масштаб — без события
        zoom.Set("https://example.com/", 1.0);       // 100% удаляет запись, но о смене сообщает
        zoom.Set("https://example.com/", 1.0);

        Assert.Equal([("example.com", 1.25), ("example.com", 1.0)], events);
        Assert.Equal(ZoomLevels.Default, zoom.Get("https://example.com/"));
    }

    [Theory]
    [InlineData("about:blank", 1.5)]
    [InlineData(null, 1.5)]
    [InlineData("https://example.com/", 0)]
    [InlineData("https://example.com/", -1)]
    [InlineData("https://example.com/", double.NaN)]
    [InlineData("https://example.com/", double.PositiveInfinity)]
    public void Set_IgnoresPagesWithoutSiteAndBadFactors(string? url, double factor)
    {
        var zoom = new SiteZoom();
        var raised = false;
        zoom.Changed += (_, _) => raised = true;
        Assert.False(zoom.Set(url, factor));
        Assert.False(raised);
        Assert.Equal(ZoomLevels.Default, zoom.Get("https://example.com/"));
    }
}

public class BrowserShortcutsTests
{
    [Theory]
    [InlineData("T", true, false, false, BrowserCommand.NewTab)]
    [InlineData("W", true, false, false, BrowserCommand.CloseTab)]
    [InlineData("F4", true, false, false, BrowserCommand.CloseTab)]
    [InlineData("Tab", true, false, false, BrowserCommand.NextTab)]
    [InlineData("Tab", true, true, false, BrowserCommand.PreviousTab)]
    [InlineData("PageUp", true, false, false, BrowserCommand.PreviousTab)]
    [InlineData("L", true, false, false, BrowserCommand.FocusAddressBar)]
    [InlineData("D", false, false, true, BrowserCommand.FocusAddressBar)]
    [InlineData("F6", false, false, false, BrowserCommand.FocusAddressBar)]
    [InlineData("F5", false, false, false, BrowserCommand.Reload)]
    [InlineData("R", true, false, false, BrowserCommand.Reload)]
    [InlineData("Escape", false, false, false, BrowserCommand.Stop)]
    [InlineData("Left", false, false, true, BrowserCommand.Back)]
    [InlineData("Right", false, false, true, BrowserCommand.Forward)]
    [InlineData("Home", false, false, true, BrowserCommand.Home)]
    [InlineData("F", true, false, false, BrowserCommand.Find)]
    [InlineData("F3", false, false, false, BrowserCommand.FindNext)]
    [InlineData("F3", false, true, false, BrowserCommand.FindPrevious)]
    [InlineData("OemPlus", true, false, false, BrowserCommand.ZoomIn)]
    [InlineData("OemPlus", true, true, false, BrowserCommand.ZoomIn)]
    [InlineData("Add", true, false, false, BrowserCommand.ZoomIn)]
    [InlineData("OemMinus", true, false, false, BrowserCommand.ZoomOut)]
    [InlineData("D0", true, false, false, BrowserCommand.ZoomReset)]
    [InlineData("BrowserBack", false, false, false, BrowserCommand.Back)]
    public void Resolve(string key, bool ctrl, bool shift, bool alt, BrowserCommand expected) =>
        Assert.Equal(expected, BrowserShortcuts.Resolve(key, ctrl, shift, alt)?.Command);

    [Theory]
    [InlineData("D1", 1)]
    [InlineData("D8", 8)]
    [InlineData("D9", 9)]
    [InlineData("NumPad3", 3)]
    public void SelectTab(string key, int number)
    {
        var shortcut = BrowserShortcuts.Resolve(key, ctrl: true, shift: false, alt: false);
        Assert.Equal(BrowserCommand.SelectTab, shortcut?.Command);
        Assert.Equal(number, shortcut?.TabNumber);
    }

    [Theory]
    [InlineData("P", true, false, false)]      // печать — нет
    [InlineData("S", true, false, false)]      // сохранение — нет
    [InlineData("I", true, true, false)]       // DevTools — нет
    [InlineData("U", true, false, false)]      // просмотр кода — нет
    [InlineData("F12", false, false, false)]
    [InlineData("C", true, false, false)]      // копирование — не наша забота, обрабатывает поле/страница
    [InlineData("T", true, false, true)]       // Ctrl+Alt+T — не наше
    [InlineData("D0", false, false, false)]
    public void NotABrowserShortcut(string key, bool ctrl, bool shift, bool alt) =>
        Assert.Null(BrowserShortcuts.Resolve(key, ctrl, shift, alt));
}
