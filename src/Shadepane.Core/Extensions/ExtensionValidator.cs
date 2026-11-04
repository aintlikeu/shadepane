using Shadepane.Core.Hosts;
using Shadepane.Core.Policy;

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
                violations.Add(new("manifest-v2", "Manifest V2 запрещён политикой (allowManifestV2=false)"));
                break;
            default:
                violations.Add(new("manifest-version", $"неподдерживаемый manifest_version: {manifest.ManifestVersion}"));
                break;
        }

        var allowed = policy.AllowedPermissionSet;
        foreach (var permission in manifest.ApiPermissions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(permission, BrowserPolicy.ClipboardReadPermission, StringComparison.OrdinalIgnoreCase))
            {
                if (!(policy.AllowExtensionClipboardRead ?? true))
                    violations.Add(new("clipboard-read-disabled", "чтение буфера обмена расширениями отключено политикой (allowExtensionClipboardRead=false)"));
                continue; // при true разрешено и в режиме белого списка
            }
            if (policy.BlockedPermissionSet.Contains(permission))
                violations.Add(new("blocked-permission", $"право '{permission}' запрещено политикой"));
            else if (allowed is not null && !allowed.Contains(permission))
                violations.Add(new("permission-not-allowed", $"право '{permission}' не входит в allowedPermissions"));
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
                var scope = pattern.Host!.Kind == HostPatternKind.Any ? "ко всем сайтам" : $"ко всем сайтам зоны .{pattern.Host.Domain}";
                violations.Add(new("broad-host-access", $"{entry.Source}: '{entry.Pattern}' даёт доступ {scope}"));
                continue;
            }

            var hit = policy.ProtectedHostPatterns.FirstOrDefault(p => pattern.Host!.Overlaps(p));
            if (hit is not null)
                violations.Add(new("protected-host", $"{entry.Source}: '{entry.Pattern}' пересекается с защищённым доменом '{hit}'"));
        }

        return new ValidationResult(violations);
    }
}
