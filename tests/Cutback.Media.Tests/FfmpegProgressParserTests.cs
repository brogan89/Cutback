namespace Cutback.Media.Tests;

public sealed class FfmpegProgressParserTests
{
    [Fact]
    public void Reports_fraction_from_out_time_us()
    {
        var parser = new FfmpegProgressParser(totalSeconds: 10.0);

        parser.Feed("frame=120").Should().BeNull();
        parser.Feed("out_time_us=2500000").Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public void Falls_back_to_out_time_ms_which_is_actually_microseconds()
    {
        // ffmpeg's out_time_ms key has always carried microseconds.
        var parser = new FfmpegProgressParser(10.0);

        parser.Feed("out_time_ms=5000000").Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Parses_the_hms_out_time_form()
    {
        var parser = new FfmpegProgressParser(120.0);

        parser.Feed("out_time=00:01:00.000000").Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Clamps_to_one()
    {
        var parser = new FfmpegProgressParser(10.0);

        parser.Feed("out_time_us=99000000").Should().Be(1.0);
    }

    [Fact]
    public void Progress_end_reports_completion()
    {
        var parser = new FfmpegProgressParser(10.0);

        parser.Feed("progress=end").Should().Be(1.0);
        parser.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Ignores_garbage_values()
    {
        var parser = new FfmpegProgressParser(10.0);

        parser.Feed("out_time_us=N/A").Should().BeNull();
        parser.Feed("out_time_us=").Should().BeNull();
    }

    [Fact]
    public void Unknown_total_yields_no_fraction()
    {
        var parser = new FfmpegProgressParser(0);

        parser.Feed("out_time_us=2500000").Should().BeNull();
    }
}
