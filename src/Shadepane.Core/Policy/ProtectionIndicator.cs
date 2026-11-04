namespace Shadepane.Core.Policy;

/// <summary>Что показывает индикатор защиты в адресной строке.</summary>
public enum ProtectionIndicator
{
    /// <summary>Окно скрыто от снимков и записи экрана — обычное состояние.</summary>
    Protected,

    /// <summary>На странице есть содержимое с домена из protectedHosts: расширения его не видят, загрузки по политике.</summary>
    ProtectedSite,

    /// <summary>Страница только что запросила захват экрана (getDisplayMedia), запрос отклонён. Показывается несколько секунд.</summary>
    CaptureBlocked,

    /// <summary>С окна сняли защиту от захвата: содержимое скрыто.</summary>
    ContentHidden,
}

public static class ProtectionStatus
{
    /// <summary>Самое важное состояние побеждает: скрытый контент → заблокированный захват → защищённый сайт → обычное.</summary>
    public static ProtectionIndicator Resolve(bool contentHidden, bool captureBlockedRecently, bool showsProtectedSite) =>
        contentHidden ? ProtectionIndicator.ContentHidden
        : captureBlockedRecently ? ProtectionIndicator.CaptureBlocked
        : showsProtectedSite ? ProtectionIndicator.ProtectedSite
        : ProtectionIndicator.Protected;
}
