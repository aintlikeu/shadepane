using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Shadepane.Core.Extensions;

/// <summary>Безопасная работа с файлами расширения: перечисление, хеш, копирование, разрешение путей.</summary>
public static class ExtensionDirectory
{
    public const int MaxFiles = 10_000;
    public const long MaxTotalBytes = 256L * 1024 * 1024;

    private static readonly EnumerationOptions FlatEnumeration = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,          // скрытые/системные файлы тоже учитываем в хеше
        IgnoreInaccessible = false,    // недоступный файл — ошибка, а не пропуск
        ReturnSpecialDirectories = false,
    };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Относительные пути всех файлов (через '/', отсортированы ordinal).
    /// Бросает ExtensionDirectoryException на ссылках, лимитах и недопустимых именах.
    /// </summary>
    public static IReadOnlyList<string> ListFiles(string root)
    {
        var rootInfo = new DirectoryInfo(Path.GetFullPath(root));
        if (!rootInfo.Exists)
            throw new ExtensionDirectoryException($"папка не найдена: {root}");
        if (IsLink(rootInfo))
            throw new ExtensionDirectoryException("папка расширения не может быть ссылкой (symlink/junction)");

        var files = new List<string>();
        long totalBytes = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in dir.EnumerateFileSystemInfos("*", FlatEnumeration))
            {
                var relative = Path.GetRelativePath(rootInfo.FullName, entry.FullName);
                if (IsLink(entry))
                    throw new ExtensionDirectoryException($"ссылки (symlink/junction) в расширении запрещены: {relative}");
                if (entry.Name.IndexOfAny(['\\', ':']) >= 0)
                    throw new ExtensionDirectoryException($"недопустимое имя файла: {relative}");

                switch (entry)
                {
                    case DirectoryInfo sub:
                        pending.Push(sub);
                        break;
                    case FileInfo file:
                        totalBytes += file.Length;
                        if (files.Count >= MaxFiles)
                            throw new ExtensionDirectoryException($"в расширении больше {MaxFiles} файлов");
                        if (totalBytes > MaxTotalBytes)
                            throw new ExtensionDirectoryException($"расширение больше {MaxTotalBytes / (1024 * 1024)} МБ");
                        files.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
                        break;
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    /// SHA-256 содержимого: H( for each file: len(path) || path || SHA256(content) ).
    /// Префикс длины и вложенный хеш делают кодирование однозначным (нельзя «перенести» байты между файлами).
    /// </summary>
    public static string ComputeHash(string root)
    {
        var rootFull = Path.GetFullPath(root);
        using var outer = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthPrefix = stackalloc byte[4];

        foreach (var relative in ListFiles(rootFull))
        {
            var pathBytes = Encoding.UTF8.GetBytes(relative);
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, pathBytes.Length);
            outer.AppendData(lengthPrefix);
            outer.AppendData(pathBytes);

            using var stream = File.OpenRead(Path.Combine(rootFull, relative));
            outer.AppendData(SHA256.HashData(stream));
        }

        return Convert.ToHexStringLower(outer.GetHashAndReset());
    }

    /// <summary>Копирует дерево. Ссылки и лимиты проверяются до копирования, результат — только обычные файлы.</summary>
    public static void CopyTree(string source, string destination)
    {
        var sourceFull = Path.GetFullPath(source);
        var destFull = Path.GetFullPath(destination);
        if (IsInside(destFull, sourceFull) || IsInside(sourceFull, destFull))
            throw new ExtensionDirectoryException("исходная и целевая папки не должны быть вложены друг в друга");

        var files = ListFiles(sourceFull);
        Directory.CreateDirectory(destFull);
        foreach (var relative in files)
        {
            var target = Path.Combine(destFull, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(sourceFull, relative), target, overwrite: false);
        }
    }

    /// <summary>
    /// Превращает путь из манифеста ("popup.html", "/ui/popup.html?tab=1") в полный путь внутри root.
    /// Отклоняет абсолютные пути, "..", обратные слеши и всё, что выходит за пределы root.
    /// urlPath — путь для chrome-extension:// URL (с сохранённым ?query/#fragment).
    /// </summary>
    public static bool TryResolveInside(string root, string? relative, out string? fullPath, out string? urlPath)
    {
        fullPath = null;
        urlPath = null;
        if (string.IsNullOrWhiteSpace(relative))
            return false;

        var cut = relative.IndexOfAny(['?', '#']);
        var pathPart = (cut >= 0 ? relative[..cut] : relative).TrimStart('/');
        var suffix = cut >= 0 ? relative[cut..] : "";

        if (pathPart.Length == 0 || pathPart.Contains('\\') || pathPart.Contains(':'))
            return false;
        var segments = pathPart.Split('/');
        if (segments.Any(s => s is "" or "." or ".."))
            return false;

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, Path.Combine(segments)));
        if (!IsInside(candidate, rootFull) || !File.Exists(candidate))
            return false;

        fullPath = candidate;
        urlPath = pathPart + suffix;
        return true;
    }

    /// <summary>Лежит ли path строго внутри directory (сравнение по полным путям).</summary>
    public static bool IsInside(string path, string directory)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(dir, PathComparison);
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
