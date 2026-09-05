using System.Diagnostics;
using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core;

/// <summary>
/// The editable partition of a source timeline into <see cref="Segment"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Invariant: <see cref="Segments"/> is always a complete, sorted, non-overlapping partition of
/// <c>[0, Duration]</c>. Adjacent segments share their boundary value exactly, the first starts at
/// <c>0.0</c>, the last ends at <see cref="Duration"/>, and every segment has positive length.
/// </para>
/// <para>
/// Every public mutation preserves the invariant and re-checks it in Debug builds. The
/// constructor that accepts existing segments always validates, because that is where untrusted
/// data (a project file) enters.
/// </para>
/// </remarks>
public sealed class SegmentList
{
    /// <summary>
    /// Smallest segment length, in seconds, that a boundary drag may leave behind. Prevents a drag
    /// from collapsing a neighbour to zero length, which would break the partition.
    /// </summary>
    public const double MinSegmentLength = 0.001;

    private readonly List<Segment> _segments;

    /// <summary>Creates a list containing one enabled segment spanning the whole duration.</summary>
    public SegmentList(double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be a positive, finite number of seconds.");
        }

        Duration = duration;
        _segments = [Segment.Create(0.0, duration, enabled: true, SegmentOrigin.Auto)];
    }

    /// <summary>
    /// Creates a list from existing segments, sorting them by start and validating the partition.
    /// </summary>
    /// <exception cref="InvalidPartitionException">The segments do not partition <c>[0, duration]</c>.</exception>
    public SegmentList(double duration, IEnumerable<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be a positive, finite number of seconds.");
        }

        Duration = duration;
        _segments = segments.OrderBy(s => s.Start).ToList();
        ValidatePartition(_segments, duration);
    }

    /// <summary>Total length of the source timeline in seconds. Fixed for the life of the list.</summary>
    public double Duration { get; }

    /// <summary>The current partition, in timeline order. The list is replaced, never mutated in place.</summary>
    public IReadOnlyList<Segment> Segments => _segments;

    public int Count => _segments.Count;

    /// <summary>Raised after every mutation that changed the partition.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Index of the segment containing <paramref name="time"/>, or -1 if the time is outside
    /// <c>[0, Duration]</c>. A boundary belongs to the segment that starts there; the duration
    /// itself maps to the last segment so the playhead at end-of-file is still "in" the timeline.
    /// </summary>
    public int IndexAt(double time)
    {
        if (!double.IsFinite(time) || time < 0 || time > Duration)
        {
            return -1;
        }

        if (time >= Duration)
        {
            return _segments.Count - 1;
        }

        // Binary search on Start.
        int lo = 0, hi = _segments.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_segments[mid].Start <= time)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>
    /// Splits the segment containing <paramref name="time"/> into two at that time. Both halves keep
    /// the original enabled state, origin and reason; the left half keeps the original id. A split
    /// is not a decision about either half, so it does not mark them manual. The toggle that
    /// usually follows does.
    /// </summary>
    /// <returns>False if <paramref name="time"/> is already a boundary, in which case nothing changes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="time"/> is outside <c>[0, Duration]</c>.</exception>
    public bool Split(double time)
    {
        if (!double.IsFinite(time) || time < 0 || time > Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, $"Split time must be within [0, {Duration}].");
        }

        var index = IndexAt(time);
        var target = _segments[index];
        if (time == target.Start || time == target.End)
        {
            return false;
        }

        var left = target with { End = time };
        var right = target with { Id = Segment.NewId(), Start = time };
        _segments[index] = left;
        _segments.Insert(index + 1, right);

        OnChanged();
        return true;
    }

    /// <summary>
    /// Moves the boundary between <c>Segments[boundaryIndex - 1]</c> and <c>Segments[boundaryIndex]</c>
    /// to <paramref name="time"/>, clamped so that both neighbours keep at least
    /// <see cref="MinSegmentLength"/>. A boundary can never cross another boundary. Disabled
    /// neighbours are marked <see cref="SegmentOrigin.Manual"/>, because shaping a cut by hand is a
    /// decision about that cut; kept neighbours keep their origin so re-detection can still find
    /// new silences inside them.
    /// </summary>
    /// <param name="boundaryIndex">In <c>[1, Count - 1]</c>. Boundary 0 and boundary <c>Count</c> are the fixed ends of the timeline.</param>
    /// <param name="time">Requested boundary time in seconds.</param>
    /// <returns>The time actually applied after clamping.</returns>
    public double MoveBoundary(int boundaryIndex, double time)
    {
        if (boundaryIndex < 1 || boundaryIndex > _segments.Count - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(boundaryIndex), boundaryIndex, $"Movable boundaries are 1..{_segments.Count - 1}; the timeline ends are fixed.");
        }

        if (!double.IsFinite(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "Boundary time must be finite.");
        }

        var prev = _segments[boundaryIndex - 1];
        var next = _segments[boundaryIndex];

        var min = prev.Start + MinSegmentLength;
        var max = next.End - MinSegmentLength;
        if (min > max)
        {
            // Both neighbours are already at or below the minimum; nothing can move.
            return prev.End;
        }

        var applied = Math.Clamp(time, min, max);
        if (applied == prev.End)
        {
            return applied;
        }

        _segments[boundaryIndex - 1] = prev with { End = applied, Origin = prev.Enabled ? prev.Origin : SegmentOrigin.Manual };
        _segments[boundaryIndex] = next with { Start = applied, Origin = next.Enabled ? next.Origin : SegmentOrigin.Manual };

        OnChanged();
        return applied;
    }

    /// <summary>Flips <see cref="Segment.Enabled"/> on the segment at <paramref name="index"/> and marks it manual.</summary>
    public void Toggle(int index)
    {
        if (index < 0 || index >= _segments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Segment index must be within 0..{_segments.Count - 1}.");
        }

        var s = _segments[index];
        _segments[index] = s with { Enabled = !s.Enabled, Origin = SegmentOrigin.Manual };

        OnChanged();
    }

    /// <summary>
    /// Merges every run of adjacent segments that share the same <see cref="Segment.Enabled"/> state
    /// into one. The merged segment keeps the first segment's id and reason. Its origin is
    /// <see cref="SegmentOrigin.Manual"/> if any part was manual, otherwise <see cref="SegmentOrigin.Claude"/>
    /// if any part was Claude's, otherwise <see cref="SegmentOrigin.Auto"/>.
    /// </summary>
    public void MergeAdjacentSameState()
    {
        if (_segments.Count < 2)
        {
            return;
        }

        var merged = new List<Segment>(_segments.Count);
        var current = _segments[0];
        var changed = false;

        for (var i = 1; i < _segments.Count; i++)
        {
            var s = _segments[i];
            if (s.Enabled == current.Enabled)
            {
                current = current with
                {
                    End = s.End,
                    Origin = MergeOrigin(current.Origin, s.Origin),
                    Reason = current.Reason ?? s.Reason,
                };
                changed = true;
            }
            else
            {
                merged.Add(current);
                current = s;
            }
        }

        merged.Add(current);

        if (!changed)
        {
            return;
        }

        _segments.Clear();
        _segments.AddRange(merged);

        OnChanged();
    }

    /// <summary>
    /// Applies a fresh detection result. Every <see cref="SegmentOrigin.Auto"/> segment is discarded
    /// and the timeline outside the user's segments is re-laid from <paramref name="cuts"/>.
    /// Segments with any other origin (manual, Claude) are preserved exactly, and cuts that overlap
    /// them are clipped around them. Re-running detection therefore never undoes an edit.
    /// </summary>
    public void ReplaceAutoSegments(IEnumerable<PlannedCut> cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);

        var locked = _segments.Where(s => s.Origin != SegmentOrigin.Auto).OrderBy(s => s.Start).ToList();
        var sortedCuts = cuts.OrderBy(c => c.Start).ToList();

        var result = new List<Segment>();
        var cursor = 0.0;
        foreach (var region in FreeRegions(locked))
        {
            // Emit the locked segment(s) between the previous free region and this one.
            foreach (var l in locked.Where(l => l.Start >= cursor && l.End <= region.Start))
            {
                result.Add(l);
            }

            LayOutFreeRegion(result, region.Start, region.End, sortedCuts);
            cursor = region.End;
        }

        foreach (var l in locked.Where(l => l.Start >= cursor))
        {
            result.Add(l);
        }

        _segments.Clear();
        _segments.AddRange(result);
        OnChanged();
    }

    /// <summary>The stretches of <c>[0, Duration]</c> not covered by locked segments, in order.</summary>
    private IEnumerable<(double Start, double End)> FreeRegions(List<Segment> locked)
    {
        var cursor = 0.0;
        foreach (var l in locked)
        {
            if (l.Start > cursor)
            {
                yield return (cursor, l.Start);
            }

            cursor = l.End;
        }

        if (cursor < Duration)
        {
            yield return (cursor, Duration);
        }
    }

    /// <summary>Fills <c>[start, end)</c> with alternating kept / cut auto segments from the cuts that intersect it.</summary>
    private static void LayOutFreeRegion(List<Segment> result, double start, double end, List<PlannedCut> cuts)
    {
        var cursor = start;
        foreach (var cut in cuts)
        {
            var cutStart = Math.Max(cut.Start, start);
            var cutEnd = Math.Min(cut.End, end);
            if (cutEnd <= cutStart)
            {
                continue;
            }

            if (cutStart > cursor)
            {
                result.Add(Segment.Create(cursor, cutStart, enabled: true, SegmentOrigin.Auto));
            }

            if (result.Count > 0 && !result[^1].Enabled && result[^1].Origin == SegmentOrigin.Auto && result[^1].End == cutStart)
            {
                // Touching cuts become one segment.
                result[^1] = result[^1] with { End = cutEnd };
            }
            else
            {
                result.Add(Segment.Create(cutStart, cutEnd, enabled: false, SegmentOrigin.Auto, cut.Reason));
            }

            cursor = cutEnd;
        }

        if (cursor < end)
        {
            result.Add(Segment.Create(cursor, end, enabled: true, SegmentOrigin.Auto));
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidPartitionException"/> unless <paramref name="segments"/> (already sorted
    /// by start) is a complete, non-overlapping partition of <c>[0, duration]</c> with unique ids.
    /// </summary>
    public static void ValidatePartition(IReadOnlyList<Segment> segments, double duration)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
        {
            throw new InvalidPartitionException("A project must contain at least one segment.");
        }

        if (segments[0].Start != 0.0)
        {
            throw new InvalidPartitionException($"The first segment must start at 0, but starts at {segments[0].Start}.");
        }

        if (segments[^1].End != duration)
        {
            throw new InvalidPartitionException($"The last segment must end at the duration {duration}, but ends at {segments[^1].End}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (!double.IsFinite(s.Start) || !double.IsFinite(s.End) || s.End <= s.Start)
            {
                throw new InvalidPartitionException($"Segment {i} has a non-positive or non-finite length: [{s.Start}, {s.End}).");
            }

            if (i > 0)
            {
                var prevEnd = segments[i - 1].End;
                if (s.Start > prevEnd)
                {
                    throw new InvalidPartitionException($"There is a gap between segment {i - 1} (ends {prevEnd}) and segment {i} (starts {s.Start}).");
                }

                if (s.Start < prevEnd)
                {
                    throw new InvalidPartitionException($"Segment {i - 1} (ends {prevEnd}) and segment {i} (starts {s.Start}) overlap.");
                }
            }

            if (string.IsNullOrEmpty(s.Id) || !ids.Add(s.Id))
            {
                throw new InvalidPartitionException($"Segment {i} has a missing or duplicate id '{s.Id}'.");
            }
        }
    }

    private static SegmentOrigin MergeOrigin(SegmentOrigin a, SegmentOrigin b)
    {
        if (a == SegmentOrigin.Manual || b == SegmentOrigin.Manual)
        {
            return SegmentOrigin.Manual;
        }

        if (a == SegmentOrigin.Claude || b == SegmentOrigin.Claude)
        {
            return SegmentOrigin.Claude;
        }

        return SegmentOrigin.Auto;
    }

    private void OnChanged()
    {
        AssertInvariant();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [Conditional("DEBUG")]
    private void AssertInvariant() => ValidatePartition(_segments, Duration);
}
