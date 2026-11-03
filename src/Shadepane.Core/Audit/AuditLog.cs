using System.Diagnostics;
using System.Text.Json;

namespace Shadepane.Core.Audit;

/// <summary>
/// Журнал событий безопасности в формате JSON Lines (одна строка — одно событие).
/// В продукте строки будут отправляться на сервер администратора; локальный файл — буфер.
/// </summary>
public sealed class AuditLog(string filePath)
{
    private readonly object _lock = new();

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public void Write(string eventName, object? details = null)
    {
        var line = JsonSerializer.Serialize(new AuditRecord(DateTimeOffset.UtcNow, eventName, details));
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Сбой журнала не должен ронять браузер; в продукте — отдельная метрика/алерт.
                Trace.WriteLine($"[audit] не удалось записать событие {eventName}: {ex.Message}");
            }
        }
    }

    private sealed record AuditRecord(DateTimeOffset Ts, string Event, object? Details);
}
