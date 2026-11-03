namespace Shadepane.Core.Browsing;

/// <summary>Ступени масштаба страницы (как в Chromium): Ctrl+= / Ctrl+- двигают по ним, Ctrl+0 — 100%.</summary>
public static class ZoomLevels
{
    public const double Default = 1.0;

    public static IReadOnlyList<double> All { get; } =
        [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    private const double Epsilon = 0.005;

    /// <summary>Следующая ступень больше текущей (текущая может быть «между» ступенями).</summary>
    public static double ZoomIn(double current)
    {
        foreach (var level in All)
        {
            if (level > current + Epsilon)
                return level;
        }
        return All[^1];
    }

    public static double ZoomOut(double current)
    {
        for (var i = All.Count - 1; i >= 0; i--)
        {
            if (All[i] < current - Epsilon)
                return All[i];
        }
        return All[0];
    }

    public static string Format(double factor) => $"{Math.Round(factor * 100)}%";
}
