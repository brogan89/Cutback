namespace Cutback.App.Playback;

/// <summary>
/// Used when libVLC is not installed so the rest of the app (waveform, detection, export) still
/// works. Every operation is a no-op.
/// </summary>
public sealed class NullVideoPlayer : IVideoPlayer
{
    public event EventHandler<double>? PositionChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? PlaybackStateChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? EndReached
    {
        add { }
        remove { }
    }

    public bool IsPlaying => false;

    public double Position => 0;

    public double Duration => 0;

    public bool HasMedia => false;

    public Task LoadAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Play()
    {
    }

    public void Pause()
    {
    }

    public void TogglePlayPause()
    {
    }

    public void Seek(double seconds)
    {
    }

    public void Unload()
    {
    }

    public void Dispose()
    {
    }
}
