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
