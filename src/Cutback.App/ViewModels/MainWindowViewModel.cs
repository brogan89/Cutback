using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cutback.App.Controls;
using Cutback.App.Playback;
using Cutback.App.Services;
using Cutback.Core;
using Cutback.Core.Models;
using Cutback.Core.Projects;
using Cutback.Media;

namespace Cutback.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IVideoPlayer _player;
    private readonly IFileDialogService _dialogs;
    private readonly AppSettingsStore _settings;
    private FfmpegLocation? _ffmpeg;
    private CancellationTokenSource? _openCts;

    public MainWindowViewModel(IVideoPlayer player, IFileDialogService dialogs, AppSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(settings);
        _player = player;
        _dialogs = dialogs;
        _settings = settings;

        _player.PositionChanged += (_, seconds) => OnPlayerPosition(seconds);
        _player.PlaybackStateChanged += (_, _) => IsPlaying = _player.IsPlaying;
    }

    // ---- project state ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(Title), nameof(SourceFileName))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    public partial CutbackProject? Project { get; private set; }

    /// <summary>The live, editable partition. Null until a project is open.</summary>
    [ObservableProperty]
    public partial SegmentList? Segments { get; private set; }

    [ObservableProperty]
    public partial Waveform? Waveform { get; private set; }

    public bool HasProject => Project is not null;

    public string SourceFileName => Project is null ? string.Empty : Path.GetFileName(Project.Source.Path);

    public string Title => Project is null ? "Cutback" : $"{SourceFileName} — Cutback";

    // ---- playback -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial double PositionSeconds { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    public partial double DurationSeconds { get; private set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    public string PositionText => TimeFormat.Clock(PositionSeconds);

    public string DurationText => TimeFormat.Clock(DurationSeconds);

    // ---- busy / errors ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenVideoCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? BusyMessage { get; private set; }

    /// <summary>0..1, or NaN when the current operation has no measurable progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusyIndeterminate))]
    public partial double BusyProgress { get; private set; } = double.NaN;

    public bool IsBusyIndeterminate => double.IsNaN(BusyProgress);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    // ---- commands -----------------------------------------------------------------------------

    private bool CanOpen => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenVideoAsync()
    {
        var path = await _dialogs.PickVideoToOpenAsync();
        if (path is not null)
        {
            await OpenVideoAsync(path);
        }
    }

    /// <summary>Opens a video as a new project. Also the drop target for the window.</summary>
    public async Task OpenVideoAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IsBusy)
        {
            return;
        }

        _openCts?.Cancel();
        _openCts = new CancellationTokenSource();
        var ct = _openCts.Token;

        ErrorMessage = null;
        BeginBusy($"Opening {Path.GetFileName(path)}…");
        try
        {
            var ffmpeg = ResolveFfmpeg();

            var info = await new MediaProbe(ffmpeg).ProbeAsync(path, ct);
            if (!info.HasAudio)
            {
                throw new MediaProbeException($"\"{Path.GetFileName(path)}\" has no audio track. Cutback edits by listening for silence, so it needs one.");
            }

            var hash = await SourceHash.ComputeAsync(path, ct);
            var source = new SourceInfo(Path.GetFullPath(path), hash, info.DurationSeconds, info.Width, info.Height, info.FrameRate);
            var project = CutbackProject.CreateNew(source);

            BusyMessage = "Reading waveform…";
            var progress = new Progress<double>(p => BusyProgress = p);
            var waveform = await new WaveformExtractor(ffmpeg).ExtractAsync(path, info.DurationSeconds, progress, ct);

            await _player.LoadAsync(path, ct);

            Project = project;
            Segments = new SegmentList(source.DurationSeconds, project.Segments);
            Waveform = waveform;
            DurationSeconds = source.DurationSeconds;
            PositionSeconds = 0;
        }
        catch (OperationCanceledException)
        {
            // Superseded by another open.
        }
        catch (Exception ex) when (ex is FfmpegNotFoundException or MediaProbeException or FfmpegException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void PlayPause() => _player.TogglePlayPause();

    /// <summary>Seeks the preview. Bound to the timeline's ruler and used by transport controls.</summary>
    [RelayCommand]
    private void Seek(double seconds)
    {
        if (!HasProject)
        {
            return;
        }

        _player.Seek(Math.Clamp(seconds, 0, DurationSeconds));
    }

    [RelayCommand]
    private void ToggleSegment(int index)
    {
        if (Segments is null || index < 0 || index >= Segments.Count)
        {
            return;
        }

        Segments.Toggle(index);
    }

    [RelayCommand]
    private void MoveBoundary(BoundaryMove move)
    {
        if (Segments is null || move.BoundaryIndex < 1 || move.BoundaryIndex >= Segments.Count)
        {
            return;
        }

        Segments.MoveBoundary(move.BoundaryIndex, move.Time);
    }

    // ---- playback skipping --------------------------------------------------------------------

    /// <summary>
    /// Preview follows the edit: while playing, entering a removed segment jumps to the start of the
    /// next kept one. The seek visibly hitches; that is accepted for the MVP (see CLAUDE.md). When
    /// paused the playhead may sit anywhere so the user can inspect a cut.
    /// </summary>
    private void OnPlayerPosition(double seconds)
    {
        PositionSeconds = seconds;

        if (!_player.IsPlaying || Segments is null)
        {
            return;
        }

        var index = Segments.IndexAt(seconds);
        if (index < 0 || Segments.Segments[index].Enabled)
        {
            return;
        }

        for (var i = index + 1; i < Segments.Count; i++)
        {
            if (Segments.Segments[i].Enabled)
            {
                _player.Seek(Segments.Segments[i].Start);
                return;
            }
        }

        // Nothing kept after this point: the edited video is over.
        _player.Pause();
        _player.Seek(DurationSeconds);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private FfmpegLocation ResolveFfmpeg()
    {
        if (_ffmpeg is not null)
        {
            return _ffmpeg;
        }

        var location = new FfmpegLocator(_settings.Current.FfmpegPath).Locate();
        if (!string.Equals(_settings.Current.FfmpegPath, location.FfmpegPath, StringComparison.Ordinal))
        {
            _settings.Update(s => s with { FfmpegPath = location.FfmpegPath });
        }

        _ffmpeg = location;
        return location;
    }

    private void BeginBusy(string message)
    {
        BusyMessage = message;
        BusyProgress = double.NaN;
        IsBusy = true;
    }

    private void EndBusy()
    {
        IsBusy = false;
        BusyMessage = null;
        BusyProgress = double.NaN;
    }
}
