using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Shadepane.Core.Browsing;
using Shadepane.Core.Localization;

namespace Shadepane.App.Browser;

/// <summary>Одна загрузка для окна загрузок. События WebView2 приходят в UI-потоке.</summary>
internal sealed class DownloadItem : INotifyPropertyChanged
{
    private readonly CoreWebView2DownloadOperation _operation;

    public DownloadItem(CoreWebView2DownloadOperation operation)
    {
        _operation = operation;
        Url = operation.Uri;
        operation.BytesReceivedChanged += (_, _) => Notify(nameof(Status));
        operation.StateChanged += (_, _) =>
        {
            Notify(nameof(Status));
            Notify(nameof(FileName)); // итоговое имя может отличаться от начального (например, «файл (1).zip»)
            Notify(nameof(IsInProgress));
            Notify(nameof(IsCompleted));
            Finished?.Invoke(this);
        };
    }

    public string Url { get; }
    /// <summary>Читаем каждый раз из операции: Chromium может переименовать файл, если такой уже есть.</summary>
    public string FilePath => _operation.ResultFilePath;
    public string FileName => Path.GetFileName(FilePath);
    public bool IsInProgress => _operation.State == CoreWebView2DownloadState.InProgress;
    public bool IsCompleted => _operation.State == CoreWebView2DownloadState.Completed;

    public string Status => _operation.State switch
    {
        CoreWebView2DownloadState.Completed => Loc.T($"Готово · {ByteSize.Format(_operation.BytesReceived)}", $"Done · {ByteSize.Format(_operation.BytesReceived)}"),
        CoreWebView2DownloadState.Interrupted => Loc.T($"Прервано: {_operation.InterruptReason}", $"Interrupted: {_operation.InterruptReason}"),
        _ => (_operation.CanResume && _paused ? Loc.T("Пауза · ", "Paused · ") : "") + ByteSize.Progress(_operation.BytesReceived, (long?)_operation.TotalBytesToReceive),
    };

    private bool _paused;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<DownloadItem>? Finished;

    public void Cancel()
    {
        if (IsInProgress)
            _operation.Cancel();
    }

    public void TogglePause()
    {
        if (!IsInProgress)
            return;
        if (_paused && _operation.CanResume)
        {
            _operation.Resume();
            _paused = false;
        }
        else if (!_paused)
        {
            _operation.Pause();
            _paused = true;
        }
        Notify(nameof(Status));
    }

    /// <summary>Открыть скачанный файл программой по умолчанию (решение пользователя; SmartScreen проверяет файл при загрузке).</summary>
    public void Open()
    {
        if (IsCompleted && File.Exists(FilePath))
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
    }

    public void ShowInFolder()
    {
        if (File.Exists(FilePath))
            Process.Start("explorer.exe", $"/select,\"{FilePath}\"");
        else if (Directory.Exists(Path.GetDirectoryName(FilePath)))
            Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(FilePath)}\"");
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Загрузки текущего сеанса (список не сохраняется между запусками).</summary>
internal sealed class DownloadService
{
    public ObservableCollection<DownloadItem> Items { get; } = [];

    /// <summary>Началась новая загрузка (имя файла — для строки состояния).</summary>
    public event Action<DownloadItem>? Started;

    public event Action<DownloadItem>? Finished;

    public void Track(CoreWebView2DownloadOperation operation)
    {
        var item = new DownloadItem(operation);
        item.Finished += i => Finished?.Invoke(i);
        Items.Insert(0, item);
        Started?.Invoke(item);
    }

    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => !i.IsInProgress).ToList())
            Items.Remove(item);
    }
}
