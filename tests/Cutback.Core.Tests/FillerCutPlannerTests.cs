using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class FillerCutPlannerTests
{
    private const double Duration = 10.0;
    private static readonly DetectionSettings Settings = DetectionSettings.Default with { MinKeepMs = 120 };

    private static FillerSpan Span(double start, double end, string word = "um") => new(start, end, word);

    private static IReadOnlyList<Segment> Partition(params (double Start, double End, bool Enabled, SegmentOrigin Origin)[] parts)
        => parts.Select(p => Segment.Create(p.Start, p.End, p.Enabled, p.Origin)).ToList();

    private static IReadOnlyList<Segment> AllKept() => Partition((0.0, Duration, true, SegmentOrigin.Auto));

    [Fact]
    public void A_span_in_kept_footage_becomes_a_cut_with_a_filler_reason()
    {
        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3)], AllKept(), Settings, Duration);

        cuts.Should().ContainSingle().Which.Should().Be(new PlannedCut(2.0, 2.3, "filler: um"));
    }

    [Fact]
    public void Spans_are_clamped_to_the_timeline_and_empty_ones_dropped()
    {
        var cuts = FillerCutPlanner.Plan([Span(-0.5, 0.2), Span(9.9, 10.5), Span(4.0, 4.0)], AllKept(), Settings, Duration);

        cuts.Select(c => (c.Start, c.End)).Should().Equal((0.0, 0.2), (9.9, 10.0));
    }

    [Fact]
    public void A_span_touching_a_manual_segment_is_dropped()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, true, SegmentOrigin.Manual), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(1.9, 2.1), Span(5.0, 5.2)], current, Settings, Duration);

        cuts.Select(c => c.Start).Should().Equal(5.0);
    }

    [Fact]
    public void A_span_already_inside_a_cut_is_dropped()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(2.2, 2.5)], current, Settings, Duration);

        cuts.Should().BeEmpty();
    }

    [Fact]
    public void A_span_straddling_a_cut_edge_is_kept()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(2.9, 3.2)], current, Settings, Duration);

        cuts.Should().ContainSingle().Which.Should().Be(new PlannedCut(2.9, 3.2, "filler: um"));
    }

    [Fact]
    public void Overlapping_and_touching_spans_merge_and_join_their_words()
    {
        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3, "um"), Span(2.3, 2.6, "uh"), Span(2.5, 2.8, "er")], AllKept(), Settings, Duration);

        cuts.Should().ContainSingle().Which.Should().Be(new PlannedCut(2.0, 2.8, "filler: um uh er"));
    }

    [Fact]
    public void A_kept_sliver_shorter_than_minKeep_between_two_spans_is_bridged()
    {
        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3), Span(2.35, 2.6, "uh")], AllKept(), Settings, Duration);

        cuts.Should().ContainSingle().Which.Should().Be(new PlannedCut(2.0, 2.6, "filler: um uh"));
    }

    [Fact]
    public void A_kept_sliver_between_a_span_and_an_existing_cut_is_bridged_on_both_sides()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(1.7, 1.95), Span(3.05, 3.3, "uh")], current, Settings, Duration);

        cuts.Select(c => (c.Start, c.End)).Should().Equal((1.7, 2.0), (3.0, 3.3));
    }

    [Fact]
    public void A_sliver_is_not_bridged_over_a_manual_segment()
    {
        var current = Partition(
            (0.0, 2.0, true, SegmentOrigin.Auto),
            (2.0, 3.0, false, SegmentOrigin.Auto),
            (3.0, 3.05, true, SegmentOrigin.Manual),
            (3.05, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(3.05, 3.3)], current, Settings, Duration);

        cuts.Should().ContainSingle().Which.Start.Should().Be(3.05);
    }

    [Fact]
    public void Slivers_at_the_ends_of_the_timeline_are_bridged()
    {
        var cuts = FillerCutPlanner.Plan([Span(0.05, 0.3), Span(9.7, 9.95, "uh")], AllKept(), Settings, Duration);

        cuts.Select(c => (c.Start, c.End)).Should().Equal((0.0, 0.3), (9.7, 10.0));
    }

    [Fact]
    public void Output_is_sorted_by_start()
    {
        var cuts = FillerCutPlanner.Plan([Span(6.0, 6.2), Span(2.0, 2.2)], AllKept(), Settings, Duration);

        cuts.Select(c => c.Start).Should().BeInAscendingOrder();
    }
}
