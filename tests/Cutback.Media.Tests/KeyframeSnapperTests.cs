using Cutback.Core.Models;
using Cutback.Media.Export;

namespace Cutback.Media.Tests;

public sealed class KeyframeSnapperTests
{
    private static Segment Kept(double start, double end) => Segment.Create(start, end, enabled: true, SegmentOrigin.Auto);

    private static readonly double[] Keyframes = [0.0, 2.0, 4.0, 6.0, 8.0, 10.0];

    [Fact]
    public void Start_moves_back_to_the_preceding_keyframe_and_the_end_stays_exact()
    {
        // Stream copy must begin on a keyframe but can stop anywhere, so a kept region only ever
        // grows: nothing the user kept is lost, some extra is included.
        var ranges = KeyframeSnapper.Snap([Kept(2.9, 7.2)], Keyframes, duration: 12.0);

        ranges.Should().Equal((2.0, 7.2));
    }

    [Fact]
    public void A_start_already_on_a_keyframe_stays_put()
    {
        var ranges = KeyframeSnapper.Snap([Kept(4.0, 7.0)], Keyframes, duration: 12.0);

        ranges.Should().Equal((4.0, 7.0));
    }

    [Fact]
    public void A_start_before_the_first_keyframe_becomes_zero()
    {
        var ranges = KeyframeSnapper.Snap([Kept(0.5, 3.0)], [2.0, 4.0], duration: 12.0);

        ranges.Should().Equal((0.0, 3.0));
    }

    [Fact]
    public void The_end_of_file_is_kept_as_is()
    {
        var ranges = KeyframeSnapper.Snap([Kept(9.5, 12.0)], Keyframes, duration: 12.0);

        ranges.Should().Equal((8.0, 12.0));
    }

    [Fact]
    public void Ranges_that_overlap_after_snapping_are_merged()
    {
        var ranges = KeyframeSnapper.Snap([Kept(0.0, 3.1), Kept(3.1, 5.0)], Keyframes, duration: 12.0);

        ranges.Should().Equal((0.0, 5.0));
    }

    [Fact]
    public void A_cut_shorter_than_the_gap_to_the_previous_keyframe_disappears()
    {
        // Kept [0,3) and [3.5,5): the second range snaps back to 2.0, swallowing the cut.
        var segments = new[] { Kept(0.0, 3.0), Segment.Create(3.0, 3.5, enabled: false, SegmentOrigin.Auto), Kept(3.5, 5.0) };

        var ranges = KeyframeSnapper.Snap(segments, Keyframes, duration: 12.0);

        ranges.Should().Equal((0.0, 5.0));
    }

    [Fact]
    public void Disabled_segments_are_ignored()
    {
        var segments = new[] { Kept(0.0, 2.0), Segment.Create(2.0, 8.0, enabled: false, SegmentOrigin.Auto), Kept(8.0, 12.0) };

        var ranges = KeyframeSnapper.Snap(segments, Keyframes, duration: 12.0);

        ranges.Should().Equal((0.0, 2.0), (8.0, 12.0));
    }

    [Fact]
    public void Without_keyframes_the_ranges_are_unchanged()
    {
        var ranges = KeyframeSnapper.Snap([Kept(2.9, 7.2)], [], duration: 12.0);

        ranges.Should().Equal((2.9, 7.2));
    }

    [Fact]
    public void Parses_ffprobe_packet_csv_lines_for_keyframes()
    {
        var lines = new[]
        {
            "0.000000,K__",
            "0.033333,___",
            "2.000000,K__",
            "N/A,K__",
            "4.000000,K_D",
            "",
        };

        KeyframeSnapper.ParseKeyframes(lines).Should().Equal(0.0, 2.0, 4.0);
    }
}
