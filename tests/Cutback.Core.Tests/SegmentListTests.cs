using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class SegmentListTests
{
    private const double Duration = 10.0;

    /// <summary>[0,3) kept, [3,6) removed, [6,10) kept, all auto-detected.</summary>
    private static SegmentList ThreeSegments() => new(
        Duration,
        [
            Segment.Create(0.0, 3.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(3.0, 6.0, enabled: false, SegmentOrigin.Auto, "silence 3s"),
            Segment.Create(6.0, 10.0, enabled: true, SegmentOrigin.Auto),
        ]);

    // ---- construction -------------------------------------------------------------------------

    [Fact]
    public void New_list_is_a_single_enabled_segment_spanning_the_duration()
    {
        var list = new SegmentList(Duration);

        list.Segments.Should().ContainSingle();
        list.Segments[0].Start.Should().Be(0.0);
        list.Segments[0].End.Should().Be(Duration);
        list.Segments[0].Enabled.Should().BeTrue();
        list.Segments[0].Origin.Should().Be(SegmentOrigin.Auto);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void New_list_rejects_non_positive_or_non_finite_duration(double duration)
    {
        var act = () => new SegmentList(duration);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructing_from_segments_accepts_a_valid_partition()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 10.0, enabled: false, SegmentOrigin.Auto, "silence"),
        };

        var list = new SegmentList(Duration, segments);

        list.Segments.Should().BeEquivalentTo(segments, o => o.WithStrictOrdering());
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Constructing_from_segments_sorts_by_start()
    {
        var segments = new[]
        {
            Segment.Create(4.0, 10.0, enabled: false, SegmentOrigin.Auto),
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
        };

        var list = new SegmentList(Duration, segments);

        list.Segments.Select(s => s.Start).Should().BeInAscendingOrder();
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Constructing_from_segments_with_a_gap_throws()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(5.0, 10.0, enabled: true, SegmentOrigin.Auto),
        };

        var act = () => new SegmentList(Duration, segments);

        act.Should().Throw<InvalidPartitionException>().WithMessage("*gap*");
    }

    [Fact]
    public void Constructing_from_segments_with_an_overlap_throws()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 5.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 10.0, enabled: true, SegmentOrigin.Auto),
        };

        var act = () => new SegmentList(Duration, segments);

        act.Should().Throw<InvalidPartitionException>().WithMessage("*overlap*");
    }

    [Fact]
    public void Constructing_from_segments_that_do_not_start_at_zero_throws()
    {
        var segments = new[] { Segment.Create(1.0, 10.0, enabled: true, SegmentOrigin.Auto) };

        var act = () => new SegmentList(Duration, segments);

        act.Should().Throw<InvalidPartitionException>();
    }

    [Fact]
    public void Constructing_from_segments_that_do_not_end_at_duration_throws()
    {
        var segments = new[] { Segment.Create(0.0, 9.0, enabled: true, SegmentOrigin.Auto) };

        var act = () => new SegmentList(Duration, segments);

        act.Should().Throw<InvalidPartitionException>();
    }

    [Fact]
    public void Constructing_from_empty_segments_throws()
    {
        var act = () => new SegmentList(Duration, Array.Empty<Segment>());

        act.Should().Throw<InvalidPartitionException>();
    }

    [Fact]
    public void Constructing_from_segments_with_duplicate_ids_throws()
    {
        var segments = new[]
        {
            new Segment("same", 0.0, 4.0, true, SegmentOrigin.Auto, null),
            new Segment("same", 4.0, 10.0, true, SegmentOrigin.Auto, null),
        };

        var act = () => new SegmentList(Duration, segments);

        act.Should().Throw<InvalidPartitionException>().WithMessage("*id*");
    }

    // ---- lookup -------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(2.999, 0)]
    [InlineData(3.0, 1)]   // boundary belongs to the segment that starts there
    [InlineData(6.0, 2)]
    [InlineData(9.999, 2)]
    [InlineData(10.0, 2)]  // the duration itself maps to the last segment
    public void IndexAt_returns_the_segment_containing_the_time(double time, int expected)
    {
        var list = ThreeSegments();

        list.IndexAt(time).Should().Be(expected);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(10.001)]
    public void IndexAt_returns_minus_one_outside_the_timeline(double time)
    {
        var list = ThreeSegments();

        list.IndexAt(time).Should().Be(-1);
    }

    // ---- Split --------------------------------------------------------------------------------

    [Fact]
    public void Split_divides_a_segment_into_two_sharing_the_boundary()
    {
        var list = new SegmentList(Duration);

        var result = list.Split(4.0);

        result.Should().BeTrue();
        list.Segments.Should().HaveCount(2);
        list.Segments[0].Start.Should().Be(0.0);
        list.Segments[0].End.Should().Be(4.0);
        list.Segments[1].Start.Should().Be(4.0);
        list.Segments[1].End.Should().Be(Duration);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Split_halves_inherit_enabled_state_and_origin()
    {
        // Splitting is not a decision about either half, so it must not lock them against
        // re-detection. Only a toggle or a boundary drag on a cut does that.
        var list = new SegmentList(Duration, [Segment.Create(0.0, Duration, enabled: false, SegmentOrigin.Auto, "silence 10.00s")]);
        var original = list.Segments[0];

        list.Split(4.0);

        list.Segments.Should().OnlyContain(s => !s.Enabled);
        list.Segments.Should().OnlyContain(s => s.Origin == SegmentOrigin.Auto);
        list.Segments.Should().OnlyContain(s => s.Reason == "silence 10.00s");
        list.Segments[0].Id.Should().Be(original.Id, "the left half keeps the original identity");
        list.Segments[1].Id.Should().NotBe(original.Id);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(6.0)]
    [InlineData(10.0)]
    public void Split_at_an_existing_boundary_is_a_no_op(double time)
    {
        var list = ThreeSegments();
        var before = list.Segments.ToList();

        var result = list.Split(time);

        result.Should().BeFalse();
        list.Segments.Should().BeEquivalentTo(before, o => o.WithStrictOrdering());
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(11.0)]
    [InlineData(double.NaN)]
    public void Split_outside_the_timeline_throws(double time)
    {
        var list = new SegmentList(Duration);

        var act = () => list.Split(time);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Split_in_the_last_segment_keeps_the_end_at_duration()
    {
        var list = ThreeSegments();

        list.Split(8.0);

        list.Segments.Should().HaveCount(4);
        list.Segments[^1].End.Should().Be(Duration);
        list.ShouldBeValidPartitionOf(Duration);
    }

    // ---- MoveBoundary -------------------------------------------------------------------------

    [Fact]
    public void MoveBoundary_shifts_the_shared_edge_of_both_neighbours()
    {
        var list = ThreeSegments();

        var applied = list.MoveBoundary(1, 3.5);

        applied.Should().Be(3.5);
        list.Segments[0].End.Should().Be(3.5);
        list.Segments[1].Start.Should().Be(3.5);
        list.Segments[1].End.Should().Be(6.0, "the far edge of the neighbour is untouched");
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void MoveBoundary_marks_the_cut_neighbour_manual_but_not_the_kept_one()
    {
        // Dragging the edge of a cut is a decision about that cut. Locking the kept neighbour too
        // would stop re-detection from finding new silences inside it.
        var list = ThreeSegments();

        list.MoveBoundary(1, 3.5);

        list.Segments[0].Origin.Should().Be(SegmentOrigin.Auto, "the kept side stays open to re-detection");
        list.Segments[1].Origin.Should().Be(SegmentOrigin.Manual, "the cut was shaped by hand");
        list.Segments[2].Origin.Should().Be(SegmentOrigin.Auto, "an unrelated segment is not touched");
    }

    [Fact]
    public void MoveBoundary_between_two_cuts_marks_both_manual()
    {
        var list = new SegmentList(Duration,
        [
            Segment.Create(0.0, 3.0, enabled: false, SegmentOrigin.Auto),
            Segment.Create(3.0, 6.0, enabled: false, SegmentOrigin.Auto),
            Segment.Create(6.0, 10.0, enabled: true, SegmentOrigin.Auto),
        ]);

        list.MoveBoundary(1, 3.5);

        list.Segments[0].Origin.Should().Be(SegmentOrigin.Manual);
        list.Segments[1].Origin.Should().Be(SegmentOrigin.Manual);
    }

    [Fact]
    public void MoveBoundary_between_two_kept_segments_changes_no_origin()
    {
        var list = new SegmentList(Duration);
        list.Split(4.0);

        list.MoveBoundary(1, 4.5);

        list.Segments.Should().OnlyContain(s => s.Origin == SegmentOrigin.Auto);
    }

    [Fact]
    public void MoveBoundary_past_the_next_boundary_is_clamped_so_the_neighbour_keeps_a_minimum_length()
    {
        var list = ThreeSegments();

        // Try to drag the 3.0 boundary to 7.0, past the 6.0 boundary.
        var applied = list.MoveBoundary(1, 7.0);

        applied.Should().BeLessThan(6.0).And.BeGreaterThan(3.0);
        applied.Should().BeApproximately(6.0 - SegmentList.MinSegmentLength, 1e-9);
        list.Segments.Should().HaveCount(3, "clamping never deletes a segment");
        list.Segments[1].Duration.Should().BeApproximately(SegmentList.MinSegmentLength, 1e-9);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void MoveBoundary_before_the_previous_boundary_is_clamped()
    {
        var list = ThreeSegments();

        // Try to drag the 6.0 boundary to 1.0, past the 3.0 boundary.
        var applied = list.MoveBoundary(2, 1.0);

        applied.Should().BeApproximately(3.0 + SegmentList.MinSegmentLength, 1e-9);
        list.Segments.Should().HaveCount(3);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void MoveBoundary_of_the_first_boundary_cannot_go_below_zero()
    {
        var list = new SegmentList(Duration);
        list.Split(2.0);

        var applied = list.MoveBoundary(1, -5.0);

        applied.Should().BeApproximately(SegmentList.MinSegmentLength, 1e-9);
        list.Segments[0].Start.Should().Be(0.0);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Theory]
    [InlineData(0)]   // the start of the timeline is not movable
    [InlineData(3)]   // the end of the timeline is not movable
    [InlineData(-1)]
    [InlineData(4)]
    public void MoveBoundary_rejects_the_fixed_ends_and_out_of_range_indices(int boundaryIndex)
    {
        var list = ThreeSegments();

        var act = () => list.MoveBoundary(boundaryIndex, 5.0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MoveBoundary_with_a_non_finite_time_throws()
    {
        var list = ThreeSegments();

        var act = () => list.MoveBoundary(1, double.NaN);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- Toggle -------------------------------------------------------------------------------

    [Fact]
    public void Toggle_flips_enabled_and_marks_the_segment_manual()
    {
        var list = new SegmentList(Duration);

        list.Toggle(0);

        list.Segments[0].Enabled.Should().BeFalse();
        list.Segments[0].Origin.Should().Be(SegmentOrigin.Manual);
        list.ShouldBeValidPartitionOf(Duration);

        list.Toggle(0);

        list.Segments[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Toggle_preserves_id_bounds_and_reason()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 10.0, enabled: false, SegmentOrigin.Auto, "silence 6s"),
        };
        var list = new SegmentList(Duration, segments);

        list.Toggle(1);

        list.Segments[1].Id.Should().Be(segments[1].Id);
        list.Segments[1].Start.Should().Be(4.0);
        list.Segments[1].End.Should().Be(10.0);
        list.Segments[1].Reason.Should().Be("silence 6s");
    }

    [Fact]
    public void Toggle_first_and_last_segments_leaves_the_partition_intact()
    {
        var list = ThreeSegments();

        list.Toggle(0);
        list.Toggle(2);

        list.Segments[0].Enabled.Should().BeFalse();
        list.Segments[2].Enabled.Should().BeFalse();
        list.Segments[0].Start.Should().Be(0.0);
        list.Segments[2].End.Should().Be(Duration);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Toggle_on_a_single_segment_project_works_both_ways()
    {
        var list = new SegmentList(Duration);

        list.Toggle(0);
        list.Segments.Should().ContainSingle(s => !s.Enabled);

        list.Toggle(0);
        list.Segments.Should().ContainSingle(s => s.Enabled);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Toggle_out_of_range_throws(int index)
    {
        var list = ThreeSegments();

        var act = () => list.Toggle(index);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- MergeAdjacentSameState ---------------------------------------------------------------

    [Fact]
    public void MergeAdjacentSameState_joins_runs_of_equal_enabled_state()
    {
        var list = new SegmentList(Duration);
        list.Split(2.0);
        list.Split(4.0);
        list.Split(6.0);
        list.Split(8.0);
        // [0,2) on, [2,4) on, [4,6) off, [6,8) off, [8,10) on
        list.Toggle(2);
        list.Toggle(3);

        list.MergeAdjacentSameState();

        list.Segments.Should().HaveCount(3);
        list.Segments[0].Should().Match<Segment>(s => s.Start == 0.0 && s.End == 4.0 && s.Enabled);
        list.Segments[1].Should().Match<Segment>(s => s.Start == 4.0 && s.End == 8.0 && !s.Enabled);
        list.Segments[2].Should().Match<Segment>(s => s.Start == 8.0 && s.End == 10.0 && s.Enabled);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void MergeAdjacentSameState_keeps_the_first_segments_id_and_reason()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: false, SegmentOrigin.Auto, "silence 4s"),
            Segment.Create(4.0, 10.0, enabled: false, SegmentOrigin.Auto, "silence 6s"),
        };
        var list = new SegmentList(Duration, segments);

        list.MergeAdjacentSameState();

        list.Segments.Should().ContainSingle();
        list.Segments[0].Id.Should().Be(segments[0].Id);
        list.Segments[0].Reason.Should().Be("silence 4s");
    }

    [Fact]
    public void MergeAdjacentSameState_promotes_origin_to_manual_if_any_part_was_manual()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 10.0, enabled: true, SegmentOrigin.Manual),
        };
        var list = new SegmentList(Duration, segments);

        list.MergeAdjacentSameState();

        list.Segments[0].Origin.Should().Be(SegmentOrigin.Manual);
    }

    [Fact]
    public void MergeAdjacentSameState_on_alternating_states_changes_nothing()
    {
        var list = ThreeSegments();
        var before = list.Segments.ToList();

        list.MergeAdjacentSameState();

        list.Segments.Should().BeEquivalentTo(before, o => o.WithStrictOrdering());
    }

    [Fact]
    public void MergeAdjacentSameState_on_a_single_segment_changes_nothing()
    {
        var list = new SegmentList(Duration);
        var before = list.Segments[0];

        list.MergeAdjacentSameState();

        list.Segments.Should().ContainSingle().Which.Should().Be(before);
    }

    // ---- change notification ------------------------------------------------------------------

    [Fact]
    public void Mutations_raise_Changed_once_each()
    {
        var list = ThreeSegments();
        var count = 0;
        list.Changed += (_, _) => count++;

        list.Split(1.0);              // [0,1) on, [1,3) on, [3,6) off, [6,10) on
        list.Toggle(1);               // [1,3) off: now adjacent to [3,6) off
        list.MoveBoundary(1, 0.5);
        list.MergeAdjacentSameState(); // joins [0.5,3) and [3,6)

        count.Should().Be(4);
    }

    [Fact]
    public void No_op_split_does_not_raise_Changed()
    {
        var list = ThreeSegments();
        var count = 0;
        list.Changed += (_, _) => count++;

        list.Split(3.0);

        count.Should().Be(0);
    }
}
