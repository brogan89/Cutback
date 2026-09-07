using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class SegmentListFillerTests
{
    private const double Duration = 10.0;

    private static PlannedCut Filler(double start, double end, string word = "um") => new(start, end, $"filler: {word}");

    private static PlannedCut Silence(double start, double end) => new(start, end, $"silence {end - start:0.00}s");

    [Fact]
    public void SetRange_with_origin_stamps_origin_and_reason()
    {
        var list = new SegmentList(Duration);

        var index = list.SetRange(2.0, 2.3, enabled: false, SegmentOrigin.Filler, "filler: um");

        list.Segments[index].Should().BeEquivalentTo(new { Start = 2.0, End = 2.3, Enabled = false, Origin = SegmentOrigin.Filler, Reason = "filler: um" });
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Three_argument_SetRange_is_still_manual_with_no_reason()
    {
        var list = new SegmentList(Duration);

        var index = list.SetRange(2.0, 2.3, enabled: false);

        list.Segments[index].Origin.Should().Be(SegmentOrigin.Manual);
        list.Segments[index].Reason.Should().BeNull();
    }

    [Fact]
    public void Filler_cuts_become_disabled_filler_segments_inside_kept_footage()
    {
        var list = new SegmentList(Duration);

        var applied = list.ApplyFillerCuts([Filler(2.0, 2.3), Filler(5.0, 5.4, "uh")]);

        applied.Should().Be(2);
        list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)).Should().Equal(
            (0.0, 2.0, true, SegmentOrigin.Auto),
            (2.0, 2.3, false, SegmentOrigin.Filler),
            (2.3, 5.0, true, SegmentOrigin.Auto),
            (5.0, 5.4, false, SegmentOrigin.Filler),
            (5.4, 10.0, true, SegmentOrigin.Auto));
        list.Segments[3].Reason.Should().Be("filler: uh");
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Re_running_replaces_earlier_filler_cuts()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);

        list.ApplyFillerCuts([Filler(6.0, 6.2)]);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled)).Should().Equal(
            (0.0, 6.0, true),
            (6.0, 6.2, false),
            (6.2, 10.0, true));
        list.Segments.Should().NotContain(s => s.Origin == SegmentOrigin.Filler && s.Start == 2.0);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Silence_re_detection_preserves_filler_cuts()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);

        list.ReplaceAutoSegments([Silence(6.0, 7.0)]);

        list.Segments.Should().Contain(s => s.Start == 2.0 && s.End == 2.3 && !s.Enabled && s.Origin == SegmentOrigin.Filler);
        list.Segments.Should().Contain(s => s.Start == 6.0 && s.End == 7.0 && !s.Enabled && s.Origin == SegmentOrigin.Auto);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Filler_re_detection_preserves_silence_cuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Silence(6.0, 7.0)]);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);

        list.ApplyFillerCuts([Filler(3.0, 3.2)]);

        list.Segments.Should().Contain(s => s.Start == 6.0 && s.End == 7.0 && !s.Enabled && s.Origin == SegmentOrigin.Auto);
        list.Segments.Should().Contain(s => s.Start == 3.0 && s.End == 3.2 && !s.Enabled && s.Origin == SegmentOrigin.Filler);
        list.Segments.Should().NotContain(s => s.Start == 2.0 && s.End == 2.3);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Removing_a_filler_cut_next_to_a_silence_cut_gives_the_footage_back_to_the_kept_side()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Silence(6.0, 7.0)]);
        list.ApplyFillerCuts([Filler(5.8, 6.0)]);

        list.ApplyFillerCuts([]);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled)).Should().Equal(
            (0.0, 6.0, true),
            (6.0, 7.0, false),
            (7.0, 10.0, true));
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void ApplyFillerCuts_raises_Changed_once()
    {
        var list = new SegmentList(Duration);
        var raised = 0;
        list.Changed += (_, _) => raised++;

        list.ApplyFillerCuts([Filler(2.0, 2.3), Filler(5.0, 5.4)]);

        raised.Should().Be(1);
    }

    [Fact]
    public void ApplyFillerCuts_with_nothing_to_do_does_not_raise_Changed()
    {
        var list = new SegmentList(Duration);
        var raised = 0;
        list.Changed += (_, _) => raised++;

        list.ApplyFillerCuts([]);

        raised.Should().Be(0);
    }

    [Fact]
    public void Planning_against_the_current_partition_and_applying_twice_keeps_the_same_cuts()
    {
        var list = new SegmentList(Duration);
        var settings = DetectionSettings.Default;
        FillerSpan[] spans = [new(2.0, 2.3, "um"), new(5.0, 5.4, "uh")];

        list.ApplyFillerCuts(FillerCutPlanner.Plan(spans, list.Segments, settings, Duration));
        var first = list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)).ToList();

        list.ApplyFillerCuts(FillerCutPlanner.Plan(spans, list.Segments, settings, Duration));

        list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)).Should().Equal(first);
        list.Segments.Where(s => !s.Enabled).Should().HaveCount(2);
        list.ShouldBeValidPartitionOf(Duration);
    }
}
