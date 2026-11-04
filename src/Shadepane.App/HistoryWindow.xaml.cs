using System.Windows;
using System.Windows.Input;
using Shadepane.App.Protection;
using Shadepane.Core.History;
using Shadepane.Core.Localization;

namespace Shadepane.App;

/// <summary>Окно истории (Ctrl+H): поиск, открыть в текущей/новой вкладке, удалить, очистить.</summary>
public partial class HistoryWindow : ProtectedWindow
{
    public sealed record Row(HistoryEntry Entry)
    {
        public string When => Entry.VisitedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        public string Title => Entry.Title;
        public string Url => Entry.Url;
    }

    private readonly Action<string> _open;
    private readonly Action<string> _openInNewTab;

    internal HistoryWindow(Action<string> open, Action<string> openInNewTab)
    {
        InitializeComponent();
        _open = open;
        _openInNewTab = openInNewTab;
        AppServices.History.Changed += Refresh;
        Closed += (_, _) => AppServices.History.Changed -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        List.ItemsSource = AppServices.History.Search(SearchBox.Text).Select(e => new Row(e)).ToList();
        SavingNote.Text = AppServices.Settings.SaveHistory ? "" : Loc.T("Сохранение истории отключено в настройках", "Saving history is turned off in settings");
    }

    private IEnumerable<Row> Selected => List.SelectedItems.Cast<Row>().ToList();

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is Row row)
            _open(row.Url);
    }

    private void OpenInNewTab_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Selected)
            _openInNewTab(row.Url);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Selected)
            AppServices.History.Remove(row.Entry);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("Удалить всю историю посещений?", "Delete all browsing history?"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            AppServices.History.Clear();
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
