using System.Text.Json;
using Shadepane.Core.Localization;

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
                throw new InvalidDataException(Loc.T("в списке есть пустые записи", "the list has empty entries"));
            return items;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Loc.T($"список расширений повреждён: {ex.Message}", $"the extension list is corrupted: {ex.Message}"), ex);
        }
    }

    public void Save(IEnumerable<ApprovedExtension> items) =>
        IO.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(items.ToList(), JsonOptions));
}
