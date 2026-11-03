using System.IO;
using Microsoft.Web.WebView2.Core;
using Shadepane.Core.Extensions;

namespace Shadepane.App.Browser;

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


    public async Task SyncAsync(CoreWebView2Profile profile)
    {
        var policy = AppServices.Policy;
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
            var check = _staging.Verify(record.StagedPath, record.Hash, policy);
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
                "В профиле есть неизвестное расширение, которое нельзя удалить или отключить: " +
                string.Join(", ", unremovable) + ". Запуск остановлен; подробности — в audit.log.");
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
    }

    /// <summary>
    /// Возвращает добавленную запись или null с причинами отказа.
    /// ManifestException / ExtensionDirectoryException пробрасываются — их показывает вызывающий.
    /// </summary>
    public async Task<(ApprovedExtension? Added, ValidationResult Validation)> AddFromFolderAsync(CoreWebView2Profile profile, string folder)
    {
        var audit = AppServices.Audit;
        var result = _staging.Stage(folder, AppServices.Policy);
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
            staged.Manifest.PopupPath, staged.Manifest.IconPath, DateTimeOffset.UtcNow);
        _approved.RemoveAll(r => r.Id == record.Id);
        _approved.Add(record);
        _store.Save(_approved);
        audit.Write("extension-added", new { record.Id, record.Name, record.Hash, folder });
        return (record, result.Validation);
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
