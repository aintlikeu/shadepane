using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Shadepane.App.Native;
using Shadepane.Core.Policy;

namespace Shadepane.App.Policy;

/// <summary>
/// Откуда браузер берёт политику, по приоритету:
/// 1) HKLM\Software\Policies\Shadepane, значение "Policy" (JSON) — как групповые политики Chrome/Edge;
/// 2) %ProgramData%\Shadepane\policy.json — только если путь без ссылок, а файл и папка закрыты для записи не-администраторам;
/// 3) только Debug-сборка: policy.json рядом с exe, БЕЗ проверки прав — для стенда.
/// Существующий, но небезопасный или битый источник останавливает запуск (см. BrowserPolicy.LoadFirst).
/// </summary>
internal static class PolicySources
{
    public const string RegistryKeyPath = @"Software\Policies\Shadepane";
    public const string RegistryValueName = "Policy";

    public static string ProgramDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Shadepane");

    public static string ProgramDataFile => Path.Combine(ProgramDataDirectory, "policy.json");

    public static string DevFile => Path.Combine(AppContext.BaseDirectory, "policy.json");

    private const long MaxPolicyBytes = 1024 * 1024;

    public static IEnumerable<PolicySourceCandidate> Candidates()
    {
        // Проверка прав и чтение содержимого идут через ОДИН дескриптор (VerifiedRead), а не через
        // повторное открытие по пути: иначе между проверкой и чтением путь можно перенаправить (TOCTOU).
        var registry = new VerifiedRead(VerifyAndReadRegistry);
        yield return new PolicySourceCandidate(
            $@"HKLM\{RegistryKeyPath}\{RegistryValueName}",
            Exists: () => WithKey(key => key?.GetValue(RegistryValueName) is not null),
            FindSecurityProblem: registry.Verify,
            ReadJson: registry.Read);

        var programData = new VerifiedRead(VerifyAndReadProgramData);
        yield return new PolicySourceCandidate(
            ProgramDataFile,
            Exists: () => File.Exists(ProgramDataFile),
            FindSecurityProblem: programData.Verify,
            ReadJson: programData.Read);

#if DEBUG
        yield return new PolicySourceCandidate(
            DevFile + " [DEBUG, права не проверяются]",
            Exists: () => File.Exists(DevFile),
            FindSecurityProblem: () => null,
            ReadJson: () => File.ReadAllText(DevFile));
#endif
    }

    // Явно 64-битное представление реестра, чтобы не зависеть от разрядности процесса.
    private static T WithKey<T>(Func<RegistryKey?, T> action)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(RegistryKeyPath, writable: false);
        return action(key);
    }

    /// <summary>Содержимое отдаётся только после успешной проверки и только то, что прочитано при проверке.</summary>
    private sealed class VerifiedRead(Func<(string? Problem, string? Json)> verifyAndRead)
    {
        private string? _json;

        public string? Verify()
        {
            var (problem, json) = verifyAndRead();
            _json = problem is null ? json : null;
            return problem;
        }

        public string Read() => _json ?? throw new InvalidOperationException("источник политики не прошёл проверку");
    }

    private static (string?, string?) VerifyAndReadRegistry() => WithKey(key =>
    {
        if (key is null)
            return ("ключ реестра исчез", (string?)null);
        var problem = CheckRegistryKey(key);
        return problem is not null ? (problem, null) : ((string?)null, ReadRegistryJson(key));
    });

    private static (string?, string?) VerifyAndReadProgramData()
    {
        // 1. Ни один элемент пути к папке не должен быть ссылкой: иначе проверка прав увидела бы цель
        //    ссылки, а сотрудник, владеющий ссылкой, мог бы её перенаправить.
        for (var dir = new DirectoryInfo(ProgramDataDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.LinkTarget is not null || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return ($"{dir.FullName} — ссылка (symlink/junction)", null);
        }

        // 2. Папка закрыта: подменить в ней файл нельзя. Саму папку (не ссылку) в ProgramData
        //    сотрудник удалить или переименовать не может.
        var dirSecurity = new DirectoryInfo(ProgramDataDirectory).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var dirProblem = AdminOnlyWriteCheck.FindProblem(Owner(dirSecurity), FileAces(dirSecurity), AdminOnlyWriteCheck.DirectoryWriteMask);
        if (dirProblem is not null)
            return ("папка " + Describe(dirProblem), null);

        // 3. Файл открываем один раз; путь, права и содержимое берём из этого дескриптора.
        //    FileShare.Read — никто не изменит файл, пока мы его читаем.
        using var stream = new FileStream(ProgramDataFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var finalPath = NativeMethods.GetFinalPath(stream.SafeFileHandle);
        if (!string.Equals(finalPath, Path.GetFullPath(ProgramDataFile), StringComparison.OrdinalIgnoreCase))
            return ($"policy.json разрешается в {finalPath}", null);

        var fileSecurity = stream.GetAccessControl();
        var fileProblem = AdminOnlyWriteCheck.FindProblem(Owner(fileSecurity), FileAces(fileSecurity), AdminOnlyWriteCheck.FileWriteMask);
        if (fileProblem is not null)
            return ("файл " + Describe(fileProblem), null);

        if (stream.Length > MaxPolicyBytes)
            return ($"policy.json больше {MaxPolicyBytes / 1024} КБ", null);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return (null, reader.ReadToEnd());
    }

    private static string ReadRegistryJson(RegistryKey key)
    {
        return key.GetValueKind(RegistryValueName) switch
        {
            RegistryValueKind.String => (string)key.GetValue(RegistryValueName)!,
            RegistryValueKind.MultiString => string.Join("\n", (string[])key.GetValue(RegistryValueName)!),
            var kind => throw new PolicyException($"значение {RegistryValueName} должно быть REG_SZ или REG_MULTI_SZ, а не {kind}"),
        };
    }

    private static string? CheckRegistryKey(RegistryKey key)
    {
        var security = key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var aces = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<RegistryAccessRule>()
            .Select(r => new AceInfo(r.IdentityReference.Value, r.AccessControlType == AccessControlType.Allow,
                r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly), (int)r.RegistryRights));
        return Describe(AdminOnlyWriteCheck.FindProblem(Owner(security), aces, AdminOnlyWriteCheck.RegistryWriteMask));
    }

    private static IEnumerable<AceInfo> FileAces(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Select(r => new AceInfo(r.IdentityReference.Value, r.AccessControlType == AccessControlType.Allow,
                r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly), (int)r.FileSystemRights));

    private static string? Owner(ObjectSecurity security) =>
        (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;

    /// <summary>Добавляет к SID имя учётной записи, чтобы администратору было понятно, что исправлять.</summary>
    private static string? Describe(string? problem)
    {
        if (problem is null)
            return null;
        foreach (var token in problem.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith("S-1-", StringComparison.Ordinal))
                continue;
            try
            {
                var account = new SecurityIdentifier(token).Translate(typeof(NTAccount)).Value;
                return problem.Replace(token, $"{token} ({account})");
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                return problem;
            }
        }
        return problem;
    }
}
