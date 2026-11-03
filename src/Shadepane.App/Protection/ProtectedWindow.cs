using System.Windows;

namespace Shadepane.App.Protection;

/// <summary>Окно, которое прячет свой веб-контент при нарушении защиты.</summary>
internal interface IContentShield
{
    void HideContent();
}

/// <summary>
/// Базовый класс для ВСЕХ окон браузера с контентом. Флаг защиты ставится до первого показа окна,
/// поэтому ни один кадр не попадает в захват незащищённым.
/// </summary>
public class ProtectedWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AppServices.Guard.Protect(this);
    }
}
