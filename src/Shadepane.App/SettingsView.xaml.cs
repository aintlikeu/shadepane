using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shadepane.Core.Localization;
using Shadepane.Core.Navigation;
using Shadepane.Core.Settings;

namespace Shadepane.App;

/// <summary>
/// Настройки во вкладке браузера. Изменение применяется сразу (как в Edge и Chrome): переключатели и флажки — по щелчку,
/// адреса — по Enter или при выходе из поля, после проверки. Поля, заданные политикой администратора, неактивны,
/// а их прежний выбор пользователя не меняется.
/// </summary>
public partial class SettingsView : UserControl
{
    private sealed record EngineItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly Func<Task> _checkForUpdates;
    private bool _loading;
    private bool _committing;

    /// <param name="checkForUpdates">Проверка обновлений по кнопке (MainWindow.CheckForUpdatesAsync).</param>
    public SettingsView(Func<Task> checkForUpdates)
    {
        _checkForUpdates = checkForUpdates;
        InitializeComponent();
        CustomSearchBox.ToolTip = Loc.T("https-адрес, где {query} будет заменён поисковым запросом", "An https address where {query} is replaced with the search query");
        CustomSearchHint.Text = Loc.T("Пример: https://search.example/?q={query}", "Example: https://search.example/?q={query}");
        foreach (var engine in SearchEngines.All)
            SearchEngineBox.Items.Add(new EngineItem(engine.Id, engine.Name));
        SearchEngineBox.Items.Add(new EngineItem(SearchEngines.CustomId, Loc.T("Свой шаблон…", "Custom template…")));

        Load();
        AppServices.ConfigChanged += OnConfigChanged; // например, Ctrl+Shift+B, пока открыта вкладка настроек
    }

    /// <summary>Вкладку закрыли: отписаться от изменений настроек.</summary>
    public void Detach() => AppServices.ConfigChanged -= OnConfigChanged;

    private void OnConfigChanged()
    {
        if (!_committing)
            Load();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var settings = AppServices.Settings;
            var config = AppServices.Config;

            StartupRestore.IsChecked = config.RestoreSession;
            StartupHome.IsChecked = !config.RestoreSession;
            SetLocked(config.RestoreSessionLocked, StartupLockedNote, StartupHome, StartupRestore);

            HomeSearchEngine.IsChecked = settings.HomePage == HomePageKind.SearchEngine;
            HomeBlank.IsChecked = settings.HomePage == HomePageKind.Blank;
            HomeCustom.IsChecked = settings.HomePage == HomePageKind.Custom;
            if (!HomeUrlBox.IsKeyboardFocusWithin)
                HomeUrlBox.Text = settings.CustomHomeUrl ?? "";
            SetLocked(config.HomeLocked, HomeLockedNote, HomeSearchEngine, HomeBlank, HomeCustom, HomeUrlBox);

            SearchEngineBox.SelectedItem = SearchEngineBox.Items.Cast<EngineItem>()
                .FirstOrDefault(i => string.Equals(i.Id, settings.SearchEngineId, StringComparison.OrdinalIgnoreCase))
                ?? SearchEngineBox.Items[0];
            if (!CustomSearchBox.IsKeyboardFocusWithin)
                CustomSearchBox.Text = settings.CustomSearchUrl ?? "";
            SetLocked(config.SearchLocked, SearchLockedNote, SearchEngineBox, CustomSearchBox);
            if (config.SearchLocked)
                SearchLockedNote.Text = Loc.T($"Задано администратором: {config.SearchEngine.Name}", $"Set by the administrator: {config.SearchEngine.Name}");

            ThemeSystem.IsChecked = settings.Theme == AppTheme.System;
            ThemeLight.IsChecked = settings.Theme == AppTheme.Light;
            ThemeDark.IsChecked = settings.Theme == AppTheme.Dark;
            LanguageSystem.IsChecked = settings.Language == AppLanguage.System;
            LanguageRussian.IsChecked = settings.Language == AppLanguage.Russian;
            LanguageEnglish.IsChecked = settings.Language == AppLanguage.English;

            SaveHistoryBox.IsChecked = settings.SaveHistory;
            ShowBookmarksBarBox.IsChecked = settings.ShowBookmarksBar;
            AllowDownloadsBox.IsChecked = config.AllowDownloads;
            SetLocked(config.DownloadsLocked, DownloadsLockedNote, AllowDownloadsBox);
            ClipboardBox.IsChecked = config.AllowExtensionClipboardRead;
            SetLocked(config.ClipboardLocked, ClipboardLockedNote, ClipboardBox);

            CheckUpdatesBox.IsChecked = config.CheckForUpdates;
            SetLocked(config.UpdatesLocked, UpdatesLockedNote, CheckUpdatesBox);
            VersionText.Text = Loc.T($"Версия {AppServices.Updates.Current}", $"Version {AppServices.Updates.Current}") +
                (Updates.UpdateService.IsStorePackage ? Loc.T(". Обновления приходят через Microsoft Store.", ". Updates come through the Microsoft Store.")
                 : Updates.UpdateService.IsInstalled ? "" : Loc.T(" (без установщика — обновление через страницу релиза)", " (no installer: updates open the release page)"));
            if (Updates.UpdateService.IsStorePackage)
            {
                CheckUpdatesBox.Visibility = Visibility.Collapsed;
                CheckNowButton.Visibility = Visibility.Collapsed;
                UpdatesLockedNote.Visibility = Visibility.Collapsed;
            }
            UpdateEnabledState();
        }
        finally
        {
            _loading = false;
        }
    }

    private static void SetLocked(bool locked, UIElement note, params Control[] controls)
    {
        note.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        foreach (var control in controls)
            control.IsEnabled = !locked;
    }

    private void UpdateEnabledState()
    {
        var config = AppServices.Config;
        HomeUrlBox.IsEnabled = !config.HomeLocked && HomeCustom.IsChecked == true;
        var custom = (SearchEngineBox.SelectedItem as EngineItem)?.Id == SearchEngines.CustomId;
        CustomSearchBox.IsEnabled = !config.SearchLocked && custom;
        // Поле своего шаблона и подсказка — только когда выбран «Свой шаблон…».
        CustomSearchBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        CustomSearchHint.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (!custom)
            SearchError.Visibility = Visibility.Collapsed;
        if (HomeCustom.IsChecked != true)
            HomeError.Visibility = Visibility.Collapsed;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Commit();

    private void HomeKind_Click(object sender, RoutedEventArgs e)
    {
        UpdateEnabledState();
        if (HomeCustom.IsChecked == true && HomeUrlBox.Text.Trim().Length == 0)
        {
            HomeUrlBox.Focus(); // адрес ещё не введён — сохраним, когда введут (Enter или выход из поля)
            return;
        }
        Commit();
    }

    private void SearchEngine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        UpdateEnabledState();
        if ((SearchEngineBox.SelectedItem as EngineItem)?.Id == SearchEngines.CustomId && CustomSearchBox.Text.Trim().Length == 0)
        {
            CustomSearchBox.Focus();
            return;
        }
        Commit();
    }

    private void TextField_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_loading)
            Commit();
    }

    private void TextField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        Commit();
    }

    /// <summary>
    /// Собирает настройки из полей и сохраняет, если что-то изменилось. Неверный адрес не сохраняется: под полем —
    /// ошибка, а в настройках остаётся прежний выбор для этого поля; остальные изменения сохраняются.
    /// </summary>
    private void Commit()
    {
        if (_loading)
            return;
        var current = AppServices.Settings;
        var config = AppServices.Config;

        var homeKind = HomeBlank.IsChecked == true ? HomePageKind.Blank
            : HomeCustom.IsChecked == true ? HomePageKind.Custom
            : HomePageKind.SearchEngine;
        var homeUrl = HomeUrlBox.Text.Trim();
        var homeValid = config.HomeLocked || homeKind != HomePageKind.Custom || EffectiveConfig.NormalizeHome(homeUrl) is not null;
        ShowError(HomeError, homeValid ? null : Loc.T("Нужен адрес http:// или https://.", "Enter an http:// or https:// address."));

        var engineId = (SearchEngineBox.SelectedItem as EngineItem)?.Id ?? SearchEngines.Default.Id;
        var template = CustomSearchBox.Text.Trim();
        string? templateError = null;
        var searchValid = config.SearchLocked || engineId != SearchEngines.CustomId || SearchEngines.TryValidateTemplate(template, out templateError);
        ShowError(SearchError, searchValid ? null : Loc.T($"Шаблон не сохранён: {templateError}.", $"The template was not saved: {templateError}."));

        var updated = current with
        {
            RestoreSession = config.RestoreSessionLocked ? current.RestoreSession : StartupRestore.IsChecked == true,
            HomePage = config.HomeLocked || !homeValid ? current.HomePage : homeKind,
            CustomHomeUrl = config.HomeLocked || !homeValid ? current.CustomHomeUrl : (homeUrl.Length > 0 ? homeUrl : null),
            SearchEngineId = config.SearchLocked || !searchValid ? current.SearchEngineId : engineId,
            CustomSearchUrl = config.SearchLocked || !searchValid ? current.CustomSearchUrl : (template.Length > 0 ? template : null),
            Theme = ThemeLight.IsChecked == true ? AppTheme.Light : ThemeDark.IsChecked == true ? AppTheme.Dark : AppTheme.System,
            Language = LanguageRussian.IsChecked == true ? AppLanguage.Russian : LanguageEnglish.IsChecked == true ? AppLanguage.English : AppLanguage.System,
            SaveHistory = SaveHistoryBox.IsChecked == true,
            ShowBookmarksBar = ShowBookmarksBarBox.IsChecked == true,
            AllowDownloads = config.DownloadsLocked ? current.AllowDownloads : AllowDownloadsBox.IsChecked == true,
            AllowExtensionClipboardRead = config.ClipboardLocked ? current.AllowExtensionClipboardRead : ClipboardBox.IsChecked == true,
            CheckForUpdates = config.UpdatesLocked ? current.CheckForUpdates : CheckUpdatesBox.IsChecked == true,
        };
        if (updated == current)
            return;

        _committing = true;
        try
        {
            AppServices.UpdateSettings(updated);
            ShowError(SaveError, null);
            AppServices.Audit.Write("settings-saved", new { updated.HomePage, updated.SearchEngineId, updated.Theme, updated.Language, updated.RestoreSession });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowError(SaveError, Loc.T($"Не удалось сохранить настройки: {ex.Message}", $"Could not save settings: {ex.Message}"));
        }
        finally
        {
            _committing = false;
        }
    }

    private static void ShowError(TextBlock target, string? message)
    {
        target.Text = message ?? "";
        target.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false; // защита от двойного нажатия
        try { await _checkForUpdates(); }
        finally { CheckNowButton.IsEnabled = true; }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (MessageBox.Show(owner!, Loc.T("Удалить всю историю посещений?", "Delete all browsing history?"), owner?.Title ?? "Shadepane",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            AppServices.History.Clear();
    }
}
