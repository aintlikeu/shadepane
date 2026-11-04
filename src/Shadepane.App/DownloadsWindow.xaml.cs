using System.ComponentModel;
using System.Windows;
using Shadepane.App.Browser;
using Shadepane.App.Protection;

namespace Shadepane.App;

/// <summary>Окно загрузок (Ctrl+J).</summary>
public partial class DownloadsWindow : ProtectedWindow
{
    public DownloadsWindow()
    {
        InitializeComponent();
        List.ItemsSource = AppServices.Downloads.Items;
        if (!AppServices.Config.AllowDownloads)
        {
            DisabledNote.Text = AppServices.Config.DownloadsLocked
                ? "Загрузка файлов запрещена администратором."
                : "Загрузка файлов отключена в настройках.";
            DisabledNote.Visibility = Visibility.Visible;
        }
    }

    private DownloadItem? Selected => List.SelectedItem as DownloadItem;

    private void Run(Action<DownloadItem> action)
    {
        if (Selected is not { } item)
            return;
        try
        {
            action(item);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Run(i => i.Open());
    private void ShowInFolder_Click(object sender, RoutedEventArgs e) => Run(i => i.ShowInFolder());
    private void Pause_Click(object sender, RoutedEventArgs e) => Run(i => i.TogglePause());
    private void Cancel_Click(object sender, RoutedEventArgs e) => Run(i => i.Cancel());
    private void ClearFinished_Click(object sender, RoutedEventArgs e) => AppServices.Downloads.ClearFinished();
    private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Run(i => i.Open());
}
