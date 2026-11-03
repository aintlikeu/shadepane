using System.Text.Json;

namespace Shadepane.Core.Extensions;

public sealed record ApprovedExtension(
    string Id,
    string Name,
    string StagedPath,
    string Hash,
    string? PopupPath,
    string? IconPath,
    DateTimeOffset AddedAt);

/// <summary>Список одобренных расширений. Повреждённый файл — InvalidDataException (вызывающий решает: fail closed).</summary>
public sealed class ApprovedExtensionStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public IReadOnlyList<ApprovedExtension> Load()
    {
        if (!File.Exists(FilePath))
            return [];
        try
        {
            var items = JsonSerializer.Deserialize<List<ApprovedExtension>>(File.ReadAllText(FilePath), JsonOptions);
            if (items is null || items.Any(i => i is null || string.IsNullOrEmpty(i.Id) || string.IsNullOrEmpty(i.StagedPath) || string.IsNullOrEmpty(i.Hash)))
                throw new InvalidDataException("в списке есть пустые записи");
            return items;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"список расширений повреждён: {ex.Message}", ex);
        }
    }

    /// <summary>Атомарная запись: временный файл + переименование, чтобы сбой не оставил полфайла.</summary>
    public void Save(IEnumerable<ApprovedExtension> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items.ToList(), JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
