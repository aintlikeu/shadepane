using Shadepane.Core.Navigation;
using Shadepane.Core.Policy;

namespace Shadepane.Core.Settings;

/// <summary>
/// Итоговые значения, с которыми работает браузер. Правило приоритета: значение из политики администратора
/// действует и блокирует настройку (…Locked = true); иначе — настройка пользователя; иначе — по умолчанию.
/// </summary>
public sealed record EffectiveConfig(
    string HomeUrl,
    bool HomeLocked,
    SearchEngine SearchEngine,
    bool SearchLocked,
    bool AllowExtensionClipboardRead,
    bool ClipboardLocked,
    bool AllowDownloads,
    bool DownloadsLocked,
    bool CheckForUpdates,
    bool UpdatesLocked,
    bool RestoreSession = false,
    bool RestoreSessionLocked = false)
{
    public static EffectiveConfig Resolve(BrowserPolicy policy, UserSettings user)
    {
        var (engine, searchLocked) = ResolveSearch(policy, user);
        var clipboard = policy.AllowExtensionClipboardRead ?? user.AllowExtensionClipboardRead;
        var clipboardLocked = policy.AllowExtensionClipboardRead is not null;
        var downloads = policy.AllowDownloads ?? user.AllowDownloads;
        var downloadsLocked = policy.AllowDownloads is not null;
        var updates = policy.CheckForUpdates ?? user.CheckForUpdates;
        var updatesLocked = policy.CheckForUpdates is not null;
        var restore = policy.RestoreSession ?? user.RestoreSession;
        var restoreLocked = policy.RestoreSession is not null;

        if (policy.HomeUrl is not null)
            return new(NormalizeHome(policy.HomeUrl) ?? engine.HomeUrl, true, engine, searchLocked, clipboard, clipboardLocked, downloads, downloadsLocked, updates, updatesLocked, restore, restoreLocked);

        var home = user.HomePage switch
        {
            HomePageKind.Blank => UrlPolicy.AboutBlank,
            HomePageKind.Custom => NormalizeHome(user.CustomHomeUrl) ?? engine.HomeUrl, // битый адрес — не ломаем запуск
            _ => engine.HomeUrl,
        };
        return new(home, false, engine, searchLocked, clipboard, clipboardLocked, downloads, downloadsLocked, updates, updatesLocked, restore, restoreLocked);
    }

    private static (SearchEngine Engine, bool Locked) ResolveSearch(BrowserPolicy policy, UserSettings user)
    {
        // Политика уже проверена (EnsureValid), поэтому её шаблон корректен.
        if (policy.SearchUrl is not null)
            return (FromTemplate(policy.SearchUrl), true);

        if (string.Equals(user.SearchEngineId, SearchEngines.CustomId, StringComparison.OrdinalIgnoreCase))
        {
            return SearchEngines.TryValidateTemplate(user.CustomSearchUrl, out _)
                ? (FromTemplate(user.CustomSearchUrl!), false)
                : (SearchEngines.Default, false); // битый свой шаблон — поисковик по умолчанию
        }
        return (SearchEngines.Find(user.SearchEngineId) ?? SearchEngines.Default, false);
    }

    /// <summary>Готовый поисковик с таким же шаблоном или «свой» с домашней страницей = корнем сайта поиска.</summary>
    public static SearchEngine FromTemplate(string template)
    {
        var preset = SearchEngines.All.FirstOrDefault(e => string.Equals(e.SearchUrl, template, StringComparison.Ordinal));
        if (preset is not null)
            return preset;
        var sample = new Uri(template.Replace(SearchEngines.QueryPlaceholder, "x", StringComparison.Ordinal));
        return new SearchEngine(SearchEngines.CustomId, sample.Host, template, $"{sample.Scheme}://{sample.Authority}/");
    }

    /// <summary>Домашняя страница: about:blank или проверенный http(s)-адрес; иначе null.</summary>
    public static string? NormalizeHome(string? url)
    {
        if (url == UrlPolicy.AboutBlank)
            return url;
        return UrlPolicy.TryNormalizeUserInput(url, out var uri, out _) ? uri!.AbsoluteUri : null;
    }
}
