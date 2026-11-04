using System.IO;
using Microsoft.Web.WebView2.Core;
using Shadepane.Core.Bookmarks;

namespace Shadepane.App.Browser;

/// <summary>
/// Значки сайтов для панели закладок (%LOCALAPPDATA%\Shadepane\favicons).
/// Приватность: значок НЕ скачивается отдельно — иначе браузер сам ходил бы на сайты из закладок; он берётся
/// у страницы, которую пользователь открыл (WebView2 его уже загрузил). Хранятся значки только сайтов из закладок:
/// удалили последнюю закладку сайта — удалён и значок, поэтому кэш не становится второй историей посещений.
/// </summary>
internal sealed class FaviconService
{
    /// <summary>Больше — не значок, а что-то странное; не храним.</summary>
    private const int MaxIconBytes = 512 * 1024;

    private readonly string _folder;

    public FaviconService(string folder)
    {
        _folder = folder;
        AppServices.Bookmarks.Changed += Prune;
    }

    /// <summary>Появился или обновился значок сайта из закладок — панель закладок перерисовывается.</summary>
    public event Action? Changed;

    /// <summary>Путь к сохранённому значку сайта или null, если значка ещё нет.</summary>
    public string? PathFor(string url)
    {
        var name = FaviconKey.FileNameFor(url);
        if (name is null)
            return null;
        var path = Path.Combine(_folder, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Сохранить значок открытой страницы, если её сайт есть в закладках.</summary>
    public async Task SaveFromAsync(CoreWebView2 core)
    {
        try
        {
            var url = core.Source;
            var name = FaviconKey.FileNameFor(url);
            if (name is null || string.IsNullOrEmpty(core.FaviconUri) ||
                !AppServices.Bookmarks.Items.Any(b => FaviconKey.SameSite(b.Url, url)))
                return;

            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null)
                return;
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxIconBytes)
                    return;
            }
            if (buffer.Length == 0)
                return;

            // Страницу могли закрыть или увести на другой сайт, пока значок читался.
            if (!FaviconKey.SameSite(core.Source, url))
                return;
            var bytes = buffer.ToArray();
            var path = Path.Combine(_folder, name);
            if (File.Exists(path) && (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(bytes))
                return; // тот же значок — панель не перерисовываем
            Directory.CreateDirectory(_folder);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            // Значок — украшение: не получилось — остаётся буква сайта.
            AppServices.Audit.Write("favicon-save-failed", new { error = ex.Message });
        }
    }

    /// <summary>Удаляет значки сайтов, которых больше нет в закладках.</summary>
    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_folder))
                return;
            var keep = AppServices.Bookmarks.Items.Select(b => FaviconKey.FileNameFor(b.Url)).OfType<string>().ToHashSet();
            foreach (var file in Directory.EnumerateFiles(_folder))
            {
                if (!keep.Contains(Path.GetFileName(file)))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppServices.Audit.Write("favicon-prune-failed", new { error = ex.Message });
        }
    }
}
