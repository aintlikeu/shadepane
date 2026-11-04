using System.Windows;
using System.Windows.Input;
using Shadepane.App.Protection;
using Shadepane.Core.Bookmarks;
using Shadepane.Core.Localization;

namespace Shadepane.App;

/// <summary>Менеджер закладок (Ctrl+Shift+O): поиск, правка, удаление, порядок, открытие.</summary>
public partial class BookmarksWindow : ProtectedWindow
{
    private readonly Action<string> _open;
    private readonly Action<string> _openInNewTab;

    internal BookmarksWindow(Action<string> open, Action<string> openInNewTab)
    {
        InitializeComponent();
        _open = open;
        _openInNewTab = openInNewTab;
        AppServices.Bookmarks.Changed += Refresh;
        Closed += (_, _) => AppServices.Bookmarks.Changed -= Refresh;
        Refresh();
    }

    private Bookmark? Selected => List.SelectedItem as Bookmark;

    private void Refresh()
    {
        var selectedId = Selected?.Id;
        var items = AppServices.Bookmarks.Search(SearchBox.Text);
        List.ItemsSource = items;
        List.SelectedItem = items.FirstOrDefault(b => b.Id == selectedId); // выбор переживает обновление списка
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        TitleBox.Text = Selected?.Title ?? "";
        UrlBox.Text = Selected?.Url ?? "";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } b)
            return;
        if (AppServices.Bookmarks.TryUpdate(b.Id, TitleBox.Text, UrlBox.Text, out var error))
        {
            ErrorText.Visibility = Visibility.Collapsed;
            return;
        }
        ErrorText.Text = Loc.T($"Не сохранено: {error}", $"Not saved: {error}");
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Move(int delta)
    {
        if (Selected is not { } b)
            return;
        var all = AppServices.Bookmarks.Items;
        var index = all.ToList().FindIndex(x => x.Id == b.Id);
        if (index >= 0)
            AppServices.Bookmarks.Move(b.Id, index + delta);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } b)
            _open(b.Url);
    }

    private void OpenInNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } b)
            _openInNewTab(b.Url);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } b &&
            MessageBox.Show(this, Loc.T($"Удалить закладку «{b.Title}»?", $"Delete the bookmark \"{b.Title}\"?"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            AppServices.Bookmarks.Remove(b.Id);
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Open_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Delete:
                Delete_Click(sender, e);
                e.Handled = true;
                break;
        }
    }
}
