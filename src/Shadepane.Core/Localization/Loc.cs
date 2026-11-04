namespace Shadepane.Core.Localization;

/// <summary>Язык интерфейса.</summary>
public enum UiLanguage
{
    Russian,
    English,
}

/// <summary>
/// Строки интерфейса на двух языках. Обе версии записаны рядом в месте использования — Loc.T("Сохранить", "Save") —
/// чтобы перевод не расходился с оригиналом. Язык выбирается один раз при запуске (App.OnStartup) до первого окна;
/// смена в настройках действует после перезапуска.
/// </summary>
public static class Loc
{
    /// <summary>По умолчанию русский: на нём написаны исходные строки (и тесты сообщений).</summary>
    public static UiLanguage Current { get; set; } = UiLanguage.Russian;

    public static bool IsEnglish => Current == UiLanguage.English;

    public static string T(string russian, string english) => Pick(Current, russian, english);

    public static string Pick(UiLanguage language, string russian, string english) =>
        language == UiLanguage.English ? english : russian;
}
