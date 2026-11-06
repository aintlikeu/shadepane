using System.Text.Json.Serialization;
using Shadepane.Core.Hosts;
using Shadepane.Core.Policy;
using Shadepane.Core.Localization;

namespace Shadepane.Core.Extensions;

/// <param name="Grant">
/// Что снимет это нарушение, если пользователь выдаст расширению право: имя права или
/// <see cref="ExtensionGrants.AllSites"/>. null — нарушение не снимается никаким выбором пользователя.
/// </param>
public sealed record PolicyViolation(string Code, string Message, string? Grant = null);

/// <summary>Права, которые пользователь выдал одному расширению при установке (сверх политики).</summary>
/// <param name="Wording">Версия текстов <see cref="Describe"/>, которые пользователь видел, когда соглашался.
/// В файлах прежних версий поля нет — это 0.</param>
public sealed record ExtensionGrants(IReadOnlyList<string> Permissions, bool AllSitesAccess, int Wording = 0)
{
    /// <summary>Значение <see cref="PolicyViolation.Grant"/> для доступа ко всем сайтам.</summary>
    public const string AllSites = "<all-sites>";

    /// <summary>
    /// Текущая версия текстов <see cref="Describe"/>. Поднимается, когда описание права меняется так, что согласие
    /// на старый текст нельзя считать согласием на новый; тогда в <see cref="WordingChangedIn"/> записывается,
    /// у каких прав и в какой версии.
    /// </summary>
    public const int CurrentWording = 1;

    /// <summary>
    /// 1 — «все сайты»: проверка на Windows показала, что такое расширение может делать снимки страниц
    /// (tabs.captureVisibleTab), не защищённые от захвата экрана; прежний текст об этом не говорил.
    /// </summary>
    private static readonly Dictionary<string, int> WordingChangedIn = new(StringComparer.OrdinalIgnoreCase)
    {
        [AllSites] = 1,
    };

    public static ExtensionGrants None { get; } = new([], false);

    /// <summary>Что даёт выдача — простыми словами, для окна «Дать права».</summary>
    public static string Describe(string key) => key.ToLowerInvariant() switch
    {
        "<all-sites>" => Loc.T("Доступ ко всем сайтам: читать и менять содержимое любой страницы и в любой момент делать её снимки — " +
            "такие снимки НЕ защищены от захвата экрана",
            "Access to all sites: read and change the content of any page and take pictures of it at any time — " +
            "these pictures are NOT protected from screen capture"),
        "activetab" => Loc.T("activeTab — когда расширение вызывают: доступ к открытой вкладке, в том числе её снимок, не защищённый от захвата экрана " +
            "(клик по значку в Shadepane его не включает — проверено)",
            "activeTab: when the extension is invoked, access to the open tab, including a picture of it that is not protected from screen capture " +
            "(a click on its icon in Shadepane does not turn it on, as tested)"),
        "tabs" => Loc.T("tabs — адреса и заголовки всех вкладок", "tabs: addresses and titles of all tabs"),
        "history" => Loc.T("history — история посещений", "history: browsing history"),
        "webnavigation" => Loc.T("webNavigation — адрес каждого перехода на всех страницах", "webNavigation: the address of every navigation on every page"),
        "declarativenetrequestfeedback" => Loc.T("declarativeNetRequestFeedback — какие запросы страниц совпали с его правилами (их адреса)",
            "declarativeNetRequestFeedback: which page requests matched its rules (their addresses)"),
        "topsites" => Loc.T("topSites — часто посещаемые сайты", "topSites: most visited sites"),
        "sessions" => Loc.T("sessions — недавно закрытые вкладки", "sessions: recently closed tabs"),
        "proxy" => Loc.T("proxy — направлять весь трафик браузера через свой сервер", "proxy: route all browser traffic through its own server"),
        _ => key,
    };

    [JsonIgnore]
    public bool IsEmpty => (Permissions?.Count ?? 0) == 0 && !AllSitesAccess;

    // Permissions может оказаться null в испорченном approved-extensions.json — считаем, что прав нет.
    public bool HasPermission(string permission) => Permissions?.Contains(permission, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Все выдачи по порядку: «все сайты», затем права — для окна подтверждения.</summary>
    [JsonIgnore]
    public IEnumerable<string> Keys => AllSitesAccess ? [AllSites, .. Permissions ?? []] : Permissions ?? [];

    /// <summary>Выданные права, описание которых изменилось после того, как пользователь согласился.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> KeysToReconfirm =>
        [.. Keys.Where(k => WordingChangedIn.TryGetValue(k, out var changedIn) && changedIn > Wording)];

    /// <summary>Те же права без перечисленных (ключи — как в <see cref="Keys"/>).</summary>
    public ExtensionGrants Without(IEnumerable<string> keys)
    {
        var drop = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        return this with
        {
            Permissions = [.. (Permissions ?? []).Where(p => !drop.Contains(p))],
            AllSitesAccess = AllSitesAccess && !drop.Contains(AllSites),
        };
    }
}

public sealed record ValidationResult(IReadOnlyList<PolicyViolation> Violations)
{
    public static ValidationResult Allowed { get; } = new([]);
    public bool IsAllowed => Violations.Count == 0;
    public static ValidationResult Denied(string code, string message) => new([new PolicyViolation(code, message)]);

    /// <summary>Отказ, который пользователь может снять, выдав права (все нарушения — выдаваемые).</summary>
    public bool CanBeGrantedByUser => Violations.Count > 0 && Violations.All(v => v.Grant is not null);

    /// <summary>Какие права нужно выдать, чтобы снять выдаваемые нарушения.</summary>
    public ExtensionGrants GrantsNeeded => new(
        [.. Violations.Select(v => v.Grant).OfType<string>().Where(g => g != ExtensionGrants.AllSites).Distinct(StringComparer.OrdinalIgnoreCase)],
        Violations.Any(v => v.Grant == ExtensionGrants.AllSites),
        ExtensionGrants.CurrentWording);
}

/// <summary>
/// Статическая проверка манифеста по политике. Работает потому, что Chromium не выдаёт
/// расширению API и хосты, которые не объявлены в манифесте: проверили манифест — ограничили расширение.
/// </summary>
public static class ExtensionValidator
{
    /// <param name="grants">Права, выданные этому расширению пользователем. Учитываются, только если политика
    /// разрешает выдачу (PermissionGrantsAllowed), и только из BrowserPolicy.UserGrantablePermissions.</param>
    public static ValidationResult Validate(ExtensionManifest manifest, BrowserPolicy policy, ExtensionGrants? grants = null)
    {
        var violations = new List<PolicyViolation>();
        var canGrant = policy.PermissionGrantsAllowed;
        grants = canGrant ? grants ?? ExtensionGrants.None : ExtensionGrants.None;

        switch (manifest.ManifestVersion)
        {
            case 3:
                break;
            case 2 when policy.AllowManifestV2:
                break;
            case 2:
                violations.Add(new("manifest-v2", Loc.T("Manifest V2 запрещён политикой (allowManifestV2=false)", "Manifest V2 is not allowed by policy (allowManifestV2=false)")));
                break;
            default:
                violations.Add(new("manifest-version", Loc.T($"неподдерживаемый manifest_version: {manifest.ManifestVersion}", $"unsupported manifest_version: {manifest.ManifestVersion}")));
                break;
        }

        var allowed = policy.AllowedPermissionSet;
        foreach (var permission in manifest.ApiPermissions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(permission, BrowserPolicy.ClipboardReadPermission, StringComparison.OrdinalIgnoreCase))
            {
                if (!(policy.AllowExtensionClipboardRead ?? true))
                    violations.Add(new("clipboard-read-disabled", Loc.T("чтение буфера обмена расширениями отключено политикой (allowExtensionClipboardRead=false)", "clipboard reading by extensions is turned off by policy (allowExtensionClipboardRead=false)")));
                continue; // при true разрешено и в режиме белого списка
            }
            if (policy.BlockedPermissionSet.Contains(permission))
            {
                var notGrantable = string.Equals(permission, BrowserPolicy.ActiveTabPermission, StringComparison.OrdinalIgnoreCase)
                    ? ActiveTabRefusal(manifest, policy) : null;
                var grantable = canGrant && BrowserPolicy.UserGrantablePermissions.Contains(permission) && notGrantable is null;
                if (grantable && grants.HasPermission(permission))
                    continue;
                violations.Add(notGrantable is not null
                    ? new("blocked-permission", notGrantable)
                    : new("blocked-permission", Loc.T($"право '{permission}' запрещено политикой", $"the '{permission}' permission is not allowed by policy"),
                        grantable ? permission : null));
            }
            else if (allowed is not null && !allowed.Contains(permission))
                violations.Add(new("permission-not-allowed", Loc.T($"право '{permission}' не входит в allowedPermissions", $"the '{permission}' permission is not in allowedPermissions")));
        }

        foreach (var entry in manifest.HostPermissions)
        {
            if (!MatchPattern.TryParse(entry.Pattern, out var pattern, out var error))
            {
                violations.Add(new("invalid-host-pattern", $"{entry.Source}: '{entry.Pattern}' — {error}"));
                continue;
            }
            if (!pattern!.AppliesToWeb)
                continue;

            if (pattern.IsBroad && !policy.AllowBroadHostAccess && !grants.AllSitesAccess)
            {
                var scope = pattern.Host!.Kind == HostPatternKind.Any ? Loc.T("ко всем сайтам", "to all sites") : Loc.T($"ко всем сайтам зоны .{pattern.Host.Domain}", $"to all sites under .{pattern.Host.Domain}");
                violations.Add(new("broad-host-access", Loc.T($"{entry.Source}: '{entry.Pattern}' даёт доступ {scope}", $"{entry.Source}: '{entry.Pattern}' gives access {scope}"),
                    canGrant ? ExtensionGrants.AllSites : null));
                // Выдаваемое нарушение: проверяем и защищённые домены ниже, чтобы пользователь сразу увидел отказ,
                // который выдачей прав не снять (а не после того, как согласился).
                if (!canGrant)
                    continue;
            }

            var hit = policy.ProtectedHostPatterns.FirstOrDefault(p => pattern.Host!.Overlaps(p));
            if (hit is not null)
                violations.Add(new("protected-host", Loc.T($"{entry.Source}: '{entry.Pattern}' пересекается с защищённым доменом '{hit}'", $"{entry.Source}: '{entry.Pattern}' overlaps the protected domain '{hit}'")));
        }

        return new ValidationResult(violations);
    }

    /// <summary>
    /// Почему activeTab нельзя выдать этому расширению, или null. Снимок по activeTab в Shadepane не получается, потому что
    /// popup открывает браузер сам, а не chrome.action (проверено на Windows). Остальные вызовы расширения не проверены,
    /// поэтому закрыты: пункты расширений в контекстном меню страницы удаляет фильтр меню, сочетания клавиш — здесь.
    /// </summary>
    private static string? ActiveTabRefusal(ExtensionManifest manifest, BrowserPolicy policy)
    {
        // activeTab открывает доступ к вкладке, на которой его вызвали, — и к защищённому домену тоже, а проверка
        // хостов его не видит (в манифесте хоста нет).
        if (policy.ProtectedHostPatterns.Count > 0)
            return Loc.T("право 'activeTab' запрещено политикой: оно даёт доступ к любой открытой вкладке, в том числе к защищённым доменам",
                "the 'activeTab' permission is not allowed by policy: it gives access to any open tab, including protected domains");
        if (manifest.HasDefaultShortcuts)
            return Loc.T("право 'activeTab' запрещено политикой и не выдаётся расширениям с сочетаниями клавиш (commands.suggested_key): нажатие сочетания может включить его",
                "the 'activeTab' permission is not allowed by policy and is not given to extensions with keyboard shortcuts (commands.suggested_key): pressing one may turn it on");
        return null;
    }
}
