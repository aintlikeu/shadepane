using System.IO;
using Shadepane.App.Protection;
using Shadepane.Core.Audit;
using Shadepane.Core.Policy;

namespace Shadepane.App;

/// <summary>Общие объекты приложения. Инициализируются один раз в App.OnStartup.</summary>
internal static class AppServices
{
    public static BrowserPolicy Policy { get; private set; } = null!;
    public static AuditLog Audit { get; private set; } = null!;
    public static CaptureGuard Guard { get; private set; } = null!;

    /// <summary>Шаблон поиска из адресной строки ({query}). Пока — поисковик по умолчанию.</summary>
    public static string SearchUrl => Shadepane.Core.Navigation.SearchEngines.Default.SearchUrl;

    public static void Initialize(BrowserPolicy policy, AuditLog audit, CaptureGuard guard)
    {
        Policy = policy;
        Audit = audit;
        Guard = guard;
    }
}

internal static class AppPaths
{
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shadepane");

    public static string WebViewData => Path.Combine(DataRoot, "WebView2");
    public static string ExtensionStaging => Path.Combine(DataRoot, "extensions");
    public static string ApprovedExtensions => Path.Combine(DataRoot, "approved-extensions.json");
    public static string AuditLog => Path.Combine(DataRoot, "audit.log");
}
