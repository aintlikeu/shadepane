using System.Text.Json;
using Shadepane.Core.Navigation;

namespace Shadepane.Core.Browsing;

/// <summary>Вкладки для восстановления при запуске: адреса по порядку и какая вкладка была активной.</summary>
public sealed record SessionState(IReadOnlyList<string> Urls, int ActiveIndex)
{
    /// <summary>Больше вкладок не восстанавливаем: файл сеанса — не место для сотен адресов.</summary>
    public const int MaxTabs = 50;

    public static SessionState Empty { get; } = new([], 0);

    public bool IsEmpty => Urls.Count == 0;

    /// <summary>
    /// Из открытых вкладок: только адреса http(s), которые браузер и так разрешает открыть (пустые вкладки,
    /// страницы расширений и прочее — не сохраняются). Активная вкладка, если её адрес не сохраняется, — ближайшая слева.
    /// </summary>
    public static SessionState FromTabs(IReadOnlyList<string?> tabUrls, int activeIndex)
    {
        var urls = new List<string>();
        var active = 0;
        for (var i = 0; i < tabUrls.Count && urls.Count < MaxTabs; i++)
        {
            var url = tabUrls[i];
            if (IsRestorable(url))
                urls.Add(url!);
            if (i == activeIndex)
                active = Math.Max(0, urls.Count - 1);
        }
        return urls.Count == 0 ? Empty : new SessionState(urls, Math.Clamp(active, 0, urls.Count - 1));
    }

    public static bool IsRestorable(string? url) =>
        !string.IsNullOrEmpty(url) && url != UrlPolicy.AboutBlank && UrlPolicy.IsAllowedTopLevelNavigation(url);
}

/// <summary>Файл сеанса. Битый или неполный файл не мешает запуску: открывается домашняя страница.</summary>
public sealed class SessionStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string FilePath { get; } = Path.GetFullPath(filePath);

    /// <param name="problem">Почему файл не прочитан (для журнала); null — всё в порядке или файла нет.</param>
    public SessionState Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(FilePath))
            return SessionState.Empty;
        try
        {
            var raw = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath), JsonOptions);
            if (raw?.Urls is null)
                return SessionState.Empty;
            // Файл мог изменить кто угодно — проверяем адреса так же, как при сохранении.
            return SessionState.FromTabs([.. raw.Urls], raw.ActiveIndex);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = ex.Message;
            return SessionState.Empty;
        }
    }

    public void Save(SessionState state)
    {
        if (state.IsEmpty)
        {
            Delete();
            return;
        }
        IO.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    /// <summary>Удаляет файл сеанса (восстановление выключено — адреса на диске не храним).</summary>
    public void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}
