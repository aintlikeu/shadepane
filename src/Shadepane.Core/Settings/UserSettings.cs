using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shadepane.Core.Settings;

public enum HomePageKind
{
    /// <summary>Главная страница выбранного поисковика.</summary>
    SearchEngine,
    Blank,
    Custom,
}

/// <summary>
/// Настройки пользователя (окно настроек). Не являются защитой: всё, что важно для безопасности,
/// задаёт политика администратора, и её значения имеют приоритет (см. EffectiveConfig).
/// </summary>
public sealed record UserSettings
{
    public static UserSettings Default { get; } = new();

    public HomePageKind HomePage { get; init; } = HomePageKind.SearchEngine;
    public string? CustomHomeUrl { get; init; }

    /// <summary>Id готового поисковика (SearchEngines.All) или "custom" — тогда CustomSearchUrl.</summary>
    public string SearchEngineId { get; init; } = Navigation.SearchEngines.Default.Id;
    public string? CustomSearchUrl { get; init; }

    /// <summary>Разрешить расширениям читать буфер обмена (если не задано политикой). Применяется при запуске.</summary>
    public bool AllowExtensionClipboardRead { get; init; } = true;

    /// <summary>Записывать посещённые страницы в историю.</summary>
    public bool SaveHistory { get; init; } = true;

    /// <summary>Показывать панель закладок под адресной строкой (Ctrl+Shift+B).</summary>
    public bool ShowBookmarksBar { get; init; } = true;
}

/// <summary>Файл настроек. Повреждённый или неполный файл не мешает запуску: берутся значения по умолчанию.</summary>
public sealed class UserSettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string FilePath { get; } = Path.GetFullPath(filePath);

    /// <param name="problem">Почему файл не прочитан (для журнала); null — всё в порядке или файла нет.</param>
    public UserSettings Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(FilePath))
            return UserSettings.Default;
        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), JsonOptions) ?? UserSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = ex.Message;
            return UserSettings.Default;
        }
    }

    public void Save(UserSettings settings) =>
        IO.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
}
