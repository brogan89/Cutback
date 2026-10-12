using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class SegmentListGameplayTests
{
    private const double Duration = 60.0;

    private static PlannedCut Idle(double start, double end) => new(start, end, $"idle {end - start:0.0}s");

    [Fact]
    public void Gameplay_cuts_become_disabled_gameplay_segments()
    {
        var list = new SegmentList(Duration);

        var applied = list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20), Idle(30, 35)]);

        applied.Should().Be(2);
        list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)).Should().Equal(
            (0.0, 10.0, true, SegmentOrigin.Auto),
            (10.0, 20.0, false, SegmentOrigin.Gameplay),
            (20.0, 30.0, true, SegmentOrigin.Auto),
            (30.0, 35.0, false, SegmentOrigin.Gameplay),
            (35.0, 60.0, true, SegmentOrigin.Auto));
        list.Segments[1].Reason.Should().Be("idle 10.0s");
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Rerun_replaces_only_previous_gameplay_cuts()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([new PlannedCut(40.0, 40.4, "filler: um")]);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(25, 28)]);

        list.Segments.Where(s => !s.Enabled).Select(s => (s.Start, s.End, s.Origin)).Should().Equal(
            (25.0, 28.0, SegmentOrigin.Gameplay),
            (40.0, 40.4, SegmentOrigin.Filler));
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Silence_redetection_keeps_gameplay_cuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ReplaceAutoSegments([new PlannedCut(5.0, 6.0, "silence 1.00s"), new PlannedCut(15.0, 16.0, "silence 1.00s")]);

        list.Segments.Where(s => !s.Enabled).Select(s => (s.Start, s.End, s.Origin)).Should().Equal(
            (5.0, 6.0, SegmentOrigin.Auto),
            (10.0, 20.0, SegmentOrigin.Gameplay));
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Filler_rerun_keeps_gameplay_cuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ApplyFillerCuts([new PlannedCut(40.0, 40.4, "filler: um")]);

        list.Segments.Should().Contain(s => s.Origin == SegmentOrigin.Gameplay && s.Start == 10.0 && s.End == 20.0 && !s.Enabled);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Theory]
    [InlineData(SegmentOrigin.Auto)]
    [InlineData(SegmentOrigin.Manual)]
    public void Auto_and_manual_cannot_be_replaced_in_bulk(SegmentOrigin origin)
    {
        var list = new SegmentList(Duration);

        var act = () => list.ReplaceOriginCuts(origin, []);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void An_empty_rerun_removes_earlier_gameplay_cuts_and_reports_zero()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        var applied = list.ReplaceOriginCuts(SegmentOrigin.Gameplay, []);

        applied.Should().Be(0);
        list.Segments.Should().OnlyContain(s => s.Enabled);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Dissolving_between_a_gameplay_cut_and_a_silence_cut_keeps_the_gameplay_origin()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);
        list.ReplaceAutoSegments([new PlannedCut(20.5, 25.0, "silence 4.50s")]);

        // Segments: [0,10] [10,20] gameplay cut, [20,20.5] kept, [20.5,25] auto cut, [25,60].
        var keptBetween = list.Segments.ToList().FindIndex(s => s.Start == 20.0 && s.End == 20.5);
        keptBetween.Should().BeGreaterThan(0);
        list.Dissolve(keptBetween).Should().BeTrue();

        var merged = list.Segments.Single(s => s.Start == 10.0 && s.End == 25.0);
        merged.Enabled.Should().BeFalse();
        merged.Origin.Should().Be(SegmentOrigin.Gameplay);

        // A later Detect silence discards Auto segments only, so the merged cut must survive it.
        list.ReplaceAutoSegments([]);

        list.Segments.Should().Contain(s => s.Origin == SegmentOrigin.Gameplay && !s.Enabled && s.Start == 10.0 && s.End == 25.0);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Rerun_withdraws_gameplay_cuts_next_to_a_silence_cut_without_touching_it()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([new PlannedCut(8, 12, "silence 4.00s")]);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(12, 29.5)]);

        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, []);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin, s.Reason)).Should().Equal(
            (0.0, 8.0, true, SegmentOrigin.Auto, (string?)null),
            (8.0, 12.0, false, SegmentOrigin.Auto, "silence 4.00s"),
            (12.0, 60.0, true, SegmentOrigin.Auto, null));
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Rerun_does_not_spread_a_manual_cut_over_the_old_gameplay_footage()
    {
        var list = new SegmentList(Duration);
        list.SetRange(5, 10, enabled: false);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(12, 18)]);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)).Should().Equal(
            (0.0, 5.0, true, SegmentOrigin.Auto),
            (5.0, 10.0, false, SegmentOrigin.Manual),
            (10.0, 12.0, true, SegmentOrigin.Auto),
            (12.0, 18.0, false, SegmentOrigin.Gameplay),
            (18.0, 60.0, true, SegmentOrigin.Auto));
        list.ShouldBeValidPartitionOf(Duration);
    }
}
