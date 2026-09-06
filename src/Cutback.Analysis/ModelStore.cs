using Whisper.net.Ggml;

namespace Cutback.Analysis;

/// <summary>
/// The per-user cache of downloaded Whisper models. Models are fetched from Hugging Face through
/// Whisper.net's downloader on first use and never bundled with the app.
/// </summary>
public sealed class ModelStore
{
    public ModelStore(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        CacheDirectory = cacheDirectory;
    }

    /// <summary><c>&lt;ApplicationData&gt;/Cutback/models</c>, next to the settings file.</summary>
    public static ModelStore CreateDefault()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cutback", "models"));

    public string CacheDirectory { get; }

    public string PathFor(WhisperModel model) => Path.Combine(CacheDirectory, WhisperModelInfo.For(model).FileName);

    public bool IsDownloaded(WhisperModel model) => File.Exists(PathFor(model));

    /// <summary>Returns the model's path, downloading it first if it is not cached.</summary>
    /// <param name="model">Which Whisper model to ensure is present.</param>
    /// <param name="progress">Fraction of the approximate size received, in <c>[0, 1]</c>.</param>
    /// <param name="cancellationToken">Cancels the download; a partial file is not left behind.</param>
    /// <exception cref="ModelDownloadException">The download failed. The partial file is removed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled. The partial file is removed.</exception>
    public async Task<string> EnsureAsync(WhisperModel model, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var info = WhisperModelInfo.For(model);
        var path = PathFor(model);
        if (File.Exists(path))
        {
            return path;
        }

        Directory.CreateDirectory(CacheDirectory);
        var partial = path + ".part";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(info.GgmlType, QuantizationType.NoQuantization, cancellationToken)
                .ConfigureAwait(false);

            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(Math.Min(1.0, (double)received / info.ApproximateBytes));
                }
            }

            File.Move(partial, path, overwrite: true);
            progress?.Report(1.0);
            return path;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            throw new ModelDownloadException(
                $"Could not download the {info.Key} speech model (about {info.ApproximateBytes / 1_000_000} MB). "
                + $"Check your internet connection and try again. Models are cached in {CacheDirectory}.\n\n{ex.Message}",
                ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort; a stale .part file is harmless and overwritten next time.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
