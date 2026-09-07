namespace Cutback.Media.Tests;

public sealed class WaveformTests
{
    [Fact]
    public void Downsample_merges_groups_of_peaks()
    {
        var level = new WaveformLevel(samplesPerBucket: 16, sampleRate: 8000, peaks:
        [
            new Peak(-1, 1), new Peak(-9, 2), new Peak(-3, 8), new Peak(0, 0),
            new Peak(-2, 2), new Peak(-4, 5),
        ]);

        var coarser = level.Downsample(4);

        coarser.SamplesPerBucket.Should().Be(64);
        coarser.Peaks.Should().Equal(new Peak(-9, 8), new Peak(-4, 5));
    }

    [Fact]
    public void SecondsPerBucket_derives_from_sample_rate()
    {
        var level = new WaveformLevel(samplesPerBucket: 16, sampleRate: 8000, peaks: []);

        level.SecondsPerBucket.Should().BeApproximately(0.002, 1e-12);
    }

    [Fact]
    public void Build_creates_levels_from_finest_by_the_given_factor()
    {
        var finest = new Peak[1000];
        Array.Fill(finest, new Peak(-100, 100));

        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 3);

        waveform.Levels.Select(l => l.SamplesPerBucket).Should().Equal(16, 64, 256);
        waveform.Levels.Select(l => l.Peaks.Count).Should().Equal(1000, 250, 63);
        waveform.SampleRate.Should().Be(8000);
    }

    [Fact]
    public void LevelFor_picks_the_finest_level_no_finer_than_needed()
    {
        var finest = new Peak[4096];
        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 4);
        // levels: 0.002s, 0.008s, 0.032s, 0.128s per bucket

        waveform.LevelFor(secondsPerPixel: 0.010).SamplesPerBucket.Should().Be(64, "0.008s buckets are the coarsest that still exceed pixel resolution");
        waveform.LevelFor(secondsPerPixel: 0.001).SamplesPerBucket.Should().Be(16, "nothing finer exists");
        waveform.LevelFor(secondsPerPixel: 10.0).SamplesPerBucket.Should().Be(1024, "nothing coarser exists");
    }

    [Fact]
    public void Duration_comes_from_the_finest_level()
    {
        var finest = new Peak[500]; // 500 * 16 samples / 8000 Hz = 1 s
        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 2);

        waveform.DurationSeconds.Should().BeApproximately(1.0, 1e-9);
    }
}

public sealed class WaveformSnapTests
{
    // 8000 Hz, 16 samples per bucket => 2 ms per bucket. 500 buckets = 1 s.
    private static Waveform Loud(int quietBucket = -1, int quietRunLength = 1)
    {
        var peaks = new Peak[500];
        Array.Fill(peaks, new Peak(-8000, 8000));
        for (var i = 0; i < quietRunLength && quietBucket >= 0; i++)
        {
            peaks[quietBucket + i] = new Peak(-50, 50);
        }

        return Waveform.Build(peaks, 16, 8000, 4, 2);
    }

    [Fact]
    public void Snaps_to_the_quietest_bucket_within_the_window()
    {
        // bucket 250 spans [0.500, 0.502); its centre is 0.501
        var waveform = Loud(quietBucket: 250);

        var snapped = waveform.SnapToZeroCrossing(0.510, windowSeconds: 0.020);

        snapped.Should().BeApproximately(0.501, 1e-9);
    }

    [Fact]
    public void Does_not_snap_beyond_the_window()
    {
        var waveform = Loud(quietBucket: 250);

        var snapped = waveform.SnapToZeroCrossing(0.600, windowSeconds: 0.020);

        snapped.Should().BeApproximately(0.600, 0.002, "nothing quieter nearby, so it stays put (within one bucket)");
    }

    [Fact]
    public void Ties_go_to_the_bucket_closest_to_the_requested_time()
    {
        var waveform = Loud(); // uniform amplitude everywhere

        var snapped = waveform.SnapToZeroCrossing(0.300, windowSeconds: 0.020);

        snapped.Should().BeApproximately(0.300, 0.002);
    }

    [Fact]
    public void Within_a_quiet_run_it_stays_near_the_requested_time()
    {
        // buckets 200..219 quiet: [0.400, 0.440)
        var waveform = Loud(quietBucket: 200, quietRunLength: 20);

        var snapped = waveform.SnapToZeroCrossing(0.420, windowSeconds: 0.020);

        snapped.Should().BeApproximately(0.420, 0.002);
    }

    [Fact]
    public void Clamps_to_the_waveform_range()
    {
        var waveform = Loud();

        waveform.SnapToZeroCrossing(-1.0, 0.020).Should().BeGreaterThanOrEqualTo(0.0);
        waveform.SnapToZeroCrossing(5.0, 0.020).Should().BeLessThanOrEqualTo(waveform.DurationSeconds);
    }

    // ---- Envelope ---------------------------------------------------------------------------

    [Fact]
    public void Envelope_averages_peak_amplitude_over_each_bucket()
    {
        // 2 ms peaks; a 10 ms envelope bucket covers five of them.
        var finest = new Peak[10];
        Array.Fill(finest, new Peak(-100, 60), 0, 5);   // amplitude 100
        Array.Fill(finest, new Peak(-20, 1000), 5, 5);  // amplitude 1000
        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 1);

        waveform.Envelope(0.010).Should().Equal(100.0, 1000.0);
    }

    [Fact]
    public void Envelope_averages_a_short_trailing_bucket_over_the_peaks_it_has()
    {
        var finest = new Peak[7];
        Array.Fill(finest, new Peak(-100, 100), 0, 5);
        finest[5] = new Peak(-200, 0);
        finest[6] = new Peak(0, 400);
        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 1);

        waveform.Envelope(0.010).Should().Equal(100.0, 300.0);
    }
}
