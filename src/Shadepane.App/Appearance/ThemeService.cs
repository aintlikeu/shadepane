using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Shadepane.App.Native;
using Shadepane.Core.Settings;

namespace Shadepane.App.Appearance;

/// <summary>
/// Светлая или тёмная тема окон: по настройке пользователя или как в Windows (AppsUseLightTheme).
/// Меняет словарь цветов (Assets/Theme.*.xaml) на лету — окна берут цвета через DynamicResource —
/// и цвет системного заголовка окон. Страницы получают тему отдельно (MainWindow, PreferredColorScheme).
/// </summary>
internal static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static bool _initialized;

    public static bool IsDark { get; private set; }

    /// <summary>Тема сменилась (настройка пользователя или системная тема Windows).</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        Refresh();
        // Смена «Режима приложения» в Windows приходит как WM_SETTINGCHANGE → UserPreferenceChanged.
        SystemEvents.UserPreferenceChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        AppServices.ConfigChanged += Refresh; // сохранили настройки — может быть, выбрали другую тему
    }

    public static void Refresh()
    {
        var dark = ThemeChoice.UseDark(AppServices.Settings.Theme, SystemUsesLightTheme());
        if (_initialized && dark == IsDark)
            return;
        _initialized = true;
        IsDark = dark;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var index = dictionaries.ToList().FindIndex(d => d.Source?.OriginalString.Contains("/Theme.", StringComparison.Ordinal) == true
                                                         || d.Source?.OriginalString.StartsWith("Assets/Theme.", StringComparison.Ordinal) == true);
        var theme = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Assets/Theme.{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute) };
        if (index >= 0)
            dictionaries[index] = theme;
        else
            dictionaries.Insert(0, theme);

        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);
        Changed?.Invoke();
    }

    /// <summary>Цвет системного заголовка окна под тему. Вызывать, когда у окна уже есть HWND.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var value = IsDark ? 1 : 0;
        // Ошибку не обрабатываем: на старой сборке Windows заголовок просто останется светлым.
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true; // не прочитали — светлая, как Windows по умолчанию
        }
    }
}
