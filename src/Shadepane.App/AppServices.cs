using System.IO;
using Shadepane.App.Protection;
using Shadepane.Core.Audit;
using Shadepane.Core.Policy;
using Shadepane.Core.Settings;

namespace Shadepane.App;

/// <summary>Общие объекты приложения. Инициализируются один раз в App.OnStartup.</summary>
internal static class AppServices
{
    public static BrowserPolicy Policy { get; private set; } = null!;
    public static AuditLog Audit { get; private set; } = null!;
    public static CaptureGuard Guard { get; private set; } = null!;

    public static UserSettingsStore SettingsStore { get; } = new(AppPaths.UserSettings);

    /// <summary>Настройки пользователя (окно настроек).</summary>
    public static UserSettings Settings { get; private set; } = UserSettings.Default;

    /// <summary>Итоговые значения: политика администратора поверх настроек пользователя.</summary>
    public static EffectiveConfig Config { get; private set; } = EffectiveConfig.Resolve(BrowserPolicy.Default, UserSettings.Default);

    /// <summary>История посещений (создаётся в Initialize, нужен Dispatcher).</summary>
    public static Browser.HistoryService History { get; private set; } = null!;

    public static Browser.BookmarkService Bookmarks { get; private set; } = null!;

    public static Browser.DownloadService Downloads { get; } = new();

    public static Updates.UpdateService Updates { get; } = new();

    /// <summary>Итоговые значения изменились (сохранены настройки).</summary>
    public static event Action? ConfigChanged;

    /// <summary>Политика для проверки расширений: выбор пользователя по буферу обмена, если администратор его не задал.</summary>
    public static BrowserPolicy ExtensionPolicy => Policy.ForExtensions(Config.AllowExtensionClipboardRead);

    /// <summary>Шаблон поиска из адресной строки ({query}).</summary>
    public static string SearchUrl => Config.SearchEngine.SearchUrl;

    public static void Initialize(BrowserPolicy policy, AuditLog audit, CaptureGuard guard)
    {
        Policy = policy;
        Audit = audit;
        Guard = guard;
        Settings = SettingsStore.Load(out var problem);
        if (problem is not null)
            audit.Write("settings-unreadable", new { file = SettingsStore.FilePath, problem });
        Config = EffectiveConfig.Resolve(policy, Settings);
        History = new Browser.HistoryService(AppPaths.History);
        Bookmarks = new Browser.BookmarkService(AppPaths.Bookmarks);
    }

    /// <summary>Сохраняет настройки пользователя и пересчитывает итоговые значения.</summary>
    public static void UpdateSettings(UserSettings settings)
    {
        SettingsStore.Save(settings);
        Settings = settings;
        Config = EffectiveConfig.Resolve(Policy, settings);
        ConfigChanged?.Invoke();
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
    public static string UserSettings => Path.Combine(DataRoot, "settings.json");
    public static string History => Path.Combine(DataRoot, "history.json");
    public static string Bookmarks => Path.Combine(DataRoot, "bookmarks.json");
}
