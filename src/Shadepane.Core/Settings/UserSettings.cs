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

/// <summary>Оформление окон браузера.</summary>
public enum AppTheme
{
    /// <summary>Как в Windows («Параметры → Персонализация → Цвета → Режим приложения»).</summary>
    System,
    Light,
    Dark,
}

public static class ThemeChoice
{
    /// <param name="systemUsesLightTheme">Значение AppsUseLightTheme из реестра; нет значения — светлая (так делает Windows).</param>
    public static bool UseDark(AppTheme theme, bool systemUsesLightTheme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => !systemUsesLightTheme,
    };
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

    /// <summary>Разрешить загрузку файлов (если не задано политикой).</summary>
    public bool AllowDownloads { get; init; } = true;

    /// <summary>Проверять обновления через GitHub Releases (если не задано политикой).</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Светлое, тёмное или как в Windows. Страницы сайтов получают ту же тему (prefers-color-scheme).</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;
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
