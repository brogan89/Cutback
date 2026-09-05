using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class SegmentListDetectionTests
{
    private const double Duration = 10.0;

    private static PlannedCut Cut(double start, double end) => new(start, end, $"silence {end - start:0.00}s");

    [Fact]
    public void On_a_fresh_list_cuts_become_disabled_auto_segments_with_kept_gaps_between()
    {
        var list = new SegmentList(Duration);

        list.ReplaceAutoSegments([Cut(2.0, 3.0), Cut(6.0, 7.5)]);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled)).Should().Equal(
            (0.0, 2.0, true),
            (2.0, 3.0, false),
            (3.0, 6.0, true),
            (6.0, 7.5, false),
            (7.5, 10.0, true));
        list.Segments.Should().OnlyContain(s => s.Origin == SegmentOrigin.Auto);
        list.Segments[1].Reason.Should().Be("silence 1.00s");
        list.Segments[0].Reason.Should().BeNull();
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Re_running_replaces_previous_auto_cuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);

        list.ReplaceAutoSegments([Cut(5.0, 6.0)]);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled)).Should().Equal(
            (0.0, 5.0, true),
            (5.0, 6.0, false),
            (6.0, 10.0, true));
    }

    [Fact]
    public void Manual_segments_are_preserved_exactly()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);
        list.Toggle(1); // the user decides to keep the 2..3 "silence": now manual + enabled
        var manual = list.Segments[1];

        list.ReplaceAutoSegments([Cut(2.0, 3.0), Cut(6.0, 7.0)]);

        list.Segments.Should().Contain(s => s.Id == manual.Id && s.Start == 2.0 && s.End == 3.0 && s.Enabled && s.Origin == SegmentOrigin.Manual);
        list.Segments.Should().Contain(s => s.Start == 6.0 && s.End == 7.0 && !s.Enabled && s.Origin == SegmentOrigin.Auto);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void A_new_cut_overlapping_a_manual_segment_is_clipped_around_it()
    {
        var list = new SegmentList(Duration,
        [
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 5.0, enabled: true, SegmentOrigin.Manual), // the user insists on keeping this
            Segment.Create(5.0, 10.0, enabled: true, SegmentOrigin.Auto),
        ]);

        list.ReplaceAutoSegments([Cut(3.0, 6.0)]);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)).Should().Equal(
            (0.0, 3.0, true, SegmentOrigin.Auto),
            (3.0, 4.0, false, SegmentOrigin.Auto),
            (4.0, 5.0, true, SegmentOrigin.Manual),
            (5.0, 6.0, false, SegmentOrigin.Auto),
            (6.0, 10.0, true, SegmentOrigin.Auto));
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Claude_segments_are_preserved_like_manual_ones()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 5.0, enabled: false, SegmentOrigin.Claude, "false start"),
            Segment.Create(5.0, 10.0, enabled: true, SegmentOrigin.Auto),
        };
        var list = new SegmentList(Duration, segments);

        list.ReplaceAutoSegments([]);

        list.Segments.Should().Contain(s => s.Id == segments[1].Id && s.Origin == SegmentOrigin.Claude);
        list.Segments.Should().HaveCount(3);
    }

    [Fact]
    public void Free_regions_with_no_cuts_collapse_to_one_kept_segment()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Cut(2.0, 3.0), Cut(4.0, 5.0)]);

        list.ReplaceAutoSegments([]);

        list.Segments.Should().ContainSingle().Which.Should().Match<Segment>(s => s.Start == 0.0 && s.End == Duration && s.Enabled);
    }

    [Fact]
    public void Cuts_outside_or_touching_edges_are_handled()
    {
        var list = new SegmentList(Duration);

        list.ReplaceAutoSegments([Cut(0.0, 1.0), Cut(9.0, 10.0)]);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled)).Should().Equal(
            (0.0, 1.0, false),
            (1.0, 9.0, true),
            (9.0, 10.0, false));
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Adjacent_cuts_are_merged_into_one_disabled_segment()
    {
        var list = new SegmentList(Duration);

        list.ReplaceAutoSegments([Cut(2.0, 3.0), Cut(3.0, 4.0)]);

        list.Segments.Should().HaveCount(3);
        list.Segments[1].Should().Match<Segment>(s => s.Start == 2.0 && s.End == 4.0 && !s.Enabled);
    }

    [Fact]
    public void ReplaceAutoSegments_raises_Changed_once()
    {
        var list = new SegmentList(Duration);
        var count = 0;
        list.Changed += (_, _) => count++;

        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);

        count.Should().Be(1);
    }

    [Fact]
    public void Existing_auto_segment_ids_are_not_reused()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);
        var oldIds = list.Segments.Select(s => s.Id).ToHashSet();

        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);

        list.Segments.Select(s => s.Id).Should().NotIntersectWith(oldIds);
    }
}
