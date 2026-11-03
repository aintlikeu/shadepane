using System.Text.Json;

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
            throw new ManifestException($"в папке нет {FileName}");
        if (info.Length > MaxManifestBytes)
            throw new ManifestException($"{FileName} больше {MaxManifestBytes / 1024} КБ");

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
            throw new ManifestException($"{FileName} — некорректный JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ManifestException($"{FileName} должен содержать JSON-объект");

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
                        throw new ManifestException($"повторяющийся ключ '{property.Name}' в {path}");
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
