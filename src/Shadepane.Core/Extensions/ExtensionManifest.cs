using System.Text.Json;

namespace Shadepane.Core.Extensions;

/// <summary>Шаблон доступа к хостам и место в манифесте, откуда он взят (для понятных сообщений).</summary>
public sealed record HostPermissionEntry(string Source, string Pattern);

/// <summary>
/// Только те поля manifest.json, которые важны для политики и для интерфейса.
/// Это не полная модель манифеста Chrome — неизвестные поля просто игнорируются.
/// </summary>
public sealed class ExtensionManifest
{
    public required int ManifestVersion { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }

    /// <summary>API-права из permissions и optional_permissions.</summary>
    public required IReadOnlyList<string> ApiPermissions { get; init; }

    /// <summary>Все шаблоны хостов: host_permissions, optional_*, content_scripts.matches, хосты в permissions (MV2).</summary>
    public required IReadOnlyList<HostPermissionEntry> HostPermissions { get; init; }

    /// <summary>Относительный путь popup (action / browser_action / page_action), если есть.</summary>
    public string? PopupPath { get; init; }

    /// <summary>Относительный путь иконки, наиболее подходящей для панели (~32px).</summary>
    public string? IconPath { get; init; }

    internal static ExtensionManifest FromJson(JsonElement root)
    {
        if (!root.TryGetProperty("manifest_version", out var mv) || mv.ValueKind != JsonValueKind.Number || !mv.TryGetInt32(out var manifestVersion))
            throw new ManifestException("поле manifest_version отсутствует или не является целым числом");

        var name = RequireString(root, "name");
        var version = RequireString(root, "version");

        var api = new List<string>();
        var hosts = new List<HostPermissionEntry>();

        // В MV2 хосты лежат прямо в permissions; в MV3 Chrome их там игнорирует,
        // но мы всё равно учитываем — строже безопаснее.
        foreach (var key in new[] { "permissions", "optional_permissions" })
        {
            foreach (var entry in ReadStringArray(root, key))
            {
                if (LooksLikeHostPattern(entry))
                    hosts.Add(new HostPermissionEntry(key, entry));
                else
                    api.Add(entry);
            }
        }

        foreach (var key in new[] { "host_permissions", "optional_host_permissions" })
        {
            foreach (var entry in ReadStringArray(root, key))
                hosts.Add(new HostPermissionEntry(key, entry));
        }

        if (root.TryGetProperty("content_scripts", out var scripts))
        {
            if (scripts.ValueKind != JsonValueKind.Array)
                throw new ManifestException("content_scripts должен быть массивом");

            var i = 0;
            foreach (var script in scripts.EnumerateArray())
            {
                if (script.ValueKind != JsonValueKind.Object)
                    throw new ManifestException($"content_scripts[{i}] должен быть объектом");
                foreach (var m in ReadStringArray(script, "matches", $"content_scripts[{i}].matches"))
                    hosts.Add(new HostPermissionEntry($"content_scripts[{i}].matches", m));
                i++;
            }
        }

        return new ExtensionManifest
        {
            ManifestVersion = manifestVersion,
            Name = name,
            Version = version,
            ApiPermissions = api,
            HostPermissions = hosts,
            PopupPath = ReadPopup(root),
            IconPath = ReadIcon(root),
        };
    }

    private static bool LooksLikeHostPattern(string entry) =>
        entry == "<all_urls>" || entry.Contains("://", StringComparison.Ordinal);

    private static string RequireString(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            throw new ManifestException($"поле {key} отсутствует или не является непустой строкой");
        return v.GetString()!;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement obj, string key, string? displayName = null)
    {
        if (!obj.TryGetProperty(key, out var arr))
            return [];
        if (arr.ValueKind != JsonValueKind.Array)
            throw new ManifestException($"{displayName ?? key} должен быть массивом строк");

        var result = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            // Объектные записи (наследие Chrome Apps) не поддерживаем: проще отказать, чем гадать.
            if (item.ValueKind != JsonValueKind.String)
                throw new ManifestException($"{displayName ?? key}: поддерживаются только строковые значения");
            result.Add(item.GetString()!);
        }
        return result;
    }

    private static string? ReadPopup(JsonElement root)
    {
        foreach (var key in new[] { "action", "browser_action", "page_action" })
        {
            if (root.TryGetProperty(key, out var action) && action.ValueKind == JsonValueKind.Object &&
                action.TryGetProperty("default_popup", out var popup) && popup.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(popup.GetString()))
            {
                return popup.GetString();
            }
        }
        return null;
    }

    private static string? ReadIcon(JsonElement root)
    {
        foreach (var key in new[] { "action", "browser_action", "page_action" })
        {
            if (root.TryGetProperty(key, out var action) && action.ValueKind == JsonValueKind.Object &&
                action.TryGetProperty("default_icon", out var icon))
            {
                var picked = PickIcon(icon);
                if (picked is not null)
                    return picked;
            }
        }
        return root.TryGetProperty("icons", out var icons) ? PickIcon(icons) : null;
    }

    /// <summary>Иконка может быть строкой или словарём {"16": "a.png", "32": "b.png"}.</summary>
    private static string? PickIcon(JsonElement icon)
    {
        if (icon.ValueKind == JsonValueKind.String)
            return icon.GetString();
        if (icon.ValueKind != JsonValueKind.Object)
            return null;

        var sized = new List<(int Size, string Path)>();
        foreach (var p in icon.EnumerateObject())
        {
            if (int.TryParse(p.Name, out var size) && p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                sized.Add((size, p.Value.GetString()!));
        }
        if (sized.Count == 0)
            return null;

        // Самая маленькая из тех, что >= 32px; иначе самая большая.
        var big = sized.Where(s => s.Size >= 32).OrderBy(s => s.Size).ToList();
        return big.Count > 0 ? big[0].Path : sized.OrderByDescending(s => s.Size).First().Path;
    }
}
