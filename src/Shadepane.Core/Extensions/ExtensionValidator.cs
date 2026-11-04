using Shadepane.Core.Hosts;
using Shadepane.Core.Policy;
using Shadepane.Core.Localization;

namespace Shadepane.Core.Extensions;

public sealed record PolicyViolation(string Code, string Message);

public sealed record ValidationResult(IReadOnlyList<PolicyViolation> Violations)
{
    public static ValidationResult Allowed { get; } = new([]);
    public bool IsAllowed => Violations.Count == 0;
    public static ValidationResult Denied(string code, string message) => new([new PolicyViolation(code, message)]);
}

/// <summary>
/// Статическая проверка манифеста по политике. Работает потому, что Chromium не выдаёт
/// расширению API и хосты, которые не объявлены в манифесте: проверили манифест — ограничили расширение.
/// </summary>
public static class ExtensionValidator
{
    public static ValidationResult Validate(ExtensionManifest manifest, BrowserPolicy policy)
    {
        var violations = new List<PolicyViolation>();

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
                violations.Add(new("blocked-permission", Loc.T($"право '{permission}' запрещено политикой", $"the '{permission}' permission is not allowed by policy")));
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

            if (pattern.IsBroad && !policy.AllowBroadHostAccess)
            {
                var scope = pattern.Host!.Kind == HostPatternKind.Any ? Loc.T("ко всем сайтам", "to all sites") : Loc.T($"ко всем сайтам зоны .{pattern.Host.Domain}", $"to all sites under .{pattern.Host.Domain}");
                violations.Add(new("broad-host-access", Loc.T($"{entry.Source}: '{entry.Pattern}' даёт доступ {scope}", $"{entry.Source}: '{entry.Pattern}' gives access {scope}")));
                continue;
            }

            var hit = policy.ProtectedHostPatterns.FirstOrDefault(p => pattern.Host!.Overlaps(p));
            if (hit is not null)
                violations.Add(new("protected-host", Loc.T($"{entry.Source}: '{entry.Pattern}' пересекается с защищённым доменом '{hit}'", $"{entry.Source}: '{entry.Pattern}' overlaps the protected domain '{hit}'")));
        }

        return new ValidationResult(violations);
    }
}
