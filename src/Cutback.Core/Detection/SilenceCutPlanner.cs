using System.Globalization;
using Cutback.Core.Models;

namespace Cutback.Core.Detection;

/// <summary>
/// Turns raw silence spans into cuts: shrinks each by the padding so consonants at the edges
/// survive, then absorbs kept fragments shorter than the minimum keep length so the result does
/// not machine-gun. Pure; no ffmpeg here.
/// </summary>
public static class SilenceCutPlanner
{
    public static IReadOnlyList<PlannedCut> Plan(IEnumerable<SilenceSpan> spans, DetectionSettings settings, double duration)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration);

        var pad = settings.PaddingMs / 1000.0;
        var minKeep = settings.MinKeepMs / 1000.0;

        // 1. Pad and clamp; drop anything that vanishes.
        var cuts = spans
            .Select(s => (Start: Math.Max(0, s.Start + pad), End: Math.Min(duration, s.End - pad)))
            .Where(c => c.End > c.Start)
            .OrderBy(c => c.Start)
            .ToList();

        // 2. Merge overlapping or touching cuts, and bridge kept gaps shorter than minKeep.
        var merged = new List<(double Start, double End)>(cuts.Count);
        foreach (var cut in cuts)
        {
            if (merged.Count > 0 && cut.Start - merged[^1].End < minKeep)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, cut.End));
            }
            else
            {
                merged.Add(cut);
            }
        }

        // 3. A short kept sliver at either end of the file is just as bad as one in the middle.
        if (merged.Count > 0)
        {
            if (merged[0].Start > 0 && merged[0].Start < minKeep)
            {
                merged[0] = (0, merged[0].End);
            }

            if (merged[^1].End < duration && duration - merged[^1].End < minKeep)
            {
                merged[^1] = (merged[^1].Start, duration);
            }
        }

        return merged
            .Select(c => new PlannedCut(c.Start, c.End, string.Create(CultureInfo.InvariantCulture, $"silence {c.End - c.Start:0.00}s")))
            .ToList();
    }
}
