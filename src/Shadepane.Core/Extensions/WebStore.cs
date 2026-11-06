using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shadepane.Core.Localization;

namespace Shadepane.Core.Extensions;

/// <summary>
/// Установка из Chrome Web Store. WebView2 ставит расширения только из распакованной папки
/// (CoreWebView2Profile.AddBrowserExtensionAsync), а кнопка магазина скачивает .crx, с которым движок ничего
/// не делает, — скачивание обрывается. Поэтому браузер перехватывает такую загрузку, сам скачивает пакет,
/// проверяет подпись (CrxPackage), распаковывает и ставит через ту же проверку политикой, что и папку.
/// </summary>
public static class WebStore
{
    /// <summary>Тип, с которым Chrome отдаёт пакеты расширений.</summary>
    public const string CrxMimeType = "application/x-chrome-extension";

    /// <summary>Максимальный размер пакета: самые большие расширения магазина — десятки мегабайт.</summary>
    public const long MaxPackageBytes = 128L * 1024 * 1024;

    public static bool IsExtensionId(string? value) =>
        value is { Length: 32 } && value.All(c => c is >= 'a' and <= 'p');

    /// <summary>ID расширения со страницы магазина (chromewebstore.google.com/detail/имя/ID и старый адрес chrome.google.com/webstore/detail/…).</summary>
    public static string? ExtensionIdFromPage(string? url)
    {
        if (!TryHttps(url, out var uri))
            return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var host = uri.IdnHost.ToLowerInvariant();
        var detail = host switch
        {
            "chromewebstore.google.com" => segments.Length >= 2 && segments[0] == "detail" ? 1 : -1,
            "chrome.google.com" => segments.Length >= 3 && segments[0] == "webstore" && segments[1] == "detail" ? 2 : -1,
            _ => -1,
        };
        if (detail < 0)
            return null;
        // ID — последний сегмент после "detail" (перед ним может быть имя-«слаг»).
        for (var i = segments.Length - 1; i >= detail; i--)
        {
            if (IsExtensionId(segments[i]))
                return segments[i];
        }
        return null;
    }

    /// <summary>
    /// Загрузка — это пакет расширения с серверов Google (то, что начинает кнопка магазина).
    /// id — ID из адреса, если он там есть (update2/crx?x=id%3D…); иначе его берут со страницы магазина.
    /// </summary>
    public static bool IsPackageDownload(string? url, string? mimeType, out string? id)
    {
        id = null;
        if (!TryHttps(url, out var uri) || !IsGoogleDownloadHost(uri))
            return false;
        var path = uri.AbsolutePath;
        var looksLikePackage =
            string.Equals(mimeType, CrxMimeType, StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/service/update2/crx", StringComparison.Ordinal) ||
            path.Contains("/crx/", StringComparison.Ordinal) ||
            path.EndsWith(".crx", StringComparison.OrdinalIgnoreCase);
        if (!looksLikePackage)
            return false;
        id = ExtensionIdFromQuery(uri.Query);
        return true;
    }

    /// <summary>Адрес пакета (тот же, что использует Chrome); prodversion — версия движка, иначе магазин может не отдать новые версии.</summary>
    public static string PackageUrl(string id, string? browserVersion)
    {
        if (!IsExtensionId(id))
            throw new ArgumentException("invalid extension id", nameof(id));
        var version = ParseVersion(browserVersion);
        return $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion={version}" +
               $"&acceptformat=crx3&x=id%3D{id}%26installsource%3Dondemand%26uc";
    }

    /// <summary>Конечный адрес после перенаправлений должен остаться на серверах загрузок Google по HTTPS.</summary>
    public static bool IsTrustedPackageSource(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps && IsGoogleDownloadHost(uri);

    private static bool IsGoogleDownloadHost(Uri uri)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        return host is "clients2.google.com" or "clients2.googleusercontent.com" ||
               host.EndsWith(".gvt1.com", StringComparison.Ordinal);
    }

    private static string ParseVersion(string? browserVersion)
    {
        // "156.0.4314.58" или "156.0.4314.58 canary": берём числовую часть.
        var token = (browserVersion ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is not null && Version.TryParse(token, out var v) ? v.ToString() : "130.0.0.0";
    }

    private static string? ExtensionIdFromQuery(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || part[..eq] != "x")
                continue;
            // x=id%3D<ID>%26uc
            foreach (var inner in Uri.UnescapeDataString(part[(eq + 1)..]).Split('&'))
            {
                if (inner.StartsWith("id=", StringComparison.Ordinal) && IsExtensionId(inner[3..]))
                    return inner[3..];
            }
        }
        return null;
    }

    private static bool TryHttps(string? url, out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps;
    }

    // ---------- Распаковка ----------

    /// <summary>
    /// Распаковывает архив пакета в пустую папку. Защита: пути только внутри папки (zip slip), лимиты на число
    /// файлов и реальный объём распакованного (zip bomb — считаем записанные байты, а не заявленные в архиве).
    /// _metadata (подписи содержимого для Chrome) не нужна распакованному расширению и пропускается.
    /// </summary>
    public static void ExtractArchive(ReadOnlyMemory<byte> archive, string destination)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("destination must be empty");

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(archive.ToArray(), writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new CrxException(Loc.T("архив пакета повреждён", "the package archive is corrupted"));
        }

        using (zip)
        {
            if (zip.Entries.Count > ExtensionDirectory.MaxFiles)
                throw new CrxException(Loc.T($"в пакете больше {ExtensionDirectory.MaxFiles} файлов", $"the package has more than {ExtensionDirectory.MaxFiles} files"));

            long total = 0;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    continue;
                if (name.StartsWith('/') || segments.Any(s => s is "." or ".." || s.Contains(':')))
                    throw new CrxException(Loc.T($"недопустимый путь в архиве: {entry.FullName}", $"invalid path in the archive: {entry.FullName}"));
                if (segments[0] == "_metadata")
                    continue;

                var target = Path.GetFullPath(Path.Combine([root, .. segments]));
                if (!ExtensionDirectory.IsInside(target, root))
                    throw new CrxException(Loc.T($"недопустимый путь в архиве: {entry.FullName}", $"invalid path in the archive: {entry.FullName}"));

                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) || Directory.Exists(target))
                    throw new CrxException(Loc.T($"файл повторяется в архиве: {entry.FullName}", $"duplicate file in the archive: {entry.FullName}"));
                try
                {
                    using var input = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        total += read;
                        if (total > ExtensionDirectory.MaxTotalBytes)
                            throw new CrxException(Loc.T($"распакованное расширение больше {ExtensionDirectory.MaxTotalBytes / (1024 * 1024)} МБ", $"the unpacked extension is larger than {ExtensionDirectory.MaxTotalBytes / (1024 * 1024)} MB"));
                        output.Write(buffer, 0, read);
                    }
                }
                catch (InvalidDataException)
                {
                    throw new CrxException(Loc.T("архив пакета повреждён", "the package archive is corrupted"));
                }
            }
        }
    }

    private static readonly JsonDocumentOptions ManifestOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    /// <summary>
    /// Записывает в manifest.json ключ разработчика ("key"): распакованное расширение получает тот же ID, что в
    /// магазине и в Chrome (от ID зависят обмен сообщениями с сайтами, вход через OAuth и сохранённые данные).
    /// Магазин убирает "key" из пакета, поэтому без этого ID вычислялся бы из пути к папке.
    /// </summary>
    public static void WriteDeveloperKey(string directory, byte[] publicKey)
    {
        var path = Path.Combine(directory, ManifestReader.FileName);
        // Сначала обычная проверка (размер, JSON, повторяющиеся ключи) — те же правила, что для папки.
        ManifestReader.ReadFromDirectory(directory);
        var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: ManifestOptions) as JsonObject
                   ?? throw new ManifestException(Loc.T($"{ManifestReader.FileName} должен содержать JSON-объект", $"{ManifestReader.FileName} must contain a JSON object"));
        node["key"] = Convert.ToBase64String(publicKey);
        File.WriteAllText(path, node.ToJsonString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Имя для окна подтверждения: в манифесте часто стоит "__MSG_appName__", а сам текст лежит в
    /// _locales/&lt;язык&gt;/messages.json. Берём язык интерфейса браузера, затем default_locale.
    /// </summary>
    public static string DisplayName(string directory, string manifestName)
    {
        const string prefix = "__MSG_", suffix = "__";
        if (!manifestName.StartsWith(prefix, StringComparison.Ordinal) || !manifestName.EndsWith(suffix, StringComparison.Ordinal) ||
            manifestName.Length <= prefix.Length + suffix.Length)
            return manifestName;
        var key = manifestName[prefix.Length..^suffix.Length];

        var locales = new List<string>();
        var culture = CultureInfo.CurrentUICulture;
        locales.Add(culture.Name.Replace('-', '_'));
        locales.Add(culture.TwoLetterISOLanguageName);
        locales.Add(Loc.IsEnglish ? "en" : "ru");
        if (ReadDefaultLocale(directory) is { } defaultLocale)
            locales.Add(defaultLocale);

        foreach (var locale in locales.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(locale) || locale.Contains('/') || locale.Contains('\\') || locale.Contains(".."))
                continue;
            if (ReadMessage(Path.Combine(directory, "_locales", locale, "messages.json"), key) is { } message)
                return message;
        }
        return manifestName;
    }

    private static string? ReadDefaultLocale(string directory)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, ManifestReader.FileName)), ManifestOptions);
            return doc.RootElement.TryGetProperty("default_locale", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static string? ReadMessage(string path, string key)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > ManifestReader.MaxManifestBytes)
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), ManifestOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                // Ключи сообщений в Chrome не зависят от регистра.
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object &&
                    property.Value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(message.GetString()))
                    return message.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
