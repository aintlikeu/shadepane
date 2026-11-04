using System.Text.Json;
using System.Text.Json.Serialization;
using Shadepane.Core.Hosts;
using Shadepane.Core.Navigation;

namespace Shadepane.Core.Policy;

public sealed class PolicyException(string message) : Exception(message);

/// <summary>
/// Источник политики. FindSecurityProblem возвращает null, если писать в источник могут только
/// администраторы/SYSTEM, иначе — описание проблемы.
/// </summary>
public sealed record PolicySourceCandidate(string Name, Func<bool> Exists, Func<string?> FindSecurityProblem, Func<string> ReadJson);

/// <summary>
/// Политика браузера. В прототипе читается из JSON; в продукте тот же объект будет
/// заполняться из групповых политик (HKLM\Software\Policies\...) или с сервера администратора.
/// </summary>
public sealed class BrowserPolicy
{
    /// <summary>
    /// API-права, которые по умолчанию делают расширение недопустимым.
    /// Содержимое: tabCapture/desktopCapture/pageCapture — прямой захват; activeTab — captureVisibleTab
    /// по клику; debugger — полный доступ к странице через CDP; downloads — скачивание мимо запрета
    /// загрузок браузера (не проверено, перехватывает ли его DownloadStarting).
    /// Метаданные (адреса и заголовки корпоративных страниц): tabs, history, webNavigation,
    /// declarativeNetRequestFeedback, topSites, sessions; proxy — весь трафик через чужой сервер.
    /// clipboardRead здесь НЕТ: им управляет AllowExtensionClipboardRead.
    /// Чёрный список не успевает за новыми правами Chromium — для продуктива используйте AllowedPermissions.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultBlockedPermissions =
    [
        "tabCapture", "desktopCapture", "pageCapture", "debugger", "activeTab", "downloads",
        "tabs", "history", "webNavigation", "declarativeNetRequestFeedback", "topSites", "sessions", "proxy",
    ];

    public const string ClipboardReadPermission = "clipboardRead";

    public static BrowserPolicy Default { get; } = new();

    /// <summary>Домашняя страница. Задана — действует и блокирует настройку пользователя; null — решает пользователь.</summary>
    public string? HomeUrl { get; init; }

    /// <summary>Шаблон поиска с {query} (https). Задан — действует и блокирует выбор поисковика пользователем.</summary>
    public string? SearchUrl { get; init; }

    /// <summary>Домены, к которым расширениям запрещён любой доступ: "crm.corp.example", "*.corp.example".</summary>
    public IReadOnlyList<string> ProtectedHosts { get; init; } = [];

    public IReadOnlyList<string> BlockedPermissions { get; init; } = DefaultBlockedPermissions;

    /// <summary>
    /// Режим белого списка: если задан, расширению разрешены ТОЛЬКО эти API-права (и при этом не из BlockedPermissions).
    /// null — режим чёрного списка.
    /// </summary>
    public IReadOnlyList<string>? AllowedPermissions { get; init; }

    /// <summary>Разрешить расширениям доступ ко всем сайтам (&lt;all_urls&gt;, *://*/*). Даже при true пересечение с ProtectedHosts запрещено.</summary>
    public bool AllowBroadHostAccess { get; init; }

    public bool AllowManifestV2 { get; init; }

    /// <summary>
    /// Разрешить расширениям право clipboardRead (чтение буфера обмена). null — решает пользователь
    /// (по умолчанию разрешено: приоритет продукта — защита от снимков экрана, а нужные расширения работают
    /// через буфер); задано — действует и блокирует настройку. Единственный источник истины для clipboardRead:
    /// в blockedPermissions/allowedPermissions его указывать нельзя.
    /// </summary>
    public bool? AllowExtensionClipboardRead { get; init; }

    /// <summary>Загрузка файлов. null — решает пользователь (по умолчанию разрешено); задано — действует и блокирует настройку.</summary>
    public bool? AllowDownloads { get; init; }

    /// <summary>В RDP-сессии скриншот делается на клиентской машине, и флаг защиты окна там не действует.</summary>
    public bool BlockInRemoteSession { get; init; } = true;

    private IReadOnlyList<HostPattern>? _protectedPatterns;
    private HashSet<string>? _blockedSet;
    private HashSet<string>? _allowedSet;

    [JsonIgnore]
    public IReadOnlyList<HostPattern> ProtectedHostPatterns => _protectedPatterns ??= ParseProtectedHosts();

    [JsonIgnore]
    public IReadOnlySet<string> BlockedPermissionSet =>
        // Права в Chrome регистрозависимы, но сравниваем без учёта регистра: строже — безопаснее.
        _blockedSet ??= new HashSet<string>(BlockedPermissions ?? [], StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public IReadOnlySet<string>? AllowedPermissionSet =>
        AllowedPermissions is null ? null : _allowedSet ??= new HashSet<string>(AllowedPermissions, StringComparer.OrdinalIgnoreCase);

    /// <summary>Адрес http(s) на защищённом домене (protectedHosts). Некорректный или иной адрес — false.</summary>
    public bool IsProtectedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !UrlPolicy.IsWebScheme(uri))
            return false;
        if (!HostPattern.TryNormalizeHost(uri.IdnHost, out var host, out _))
            return false;
        var page = new HostPattern(HostPatternKind.Exact, host!);
        return ProtectedHostPatterns.Any(p => p.Overlaps(page));
    }

    /// <summary>
    /// Политика для проверки расширений с учётом настройки пользователя: если администратор не задал
    /// allowExtensionClipboardRead, подставляется выбор пользователя. Копируются ВСЕ свойства — тест
    /// BrowserPolicyCopyTests ловит забытые при добавлении новых.
    /// </summary>
    public BrowserPolicy ForExtensions(bool userAllowsClipboardRead) =>
        AllowExtensionClipboardRead is not null
            ? this
            : new BrowserPolicy
            {
                HomeUrl = HomeUrl,
                SearchUrl = SearchUrl,
                ProtectedHosts = ProtectedHosts,
                BlockedPermissions = BlockedPermissions,
                AllowedPermissions = AllowedPermissions,
                AllowBroadHostAccess = AllowBroadHostAccess,
                AllowManifestV2 = AllowManifestV2,
                AllowExtensionClipboardRead = userAllowsClipboardRead,
                AllowDownloads = AllowDownloads,
                BlockInRemoteSession = BlockInRemoteSession,
            }.EnsureValid();

    /// <summary>Проверяет политику целиком; бросает PolicyException с понятным сообщением.</summary>
    public BrowserPolicy EnsureValid()
    {
        if (ProtectedHosts is null || BlockedPermissions is null)
            throw new PolicyException("protectedHosts и blockedPermissions не могут быть null");
        if (BlockedPermissions.Any(string.IsNullOrWhiteSpace))
            throw new PolicyException("blockedPermissions содержит пустое значение");
        if (BlockedPermissions.Concat(AllowedPermissions ?? []).Any(p => string.Equals(p, ClipboardReadPermission, StringComparison.OrdinalIgnoreCase)))
            throw new PolicyException($"'{ClipboardReadPermission}' нельзя указывать в blockedPermissions/allowedPermissions — используйте allowExtensionClipboardRead (true/false)");
        if (AllowedPermissions is not null)
        {
            if (AllowedPermissions.Any(string.IsNullOrWhiteSpace))
                throw new PolicyException("allowedPermissions содержит пустое значение");
            var conflict = AllowedPermissions.FirstOrDefault(BlockedPermissionSet.Contains);
            if (conflict is not null)
                throw new PolicyException($"право '{conflict}' одновременно в allowedPermissions и blockedPermissions");
        }
        _ = ProtectedHostPatterns;
        if (HomeUrl is not null && HomeUrl != UrlPolicy.AboutBlank && !UrlPolicy.TryNormalizeUserInput(HomeUrl, out _, out var error))
            throw new PolicyException($"homeUrl: {error}");
        if (SearchUrl is not null && !SearchEngines.TryValidateTemplate(SearchUrl, out var searchError))
            throw new PolicyException($"searchUrl: {searchError}");
        return this;
    }

    private List<HostPattern> ParseProtectedHosts()
    {
        var list = new List<HostPattern>();
        foreach (var raw in ProtectedHosts ?? [])
        {
            if (!HostPattern.TryParse(raw, out var p, out var error))
                throw new PolicyException($"protectedHosts: '{raw}' — {error}");
            if (p!.Kind == HostPatternKind.Any)
                throw new PolicyException("protectedHosts: '*' запрещён — это заблокировало бы расширениям весь веб; используйте allowBroadHostAccess=false");
            list.Add(p);
        }
        return list;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Опечатка в имени поля политики не должна молча превращаться в значение по умолчанию.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static BrowserPolicy Parse(string json)
    {
        BrowserPolicy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<BrowserPolicy>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PolicyException($"некорректный JSON политики: {ex.Message}");
        }
        return (policy ?? throw new PolicyException("политика пуста")).EnsureValid();
    }

    /// <summary>
    /// Берёт ПЕРВЫЙ существующий источник (например, реестр HKLM, затем ProgramData).
    /// Источник есть, но его может изменить не администратор, или JSON битый — исключение (fail closed),
    /// без перехода к следующему источнику. Нет ни одного источника — политика по умолчанию.
    /// </summary>
    public static (BrowserPolicy Policy, string Source) LoadFirst(IEnumerable<PolicySourceCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (!candidate.Exists())
                    continue;

                // Сначала права, потом чтение: содержимое из небезопасного источника даже не разбираем.
                var problem = candidate.FindSecurityProblem();
                if (problem is not null)
                    throw new PolicyException($"{candidate.Name}: политику может изменить не только администратор ({problem})");

                return (Parse(candidate.ReadJson()), candidate.Name);
            }
            catch (PolicyException ex) when (!ex.Message.StartsWith(candidate.Name, StringComparison.Ordinal))
            {
                throw new PolicyException($"{candidate.Name}: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Источник есть, но прочитать его нельзя (например, пустой ACL) — тоже отказ, без перехода к следующему.
                throw new PolicyException($"{candidate.Name}: не удалось прочитать политику ({ex.Message})");
            }
        }
        return (Default, "<default>");
    }
}
