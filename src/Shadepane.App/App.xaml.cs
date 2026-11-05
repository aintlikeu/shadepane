using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Shadepane.App.Policy;
using Shadepane.App.Protection;
using Shadepane.Core.Audit;
using Shadepane.Core.Localization;
using Shadepane.Core.Policy;
using Shadepane.Core.Settings;

namespace Shadepane.App;

public partial class App : Application
{
    private const string Caption = "Shadepane";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Язык — первым: на нём уже и сообщение о битой политике. Настройки читаются ещё раз в AppServices.Initialize.
        Loc.Current = LanguageChoice.Resolve(AppServices.SettingsStore.Load(out _).Language,
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

        var audit = new AuditLog(AppPaths.AuditLog);
        DispatcherUnhandledException += (_, args) => OnUnhandled(audit, args);

        BrowserPolicy policy;
        string policySource;
        try
        {
            (policy, policySource) = BrowserPolicy.LoadFirst(PolicySources.Candidates());
        }
        catch (Exception ex) when (ex is PolicyException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Битая или небезопасная политика — не запускаемся вовсе, а не работаем с ослабленной защитой.
            audit.Write("policy-invalid", new { error = ex.Message });
            MessageBox.Show(Loc.T($"Политика браузера некорректна или недоступна, запуск невозможен:\n\n{ex.Message}",
                $"The browser policy is invalid or unavailable, so the browser cannot start:\n\n{ex.Message}"), Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (policy.BlockInRemoteSession && CaptureGuard.IsRemoteSession)
        {
            audit.Write("remote-session-blocked");
            MessageBox.Show(Loc.T("Запуск в удалённой сессии (RDP) запрещён политикой: снимок экрана делается на клиентской машине, " +
                            "и защита окна там не действует.",
                "The policy does not allow running in a remote desktop (RDP) session: screenshots are taken on the client machine, " +
                "where window protection does not apply."), Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }

        var guard = new CaptureGuard(policy, audit);
        guard.Tripped += OnGuardTripped;
        AppServices.Initialize(policy, policySource, audit, guard);
        ProtectTransientWindows(guard);
        Appearance.ThemeService.Initialize(); // до первого окна: оно сразу открывается в нужной теме

        audit.Write("app-start", new
        {
            policySource,
            os = Environment.OSVersion.VersionString,
            // WDA_EXCLUDEFROMCAPTURE полностью работает с Windows 10 2004 (сборка 19041).
            excludeFromCaptureSupported = Environment.OSVersion.Version.Build >= 19041,
        });

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
        guard.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Guard?.Stop();
        AppServices.History?.Flush(); // несохранённые посещения последних секунд
        AppServices.Audit?.Write("app-exit", new { code = e.ApplicationExitCode });
        base.OnExit(e);
    }

    /// <summary>
    /// Меню и подсказки WPF открываются в собственных окнах верхнего уровня. В них бывают заголовки и адреса страниц
    /// (закладки, вкладки), поэтому каждое такое окно получает тот же флаг защиты от захвата при открытии;
    /// не получилось — окно закрывается (fail closed).
    /// </summary>
    private static void ProtectTransientWindows(CaptureGuard guard)
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is ContextMenu menu)
                    OnContextMenuOpened(guard, menu);
            }));
        // Подсказку без защиты просто не показываем: она дублирует подпись или значок.
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is ToolTip tip && !guard.ProtectTransient(tip, "tooltip").Protected)
                    tip.IsOpen = false;
            }));
    }

    private static readonly TimeSpan ReopenPollInterval = TimeSpan.FromMilliseconds(15);
    private const int ReopenPollLimit = 40; // ~600 мс: закрытие с анимацией уничтожает окно через 150 мс

    /// <summary>
    /// WPF открывает ContextMenu всегда в прозрачном окне (AllowsTransparency = true, задано в самом ContextMenu).
    /// Windows 10 21H2 отказывается защищать такие окна (ошибка 8), обычные — защищает. Поэтому при отказе меню
    /// закрываем и открываем заново в непрозрачном окне: AllowsTransparency применяется, когда Popup создаёт окно.
    /// Popup меню живёт вместе с ним, так что повторно открытые меню (правый клик по закладке) сразу непрозрачные.
    /// </summary>
    private static void OnContextMenuOpened(CaptureGuard guard, ContextMenu menu)
    {
        var result = guard.ProtectTransient(menu, "context-menu");
        if (result.Protected)
            return;

        var target = menu.PlacementTarget;
        menu.IsOpen = false;
        if (result.Layered && menu.Parent is Popup { AllowsTransparency: true } popup)
        {
            popup.AllowsTransparency = false;
            AppServices.Audit.Write("popup-reopen-opaque", new { kind = "context-menu" });
            ReopenWhenWindowDestroyed(menu, target);
            return;
        }
        WarnMenuNotShown(target);
    }

    /// <summary>
    /// Пока старое (прозрачное) окно не уничтожено, Popup открылся бы в нём же, поэтому ждём. После закрытия
    /// ContextMenuService забывает, у какого элемента меню открывалось, — возвращаем его на время повторного открытия.
    /// </summary>
    private static void ReopenWhenWindowDestroyed(ContextMenu menu, UIElement? target)
    {
        var polls = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ReopenPollInterval };
        timer.Tick += (_, _) =>
        {
            var windowAlive = PresentationSource.FromVisual(menu) is not null;
            if (windowAlive && ++polls < ReopenPollLimit)
                return;
            timer.Stop();
            if (menu.IsOpen)
                return; // уже открыли снова
            if (windowAlive)
            {
                AppServices.Audit.Write("popup-reopen-failed", new { kind = "context-menu", reason = "window-not-destroyed" });
                WarnMenuNotShown(target);
                return;
            }
            if (target is not null && menu.ReadLocalValue(ContextMenu.PlacementTargetProperty) == DependencyProperty.UnsetValue)
            {
                menu.PlacementTarget = target;
                void Restore(object? s, RoutedEventArgs e)
                {
                    menu.Closed -= Restore;
                    menu.ClearValue(ContextMenu.PlacementTargetProperty);
                }
                menu.Closed += Restore;
            }
            menu.IsOpen = true; // снова Opened → защита; не вышло и у непрозрачного окна — WarnMenuNotShown
        };
        timer.Start();
    }

    private static void WarnMenuNotShown(UIElement? target)
    {
        var owner = (target is not null ? Window.GetWindow(target) : null) ?? Current.MainWindow;
        (owner as MainWindow)?.Warn(Loc.T(
            "Меню не открыто: Windows не дала защитить его окно от захвата экрана (подробности в audit.log)",
            "The menu was not shown: Windows did not allow protecting its window from screen capture (see audit.log)"));
    }

    private void OnGuardTripped(string reason)
    {
        // Сначала прячем контент во всех окнах, потом показываем сообщение (оно модальное).
        foreach (Window window in Windows)
        {
            if (window is IContentShield shield)
                shield.HideContent();
        }
        MessageBox.Show(Loc.T($"Защита от захвата экрана нарушена ({reason}).\nКонтент скрыт, браузер будет закрыт.",
                $"Screen capture protection was broken ({reason}).\nContent is hidden and the browser will close."),
            Caption, MessageBoxButton.OK, MessageBoxImage.Stop);
        Shutdown(3);
    }

    private static void OnUnhandled(AuditLog audit, DispatcherUnhandledExceptionEventArgs args)
    {
        audit.Write("unhandled-exception", Diagnostics.Details(args.Exception));
        MessageBox.Show(Loc.T($"Непредвиденная ошибка:\n\n{Diagnostics.Describe(args.Exception)}", $"Unexpected error:\n\n{Diagnostics.Describe(args.Exception)}"), Caption, MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }
}
