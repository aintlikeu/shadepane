using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Shadepane.Core.Updates;
using Shadepane.Core.Localization;

namespace Shadepane.App.Updates;

/// <summary>
/// Автообновление через GitHub Releases: проверка последнего релиза, скачивание установщика только из релизов
/// этого репозитория, сверка SHA-256 по SHA256SUMS.txt того же релиза, тихая установка (Inno Setup) и выход.
/// Сеть: запрос к api.github.com раскрывает GitHub IP-адрес и версию браузера — поэтому проверку можно отключить.
/// </summary>
internal sealed class UpdateService
{
    private const long MaxInstallerBytes = 500L * 1024 * 1024;
    private const long MaxChecksumsBytes = 64 * 1024;

    private static readonly HttpClient Http = CreateClient();

    public UpdateService()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Current = ReleaseVersion.FromAssembly(info) ?? new ReleaseVersion(0, 0, 0);
    }

    public ReleaseVersion Current { get; }

    /// <summary>Установлен ли браузер нашим установщиком (рядом лежит деинсталлятор Inno Setup). Иначе — zip-версия.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>null — обновлений нет (или релизов ещё нет).</summary>
    public async Task<UpdateCandidate?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(UpdatePlanner.LatestReleaseApi, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return UpdatePlanner.Select(ReleaseInfo.TryParse(json), Current);
    }

    /// <summary>Скачивает установщик во временную папку и проверяет SHA-256. Возвращает путь к проверенному файлу.</summary>
    public async Task<string> DownloadAndVerifyAsync(UpdateCandidate update, IProgress<string> progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Shadepane-Update", update.Version.ToString());
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true); // никаких остатков от прошлых попыток
        Directory.CreateDirectory(dir);

        progress.Report(Loc.T("Загрузка контрольных сумм…", "Downloading checksums…"));
        var sumsText = await DownloadStringAsync(update.Checksums.DownloadUrl, MaxChecksumsBytes, ct);
        if (!Sha256Sums.Parse(sumsText).TryGetValue(update.Installer.Name, out var expected))
            throw new InvalidDataException(Loc.T($"в {UpdatePlanner.ChecksumsFile} нет строки для {update.Installer.Name}", $"{UpdatePlanner.ChecksumsFile} has no line for {update.Installer.Name}"));

        var path = Path.Combine(dir, update.Installer.Name);
        using (var response = await Http.GetAsync(update.Installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Installer.Size;
            if (total > MaxInstallerBytes)
                throw new InvalidDataException(Loc.T("установщик слишком большой", "the installer is too large"));
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            var lastReport = 0L;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                received += read;
                if (received > MaxInstallerBytes)
                    throw new InvalidDataException(Loc.T("установщик слишком большой", "the installer is too large"));
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                if (received - lastReport > 1024 * 1024)
                {
                    lastReport = received;
                    progress.Report(Loc.T($"Загрузка обновления: {Shadepane.Core.Browsing.ByteSize.Progress(received, total > 0 ? total : null)}", $"Downloading the update: {Shadepane.Core.Browsing.ByteSize.Progress(received, total > 0 ? total : null)}"));
                }
            }
        }

        progress.Report(Loc.T("Проверка контрольной суммы…", "Verifying the checksum…"));
        await using (var file = File.OpenRead(path))
        {
            if (!Sha256Sums.Matches(file, expected))
            {
                file.Close();
                File.Delete(path);
                throw new InvalidDataException(Loc.T("контрольная сумма установщика не совпала — файл удалён", "the installer checksum does not match; the file was deleted"));
            }
        }

        // Контрольная сумма из того же релиза защищает от повреждения, но не от подмены релиза.
        // Если сам браузер подписан, требуем действительную подпись того же издателя и у обновления.
        progress.Report(Loc.T("Проверка подписи…", "Verifying the signature…"));
        var ownSigner = Authenticode.SignerSubject(Environment.ProcessPath ?? "");
        if (ownSigner is not null)
        {
            var updateSigner = Authenticode.SignerSubject(path);
            if (!Authenticode.IsValidlySigned(path) || !string.Equals(ownSigner, updateSigner, StringComparison.Ordinal))
            {
                File.Delete(path);
                throw new InvalidDataException(Loc.T($"подпись установщика недействительна или от другого издателя ({updateSigner ?? "нет подписи"}) — файл удалён", $"the installer signature is invalid or from another publisher ({updateSigner ?? "unsigned"}); the file was deleted"));
            }
        }
        return path;
    }

    /// <summary>Тихая установка: Inno Setup закроет браузер, заменит файлы и запустит новую версию.</summary>
    public static void LaunchInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true, // для установки «для всех пользователей» Windows запросит повышение прав
        });

    private static async Task<string> DownloadStringAsync(string url, long maxBytes, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException(Loc.T($"{UpdatePlanner.ChecksumsFile} слишком большой", $"{UpdatePlanner.ChecksumsFile} is too large"));
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length > maxBytes ? throw new InvalidDataException(Loc.T($"{UpdatePlanner.ChecksumsFile} слишком большой", $"{UpdatePlanner.ChecksumsFile} is too large")) : text;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        // Общее имя клиента без личных данных; GitHub API требует User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Shadepane-Updater (+https://github.com/aintlikeu/shadepane)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
