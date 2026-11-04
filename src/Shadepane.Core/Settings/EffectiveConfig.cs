using Shadepane.Core.Navigation;
using Shadepane.Core.Policy;

namespace Shadepane.Core.Settings;

/// <summary>
/// Итоговые значения, с которыми работает браузер. Правило приоритета: значение из политики администратора
/// действует и блокирует настройку (…Locked = true); иначе — настройка пользователя; иначе — по умолчанию.
/// </summary>
public sealed record EffectiveConfig(string HomeUrl, bool HomeLocked, SearchEngine SearchEngine)
{
    public static EffectiveConfig Resolve(BrowserPolicy policy, UserSettings user)
    {
        var engine = SearchEngines.Default;

        if (policy.HomeUrl is not null)
            return new(NormalizeHome(policy.HomeUrl) ?? engine.HomeUrl, true, engine);

        var home = user.HomePage switch
        {
            HomePageKind.Blank => UrlPolicy.AboutBlank,
            HomePageKind.Custom => NormalizeHome(user.CustomHomeUrl) ?? engine.HomeUrl, // битый адрес — не ломаем запуск
            _ => engine.HomeUrl,
        };
        return new(home, false, engine);
    }

    /// <summary>Домашняя страница: about:blank или проверенный http(s)-адрес; иначе null.</summary>
    public static string? NormalizeHome(string? url)
    {
        if (url == UrlPolicy.AboutBlank)
            return url;
        return UrlPolicy.TryNormalizeUserInput(url, out var uri, out _) ? uri!.AbsoluteUri : null;
    }
}
