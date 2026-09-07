using Cutback.Core.Models;
using Cutback.Media;
using Whisper.net;

namespace Cutback.Analysis;

/// <summary>
/// Local speech-to-text with Whisper.net on the CPU. Decodes the audio with ffmpeg, runs the
/// model with token timestamps, and assembles words. Whisper was trained on transcripts with the
/// disfluencies removed, so left alone it drops most "um"s; the initial prompt is written the way
/// a transcript with fillers looks, and is carried into every window, to steer it back.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    public const string DisfluentPrompt =
        "Um, so, uh, I was going to, um, show you this. Er, hmm, let me, uh, think. Ah, okay, so, um, here we go.";

    /// <summary>Share of the progress bar given to audio decoding; the rest is recognition.</summary>
    private const double DecodeShare = 0.15;

    private readonly FfmpegLocation _ffmpeg;
    private readonly string _modelPath;

    public WhisperTranscriber(FfmpegLocation ffmpeg, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        _ffmpeg = ffmpeg;
        _modelPath = modelPath;
    }

    public async Task<IReadOnlyList<Word>> TranscribeAsync(string mediaPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var samples = await new PcmExtractor(_ffmpeg)
            .ExtractAsync(mediaPath, 0, progress is null ? null : new ScaledProgress(progress, 0, DecodeShare), cancellationToken)
            .ConfigureAwait(false);

        // whisper.cpp is synchronous and CPU-bound; keep it off the caller's thread.
        var words = await Task.Run(() => RecognizeAsync(samples, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
        return words;
    }

    private async Task<List<Word>> RecognizeAsync(ReadOnlyMemory<float> samples, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var factory = LoadFactory();
        await using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithTokenTimestamps()
            .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8))
            .WithPrompt(DisfluentPrompt)
            .WithCarryInitialPrompt(true)
            .WithProgressHandler(percent => progress?.Report(DecodeShare + (1 - DecodeShare) * Math.Clamp(percent, 0, 100) / 100.0))
            .Build();

        var words = new List<Word>();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            if (segment.Tokens is null)
            {
                continue;
            }

            words.AddRange(WordAssembler.FromTokens(segment.Tokens.Select(t =>
                new TokenTiming(t.Text ?? string.Empty, t.Start / 100.0, t.End / 100.0, t.Probability))));
        }

        return words.OrderBy(w => w.Start).ToList();
    }

    /// <summary>Loads the Whisper model, wrapping any failure in a <see cref="ModelLoadException"/> naming the model path.</summary>
    private WhisperFactory LoadFactory()
    {
        try
        {
            return WhisperFactory.FromPath(_modelPath);
        }
        catch (Exception ex)
        {
            throw new ModelLoadException(
                $"The speech model at {_modelPath} could not be loaded. It may be corrupt; it will be downloaded again next time.",
                _modelPath,
                ex);
        }
    }

    /// <summary>Maps a child's <c>[0, 1]</c> onto <c>[offset, offset + share]</c> of the parent's.</summary>
    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double> _parent;
        private readonly double _offset;
        private readonly double _share;

        public ScaledProgress(IProgress<double> parent, double offset, double share)
        {
            _parent = parent;
            _offset = offset;
            _share = share;
        }

        public void Report(double value) => _parent.Report(_offset + _share * Math.Clamp(value, 0, 1));
    }
}
