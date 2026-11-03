namespace Shadepane.App;

/// <summary>Единое описание исключения для сообщений и журнала: тип, HRESULT, текст.</summary>
internal static class Diagnostics
{
    public static string Describe(Exception ex) =>
        $"{ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}: {ex.Message}";

    /// <summary>Подробности для audit.log: включая вложенные исключения и стек.</summary>
    public static object Details(Exception ex, string? stage = null) => new
    {
        stage,
        type = ex.GetType().FullName,
        hresult = $"0x{ex.HResult:X8}",
        ex.Message,
        full = ex.ToString(),
    };
}
