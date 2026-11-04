using System.IO;
using System.Windows.Threading;
using Shadepane.Core.History;

namespace Shadepane.App.Browser;

/// <summary>
/// История посещений приложения: запись из вкладок, сохранение с задержкой (не на каждый переход)
/// и при выходе. Не пишет, если пользователь отключил «Сохранять историю».
/// </summary>
internal sealed class HistoryService
{
    private readonly HistoryStore _store;
    private readonly BrowsingHistory _history;
    private readonly DispatcherTimer _saveTimer;

    public HistoryService(string filePath)
    {
        _store = new HistoryStore(filePath);
        _history = _store.Load(out var problem);
        if (problem is not null)
            AppServices.Audit.Write("history-unreadable", new { file = _store.FilePath, problem });
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _saveTimer.Tick += (_, _) => Flush();
    }

    /// <summary>История изменилась (для открытого окна истории).</summary>
    public event Action? Changed;

    public IReadOnlyList<HistoryEntry> Search(string? query) => _history.Search(query);

    public void RecordVisit(string? url, string? title)
    {
        if (!AppServices.Settings.SaveHistory)
            return;
        if (_history.Add(url, title, DateTimeOffset.Now))
            Touch();
    }

    public void UpdateTitle(string? url, string? title)
    {
        if (AppServices.Settings.SaveHistory && _history.UpdateTitle(url, title))
            Touch();
    }

    public void Remove(HistoryEntry entry)
    {
        if (_history.Remove(entry))
            Touch();
    }

    public void Clear()
    {
        _history.Clear();
        Touch();
        Flush(); // очистку сохраняем сразу, а не через несколько секунд
    }

    public void Flush()
    {
        _saveTimer.Stop();
        if (!_history.IsDirty)
            return;
        try
        {
            _store.Save(_history);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppServices.Audit.Write("history-save-failed", new { error = ex.Message });
        }
    }

    private void Touch()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke();
    }
}
