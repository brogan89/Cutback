using Cutback.Core.Playback;

namespace Cutback.Core.Tests;

public sealed class PlayheadInterpolatorTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void Before_any_report_the_estimate_is_zero()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };

        clock.Estimate(now: 100).Should().Be(0);
    }

    [Fact]
    public void The_estimate_advances_with_the_wall_clock_between_reports()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);

        clock.Estimate(now: 100.25).Should().BeApproximately(10.25, Tolerance);
    }

    [Fact]
    public void A_report_behind_the_estimate_does_not_move_the_playhead_backwards()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);
        clock.Estimate(now: 100.4).Should().BeApproximately(10.4, Tolerance);

        clock.Report(position: 10.3, now: 100.4);

        clock.Estimate(now: 100.4).Should().BeApproximately(10.4, Tolerance);
        clock.Estimate(now: 100.6).Should().BeApproximately(10.6, Tolerance);
    }

    [Fact]
    public void A_report_ahead_of_the_estimate_jumps_forward_to_it()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);

        clock.Report(position: 12, now: 100.1);

        clock.Estimate(now: 100.1).Should().BeApproximately(12, Tolerance);
    }

    [Fact]
    public void A_seek_may_move_the_playhead_backwards()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);
        clock.Estimate(now: 100.5);

        clock.Seek(position: 2, now: 100.5);

        clock.Estimate(now: 100.5).Should().BeApproximately(2, Tolerance);
        clock.Estimate(now: 100.7).Should().BeApproximately(2.2, Tolerance);
    }

    [Fact]
    public void The_estimate_never_passes_the_duration()
    {
        var clock = new PlayheadInterpolator { Duration = 11 };
        clock.Report(position: 10.9, now: 100);

        clock.Estimate(now: 101).Should().Be(11);
    }

    [Fact]
    public void While_paused_the_estimate_freezes_and_resumes_from_where_it_stopped()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);

        clock.Pause(now: 100.3);
        clock.Estimate(now: 101).Should().BeApproximately(10.3, Tolerance);

        clock.Resume(now: 101);
        clock.Estimate(now: 101.2).Should().BeApproximately(10.5, Tolerance);
    }

    [Fact]
    public void A_report_while_paused_moves_the_frozen_playhead()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);
        clock.Pause(now: 100);

        clock.Report(position: 10.5, now: 100.2);

        clock.Estimate(now: 105).Should().BeApproximately(10.5, Tolerance);
    }
}
