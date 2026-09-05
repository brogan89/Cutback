namespace Cutback.App.Playback;

/// <summary>
/// Preview playback of the source video. Deliberately small so the implementation can be swapped
/// (libVLC today, libmpv if the seek hitch when skipping cuts proves unbearable). Implementations
/// raise every event on the UI thread. Times are seconds.
/// </summary>
public interface IVideoPlayer : IDisposable
{
    /// <summary>Playhead moved. Fires several times a second during playback and once after a seek.</summary>
    event EventHandler<double>? PositionChanged;

    /// <summary><see cref="IsPlaying"/> changed.</summary>
    event EventHandler? PlaybackStateChanged;

    /// <summary>Playback reached the end of the media.</summary>
    event EventHandler? EndReached;

    bool IsPlaying { get; }

    double Position { get; }

    double Duration { get; }

    bool HasMedia { get; }

    /// <summary>Loads a file, shows its first frame paused, and leaves the playhead at 0.</summary>
    Task LoadAsync(string path, CancellationToken cancellationToken);

    void Play();

    void Pause();

    void TogglePlayPause();

    void Seek(double seconds);

    void Unload();
}
