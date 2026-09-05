using System.Globalization;

namespace Cutback.App;

public static class TimeFormat
{
    /// <summary><c>m:ss.t</c> under an hour, <c>h:mm:ss.t</c> above.</summary>
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var t = TimeSpan.FromSeconds(seconds);
        var tenths = (int)Math.Floor(t.Milliseconds / 100.0);
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{tenths}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}.{tenths}");
    }

    /// <summary>Compact ruler label: <c>m:ss</c> or <c>h:mm:ss</c>, no fraction.</summary>
    public static string Ruler(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }
}
