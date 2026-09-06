using Cutback.Core.Models;
using Cutback.Core.Transcript;

namespace Cutback.Core.Tests;

public sealed class OutputTimelineTests
{
    private static readonly IReadOnlyList<Segment> Segments =
    [
        Segment.Create(0.0, 2.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(2.0, 3.0, enabled: false, SegmentOrigin.Auto),
        Segment.Create(3.0, 6.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(6.0, 10.0, enabled: false, SegmentOrigin.Auto),
    ];

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.0, 2.0)]   // start of the cut maps to where the next kept region begins
    [InlineData(2.7, 2.0)]   // inside the cut: same
    [InlineData(3.0, 2.0)]
    [InlineData(4.5, 3.5)]
    [InlineData(6.0, 5.0)]   // trailing cut maps to the output end
    [InlineData(9.9, 5.0)]
    public void Source_times_map_to_output_times(double source, double expected)
    {
        new OutputTimeline(Segments).ToOutput(source).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Output_duration_is_the_sum_of_kept_segments()
    {
        new OutputTimeline(Segments).OutputDuration.Should().BeApproximately(5.0, 1e-9);
    }

    [Fact]
    public void With_nothing_kept_everything_maps_to_zero()
    {
        var timeline = new OutputTimeline([Segment.Create(0.0, 5.0, enabled: false, SegmentOrigin.Auto)]);

        timeline.OutputDuration.Should().Be(0.0);
        timeline.ToOutput(3.0).Should().Be(0.0);
    }
}
