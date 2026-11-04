using System.Globalization;

namespace Shadepane.Core.Browsing;

public static class ByteSize
{
    private static string[] Units => Localization.Loc.IsEnglish ? ["B", "KB", "MB", "GB", "TB"] : ["Б", "КБ", "МБ", "ГБ", "ТБ"];

    /// <summary>1536 → "1,5 КБ" (десятичная запятая — по-русски).</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
            return "?";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var number = unit == 0 || value >= 100 ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{number.Replace('.', ',')} {Units[unit]}";
    }

    /// <summary>"3,2 МБ из 10 МБ (32%)" или "3,2 МБ", если размер неизвестен.</summary>
    public static string Progress(long received, long? total) =>
        total is > 0
            ? Localization.Loc.T($"{Format(received)} из {Format(total.Value)} ({Math.Min(100, received * 100 / total.Value)}%)", $"{Format(received)} of {Format(total.Value)} ({Math.Min(100, received * 100 / total.Value)}%)")
            : Format(received);
}
