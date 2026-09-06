using Avalonia.Threading;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Cutback.App.Playback;

/// <summary>
/// <see cref="IVideoPlayer"/> over LibVLCSharp. VLC raises its events on its own threads and
/// deadlocks if you call back into it from them, so every callback is posted to the UI thread
/// before anything else happens.
/// </summary>
public sealed class VlcVideoPlayer : IVideoPlayer
{
    private readonly LibVLC _libVlc;
    private VlcMedia? _media;
    private bool _isPlaying;
    private double _position;
    private double _duration;
    private long? _pendingSeekMs;
    private bool _pauseOnNextPlaying;
    private bool _disposed;

    public VlcVideoPlayer()
    {
        _libVlc = new LibVLC(enableDebugLogs: false, "--no-video-title-show", "--no-osd");
        MediaPlayer = new MediaPlayer(_libVlc);

        MediaPlayer.TimeChanged += (_, e) => Post(() => OnTime(e.Time));
        MediaPlayer.Playing += (_, _) => Post(OnPlaying);
        MediaPlayer.Paused += (_, _) => Post(() => SetPlaying(false));
        MediaPlayer.Stopped += (_, _) => Post(() => SetPlaying(false));
        MediaPlayer.EndReached += (_, _) => Post(OnEndReached);
        MediaPlayer.LengthChanged += (_, e) => Post(() =>
        {
            if (e.Length > 0)
            {
                _duration = e.Length / 1000.0;
            }
        });
    }

    public event EventHandler<double>? PositionChanged;

    public event EventHandler? PlaybackStateChanged;

    public event EventHandler? EndReached;

    /// <summary>For the view to hand to <c>VideoView.MediaPlayer</c>. Not for view models.</summary>
    public MediaPlayer MediaPlayer { get; }

    public bool IsPlaying => _isPlaying;

    public double Position => _position;

    public double Duration => _duration;

    public bool HasMedia => _media is not null;

    public async Task LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Unload();

        var media = new VlcMedia(_libVlc, path, FromType.FromPath);
        await media.Parse(MediaParseOptions.ParseLocal, cancellationToken: cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        _media = media;
        _duration = media.Duration > 0 ? media.Duration / 1000.0 : 0;
        _position = 0;
        MediaPlayer.Media = media;

        // VLC shows nothing until it has decoded a frame, so start and pause on the first
        // Playing event. The seek back to 0 lands on the first frame.
        _pauseOnNextPlaying = true;
        _pendingSeekMs = 0;
        MediaPlayer.Play();
    }

    public void Play()
    {
        if (_media is null)
        {
            return;
        }

        var state = MediaPlayer.State;
        if (state is VLCState.Playing or VLCState.Paused)
        {
            MediaPlayer.SetPause(false);
            return;
        }

        // Stopped (including after the end was reached): restart from the beginning, or from a
        // pending seek target if one was requested while stopped.
        MediaPlayer.Play();
    }

    public void Pause()
    {
        if (_media is null)
        {
            return;
        }

        if (MediaPlayer.CanPause)
        {
            MediaPlayer.SetPause(true);
        }
    }

    public void TogglePlayPause()
    {
        if (_isPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    public void Seek(double seconds)
    {
        if (_media is null)
        {
            return;
        }

        // Round up, never down: a seek aimed at a segment boundary must land inside that segment,
        // or the caller that skips removed regions sees itself still inside the cut and seeks again.
        var ms = (long)Math.Ceiling(Math.Clamp(seconds, 0, Math.Max(0, _duration)) * 1000 - 1e-6);
        var state = MediaPlayer.State;
        if (state is VLCState.Playing or VLCState.Paused)
        {
            MediaPlayer.Time = ms;
            OnTime(ms);
            return;
        }

        // Ended or stopped: VLC ignores Time here. Restart paused at the requested point.
        _pendingSeekMs = ms;
        _pauseOnNextPlaying = true;
        MediaPlayer.Play();
    }

    public void Unload()
    {
        if (_media is null)
        {
            return;
        }

        MediaPlayer.Stop();
        MediaPlayer.Media = null;
        _media.Dispose();
        _media = null;
        _pendingSeekMs = null;
        _pauseOnNextPlaying = false;
        _duration = 0;
        _position = 0;
        SetPlaying(false);
        PositionChanged?.Invoke(this, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unload();
        MediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private void OnPlaying()
    {
        if (_pendingSeekMs is { } ms)
        {
            _pendingSeekMs = null;
            MediaPlayer.Time = ms;
            OnTime(ms);
        }

        if (_pauseOnNextPlaying)
        {
            _pauseOnNextPlaying = false;
            MediaPlayer.SetPause(true);
            return;
        }

        SetPlaying(true);
    }

    private void OnTime(long ms)
    {
        _position = ms / 1000.0;
        PositionChanged?.Invoke(this, _position);
    }

    private void OnEndReached()
    {
        SetPlaying(false);
        if (_duration > 0)
        {
            OnTime((long)(_duration * 1000));
        }

        // libVLC 3 will not restart or seek a player in the Ended state; Play() is silently
        // ignored. Stopping here (on the UI thread, never from VLC's callback) resets it so the
        // next Play or Seek works. Safe because this runs from a posted callback, not VLC's thread.
        MediaPlayer.Stop();

        EndReached?.Invoke(this, EventArgs.Empty);
    }

    private void SetPlaying(bool playing)
    {
        if (_isPlaying == playing)
        {
            return;
        }

        _isPlaying = playing;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
