using Microsoft.Web.WebView2.Core;
using Shadepane.Core.Navigation;

namespace Shadepane.App.Browser;

/// <summary>
/// Единые ограничения для КАЖДОГО CoreWebView2 (главное окно и popup расширений):
/// закрытые каналы утечки внутри браузера, о которых флаг окна ничего не знает.
/// </summary>
internal static class WebViewHardening
{
    // Контекстное меню — БЕЛЫЙ список: на стенде (Runtime 156) встроенный просмотрщик PDF назвал пункт
    // «Сохранение» не "saveAs", а "other" — чёрный список имён его пропустил. Оставляем только известные
    // безопасные пункты и разделители; всё прочее (включая "other": повороты PDF, возможно подсказки
    // орфографии) удаляется. Копирование оставлено — контроль буфера обмена будет отдельной функцией.
    private static readonly HashSet<string> AllowedContextMenuItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "back", "forward", "reload",
        "cut", "copy", "paste", "pasteAndMatchStyle", "selectAll", "undo", "redo",
        "copyLinkLocation", "copyLinkText", "copyImageLocation", "emoji",
    };

    private const string BlockPrintScript = """
        (() => {
          try {
            const blocked = function print() { console.warn('[Shadepane] печать отключена политикой'); };
            Object.defineProperty(window, 'print', { value: blocked, writable: false, configurable: false });
          } catch (_) { }
        })();
        """;

    /// <param name="openInMainView">Куда отправлять http(s)-ссылки, которые страница пытается открыть в новом окне.</param>
    public static async Task ApplyAsync(CoreWebView2 core, string role, Action<Uri> openInMainView)
    {
        var policy = AppServices.Policy;
        var audit = AppServices.Audit;

        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        // Отключает Ctrl+P, Ctrl+S, F12, Ctrl+Shift+I, Ctrl+F, F5 и т.п. Ctrl+C/Ctrl+V этим НЕ отключаются.
        settings.AreBrowserAcceleratorKeysEnabled = false;
        // Корпоративные данные не должны оседать в профиле в виде автозаполнения и сохранённых паролей.
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        // Встроенный просмотрщик PDF (компонент Runtime) имеет свои кнопки сохранения и печати —
        // наши блокировки window.print и горячих клавиш их не касаются.
        settings.HiddenPdfToolbarItems =
            CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.SaveAs | CoreWebView2PdfToolbarItems.Print;

        // getDisplayMedia() из страницы или расширения.
        core.ScreenCaptureStarting += (_, e) =>
        {
            e.Cancel = true;
            audit.Write("screen-capture-blocked", new { role, source = core.Source });
        };

        core.DownloadStarting += (_, e) =>
        {
            if (policy.AllowDownloads)
                return;
            e.Cancel = true;
            audit.Write("download-blocked", new { role, uri = e.DownloadOperation.Uri });
        };

        // Без этого WebView2 создаст СВОЁ окно, на которое мы не можем поставить флаг защиты.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (UrlPolicy.IsAllowedTopLevelNavigation(e.Uri) && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && UrlPolicy.IsWebScheme(uri))
                openInMainView(uri);
            else
                audit.Write("new-window-blocked", new { role, uri = e.Uri });
        };

        core.ContextMenuRequested += (_, e) =>
        {
#if DEBUG
            // Для сверки пунктов меню на стенде (docs/SPIKE.md, шаг 6): имя, подпись, CommandId, подменю.
            audit.Write("debug-context-menu", new { role, items = e.MenuItems.Select(Describe).ToArray() });
#endif
            FilterContextMenu(e.MenuItems);
        };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(BlockPrintScript);
    }

    /// <summary>Оставляет разрешённые пункты и разделители между ними (без висячих и двойных разделителей).</summary>
    private static void FilterContextMenu(IList<CoreWebView2ContextMenuItem> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            var keep = item.Kind == CoreWebView2ContextMenuItemKind.Separator
                || (item.Kind != CoreWebView2ContextMenuItemKind.Submenu && AllowedContextMenuItems.Contains(item.Name));
            if (!keep)
                items.RemoveAt(i);
        }

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Kind != CoreWebView2ContextMenuItemKind.Separator)
                continue;
            var isEdge = i == 0 || i == items.Count - 1;
            var isDouble = i > 0 && items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator;
            if (isEdge || isDouble)
                items.RemoveAt(i);
        }
    }

    /// <summary>
    /// Ctrl+S / Ctrl+P / Ctrl+Shift+S внутри страницы. AreBrowserAcceleratorKeysEnabled=false отключает их
    /// у браузера, но встроенный просмотрщик PDF обрабатывает Ctrl+S сам (на стенде — окно сохранения;
    /// файл не сохранился только благодаря запрету загрузок). WPF-контрол WebView2 пересылает
    /// клавиши-ускорители в PreviewKeyDown; Handled=true должен отменить действие — НЕ ПРОВЕРЕНО для PDF.
    /// </summary>
    public static void BlockLeakShortcuts(Microsoft.Web.WebView2.Wpf.WebView2 control, string role)
    {
        control.PreviewKeyDown += (_, e) =>
        {
            var modifiers = System.Windows.Input.Keyboard.Modifiers;
            var ctrlOnly = modifiers == System.Windows.Input.ModifierKeys.Control
                || modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);
            if (ctrlOnly && e.Key is System.Windows.Input.Key.S or System.Windows.Input.Key.P)
            {
                e.Handled = true;
                AppServices.Audit.Write("shortcut-blocked", new { role, key = $"{modifiers}+{e.Key}" });
            }
        };
    }

#if DEBUG
    private static object Describe(CoreWebView2ContextMenuItem item) => new
    {
        item.Name,
        item.Label,
        item.CommandId,
        kind = item.Kind.ToString(),
        children = item.Children.Count > 0 ? item.Children.Select(Describe).ToArray() : null,
    };
#endif
}
