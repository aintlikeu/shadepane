using System.Text.Json;
using Shadepane.Core.Localization;

namespace Shadepane.Core.Extensions;

public static class ManifestReader
{
    public const string FileName = "manifest.json";
    public const long MaxManifestBytes = 1024 * 1024;

    private static readonly JsonDocumentOptions Options = new()
    {
        // Chrome терпит комментарии и висячие запятые в manifest.json — терпим и мы.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    public static ExtensionManifest ReadFromDirectory(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new ManifestException(Loc.T($"в папке нет {FileName}", $"the folder has no {FileName}"));
        if (info.Length > MaxManifestBytes)
            throw new ManifestException(Loc.T($"{FileName} больше {MaxManifestBytes / 1024} КБ", $"{FileName} is larger than {MaxManifestBytes / 1024} KB"));

        // ReadAllText сам снимает UTF-8 BOM.
        return Parse(File.ReadAllText(path));
    }

    public static ExtensionManifest Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ManifestException(Loc.T($"{FileName} — некорректный JSON: {ex.Message}", $"{FileName} is not valid JSON: {ex.Message}"));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ManifestException(Loc.T($"{FileName} должен содержать JSON-объект", $"{FileName} must contain a JSON object"));

            EnsureNoDuplicateKeys(root, "$");
            return ExtensionManifest.FromJson(root);
        }
    }

    /// <summary>
    /// Отклоняем манифесты с повторяющимися ключами: разные парсеры выбирают разное значение
    /// (первое или последнее), и проверка могла бы увидеть не то, что загрузит браузер.
    /// </summary>
    private static void EnsureNoDuplicateKeys(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                        throw new ManifestException(Loc.T($"повторяющийся ключ '{property.Name}' в {path}", $"duplicate key '{property.Name}' in {path}"));
                    EnsureNoDuplicateKeys(property.Value, $"{path}.{property.Name}");
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    EnsureNoDuplicateKeys(item, $"{path}[{i++}]");
                break;
        }
    }
}
