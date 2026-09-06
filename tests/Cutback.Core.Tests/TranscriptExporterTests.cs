using Cutback.Core.Models;
using Cutback.Core.Transcript;

namespace Cutback.Core.Tests;

public sealed class TranscriptExporterTests
{
    // "um" (1.0..1.2) is cut; a 3.3 s pause precedes "Bye".
    private static readonly IReadOnlyList<Segment> Segments =
    [
        Segment.Create(0.0, 1.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(1.0, 1.2, enabled: false, SegmentOrigin.Filler, "filler: um"),
        Segment.Create(1.2, 6.0, enabled: true, SegmentOrigin.Auto),
    ];

    private static readonly IReadOnlyList<Word> Words =
    [
        new("Hello", 0.0, 0.4, 0.9),
        new("there", 0.5, 0.9, 0.9),
        new("um", 1.0, 1.2, 0.9),
        new("friend", 1.3, 1.7, 0.9),
        new("Bye", 5.0, 5.3, 0.9),
    ];

    [Fact]
    public void Plain_text_omits_cut_words_and_breaks_paragraphs_on_long_pauses()
    {
        TranscriptExporter.ToPlainText(Words, Segments).Should().Be("Hello there friend\n\nBye\n");
    }

    [Fact]
    public void Plain_text_of_an_empty_transcript_is_empty()
    {
        TranscriptExporter.ToPlainText([], Segments).Should().BeEmpty();
    }

    [Fact]
    public void Srt_remaps_times_to_the_output_and_splits_cues_on_pauses()
    {
        var expected =
            "1\n00:00:00,000 --> 00:00:01,500\nHello there friend\n\n" +
            "2\n00:00:04,800 --> 00:00:05,100\nBye\n";

        TranscriptExporter.ToSrt(Words, Segments).Should().Be(expected);
    }

    [Fact]
    public void Srt_wraps_long_cues_into_two_lines_of_at_most_42_characters()
    {
        var words = Enumerable.Range(1, 12).Select(i => new Word($"word{i}", i * 0.3, i * 0.3 + 0.2, 0.9)).ToList();
        var segments = new[] { Segment.Create(0.0, 10.0, enabled: true, SegmentOrigin.Auto) };

        var srt = TranscriptExporter.ToSrt(words, segments);

        var lines = srt.Split('\n');
        lines[0].Should().Be("1");
        lines[2].Length.Should().BeLessThanOrEqualTo(42);
        lines[3].Length.Should().BeLessThanOrEqualTo(42);
        (lines[2] + " " + lines[3]).Should().Be(string.Join(' ', words.Select(w => w.Text)));
        lines[4].Should().BeEmpty();
    }

    [Fact]
    public void Srt_starts_a_new_cue_when_the_text_would_exceed_84_characters()
    {
        var words = Enumerable.Range(1, 30).Select(i => new Word("abcde", i * 0.3, i * 0.3 + 0.2, 0.9)).ToList();
        var segments = new[] { Segment.Create(0.0, 20.0, enabled: true, SegmentOrigin.Auto) };

        var srt = TranscriptExporter.ToSrt(words, segments);

        srt.Should().Contain("\n\n2\n");
        foreach (var cueText in srt.Split("\n\n").Select(block => string.Join(' ', block.Split('\n').Skip(2))))
        {
            cueText.Length.Should().BeLessThanOrEqualTo(84);
        }
    }

    [Fact]
    public void Srt_starts_a_new_cue_when_a_cue_would_exceed_five_seconds()
    {
        var words = Enumerable.Range(0, 25).Select(i => new Word("go", i * 0.5, i * 0.5 + 0.3, 0.9)).ToList();
        var segments = new[] { Segment.Create(0.0, 20.0, enabled: true, SegmentOrigin.Auto) };

        var srt = TranscriptExporter.ToSrt(words, segments);

        srt.Should().Contain("\n\n2\n").And.Contain("\n\n3\n");
    }

    [Theory]
    [InlineData(0.0, "00:00:00,000")]
    [InlineData(1.5, "00:00:01,500")]
    [InlineData(61.0, "00:01:01,000")]
    [InlineData(3661.25, "01:01:01,250")]
    [InlineData(1.1000000000000001, "00:00:01,100")]
    public void Srt_time_format(double seconds, string expected)
    {
        TranscriptExporter.FormatSrtTime(seconds).Should().Be(expected);
    }
}
