using Microsoft.Web.WebView2.Core;
using Shadepane.Core.Extensions;
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
    public delegate void NewWindowHandler(CoreWebView2 opener, CoreWebView2NewWindowRequestedEventArgs e);

    /// <summary>
    /// Было ли содержимое с защищённого домена в документе этого WebView (для DownloadGate).
    /// Committed — текущий показанный документ; Pending — идущая навигация. Пока навигация не завершилась
    /// успешно, на экране может оставаться старый документ, поэтому действуют оба значения.
    /// </summary>
    private sealed class ProtectionState
    {
        public bool Committed;
        public bool Pending;
        public bool Effective => Committed || Pending;

        private DateTime _lastUnresponsiveLog = DateTime.MinValue;

        public bool ShouldLogUnresponsive()
        {
            var now = DateTime.UtcNow;
            if (now - _lastUnresponsiveLog < TimeSpan.FromMinutes(1))
                return false;
            _lastUnresponsiveLog = now;
            return true;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CoreWebView2, ProtectionState> States = new();

    /// <summary>Страница запросила захват экрана (getDisplayMedia), запрос отклонён — для индикатора защиты.</summary>
    public static event Action<CoreWebView2>? ScreenCaptureBlocked;

    /// <summary>Изменилась отметка «показано содержимое защищённого домена» — для индикатора защиты.</summary>
    public static event Action<CoreWebView2>? ProtectedContentChanged;

    /// <summary>
    /// Вкладка начала скачивать пакет расширения из Chrome Web Store (кнопка «Установить»). Загрузка отменена;
    /// аргумент — ID расширения (из адреса пакета или страницы магазина) или null, если его не удалось определить.
    /// </summary>
    public static event Action<CoreWebView2, string?>? WebStoreInstallRequested;

    /// <summary>
    /// Сбой процесса движка (уже записан в журнал). BrowserProcessExited — все WebView окружения закрыты, их нужно
    /// создать заново (MainWindow.RecoverEngineAsync); остальные виды WebView2 восстанавливает сам или касаются одной страницы.
    /// </summary>
    public static event Action<CoreWebView2, CoreWebView2ProcessFailedKind>? EngineProcessFailed;

    /// <summary>В показанном сейчас документе есть содержимое с домена из protectedHosts (сама страница или фрейм).</summary>
    public static bool ShowsProtectedContent(CoreWebView2 core) => States.TryGetValue(core, out var state) && state.Committed;

    /// <summary>Новое окно, открытое защищённым документом, наследует его отметку (window.open + запись в окно).</summary>
    public static void InheritProtection(CoreWebView2 opener, CoreWebView2 child)
    {
        if (States.TryGetValue(opener, out var from) && from.Effective)
            States.GetOrCreateValue(child).Committed = true;
    }

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
            ScreenCaptureBlocked?.Invoke(core);
        };

        // Загрузки: запрет (политика или настройка) — отмена; иначе встроенное окно WebView2 скрываем
        // и показываем загрузку в своём окне загрузок (Ctrl+J).
        // Было ли в документе содержимое с защищённого домена: сама страница, любое перенаправление по пути
        // к ней, любой фрейм. Новое значение вступает в силу только после УСПЕШНОГО завершения навигации:
        // отменённая/неудачная навигация или навигация, ставшая загрузкой, старый документ не «очищает».
        var state = States.GetOrCreateValue(core);
        core.NavigationStarting += (_, e) =>
        {
            // Навигация не по http(s) (about:blank с любым #/?, about:srcdoc, data: …) отметку не сбрасывает:
            // такой документ получает содержимое от открывшей/родительской страницы.
            var isWeb = Uri.TryCreate(e.Uri, UriKind.Absolute, out var navUri) && UrlPolicy.IsWebScheme(navUri);
            var isProtected = isWeb ? AppServices.Policy.IsProtectedUrl(e.Uri) : state.Committed;
            state.Pending = e.IsRedirected ? state.Pending || isProtected : isProtected;
        };
        core.NavigationCompleted += (_, e) =>
        {
            var before = state.Committed;
            if (e.IsSuccess)
                state.Committed = state.Pending;
            state.Pending = false;
            if (state.Committed != before)
                ProtectedContentChanged?.Invoke(core);
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (AppServices.Policy.IsProtectedUrl(e.Uri))
            {
                var before = state.Committed;
                state.Committed = true; // фрейм в текущем документе
                state.Pending = true;   // или в документе, который сейчас загружается
                if (!before)
                    ProtectedContentChanged?.Invoke(core);
            }
        };

        core.ProcessFailed += (_, e) =>
        {
            object details;
            try
            {
                details = new { role, kind = e.ProcessFailedKind.ToString(), reason = e.Reason.ToString(), exitCode = e.ExitCode, process = e.ProcessDescription };
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or NotImplementedException)
            {
                details = new { role, kind = e.ProcessFailedKind.ToString() };
            }
            // Зависшая страница сообщает об этом каждые несколько секунд — в журнал пишем, но не чаще раза в минуту.
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive || state.ShouldLogUnresponsive())
                audit.Write("process-failed", details);
            EngineProcessFailed?.Invoke(core, e.ProcessFailedKind);
        };

        core.DownloadStarting += (_, e) =>
        {
            // Пакет расширения: WebView2 его не установит (только распакованные папки), и Chromium обрывает
            // такую загрузку. Отменяем её и ставим расширение сами (MainWindow.InstallFromWebStoreAsync).
            // До DownloadGate: файл не попадает к пользователю, а установка проходит проверку политикой расширений.
            var storePageId = WebStore.ExtensionIdFromPage(core.Source);
            var isPackage = WebStore.IsPackageDownload(e.DownloadOperation.Uri, e.DownloadOperation.MimeType, out var packageId);
            // Со страницы расширения в магазине других загрузок нет, а адрес, по которому кнопка качает пакет в WebView2,
            // может отличаться от адресов Chrome, поэтому любая загрузка с такой страницы — тоже установка.
            if (isPackage || storePageId is not null)
            {
                e.Cancel = true;
                // Окно установки — только на странице этого расширения в магазине. Иначе любой сайт мог бы отправить
                // вкладку на адрес пакета и показать «Установить …?» для расширения по своему выбору (как и Chrome,
                // ставим только со страницы магазина). Окна расширений ничего не ставят.
                var refusal = !role.StartsWith("tab:", StringComparison.Ordinal) ? "not-a-tab"
                    : storePageId is null ? "not-a-store-page"
                    : packageId is not null && packageId != storePageId ? "id-mismatch"
                    : null;
                audit.Write("webstore-package-intercepted", new { role, uri = e.DownloadOperation.Uri, source = core.Source, id = storePageId, packageId, refusal });
                if (refusal is null)
                    WebStoreInstallRequested?.Invoke(core, storePageId);
                return;
            }

            var config = AppServices.Config;
            var reason = DownloadGate.BlockReason(AppServices.Policy, config.AllowDownloads, config.DownloadsLocked,
                fromExtensionPopup: role.StartsWith("popup:", StringComparison.Ordinal),
                tabShowedProtectedContent: state.Effective, core.Source, e.DownloadOperation.Uri);
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
            onNewWindow(core, e);
        };

        core.ContextMenuRequested += (_, e) =>
        {
#if DEBUG
            // Для сверки пунктов меню на стенде (docs/TESTING.md, «Каналы утечки»): имя, подпись, CommandId, подменю.
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
