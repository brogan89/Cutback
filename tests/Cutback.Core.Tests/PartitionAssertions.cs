namespace Cutback.Core.Tests;

internal static class PartitionAssertions
{
    /// <summary>
    /// Asserts the CLAUDE.md partition invariant directly, independent of SegmentList's own checks:
    /// sorted, no gaps, no overlaps, first starts at 0, last ends at duration, every segment has
    /// positive length, ids are unique.
    /// </summary>
    public static void ShouldBeValidPartitionOf(this SegmentList list, double duration)
    {
        var segments = list.Segments;
        segments.Should().NotBeEmpty();
        segments[0].Start.Should().Be(0.0);
        segments[^1].End.Should().Be(duration);
        list.Duration.Should().Be(duration);

        for (var i = 0; i < segments.Count; i++)
        {
            segments[i].End.Should().BeGreaterThan(segments[i].Start, $"segment {i} must have positive length");
            if (i > 0)
            {
                segments[i].Start.Should().Be(segments[i - 1].End, $"segments {i - 1} and {i} must share a boundary exactly");
            }
        }

        segments.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }
}
