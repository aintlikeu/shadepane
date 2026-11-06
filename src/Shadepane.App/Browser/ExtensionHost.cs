using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;
using Shadepane.Core.Extensions;
using Shadepane.Core.Localization;

namespace Shadepane.App.Browser;

/// <summary>
/// Пакет из Chrome Web Store, проверенный и распакованный во временную папку, — ещё не установлен.
/// Dispose удаляет папку (после установки копия уже лежит в ExtensionStaging).
/// </summary>
internal sealed class WebStorePackage(string id, string folder, ExtensionManifest manifest, string displayName, ValidationResult validation) : IDisposable
{
    public string Id { get; } = id;
    public string Folder { get; } = folder;
    public ExtensionManifest Manifest { get; } = manifest;
    public string DisplayName { get; } = displayName;
    /// <summary>Предварительная проверка политикой — чтобы не спрашивать подтверждение у заведомо запрещённого.</summary>
    public ValidationResult Validation { get; } = validation;

    public void Dispose() => ExtensionHost.DeleteQuietly(Folder);
}

/// <summary>
/// Единственный путь установки расширений: проверка → копия → загрузка копии в WebView2.
/// При запуске сверяет профиль WebView2 со списком одобренных и удаляет всё лишнее (fail closed).
/// </summary>
internal sealed class ExtensionHost
{
    private readonly ExtensionStaging _staging = new(AppPaths.ExtensionStaging);
    private readonly ApprovedExtensionStore _store = new(AppPaths.ApprovedExtensions);
    private List<ApprovedExtension> _approved = [];

    /// <summary>
    /// Компоненты, которые приносит сам WebView2 Runtime (найдены на стенде, Runtime 156.0.4314.58):
    /// удалить их нельзя (E_FAIL), а отключение не сохраняется между запусками и только ломает функцию.
    /// Оставляем их, НО только если удаление не удалось: ID вычисляется из открытого ключа, и распакованное
    /// расширение с тем же "key" получило бы тот же ID — такая подделка удаляется обычным RemoveAsync.
    /// </summary>
    private static readonly Dictionary<string, string> KnownRuntimeComponents = new(StringComparer.Ordinal)
    {
        ["mhjfbmdgcfjbbpaeojofohoefgiehjai"] = "Microsoft Edge PDF Viewer",
        ["dgiklkfkllikcanfonkcabmbdfmgleag"] = "Microsoft Clipboard Extension",
    };

    public IReadOnlyList<ApprovedExtension> Approved => _approved;


    /// <summary>
    /// До создания окружения WebView2: записи, которые больше не проходят проверку (например, тема Chrome, поставленная
    /// прежней версией), удаляются вместе с подготовленной копией. Без файлов движок не загрузит такое расширение при
    /// старте — иначе оно могло бы ронять движок раньше, чем SyncAsync успеет убрать его из профиля.
    /// </summary>
    public void RevokeInvalidBeforeEngineStart()
    {
        List<ApprovedExtension> stored;
        try
        {
            stored = [.. _store.Load()];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return; // повреждённый список — разберётся SyncAsync (fail closed)
        }

        var keep = new List<ApprovedExtension>();
        foreach (var record in stored)
        {
            var check = _staging.Verify(record.StagedPath, record.Hash, AppServices.ExtensionPolicy, record.Grants);
            if (check.IsAllowed)
            {
                keep.Add(record);
                continue;
            }
            AppServices.Audit.Write("extension-revoked", new { record.Id, record.Name, stage = "before-engine-start", reasons = check.Violations.Select(v => v.Message) });
            // Удаляем только внутри своей папки: путь из файла списка мог быть подменён.
            if (ExtensionDirectory.IsInside(record.StagedPath, _staging.Root))
                DeleteQuietly(record.StagedPath);
        }
        if (keep.Count != stored.Count)
            _store.Save(keep);
    }

    public async Task SyncAsync(CoreWebView2Profile profile)
    {
        var policy = AppServices.ExtensionPolicy;
        var audit = AppServices.Audit;

        List<ApprovedExtension> stored;
        try
        {
            stored = [.. _store.Load()];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            audit.Write("extension-store-corrupted", new { error = ex.Message });
            stored = []; // fail closed: ниже из профиля будет удалено всё
        }

        var valid = new List<ApprovedExtension>();
        foreach (var record in stored)
        {
            var check = _staging.Verify(record.StagedPath, record.Hash, policy, record.Grants);
            if (check.IsAllowed)
                valid.Add(record);
            else
                audit.Write("extension-revoked", new { record.Id, record.Name, reasons = check.Violations.Select(v => v.Message) });
        }

        var installed = await profile.GetBrowserExtensionsAsync();

        var unremovable = new List<string>();
        foreach (var extension in installed)
        {
            if (valid.Any(r => r.Id == extension.Id))
                continue;
            await DisposeOfUnapprovedAsync(extension, unremovable);
        }
        if (unremovable.Count > 0)
        {
            // Известные компоненты Runtime сюда не попадают (см. KnownRuntimeComponents). Неизвестное
            // расширение, которое нельзя ни удалить, ни отключить, — это обход установщика: fail closed.
            throw new InvalidOperationException(
                Loc.T("В профиле есть неизвестное расширение, которое нельзя удалить или отключить: " +
                string.Join(", ", unremovable) + ". Запуск остановлен; подробности — в audit.log.", "The profile contains an unknown extension that cannot be removed or disabled: " +
                string.Join(", ", unremovable) + ". Startup stopped; details are in audit.log."));
        }

        var synced = new List<ApprovedExtension>();
        foreach (var record in valid)
        {
            var extension = installed.FirstOrDefault(e => e.Id == record.Id);
            try
            {
                // WebView2 сам удаляет расширение из профиля, если его файлы менялись, — тогда ставим заново.
                extension ??= await profile.AddBrowserExtensionAsync(record.StagedPath);
                if (!extension.IsEnabled)
                    await extension.EnableAsync(true);
                synced.Add(record with { Id = extension.Id, Name = extension.Name });
            }
            catch (Exception ex)
            {
                audit.Write("extension-load-failed", new { record.Id, record.Name, error = ex.Message });
            }
        }

        _approved = synced;
        _store.Save(_approved);
        CleanupStaging();
        DeleteQuietly(AppPaths.WebStoreIncoming); // остатки прерванной установки из магазина
    }

    /// <summary>
    /// Возвращает добавленную запись или null с причинами отказа.
    /// ManifestException / ExtensionDirectoryException пробрасываются — их показывает вызывающий.
    /// </summary>
    /// <param name="grants">Права, которые пользователь выдал этому расширению в окне при установке (запоминаются в записи).</param>
    public async Task<(ApprovedExtension? Added, ValidationResult Validation)> AddFromFolderAsync(CoreWebView2Profile profile, string folder, ExtensionGrants? grants = null)
    {
        var audit = AppServices.Audit;
        grants = grants is { IsEmpty: false } ? grants : null;
        var result = _staging.Stage(folder, AppServices.ExtensionPolicy, grants);
        if (result.Staged is null)
        {
            audit.Write("extension-rejected", new { folder, reasons = result.Validation.Violations.Select(v => v.Message) });
            return (null, result.Validation);
        }

        var staged = result.Staged;
        CoreWebView2BrowserExtension extension;
        try
        {
            extension = await profile.AddBrowserExtensionAsync(staged.Path);
        }
        catch
        {
            CleanupStaging(); // копия больше никому не нужна
            throw;
        }

        var record = new ApprovedExtension(extension.Id, extension.Name, staged.Path, staged.Hash,
            staged.Manifest.PopupPath, staged.Manifest.IconPath, DateTimeOffset.UtcNow, grants);
        _approved.RemoveAll(r => r.Id == record.Id);
        _approved.Add(record);
        _store.Save(_approved);
        audit.Write("extension-added", new { record.Id, record.Name, record.Hash, folder, grantedPermissions = grants?.Permissions, grantedAllSites = grants?.AllSitesAccess });
        return (record, result.Validation);
    }

    private static readonly HttpClient WebStoreHttp = CreateWebStoreClient();

    /// <summary>
    /// Скачивает пакет расширения из Chrome Web Store, проверяет подпись и ID, распаковывает и записывает ключ
    /// разработчика в manifest.json (тот же ID, что в магазине). Не устанавливает: дальше — подтверждение
    /// пользователя и <see cref="AddFromFolderAsync"/> с обычной проверкой политикой.
    /// Бросает CrxException, HttpRequestException, TaskCanceledException (тайм-аут), ManifestException, IOException.
    /// </summary>
    public async Task<WebStorePackage> DownloadFromWebStoreAsync(string id, string? browserVersion, CancellationToken ct)
    {
        var url = WebStore.PackageUrl(id, browserVersion);
        byte[] bytes;
        Uri? finalUri;
        using (var response = await WebStoreHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            finalUri = response.RequestMessage?.RequestUri;
            if (response.StatusCode == HttpStatusCode.NoContent)
                throw new CrxException(Loc.T("магазин не отдал пакет: расширение удалено, недоступно в вашей стране или требует более новую версию движка",
                    "the store returned no package: the extension was removed, is not available in your region, or needs a newer engine version"));
            response.EnsureSuccessStatusCode();
            if (!WebStore.IsTrustedPackageSource(finalUri))
                throw new CrxException(Loc.T($"пакет пришёл не с серверов Google: {finalUri?.Host}", $"the package did not come from Google servers: {finalUri?.Host}"));
            if (response.Content.Headers.ContentLength > WebStore.MaxPackageBytes)
                throw new CrxException(Loc.T("пакет слишком большой", "the package is too large"));
            bytes = await ReadLimitedAsync(response.Content, WebStore.MaxPackageBytes, ct);
        }

        var package = CrxPackage.Parse(bytes);
        if (package.ExtensionId != id)
            throw new CrxException(Loc.T($"в пакете другое расширение ({package.ExtensionId}), а не {id}", $"the package contains a different extension ({package.ExtensionId}), not {id}"));
        AppServices.Audit.Write("webstore-package-downloaded", new { id, bytes = bytes.Length, host = finalUri?.Host });

        var folder = Path.Combine(AppPaths.WebStoreIncoming, Guid.NewGuid().ToString("N"));
        try
        {
            WebStore.ExtractArchive(package.Archive, folder);
            WebStore.WriteDeveloperKey(folder, package.PublicKey);
            var manifest = ManifestReader.ReadFromDirectory(folder);
            var validation = ExtensionValidator.Validate(manifest, AppServices.ExtensionPolicy);
            return new WebStorePackage(id, folder, manifest, WebStore.DisplayName(folder, manifest.Name), validation);
        }
        catch
        {
            DeleteQuietly(folder);
            throw;
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new CrxException(Loc.T("пакет слишком большой", "the package is too large"));
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static HttpClient CreateWebStoreClient()
    {
        // Перенаправления: clients2.google.com → clients2.googleusercontent.com; с HTTPS на HTTP .NET не переходит.
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        // Общее имя клиента без личных данных.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Shadepane (+https://github.com/aintlikeu/shadepane)");
        return client;
    }

    internal static void DeleteQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppServices.Audit.Write("webstore-cleanup-deferred", new { folder, error = ex.Message });
        }
    }

    public async Task RemoveAsync(CoreWebView2Profile profile, string id)
    {
        var installed = await profile.GetBrowserExtensionsAsync();
        var extension = installed.FirstOrDefault(e => e.Id == id);
        if (extension is not null)
            await extension.RemoveAsync();

        _approved.RemoveAll(r => r.Id == id);
        _store.Save(_approved);
        CleanupStaging();
        AppServices.Audit.Write("extension-removed", new { id });
    }

    /// <summary>
    /// Неодобренное расширение: удалить; если нельзя и это известный компонент Runtime — оставить;
    /// иначе отключить. Расширение, поставленное через AddBrowserExtension (в т.ч. в обход нашего
    /// установщика), удаляется. Неизвестное, которое нельзя ни удалить, ни отключить, останавливает запуск.
    /// </summary>
    private static async Task DisposeOfUnapprovedAsync(CoreWebView2BrowserExtension extension, List<string> unremovable)
    {
        var audit = AppServices.Audit;
        audit.Write("extension-unapproved-found", new { extension.Id, extension.Name, extension.IsEnabled });

        var removeError = await TryAsync(extension.RemoveAsync);
        if (removeError is null)
        {
            audit.Write("extension-removed-unapproved", new { extension.Id, extension.Name });
            return;
        }

        if (KnownRuntimeComponents.ContainsKey(extension.Id))
        {
            audit.Write("extension-runtime-component-kept", new { extension.Id, extension.Name, removeError });
            return;
        }

        var disableError = extension.IsEnabled ? await TryAsync(() => extension.EnableAsync(false)) : null;
        if (disableError is null)
        {
            audit.Write("extension-disabled-unapproved", new { extension.Id, extension.Name, removeError });
            return;
        }

        audit.Write("extension-unremovable", new { extension.Id, extension.Name, removeError, disableError });
        unremovable.Add($"{extension.Name} ({extension.Id})");
    }

    /// <returns>null при успехе, иначе описание ошибки (тип и HRESULT).</returns>
    private static async Task<string?> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotImplementedException)
        {
            return Diagnostics.Describe(ex);
        }
    }

    private void CleanupStaging()
    {
        foreach (var dir in _staging.DeleteUnreferenced(_approved.Select(a => a.StagedPath)))
            AppServices.Audit.Write("staging-cleanup-deferred", new { dir });
    }
}
