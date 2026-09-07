using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

/// <summary>
/// Envelopes here are 10 ms buckets of loudness. The shapes replay what a real recording showed:
/// an "uh" alone in its own burst of speech, an "uh" whose Whisper timestamp sat in a pause next
/// to it, and an "uh" run together with the word before it, whose heuristic span had swallowed
/// the whole burst.
/// </summary>
public sealed class FillerSpanLocatorTests
{
    private const double Bucket = 0.010;
    private const double Quiet = 100;
    private const double Loud = 2000;
    private const double Threshold = 800;

    /// <summary>Three seconds of silence with loud stretches painted over it.</summary>
    private static double[] Envelope(params (double Start, double End, double Level)[] runs)
    {
        var env = new double[300];
        Array.Fill(env, Quiet);
        foreach (var (start, end, level) in runs)
        {
            for (var b = (int)Math.Round(start / Bucket); b < (int)Math.Round(end / Bucket); b++)
            {
                env[b] = level;
            }
        }

        return env;
    }

    private static Word W(string text, double start, double end, double? anchor) => new(text, start, end, 0.9, anchor);

    private static void ShouldBeSpan((double Start, double End)? span, double start, double end)
    {
        span.Should().NotBeNull();
        span!.Value.Start.Should().BeApproximately(start, 1e-9);
        span.Value.End.Should().BeApproximately(end, 1e-9);
    }

    [Fact]
    public void A_filler_alone_in_a_burst_of_speech_is_the_whole_burst()
    {
        var env = Envelope((0.5, 0.8, Loud), (1.0, 1.5, Loud));

        var span = FillerSpanLocator.Locate(env, Bucket, Threshold, W("that,", 0.5, 0.95, 0.6), W("uh,", 0.95, 1.5, 1.3), W("I'm", 1.5, 2.0, 2.0));

        ShouldBeSpan(span, 1.0, 1.5);
    }

    [Fact]
    public void A_previous_word_whose_span_barely_touches_the_burst_does_not_count_as_merged()
    {
        // The real first "uh": the previous word's heuristic span overlapped the burst by 80 ms and
        // its comma's anchor even fell inside it, yet the burst was the filler alone.
        var env = Envelope((1.0, 1.6, Loud), (1.30, 1.34, 1700));

        var span = FillerSpanLocator.Locate(env, Bucket, Threshold, W("that,", 0.5, 1.08, 1.05), W("uh,", 1.08, 1.6, 1.5), null);

        ShouldBeSpan(span, 1.0, 1.6);
    }

    [Fact]
    public void An_anchor_in_a_pause_takes_the_nearest_burst_within_the_search_radius()
    {
        var env = Envelope((1.0, 1.3, Loud));

        ShouldBeSpan(FillerSpanLocator.Locate(env, Bucket, Threshold, null, W("uh", 1.4, 1.6, 1.45), null), 1.0, 1.3);
        FillerSpanLocator.Locate(env, Bucket, Threshold, null, W("uh", 1.6, 1.8, 1.7), null).Should().BeNull();
    }

    [Fact]
    public void A_filler_without_an_anchor_cannot_be_located()
    {
        var env = Envelope((1.0, 1.3, Loud));

        FillerSpanLocator.Locate(env, Bucket, Threshold, null, W("uh", 1.0, 1.3, null), null).Should().BeNull();
    }

    [Fact]
    public void A_gap_shorter_than_the_merge_gap_does_not_split_a_burst()
    {
        var env = Envelope((1.0, 1.2, Loud), (1.24, 1.5, Loud));

        ShouldBeSpan(FillerSpanLocator.Locate(env, Bucket, Threshold, null, W("uh", 1.3, 1.5, 1.4), null), 1.0, 1.5);
    }

    [Fact]
    public void A_filler_merged_with_the_previous_word_is_split_at_the_valley_before_its_anchor()
    {
        var env = Envelope((1.0, 1.6, Loud), (1.30, 1.34, 1200)); // a dip to 60% of the run's level

        // The previous word's heuristic span swallowed the burst; the filler's own span sits in the pause after it.
        var span = FillerSpanLocator.Locate(env, Bucket, Threshold, W("like,", 0.9, 1.6, 0.8), W("uh,", 1.6, 2.0, 1.45), W("like", 2.0, 2.4, 2.5));

        ShouldBeSpan(span, 1.30, 1.6);
    }

    [Fact]
    public void A_filler_merged_with_the_previous_word_and_no_valley_starts_shortly_before_its_anchor()
    {
        var env = Envelope((1.0, 1.6, Loud));

        var span = FillerSpanLocator.Locate(env, Bucket, Threshold, W("like,", 0.9, 1.6, 0.8), W("uh,", 1.6, 2.0, 1.45), null);

        ShouldBeSpan(span, 1.45 - FillerSpanLocator.AnchorLeadSeconds, 1.6);
    }

    [Fact]
    public void A_ramped_burst_edge_is_not_mistaken_for_a_valley()
    {
        // The first bucket of a burst is often a ramp: above the speech threshold, well below the median.
        var env = Envelope((1.0, 1.6, Loud), (1.0, 1.01, 1000));

        var span = FillerSpanLocator.Locate(env, Bucket, Threshold, W("like,", 0.9, 1.6, 0.8), W("uh,", 1.6, 2.0, 1.45), null);

        ShouldBeSpan(span, 1.45 - FillerSpanLocator.AnchorLeadSeconds, 1.6);
    }

    [Fact]
    public void A_filler_merged_with_the_next_word_is_split_at_the_valley_after_its_anchor()
    {
        var env = Envelope((1.0, 1.6, Loud), (1.40, 1.44, 1200));

        var span = FillerSpanLocator.Locate(env, Bucket, Threshold, null, W("uh", 0.8, 1.0, 1.2), W("so", 1.0, 1.6, 1.5));

        ShouldBeSpan(span, 1.0, 1.40);
    }

    [Fact]
    public void A_filler_merged_with_the_next_word_and_no_valley_ends_within_the_tail_allowance()
    {
        var env = Envelope((1.0, 1.6, Loud));

        var span = FillerSpanLocator.Locate(env, Bucket, Threshold, null, W("uh", 0.8, 1.0, 1.2), W("so", 1.0, 1.6, 1.5));

        ShouldBeSpan(span, 1.0, 1.2 + FillerSpanLocator.AnchorTailSeconds);
    }

    [Fact]
    public void A_burst_at_the_end_of_the_envelope_ends_at_the_envelope()
    {
        var env = Envelope((2.8, 3.0, Loud));

        ShouldBeSpan(FillerSpanLocator.Locate(env, Bucket, Threshold, null, W("uh", 2.8, 3.0, 2.9), null), 2.8, 3.0);
    }

    [Fact]
    public void Speech_threshold_is_four_times_the_quiet_floor()
    {
        var env = Envelope((1.0, 1.5, 3000));

        FillerSpanLocator.SpeechThreshold(env).Should().Be(400);
    }

    [Fact]
    public void Speech_threshold_never_drops_below_two_percent_of_the_loudest_level()
    {
        var env = Envelope((1.0, 1.5, 3000));
        Array.Fill(env, 1.0, 0, 100); // a near-digital-silence stretch would make the floor tiny

        FillerSpanLocator.SpeechThreshold(env).Should().BeGreaterThanOrEqualTo(60);
    }
}
