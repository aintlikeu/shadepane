using System.IO;
using System.Windows;
using System.Windows.Threading;
using Shadepane.App.Policy;
using Shadepane.App.Protection;
using Shadepane.Core.Audit;
using Shadepane.Core.Policy;

namespace Shadepane.App;

public partial class App : Application
{
    private const string Caption = "Shadepane";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            MessageBox.Show($"Политика браузера некорректна или недоступна, запуск невозможен:\n\n{ex.Message}", Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (policy.BlockInRemoteSession && CaptureGuard.IsRemoteSession)
        {
            audit.Write("remote-session-blocked");
            MessageBox.Show("Запуск в удалённой сессии (RDP) запрещён политикой: снимок экрана делается на клиентской машине, " +
                            "и защита окна там не действует.", Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
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
    /// не получилось — окно закрывается.
    /// </summary>
    private static void ProtectTransientWindows(CaptureGuard guard)
    {
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is System.Windows.Controls.ContextMenu menu && !guard.ProtectTransient(menu, "context-menu"))
                    menu.IsOpen = false;
            }));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is System.Windows.Controls.ToolTip tip && !guard.ProtectTransient(tip, "tooltip"))
                    tip.IsOpen = false;
            }));
    }

    private void OnGuardTripped(string reason)
    {
        // Сначала прячем контент во всех окнах, потом показываем сообщение (оно модальное).
        foreach (Window window in Windows)
        {
            if (window is IContentShield shield)
                shield.HideContent();
        }
        MessageBox.Show($"Защита от захвата экрана нарушена ({reason}).\nКонтент скрыт, браузер будет закрыт.",
            Caption, MessageBoxButton.OK, MessageBoxImage.Stop);
        Shutdown(3);
    }

    private static void OnUnhandled(AuditLog audit, DispatcherUnhandledExceptionEventArgs args)
    {
        audit.Write("unhandled-exception", Diagnostics.Details(args.Exception));
        MessageBox.Show($"Непредвиденная ошибка:\n\n{Diagnostics.Describe(args.Exception)}", Caption, MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }
}
