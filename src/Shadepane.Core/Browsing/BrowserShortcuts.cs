namespace Shadepane.Core.Browsing;

public enum BrowserCommand
{
    NewTab,
    CloseTab,
    NextTab,
    PreviousTab,
    SelectTab,
    FocusAddressBar,
    Reload,
    Stop,
    Back,
    Forward,
    Home,
    Find,
    FindNext,
    FindPrevious,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    History,
    ToggleBookmark,
    BookmarksManager,
}

/// <param name="TabNumber">Для SelectTab: 1–8 — вкладка по номеру, 9 — последняя.</param>
public sealed record BrowserShortcut(BrowserCommand Command, int TabNumber = 0);

/// <summary>
/// Горячие клавиши браузера. Встроенные клавиши WebView2 отключены целиком ради защиты
/// (AreBrowserAcceleratorKeysEnabled=false), поэтому безопасные возвращаем сами. Печать, сохранение,
/// DevTools и просмотр кода здесь намеренно отсутствуют.
/// Ключи — имена клавиш WPF (System.Windows.Input.Key.ToString()), чтобы таблицу можно было тестировать без WPF.
/// </summary>
public static class BrowserShortcuts
{
    public static BrowserShortcut? Resolve(string key, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && !alt)
        {
            if (!shift)
            {
                switch (key)
                {
                    case "T": return new(BrowserCommand.NewTab);
                    case "W" or "F4": return new(BrowserCommand.CloseTab);
                    case "Tab" or "PageDown": return new(BrowserCommand.NextTab);
                    case "PageUp": return new(BrowserCommand.PreviousTab);
                    case "L": return new(BrowserCommand.FocusAddressBar);
                    case "R": return new(BrowserCommand.Reload);
                    case "F": return new(BrowserCommand.Find);
                    case "D0" or "NumPad0": return new(BrowserCommand.ZoomReset);
                    case "H": return new(BrowserCommand.History);
                    case "D": return new(BrowserCommand.ToggleBookmark);
                }
                var number = TabNumber(key);
                if (number > 0)
                    return new(BrowserCommand.SelectTab, number);
            }
            else
            {
                switch (key)
                {
                    case "Tab": return new(BrowserCommand.PreviousTab);
                    case "O": return new(BrowserCommand.BookmarksManager);
                }
            }

            // Ctrl+= на большинстве раскладок — это OemPlus без Shift; Ctrl+Shift+= тоже приближает.
            switch (key)
            {
                case "OemPlus" or "Add": return new(BrowserCommand.ZoomIn);
                case "OemMinus" or "Subtract": return new(BrowserCommand.ZoomOut);
            }
            return null;
        }

        if (alt && !ctrl && !shift)
        {
            return key switch
            {
                "Left" => new(BrowserCommand.Back),
                "Right" => new(BrowserCommand.Forward),
                "Home" => new(BrowserCommand.Home),
                "D" => new(BrowserCommand.FocusAddressBar),
                _ => null,
            };
        }

        if (!ctrl && !alt)
        {
            return (key, shift) switch
            {
                ("F5", false) => new(BrowserCommand.Reload),
                ("F6", false) => new(BrowserCommand.FocusAddressBar),
                ("F3", false) => new(BrowserCommand.FindNext),
                ("F3", true) => new(BrowserCommand.FindPrevious),
                ("Escape", false) => new(BrowserCommand.Stop),
                ("BrowserBack", _) => new(BrowserCommand.Back),
                ("BrowserForward", _) => new(BrowserCommand.Forward),
                ("BrowserRefresh", _) => new(BrowserCommand.Reload),
                ("BrowserHome", _) => new(BrowserCommand.Home),
                _ => null,
            };
        }

        return null;
    }

    private static int TabNumber(string key)
    {
        // "D1".."D9" — цифры над буквами, "NumPad1".."NumPad9" — цифровой блок.
        var digit = key.StartsWith("NumPad", StringComparison.Ordinal) ? key[6..] : key.StartsWith('D') ? key[1..] : "";
        return digit.Length == 1 && digit[0] is >= '1' and <= '9' ? digit[0] - '0' : 0;
    }
}
