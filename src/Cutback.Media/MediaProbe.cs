using FFMpegCore;
using FFMpegCore.Exceptions;

namespace Cutback.Media;

/// <summary>Reads duration, dimensions, frame rate and stream presence from a source file via ffprobe.</summary>
public sealed class MediaProbe
{
    private readonly FfmpegLocation _ffmpeg;

    public MediaProbe(FfmpegLocation ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        _ffmpeg = ffmpeg;
    }

    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="MediaProbeException">ffprobe failed or the file has no video stream.</exception>
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The video file could not be found.", path);
        }

        var options = new FFOptions { BinaryFolder = _ffmpeg.Directory };

        IMediaAnalysis analysis;
        try
        {
            analysis = await FFProbe.AnalyseAsync(path, options, cancellationToken).ConfigureAwait(false);
        }
        catch (FFMpegException ex)
        {
            throw new MediaProbeException($"ffprobe could not read \"{Path.GetFileName(path)}\". It may not be a video file, or it may be damaged.\n\n{ex.Message}", ex);
        }

        return FromAnalysis(analysis, path);
    }

    internal static MediaInfo FromAnalysis(IMediaAnalysis analysis, string path)
    {
        var video = analysis.PrimaryVideoStream
            ?? throw new MediaProbeException($"\"{Path.GetFileName(path)}\" has no video stream.");

        var duration = analysis.Duration.TotalSeconds;
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw new MediaProbeException($"\"{Path.GetFileName(path)}\" reports no duration. Live streams and some raw formats are not supported.");
        }

        // Phone recordings store landscape frames with a rotation tag; present the displayed size.
        var rotated = Math.Abs(video.Rotation) % 180 == 90;
        var width = rotated ? video.Height : video.Width;
        var height = rotated ? video.Width : video.Height;

        // r_frame_rate can be a bogus 90000 for variable-rate sources; prefer the average when it is sane.
        var frameRate = video.AvgFrameRate is > 0 and < 1000 ? video.AvgFrameRate : video.FrameRate;

        var audio = analysis.PrimaryAudioStream;
        return new MediaInfo(
            duration,
            width,
            height,
            frameRate,
            HasAudio: audio is not null,
            VideoCodec: video.CodecName,
            AudioCodec: audio?.CodecName);
    }
}
