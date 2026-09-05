using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class SilenceCutPlannerTests
{
    private const double Duration = 20.0;

    private static DetectionSettings Settings(int paddingMs = 60, int minKeepMs = 120)
        => DetectionSettings.Default with { PaddingMs = paddingMs, MinKeepMs = minKeepMs };

    [Fact]
    public void A_silence_becomes_a_cut_shrunk_by_the_padding_on_each_side()
    {
        var spans = new[] { new SilenceSpan(3.00, 5.00) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60), Duration);

        cuts.Should().ContainSingle();
        cuts[0].Start.Should().BeApproximately(3.06, 1e-9);
        cuts[0].End.Should().BeApproximately(4.94, 1e-9);
    }

    [Fact]
    public void The_reason_states_the_removed_length()
    {
        var spans = new[] { new SilenceSpan(3.00, 5.00) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60), Duration);

        cuts[0].Reason.Should().Be("silence 1.88s");
    }

    [Fact]
    public void A_silence_shorter_than_twice_the_padding_is_dropped()
    {
        var spans = new[] { new SilenceSpan(3.00, 3.10) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60), Duration);

        cuts.Should().BeEmpty();
    }

    [Fact]
    public void A_kept_fragment_shorter_than_minKeep_between_two_cuts_is_absorbed()
    {
        // cuts after padding: [1.06, 1.94] and [2.00, 2.94] -> kept gap 0.06 s < 0.12 s
        var spans = new[] { new SilenceSpan(1.00, 2.00), new SilenceSpan(1.94, 3.00) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60, minKeepMs: 120), Duration);

        cuts.Should().ContainSingle();
        cuts[0].Start.Should().BeApproximately(1.06, 1e-9);
        cuts[0].End.Should().BeApproximately(2.94, 1e-9);
    }

    [Fact]
    public void A_kept_fragment_longer_than_minKeep_survives()
    {
        var spans = new[] { new SilenceSpan(1.00, 2.00), new SilenceSpan(2.30, 3.00) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60, minKeepMs: 120), Duration);

        cuts.Should().HaveCount(2);
    }

    [Fact]
    public void A_short_kept_fragment_at_the_start_of_the_file_is_absorbed()
    {
        var spans = new[] { new SilenceSpan(0.02, 2.00) }; // cut starts at 0.08

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60, minKeepMs: 120), Duration);

        cuts.Should().ContainSingle();
        cuts[0].Start.Should().Be(0.0);
    }

    [Fact]
    public void A_short_kept_fragment_at_the_end_of_the_file_is_absorbed()
    {
        var spans = new[] { new SilenceSpan(18.00, 19.95) }; // cut ends at 19.89, 0.11 s before the end

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60, minKeepMs: 120), Duration);

        cuts.Should().ContainSingle();
        cuts[0].End.Should().Be(Duration);
    }

    [Fact]
    public void Spans_are_clamped_to_the_duration_and_sorted()
    {
        var spans = new[] { new SilenceSpan(15.0, 25.0), new SilenceSpan(-1.0, 2.0) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 0), Duration);

        cuts.Select(c => (c.Start, c.End)).Should().Equal((0.0, 2.0), (15.0, Duration));
    }

    [Fact]
    public void Overlapping_spans_are_merged()
    {
        var spans = new[] { new SilenceSpan(1.0, 3.0), new SilenceSpan(2.0, 4.0) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 0), Duration);

        cuts.Should().ContainSingle();
        cuts[0].Start.Should().Be(1.0);
        cuts[0].End.Should().Be(4.0);
    }

    [Fact]
    public void No_spans_means_no_cuts()
    {
        SilenceCutPlanner.Plan([], Settings(), Duration).Should().BeEmpty();
    }

    [Fact]
    public void Zero_padding_and_zero_minKeep_are_allowed()
    {
        var spans = new[] { new SilenceSpan(1.0, 2.0) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 0, minKeepMs: 0), Duration);

        cuts.Should().ContainSingle().Which.Should().Match<PlannedCut>(c => c.Start == 1.0 && c.End == 2.0);
    }
}
