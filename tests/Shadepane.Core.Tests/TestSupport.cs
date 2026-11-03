using System.Diagnostics;
using Shadepane.Core.Policy;

namespace Shadepane.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try
        {
            // Рекурсивное удаление на Windows вызывает для junction DeleteVolumeMountPoint, которому нужны
            // права администратора. Поэтому сначала удаляем ссылки по одной (без рекурсии — удаляется
            // только сама ссылка, не цель), потом остальное.
            RemoveLinks(new DirectoryInfo(Path));
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // временная папка: уборка не должна ронять тест
        }
    }

    private static void RemoveLinks(DirectoryInfo dir)
    {
        foreach (var sub in dir.EnumerateDirectories())
        {
            if (sub.LinkTarget is not null || sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                sub.Delete();
            else
                RemoveLinks(sub);
        }
    }
}

internal static class Ext
{
    /// <summary>Создаёт папку расширения с manifest.json и дополнительными файлами.</summary>
    public static string Create(string dir, string manifestJson, params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "manifest.json"), manifestJson);
        foreach (var (path, content) in files)
        {
            var full = System.IO.Path.Combine(dir, path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return dir;
    }

    public static string Mv3(string extra = "") =>
        $$"""{ "manifest_version": 3, "name": "Test", "version": "1.0"{{(extra.Length > 0 ? ", " + extra : "")}} }""";

    public static BrowserPolicy CorpPolicy => new BrowserPolicy { ProtectedHosts = ["*.corp.example", "crm.partner.example"] }.EnsureValid();

    public static string SamplesDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(System.IO.Path.Combine(dir.FullName, "samples")))
                dir = dir.Parent;
            return System.IO.Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("samples"), "samples");
        }
    }
}

/// <summary>
/// На Windows symlink требует SeCreateSymbolicLinkPrivilege (администратор или режим разработчика).
/// Без него тест пропускается, а не падает: это ограничение окружения, а не ошибка кода.
/// </summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (!LinkSupport.CanCreateSymlinks)
            Skip = "нет права создавать symlink (Windows без прав администратора и без режима разработчика)";
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "только Windows";
    }
}

internal static class LinkSupport
{
    public static bool CanCreateSymlinks { get; } = Probe();

    private static bool Probe()
    {
        using var tmp = new TempDir();
        var target = System.IO.Path.Combine(tmp.Path, "target.txt");
        File.WriteAllText(target, "");
        try
        {
            File.CreateSymbolicLink(System.IO.Path.Combine(tmp.Path, "link.txt"), target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Junction (mklink /J) не требует особых прав — именно его создаст злоумышленник без прав администратора.</summary>
    public static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidOperationException($"mklink /J завершился с кодом {process.ExitCode}: {stderr}");
    }
}
