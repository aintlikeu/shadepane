using Microsoft.Web.WebView2.Core;
using Shadepane.Core.Navigation;
using Shadepane.Core.Policy;

namespace Shadepane.App.Browser;

/// <summary>
/// Единые ограничения для КАЖДОГО CoreWebView2 (каждая вкладка и popup расширений):
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

    /// <summary>
    /// Решает, куда открыть разрешённое новое окно (window.open, target=_blank, Ctrl+клик).
    /// Обработчик ОБЯЗАН выставить e.Handled = true или e.NewWindow — иначе WebView2 создаст своё окно,
    /// на которое мы не можем поставить флаг защиты.
    /// </summary>
    public delegate void NewWindowHandler(CoreWebView2NewWindowRequestedEventArgs e);

    /// <param name="onNewWindow">Куда открыть новое окно, если адрес разрешён политикой.</param>
    public static async Task ApplyAsync(CoreWebView2 core, string role, NewWindowHandler onNewWindow)
    {
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

        // Загрузки: запрет (политика или настройка) — отмена; иначе встроенное окно WebView2 скрываем
        // и показываем загрузку в своём окне загрузок (Ctrl+J).
        // Было ли в ТЕКУЩЕМ документе вкладки содержимое с защищённого домена: сама страница, любое
        // перенаправление по пути к ней, любой фрейм. Сбрасывается только новой обычной навигацией верхнего уровня.
        var protectedContentSeen = false;
        core.NavigationStarting += (_, e) =>
        {
            var isProtected = AppServices.Policy.IsProtectedUrl(e.Uri);
            protectedContentSeen = e.IsRedirected ? protectedContentSeen || isProtected : isProtected;
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (AppServices.Policy.IsProtectedUrl(e.Uri))
                protectedContentSeen = true;
        };

        core.DownloadStarting += (_, e) =>
        {
            var config = AppServices.Config;
            var reason = DownloadGate.BlockReason(AppServices.Policy, config.AllowDownloads, config.DownloadsLocked,
                fromExtensionPopup: role.StartsWith("popup:", StringComparison.Ordinal),
                tabShowedProtectedContent: protectedContentSeen, core.Source, e.DownloadOperation.Uri);
            if (reason is not null)
            {
                e.Cancel = true;
                audit.Write("download-blocked", new { role, uri = e.DownloadOperation.Uri, source = core.Source, reason });
                return;
            }
            e.Handled = true;
            AppServices.Downloads.Track(e.DownloadOperation);
            audit.Write("download-started", new { role, uri = e.DownloadOperation.Uri, file = e.ResultFilePath });
        };

        // Без этого WebView2 создаст СВОЁ окно, на которое мы не можем поставить флаг защиты.
        // about:blank разрешён: так открываются окна входа (OAuth), которые потом переходят на сайт —
        // этот переход проверит NavigationStarting новой вкладки.
        core.NewWindowRequested += (_, e) =>
        {
            if (!UrlPolicy.IsAllowedTopLevelNavigation(e.Uri))
            {
                e.Handled = true;
                audit.Write("new-window-blocked", new { role, uri = e.Uri });
                return;
            }
            onNewWindow(e);
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
