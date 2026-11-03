using Shadepane.Core.Policy;

namespace Shadepane.Core.Extensions;

public sealed record StagedExtension(string Path, string Hash, ExtensionManifest Manifest);

public sealed record StagingResult(StagedExtension? Staged, ValidationResult Validation);

/// <summary>
/// Копирует расширение в папку, которую контролирует браузер, проверяет КОПИЮ и только её отдаёт движку.
/// Так между проверкой и загрузкой нельзя подменить файлы в исходной папке (TOCTOU).
/// </summary>
public sealed class ExtensionStaging(string stagingRoot)
{
    private const string IncomingPrefix = ".incoming-";

    public string Root { get; } = Path.GetFullPath(stagingRoot);

    public StagingResult Stage(string sourceDirectory, BrowserPolicy policy)
    {
        var source = Path.GetFullPath(sourceDirectory);
        if (ExtensionDirectory.IsInside(source, Root) || string.Equals(source, Root, StringComparison.OrdinalIgnoreCase))
            throw new ExtensionDirectoryException("нельзя добавить расширение из служебной папки браузера");

        Directory.CreateDirectory(Root);
        var incoming = Path.Combine(Root, IncomingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            ExtensionDirectory.CopyTree(source, incoming);

            var manifest = ManifestReader.ReadFromDirectory(incoming);
            var validation = ExtensionValidator.Validate(manifest, policy);
            if (!validation.IsAllowed)
                return new StagingResult(null, validation);

            var hash = ExtensionDirectory.ComputeHash(incoming);
            // Hex-имя никогда не начинается с '_' (такие папки WebView2 отклоняет).
            var final = Path.Combine(Root, hash[..32]);

            if (Directory.Exists(final))
            {
                // Та же версия уже подготовлена; если содержимое расходится — копию подменили.
                if (SafeHash(final) != hash)
                {
                    Directory.Delete(final, recursive: true);
                    Directory.Move(incoming, final);
                }
            }
            else
            {
                Directory.Move(incoming, final);
            }

            return new StagingResult(new StagedExtension(final, hash, manifest), validation);
        }
        finally
        {
            if (Directory.Exists(incoming))
                Directory.Delete(incoming, recursive: true);
        }
    }

    /// <summary>
    /// Перепроверка при каждом запуске: копия цела (хеш) и всё ещё проходит ТЕКУЩУЮ политику
    /// (администратор мог ужесточить её после одобрения расширения).
    /// </summary>
    public ValidationResult Verify(string stagedPath, string expectedHash, BrowserPolicy policy)
    {
        if (!ExtensionDirectory.IsInside(stagedPath, Root))
            return ValidationResult.Denied("staging-path", "путь расширения вне служебной папки браузера");
        if (!Directory.Exists(stagedPath))
            return ValidationResult.Denied("missing", "файлы расширения не найдены");

        try
        {
            if (ExtensionDirectory.ComputeHash(stagedPath) != expectedHash)
                return ValidationResult.Denied("tampered", "файлы расширения изменены после одобрения");
            return ExtensionValidator.Validate(ManifestReader.ReadFromDirectory(stagedPath), policy);
        }
        catch (Exception ex) when (ex is ManifestException or ExtensionDirectoryException or IOException or UnauthorizedAccessException)
        {
            return ValidationResult.Denied("invalid", ex.Message);
        }
    }

    /// <summary>Удаляет подготовленные копии, на которые больше никто не ссылается. Возвращает пути, которые удалить не удалось.</summary>
    public IReadOnlyList<string> DeleteUnreferenced(IEnumerable<string> keepPaths)
    {
        var failed = new List<string>();
        if (!Directory.Exists(Root))
            return failed;

        var keep = new HashSet<string>(keepPaths.Select(Path.GetFullPath), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (keep.Contains(Path.GetFullPath(dir)))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(dir); // например, файлы ещё держит движок — удалим при следующем запуске
            }
        }
        return failed;
    }

    private static string? SafeHash(string dir)
    {
        try { return ExtensionDirectory.ComputeHash(dir); }
        catch (ExtensionDirectoryException) { return null; }
    }
}
