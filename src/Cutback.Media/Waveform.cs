namespace Cutback.Media;

/// <summary>One zoom level of a waveform: peaks at a fixed number of source samples per bucket.</summary>
public sealed class WaveformLevel
{
    public WaveformLevel(int samplesPerBucket, int sampleRate, IReadOnlyList<Peak> peaks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(samplesPerBucket, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentNullException.ThrowIfNull(peaks);
        SamplesPerBucket = samplesPerBucket;
        SampleRate = sampleRate;
        Peaks = peaks;
    }

    public int SamplesPerBucket { get; }

    public int SampleRate { get; }

    public IReadOnlyList<Peak> Peaks { get; }

    public double SecondsPerBucket => (double)SamplesPerBucket / SampleRate;

    /// <summary>Merges every <paramref name="factor"/> peaks into one. A short trailing group is kept.</summary>
    public WaveformLevel Downsample(int factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factor, 0);

        var count = (Peaks.Count + factor - 1) / factor;
        var result = new Peak[count];
        for (var i = 0; i < count; i++)
        {
            var min = short.MaxValue;
            var max = short.MinValue;
            var end = Math.Min(Peaks.Count, (i + 1) * factor);
            for (var j = i * factor; j < end; j++)
            {
                var p = Peaks[j];
                if (p.Min < min)
                {
                    min = p.Min;
                }

                if (p.Max > max)
                {
                    max = p.Max;
                }
            }

            result[i] = new Peak(min, max);
        }

        return new WaveformLevel(SamplesPerBucket * factor, SampleRate, result);
    }
}

/// <summary>
/// A reduced waveform at several zoom levels, finest first. Built once per source and cached for
/// the life of the project.
/// </summary>
public sealed class Waveform
{
    private Waveform(int sampleRate, IReadOnlyList<WaveformLevel> levels)
    {
        SampleRate = sampleRate;
        Levels = levels;
    }

    public int SampleRate { get; }

    /// <summary>Zoom levels ordered from finest (smallest bucket) to coarsest.</summary>
    public IReadOnlyList<WaveformLevel> Levels { get; }

    public double DurationSeconds => Levels[0].Peaks.Count * Levels[0].SecondsPerBucket;

    /// <summary>
    /// Builds <paramref name="levelCount"/> levels starting from the finest peaks, each
    /// <paramref name="levelFactor"/> times coarser than the last.
    /// </summary>
    public static Waveform Build(IReadOnlyList<Peak> finestPeaks, int finestSamplesPerBucket, int sampleRate, int levelFactor, int levelCount)
    {
        ArgumentNullException.ThrowIfNull(finestPeaks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(levelFactor, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(levelCount, 0);

        var levels = new List<WaveformLevel>(levelCount)
        {
            new(finestSamplesPerBucket, sampleRate, finestPeaks),
        };
        for (var i = 1; i < levelCount; i++)
        {
            levels.Add(levels[^1].Downsample(levelFactor));
        }

        return new Waveform(sampleRate, levels);
    }

    /// <summary>
    /// The level to draw at a given zoom: the coarsest whose buckets are still no wider than one
    /// pixel, so the renderer only ever merges buckets, never stretches them. Falls back to the
    /// finest level when zoomed in past it and the coarsest when zoomed out past it.
    /// </summary>
    public WaveformLevel LevelFor(double secondsPerPixel)
    {
        var chosen = Levels[0];
        foreach (var level in Levels)
        {
            if (level.SecondsPerBucket <= secondsPerPixel)
            {
                chosen = level;
            }
            else
            {
                break;
            }
        }

        return chosen;
    }
}
