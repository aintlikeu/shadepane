using System.Windows;
using System.Windows.Controls;
using Shadepane.App.Protection;
using Shadepane.Core.Navigation;
using Shadepane.Core.Settings;

namespace Shadepane.App;

/// <summary>
/// Настройки пользователя. Поля, заданные политикой администратора, показаны и заблокированы.
/// Закрытие крестиком или «Отмена» ничего не сохраняет.
/// </summary>
public partial class SettingsWindow : ProtectedWindow
{
    private sealed record EngineItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    public SettingsWindow()
    {
        InitializeComponent();

        foreach (var engine in SearchEngines.All)
            SearchEngineBox.Items.Add(new EngineItem(engine.Id, engine.Name));
        SearchEngineBox.Items.Add(new EngineItem(SearchEngines.CustomId, "Свой шаблон…"));

        Load(AppServices.Settings, AppServices.Config);
    }

    private void Load(UserSettings settings, EffectiveConfig config)
    {
        HomeSearchEngine.IsChecked = settings.HomePage == HomePageKind.SearchEngine;
        HomeBlank.IsChecked = settings.HomePage == HomePageKind.Blank;
        HomeCustom.IsChecked = settings.HomePage == HomePageKind.Custom;
        HomeUrlBox.Text = settings.CustomHomeUrl ?? "";
        SetLocked(config.HomeLocked, HomeLockedNote, HomeSearchEngine, HomeBlank, HomeCustom, HomeUrlBox);

        SearchEngineBox.SelectedItem = SearchEngineBox.Items.Cast<EngineItem>()
            .FirstOrDefault(i => string.Equals(i.Id, settings.SearchEngineId, StringComparison.OrdinalIgnoreCase))
            ?? SearchEngineBox.Items[0];
        CustomSearchBox.Text = settings.CustomSearchUrl ?? "";
        SetLocked(config.SearchLocked, SearchLockedNote, SearchEngineBox, CustomSearchBox);
        if (config.SearchLocked)
            SearchLockedNote.Text = $"Задано администратором: {config.SearchEngine.Name}";

        ThemeSystem.IsChecked = settings.Theme == AppTheme.System;
        ThemeLight.IsChecked = settings.Theme == AppTheme.Light;
        ThemeDark.IsChecked = settings.Theme == AppTheme.Dark;
        SaveHistoryBox.IsChecked = settings.SaveHistory;
        ShowBookmarksBarBox.IsChecked = settings.ShowBookmarksBar;
        CheckUpdatesBox.IsChecked = config.CheckForUpdates;
        SetLocked(config.UpdatesLocked, UpdatesLockedNote, CheckUpdatesBox);
        VersionText.Text = $"Версия: {AppServices.Updates.Current}" +
            (Updates.UpdateService.IsInstalled ? "" : " (без установщика — обновление через страницу релиза)");
        AllowDownloadsBox.IsChecked = config.AllowDownloads;
        SetLocked(config.DownloadsLocked, DownloadsLockedNote, AllowDownloadsBox);
        ClipboardBox.IsChecked = config.AllowExtensionClipboardRead;
        SetLocked(config.ClipboardLocked, ClipboardLockedNote, ClipboardBox);

        UpdateEnabledState();
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
        CustomSearchHint.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow main && sender is Button button)
        {
            button.IsEnabled = false;
            try { await main.CheckForUpdatesAsync(manual: true); }
            finally { button.IsEnabled = true; }
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Удалить всю историю посещений?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            AppServices.History.Clear();
    }

    private void HomeKind_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) UpdateEnabledState(); }

    private void SearchEngine_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateEnabledState(); }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false; // защита от двойного нажатия
        try
        {
            var current = AppServices.Settings;
            var errors = new List<string>();

            var homeKind = HomeBlank.IsChecked == true ? HomePageKind.Blank
                : HomeCustom.IsChecked == true ? HomePageKind.Custom
                : HomePageKind.SearchEngine;
            var homeUrl = HomeUrlBox.Text.Trim();
            if (!AppServices.Config.HomeLocked && homeKind == HomePageKind.Custom && EffectiveConfig.NormalizeHome(homeUrl) is null)
                errors.Add("Домашняя страница: нужен адрес http:// или https://.");

            var engineId = (SearchEngineBox.SelectedItem as EngineItem)?.Id ?? SearchEngines.Default.Id;
            var template = CustomSearchBox.Text.Trim();
            if (!AppServices.Config.SearchLocked && engineId == SearchEngines.CustomId &&
                !SearchEngines.TryValidateTemplate(template, out var templateError))
                errors.Add($"Поиск: {templateError}.");

            if (errors.Count > 0)
            {
                ErrorText.Text = string.Join("\n", errors);
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            // Заблокированные политикой поля не трогаем: сохраняем прежний выбор пользователя.
            var updated = current with
            {
                HomePage = AppServices.Config.HomeLocked ? current.HomePage : homeKind,
                CustomHomeUrl = AppServices.Config.HomeLocked ? current.CustomHomeUrl : (homeUrl.Length > 0 ? homeUrl : null),
                SearchEngineId = AppServices.Config.SearchLocked ? current.SearchEngineId : engineId,
                CustomSearchUrl = AppServices.Config.SearchLocked ? current.CustomSearchUrl : (template.Length > 0 ? template : null),
                AllowExtensionClipboardRead = AppServices.Config.ClipboardLocked ? current.AllowExtensionClipboardRead : ClipboardBox.IsChecked == true,
                SaveHistory = SaveHistoryBox.IsChecked == true,
                ShowBookmarksBar = ShowBookmarksBarBox.IsChecked == true,
                AllowDownloads = AppServices.Config.DownloadsLocked ? current.AllowDownloads : AllowDownloadsBox.IsChecked == true,
                CheckForUpdates = AppServices.Config.UpdatesLocked ? current.CheckForUpdates : CheckUpdatesBox.IsChecked == true,
                Theme = ThemeLight.IsChecked == true ? AppTheme.Light : ThemeDark.IsChecked == true ? AppTheme.Dark : AppTheme.System,
            };
            AppServices.UpdateSettings(updated);
            AppServices.Audit.Write("settings-saved", new { updated.HomePage, updated.SearchEngineId, updated.AllowExtensionClipboardRead });
            DialogResult = true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = $"Не удалось сохранить настройки: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
