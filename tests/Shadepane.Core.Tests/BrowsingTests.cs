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
