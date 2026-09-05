using Cutback.Core.Models;
using Cutback.Media.Export;

namespace Cutback.Media.Tests;

public sealed class FilterGraphBuilderTests
{
    private static Segment Kept(double start, double end) => Segment.Create(start, end, enabled: true, SegmentOrigin.Auto);

    private static Segment Cut(double start, double end) => Segment.Create(start, end, enabled: false, SegmentOrigin.Auto);

    [Fact]
    public void Single_segment_trims_fades_and_concats_once()
    {
        var graph = FilterGraphBuilder.Build([Kept(0.0, 3.21)]);

        graph.Should().Be(
            "[0:v]trim=start=0:end=3.21,setpts=PTS-STARTPTS[v0];\n" +
            "[0:a]atrim=start=0:end=3.21,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.008,afade=t=out:st=3.202:d=0.008[a0];\n" +
            "[v0][a0]concat=n=1:v=1:a=1[outv][outa]");
    }

    [Fact]
    public void Disabled_segments_are_skipped_and_indices_stay_dense()
    {
        var graph = FilterGraphBuilder.Build([Kept(0.0, 3.21), Cut(3.21, 4.86), Kept(4.86, 12.04)]);

        graph.Should().Be(
            "[0:v]trim=start=0:end=3.21,setpts=PTS-STARTPTS[v0];\n" +
            "[0:a]atrim=start=0:end=3.21,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.008,afade=t=out:st=3.202:d=0.008[a0];\n" +
            "[0:v]trim=start=4.86:end=12.04,setpts=PTS-STARTPTS[v1];\n" +
            "[0:a]atrim=start=4.86:end=12.04,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.008,afade=t=out:st=7.172:d=0.008[a1];\n" +
            "[v0][a0][v1][a1]concat=n=2:v=1:a=1[outv][outa]");
    }

    [Fact]
    public void Every_kept_segment_gets_an_8ms_fade_in_and_out()
    {
        var graph = FilterGraphBuilder.Build([Kept(0.0, 1.0), Cut(1.0, 2.0), Kept(2.0, 3.0), Cut(3.0, 4.0), Kept(4.0, 5.0)]);

        graph.Split('\n').Where(l => l.StartsWith("[0:a]", StringComparison.Ordinal))
            .Should().HaveCount(3)
            .And.OnlyContain(l => l.Contains("afade=t=in:st=0:d=0.008") && l.Contains("afade=t=out:st=0.992:d=0.008"));
    }

    [Fact]
    public void A_segment_shorter_than_two_fades_uses_half_its_length_per_fade()
    {
        var graph = FilterGraphBuilder.Build([Kept(0.0, 0.01)]);

        graph.Should().Contain("afade=t=in:st=0:d=0.005,afade=t=out:st=0.005:d=0.005");
    }

    [Fact]
    public void Numbers_are_invariant_culture_with_no_scientific_notation()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var graph = FilterGraphBuilder.Build([Kept(0.0000001, 1234567.5)]);

            graph.Should().Contain("trim=start=0:end=1234567.5,");
            graph.Should().NotContain(",5").And.NotContain("E-");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Times_keep_microsecond_precision()
    {
        var graph = FilterGraphBuilder.Build([Kept(1.123456789, 2.5)]);

        graph.Should().Contain("trim=start=1.123457:end=2.5,");
    }

    [Fact]
    public void No_kept_segments_is_an_error()
    {
        var act = () => FilterGraphBuilder.Build([Cut(0.0, 1.0)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*nothing*");
    }

    [Fact]
    public void Kept_duration_sums_enabled_segments()
    {
        FilterGraphBuilder.KeptDuration([Kept(0.0, 1.0), Cut(1.0, 2.0), Kept(2.0, 3.5)]).Should().BeApproximately(2.5, 1e-9);
    }
}
