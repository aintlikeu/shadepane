using System.Windows.Markup;
using Shadepane.Core.Localization;

namespace Shadepane.App.Localization;

/// <summary>
/// Строка интерфейса в XAML на двух языках: Content="{l:Tr Ru='Сохранить', En='Save'}".
/// Значение выбирается при загрузке окна по Loc.Current (язык задаётся при запуске).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class Tr : MarkupExtension
{
    public string Ru { get; set; } = "";
    public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Ru, En);
}
