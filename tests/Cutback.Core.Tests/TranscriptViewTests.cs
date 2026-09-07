using Cutback.Core.Models;
using Cutback.Core.Transcript;

namespace Cutback.Core.Tests;

public sealed class TranscriptViewTests
{
    private static readonly IReadOnlyList<Segment> Segments =
    [
        Segment.Create(0.0, 2.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(2.0, 3.0, enabled: false, SegmentOrigin.Auto),
        Segment.Create(3.0, 10.0, enabled: true, SegmentOrigin.Auto),
    ];

    private static readonly IReadOnlyList<Word> Words =
    [
        new("hello", 0.5, 0.9, 0.9),
        new("um", 1.9, 2.3, 0.9),      // midpoint 2.1: cut
        new("uh", 2.8, 3.1, 0.9),      // midpoint 2.95: cut
        new("world", 2.9, 3.5, 0.9),   // midpoint 3.2: kept
    ];

    [Fact]
    public void A_word_is_cut_when_its_midpoint_lies_in_a_disabled_segment()
    {
        TranscriptView.IsCut(Words[0], Segments).Should().BeFalse();
        TranscriptView.IsCut(Words[1], Segments).Should().BeTrue();
        TranscriptView.IsCut(Words[2], Segments).Should().BeTrue();
        TranscriptView.IsCut(Words[3], Segments).Should().BeFalse();
    }

    [Fact]
    public void CutStates_matches_IsCut_for_every_word()
    {
        TranscriptView.CutStates(Words, Segments).Should().Equal(false, true, true, false);
    }

    [Fact]
    public void A_word_with_an_anchor_is_judged_by_the_anchor_not_the_midpoint()
    {
        // Whisper put this "uh" on the pause after it (midpoint 3.5, kept) but its anchor is in the cut.
        var anchored = new Word("uh", 3.2, 3.8, 0.9, Anchor: 2.5);
        // And this neighbour's heuristic span swallowed the cut (midpoint 2.4) though it was spoken before it.
        var neighbour = new Word("like", 1.8, 3.0, 0.9, Anchor: 1.9);

        TranscriptView.IsCut(anchored, Segments).Should().BeTrue();
        TranscriptView.IsCut(neighbour, Segments).Should().BeFalse();
    }

    [Fact]
    public void A_word_past_the_end_of_the_timeline_takes_the_last_segment()
    {
        var late = new Word("late", 9.9, 10.3, 0.9);

        TranscriptView.IsCut(late, Segments).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(0.89, 0)]
    [InlineData(0.9, -1)]
    [InlineData(1.0, -1)]
    [InlineData(2.0, 1)]
    [InlineData(3.4, 3)]
    [InlineData(50.0, -1)]
    public void IndexAtTime_finds_the_word_containing_the_time(double seconds, int expected)
    {
        TranscriptView.IndexAtTime(Words, seconds).Should().Be(expected);
    }

    [Fact]
    public void IndexAtTime_on_an_empty_transcript_is_minus_one()
    {
        TranscriptView.IndexAtTime([], 1.0).Should().Be(-1);
    }
}
