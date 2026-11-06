namespace Shadepane.Core.Browsing;

/// <summary>
/// Масштаб по сайтам, как в Chromium: один масштаб на имя хоста, общий для всех вкладок и страниц этого сайта.
/// WebView2 сам этого не делает: масштаб, заданный пользователем (Ctrl+колесо), действует только на текущую
/// страницу и сбрасывается при переходе, а новая вкладка всегда открывается в 100%.
/// Хранится только в памяти, до закрытия браузера: список сайтов с масштабом на диске выдал бы, где пользователь
/// был, даже при выключенной истории.
/// </summary>
public sealed class SiteZoom
{
    private const double Epsilon = 0.005;

    private readonly Dictionary<string, double> _byHost = new(StringComparer.Ordinal);

    /// <summary>Масштаб сайта изменился: хост и новый масштаб (для остальных вкладок этого сайта).</summary>
    public event Action<string, double>? Changed;

    /// <summary>
    /// Ключ сайта: хост http(s)-адреса (поддомены — разные сайты, порт не учитывается, как в Chromium).
    /// У остальных адресов (about:blank, file:, страницы расширений) ключа нет — их масштаб не запоминается.
    /// </summary>
    public static string? KeyOf(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        var host = uri.IdnHost;
        return string.IsNullOrEmpty(host) ? null : host.ToLowerInvariant();
    }

    /// <summary>Масштаб для страницы по адресу; без запомненного — 100%.</summary>
    public double Get(string? url) =>
        KeyOf(url) is { } host && _byHost.TryGetValue(host, out var factor) ? factor : ZoomLevels.Default;

    /// <summary>
    /// Запомнить масштаб сайта страницы. 100% запись удаляет. Возвращает false, если запоминать нечего
    /// (у адреса нет сайта, масштаб некорректный или не изменился); тогда <see cref="Changed"/> не вызывается.
    /// </summary>
    public bool Set(string? url, double factor)
    {
        if (KeyOf(url) is not { } host || !double.IsFinite(factor) || factor <= 0)
            return false;
        var previous = _byHost.TryGetValue(host, out var stored) ? stored : ZoomLevels.Default;
        if (Math.Abs(previous - factor) < Epsilon)
            return false;
        if (Math.Abs(factor - ZoomLevels.Default) < Epsilon)
            _byHost.Remove(host);
        else
            _byHost[host] = factor;
        Changed?.Invoke(host, factor);
        return true;
    }

    public static bool SameZoom(double a, double b) => Math.Abs(a - b) < Epsilon;
}
