namespace Shadepane.Core.Policy;

/// <summary>Одна запись ACL в платформенно-независимом виде (SID строкой, маска прав числом).</summary>
public sealed record AceInfo(string Sid, bool IsAllow, bool IsInheritOnly, int AccessMask);

/// <summary>
/// Правило «изменять может только администратор» для источника политики.
/// Чистая функция над SID и масками — тестируется без Windows; перевод реальных ACL — в приложении.
/// </summary>
public static class AdminOnlyWriteCheck
{
    public const string Administrators = "S-1-5-32-544";
    public const string LocalSystem = "S-1-5-18";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    /// <summary>CREATOR OWNER — шаблон для дочерних объектов, на сам объект не действует.</summary>
    public const string CreatorOwner = "S-1-3-0";

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase) { Administrators, LocalSystem, TrustedInstaller };

    // Общие биты: DELETE, WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE.
    private const int CommonWrite = 0x0001_0000 | 0x0004_0000 | 0x0008_0000 | 0x1000_0000 | 0x4000_0000;

    /// <summary>Файл: WriteData, AppendData, WriteExtendedAttributes, WriteAttributes + общие.</summary>
    public const int FileWriteMask = 0x2 | 0x4 | 0x10 | 0x100 | CommonWrite;

    /// <summary>Папка: создание файлов/подпапок, удаление дочерних (подмена файла) + как у файла.</summary>
    public const int DirectoryWriteMask = FileWriteMask | 0x40;

    /// <summary>Ключ реестра: SetValue, CreateSubKey, CreateLink + общие.</summary>
    public const int RegistryWriteMask = 0x2 | 0x4 | 0x20 | CommonWrite;

    public static bool IsTrusted(string? sid) => sid is not null && Trusted.Contains(sid);

    /// <summary>null — безопасно; иначе описание проблемы (с SID, чтобы администратор мог исправить).</summary>
    public static string? FindProblem(string? ownerSid, IEnumerable<AceInfo> aces, int writeMask)
    {
        // Владелец всегда может переписать ACL, поэтому он должен быть доверенным.
        if (!IsTrusted(ownerSid))
            return $"владелец {ownerSid ?? "неизвестен"}";

        foreach (var ace in aces)
        {
            // Deny только сужает права; inherit-only и CREATOR OWNER на сам объект не действуют.
            if (!ace.IsAllow || ace.IsInheritOnly || ace.Sid == CreatorOwner)
                continue;
            if (!IsTrusted(ace.Sid) && (ace.AccessMask & writeMask) != 0)
                return $"право записи у {ace.Sid} (маска 0x{ace.AccessMask:X})";
        }
        return null;
    }
}
