namespace Shadepane.Core.IO;

/// <summary>Запись файла целиком через временный файл и переименование: сбой не оставит полфайла.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, full, overwrite: true);
    }
}
