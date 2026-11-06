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
public sealed record ExtensionGrants(IReadOnlyList<string> Permissions, bool AllSitesAccess)
{
    /// <summary>Значение <see cref="PolicyViolation.Grant"/> для доступа ко всем сайтам.</summary>
    public const string AllSites = "<all-sites>";

    public static ExtensionGrants None { get; } = new([], false);

    /// <summary>Что даёт выдача — простыми словами, для окна «Дать права».</summary>
    public static string Describe(string key) => key.ToLowerInvariant() switch
    {
        "<all-sites>" => Loc.T("Доступ ко всем сайтам: читать и менять содержимое любой страницы, в том числе текст, который защищён от снимков экрана",
            "Access to all sites: read and change the content of any page, including text that is protected from screenshots"),
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
        Violations.Any(v => v.Grant == ExtensionGrants.AllSites));
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
                var grantable = canGrant && BrowserPolicy.UserGrantablePermissions.Contains(permission);
                if (grantable && grants.HasPermission(permission))
                    continue;
                violations.Add(new("blocked-permission", Loc.T($"право '{permission}' запрещено политикой", $"the '{permission}' permission is not allowed by policy"),
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
}
