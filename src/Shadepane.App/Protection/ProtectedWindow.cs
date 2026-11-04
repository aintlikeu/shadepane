using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
    // BitmapFrame из .ico: WPF сам выбирает из файла подходящие размеры для маленькой (заголовок) и
    // большой (Alt+Tab) иконки окна, а не масштабирует один кадр.
    private static readonly Lazy<ImageSource> AppIcon = new(() =>
    {
        var frame = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Shadepane.ico", UriKind.Absolute));
        frame.Freeze();
        return frame;
    });

    public ProtectedWindow()
    {
        Icon = AppIcon.Value;
        // Цвета всех окон — из словаря темы (Assets/Theme.*.xaml); окно может задать свой фон в XAML.
        SetResourceReference(BackgroundProperty, "Brush.Surface");
        SetResourceReference(ForegroundProperty, "Brush.Ink");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        SetResourceReference(FontSizeProperty, "FontSize.UI");
        // Display: текст привязан к пиксельной сетке — мелкий шрифт чётче при масштабе 100%.
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AppServices.Guard.Protect(this);
    }
}
