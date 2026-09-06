using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cutback.Analysis;
using Cutback.App.Controls;
using Cutback.App.Playback;
using Cutback.App.Services;
using Cutback.Core;
using Cutback.Core.Detection;
using Cutback.Core.Models;
using Cutback.Core.Projects;
using Cutback.Core.Transcript;
using Cutback.Media;

namespace Cutback.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IVideoPlayer _player;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly AppSettingsStore _settings;
    private readonly TempSession _temp;
    private readonly ModelStore _models;
    private readonly EditHistory<Segment[]> _history;
    private Segment[]? _dragSnapshot;
    private FfmpegLocation? _ffmpeg;
    private CancellationTokenSource? _openCts;
    private CancellationTokenSource? _busyCts;
    private bool _loadingSettings;
    private bool _skipping;
    private double? _skipTarget;

    public MainWindowViewModel(IVideoPlayer player, IFileDialogService files, IDialogService dialogs, AppSettingsStore settings, TempSession temp, ModelStore models)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(temp);
        ArgumentNullException.ThrowIfNull(models);
        _player = player;
        _files = files;
        _dialogs = dialogs;
        _settings = settings;
        _temp = temp;
        _models = models;

        _history = new EditHistory<Segment[]>(settings.Current.UndoHistoryLimit);
        _history.Changed += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };

        _player.PositionChanged += (_, seconds) => OnPlayerPosition(seconds);
        _player.PlaybackStateChanged += (_, _) => IsPlaying = _player.IsPlaying;

        RefreshRecentFiles();
    }

    // ---- project state ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(Title), nameof(SourceFileName), nameof(ProjectName))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(DetectSilenceCommand), nameof(SaveProjectCommand), nameof(SaveProjectAsCommand), nameof(NewProjectCommand), nameof(ExportCommand), nameof(TranscribeCommand), nameof(RemoveFillerWordsCommand))]
    public partial CutbackProject? Project { get; private set; }

    /// <summary>Where the project was last saved or loaded from. Null for an unsaved project.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ProjectName))]
    public partial string? ProjectPath { get; private set; }

    /// <summary>True when there are edits not yet written to <see cref="ProjectPath"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsDirty { get; private set; }

    /// <summary>The live, editable partition. Null until a project is open.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(RedoCommand))]
    public partial SegmentList? Segments { get; private set; }

    [ObservableProperty]
    public partial Waveform? Waveform { get; private set; }

    public bool HasProject => Project is not null;

    public string SourceFileName => Project is null ? string.Empty : Path.GetFileName(Project.Source.Path);

    /// <summary>Project file name without extension, or the source name for an unsaved project.</summary>
    public string ProjectName => ProjectPath is not null
        ? Path.GetFileNameWithoutExtension(ProjectPath)
        : Path.GetFileNameWithoutExtension(SourceFileName);

    public string Title => Project is null ? "Cutback" : $"{(IsDirty ? "• " : string.Empty)}{ProjectName} — Cutback";

    public ObservableCollection<RecentFileItem> RecentFiles { get; } = [];

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

    // ---- detection settings -------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial double SilenceThresholdDb { get; set; } = DetectionSettings.Default.SilenceThresholdDb;

    [ObservableProperty]
    public partial int MinSilenceMs { get; set; } = DetectionSettings.Default.MinSilenceMs;

    [ObservableProperty]
    public partial int PaddingMs { get; set; } = DetectionSettings.Default.PaddingMs;

    [ObservableProperty]
    public partial int MinKeepMs { get; set; } = DetectionSettings.Default.MinKeepMs;

    /// <summary>The settings as currently shown in the panel.</summary>
    public DetectionSettings CurrentSettings => new(PaddingMs, MinSilenceMs, SilenceThresholdDb, MinKeepMs);

    partial void OnSilenceThresholdDbChanged(double value) => MarkDirty();

    partial void OnMinSilenceMsChanged(int value) => MarkDirty();

    partial void OnPaddingMsChanged(int value) => MarkDirty();

    partial void OnMinKeepMsChanged(int value) => MarkDirty();

    private void LoadSettings(DetectionSettings settings)
    {
        _loadingSettings = true;
        try
        {
            SilenceThresholdDb = settings.SilenceThresholdDb;
            MinSilenceMs = settings.MinSilenceMs;
            PaddingMs = settings.PaddingMs;
            MinKeepMs = settings.MinKeepMs;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void ResetSettings()
    {
        LoadSettings(DetectionSettings.Default);
        MarkDirty();
    }

    // ---- transcript ---------------------------------------------------------------------------

    /// <summary>Mirrors <c>Project.Transcript</c> for binding. Empty until the project is transcribed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscript))]
    public partial IReadOnlyList<Word> Transcript { get; private set; } = [];

    public bool HasTranscript => Transcript.Count > 0;

    [ObservableProperty]
    public partial bool IsTranscriptOpen { get; set; }

    /// <summary>The source file changed after this transcript was made (the hash warning fired on open).</summary>
    [ObservableProperty]
    public partial bool IsTranscriptStale { get; private set; }

    /// <summary>Word under the playhead, or -1. Highlighted in the transcript panel.</summary>
    [ObservableProperty]
    public partial int CurrentWordIndex { get; private set; } = -1;

    [RelayCommand]
    private void ToggleTranscript() => IsTranscriptOpen = !IsTranscriptOpen;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task TranscribeAsync()
    {
        if (await TranscribeCoreAsync())
        {
            IsTranscriptOpen = true;
        }
    }

    /// <summary>
    /// Downloads the chosen model if needed, then transcribes the source. Both phases are
    /// cancellable from the status bar. Returns true if a non-empty transcript is now loaded.
    /// </summary>
    private async Task<bool> TranscribeCoreAsync()
    {
        if (Project is null)
        {
            return false;
        }

        var model = WhisperModelInfo.Parse(_settings.Current.WhisperModel);
        ErrorMessage = null;
        BeginBusy(_models.IsDownloaded(model) ? "Transcribing…" : $"Downloading the {WhisperModelInfo.For(model).Key} speech model…", cancellable: true);
        try
        {
            var ct = _busyCts!.Token;
            var ffmpeg = ResolveFfmpeg();
            var progress = new Progress<double>(p => BusyProgress = p);

            var modelPath = await _models.EnsureAsync(model, progress, ct);
            BusyMessage = "Transcribing…";
            BusyProgress = 0;

            ITranscriber transcriber = new WhisperTranscriber(ffmpeg, modelPath);
            var words = await transcriber.TranscribeAsync(Project.Source.Path, progress, ct);

            Project = Project with { Transcript = words };
            Transcript = words;
            IsTranscriptStale = false;
            CurrentWordIndex = TranscriptView.IndexAtTime(words, PositionSeconds);
            MarkDirty();
            StatusMessage = words.Count == 0 ? "No speech was recognised." : $"Transcribed {words.Count} words.";
            return words.Count > 0;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Transcription cancelled.";
            return false;
        }
        catch (Exception ex) when (ex is FfmpegNotFoundException or FfmpegException or ModelDownloadException or IOException)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            EndBusy();
        }
    }

    // ---- messages -----------------------------------------------------------------------------

    /// <summary>Non-error feedback shown in the footer when nothing is running.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Something the user should know but that does not stop them, e.g. a changed source file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? WarningMessage { get; private set; }

    public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);

    [RelayCommand]
    private void DismissWarning() => WarningMessage = null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    // ---- busy ---------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenVideoCommand), nameof(OpenProjectCommand), nameof(DetectSilenceCommand), nameof(SaveProjectCommand), nameof(SaveProjectAsCommand), nameof(NewProjectCommand), nameof(ExportCommand), nameof(UndoCommand), nameof(RedoCommand), nameof(TranscribeCommand), nameof(RemoveFillerWordsCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? BusyMessage { get; private set; }

    /// <summary>0..1, or NaN when the current operation has no measurable progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusyIndeterminate))]
    public partial double BusyProgress { get; private set; } = double.NaN;

    public bool IsBusyIndeterminate => double.IsNaN(BusyProgress);

    /// <summary>True while the running operation can be stopped from the status bar.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelBusyCommand))]
    public partial bool CanCancelBusy { get; private set; }

    [RelayCommand(CanExecute = nameof(CanCancelBusy))]
    private void CancelBusy() => _busyCts?.Cancel();

    private bool NotBusy => !IsBusy;

    private bool CanEdit => HasProject && !IsBusy;

    // ---- opening ------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenVideoAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            return;
        }

        var path = await _files.PickVideoToOpenAsync();
        if (path is not null)
        {
            await OpenVideoAsync(path);
        }
    }

    /// <summary>Opens a video as a new, unsaved project. Also the drop target and command-line path.</summary>
    public async Task OpenVideoAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IsBusy)
        {
            return;
        }

        if (string.Equals(Path.GetExtension(path), ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            await OpenProjectAsync(path);
            return;
        }

        await RunOpenAsync($"Opening {Path.GetFileName(path)}…", async ct =>
        {
            var ffmpeg = ResolveFfmpeg();
            var info = await new MediaProbe(ffmpeg).ProbeAsync(path, ct);
            if (!info.HasAudio)
            {
                throw new MediaProbeException($"\"{Path.GetFileName(path)}\" has no audio track. Cutback edits by listening for silence, so it needs one.");
            }

            var hash = await SourceHash.ComputeAsync(path, ct);
            var source = new SourceInfo(Path.GetFullPath(path), hash, info.DurationSeconds, info.Width, info.Height, info.FrameRate);
            return (CutbackProject.CreateNew(source), (string?)null, (string?)null);
        });
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenProjectAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            return;
        }

        var path = await _files.PickProjectToOpenAsync();
        if (path is not null)
        {
            await OpenProjectAsync(path);
        }
    }

    /// <summary>Loads a saved <c>.cutback</c> file.</summary>
    public async Task OpenProjectAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IsBusy)
        {
            return;
        }

        await RunOpenAsync($"Opening {Path.GetFileName(path)}…", async ct =>
        {
            var project = await ProjectSerializer.LoadAsync(path, ct);
            var source = project.Source.Path;
            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    $"The source video for this project is missing:\n{source}\n\nMove it back, or open the video again to start a new project.", source);
            }

            string? warning = null;
            if (!await SourceHash.MatchesAsync(source, project.Source.Sha256, ct))
            {
                warning = $"The source video has changed since this project was saved ({Path.GetFileName(source)}). "
                    + "Cut points may no longer line up with the audio. Re-run detection if the result looks wrong.";
            }

            return (project, path, warning);
        });
    }

    [RelayCommand]
    private Task OpenRecentAsync(string path) => File.Exists(path)
        ? OpenRecentExistingAsync(path)
        : Task.FromResult(ErrorMessage = $"\"{path}\" no longer exists.");

    private async Task OpenRecentExistingAsync(string path)
    {
        if (await ConfirmDiscardChangesAsync())
        {
            await OpenProjectAsync(path);
        }
    }

    /// <summary>Shared open pipeline: run the loader, then extract the waveform and load the player.</summary>
    private async Task RunOpenAsync(string busyMessage, Func<CancellationToken, Task<(CutbackProject Project, string? Path, string? Warning)>> load)
    {
        _openCts?.Cancel();
        _openCts = new CancellationTokenSource();
        var ct = _openCts.Token;

        ErrorMessage = null;
        BeginBusy(busyMessage);
        try
        {
            var (project, path, warning) = await load(ct);
            var ffmpeg = ResolveFfmpeg();

            BusyMessage = "Reading waveform…";
            var progress = new Progress<double>(p => BusyProgress = p);
            var waveform = await new WaveformExtractor(ffmpeg).ExtractAsync(project.Source.Path, project.Source.DurationSeconds, progress, ct);

            await _player.LoadAsync(project.Source.Path, ct);

            Project = project;
            ProjectPath = path;
            _history.Clear();
            _dragSnapshot = null;
            Segments = new SegmentList(project.Source.DurationSeconds, project.Segments);
            Segments.Changed += (_, _) => MarkDirty();
            Transcript = project.Transcript;
            IsTranscriptStale = warning is not null && project.Transcript.Count > 0;
            CurrentWordIndex = -1;
            Waveform = waveform;
            DurationSeconds = project.Source.DurationSeconds;
            PositionSeconds = 0;
            LoadSettings(project.Settings);
            IsDirty = false;
            StatusMessage = null;
            WarningMessage = warning;

            if (path is not null)
            {
                RememberRecent(path);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by another open.
        }
        catch (Exception ex) when (ex is FfmpegNotFoundException or MediaProbeException or FfmpegException
                                   or ProjectFormatException or InvalidPartitionException
                                   or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    // ---- new / save ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task NewProjectAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            return;
        }

        _player.Unload();
        Project = null;
        ProjectPath = null;
        _history.Clear();
        _dragSnapshot = null;
        Segments = null;
        Waveform = null;
        Transcript = [];
        IsTranscriptStale = false;
        CurrentWordIndex = -1;
        DurationSeconds = 0;
        PositionSeconds = 0;
        IsDirty = false;
        StatusMessage = null;
        WarningMessage = null;
        ErrorMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task<bool> SaveProjectAsync() => ProjectPath is null ? SaveProjectAsAsync() : SaveToAsync(ProjectPath);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task<bool> SaveProjectAsAsync()
    {
        if (Project is null)
        {
            return false;
        }

        // Suggest the stem only. The macOS save panel appends DefaultExtension even when the
        // suggested name already has it, which produced "name.cutback.cutback".
        var suggested = Path.GetFileNameWithoutExtension(ProjectPath ?? Project.Source.Path);
        var path = await _files.PickProjectToSaveAsync(suggested);
        return path is not null && await SaveToAsync(path);
    }

    private async Task<bool> SaveToAsync(string path)
    {
        if (Project is null || Segments is null)
        {
            return false;
        }

        path = NormalizeExtension(path, ProjectSerializer.FileExtension);

        try
        {
            var snapshot = Project with { Segments = Segments.Segments.ToList(), Settings = CurrentSettings };
            await ProjectSerializer.SaveAsync(snapshot, path, CancellationToken.None);
            Project = snapshot;
            ProjectPath = path;
            IsDirty = false;
            StatusMessage = $"Saved {Path.GetFileName(path)}.";
            RememberRecent(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not save the project.\n\n{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// If there are unsaved changes, asks the user what to do. Returns true if it is fine to go
    /// ahead and discard or replace the current project (saved, or the user chose not to save).
    /// </summary>
    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!IsDirty || Project is null)
        {
            return true;
        }

        return await _dialogs.ConfirmSaveChangesAsync(ProjectName) switch
        {
            SaveChoice.Save => await SaveProjectAsync(),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>
    /// Ensures exactly one <paramref name="extension"/> on the end of <paramref name="path"/>: adds it
    /// if missing and collapses "name.ext.ext", which a file picker can hand back.
    /// </summary>
    internal static string NormalizeExtension(string path, string extension)
    {
        while (path.EndsWith(extension + extension, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^extension.Length];
        }

        return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
    }

    private void MarkDirty()
    {
        if (!_loadingSettings && Project is not null)
        {
            IsDirty = true;
        }
    }

    private void RememberRecent(string path)
    {
        _settings.Update(s => s with
        {
            RecentFiles = new[] { path }
                .Concat(s.RecentFiles.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                .Take(AppSettingsStore.MaxRecentFiles)
                .ToList(),
        });
        RefreshRecentFiles();
    }

    private void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var path in _settings.Current.RecentFiles)
        {
            RecentFiles.Add(new RecentFileItem(path, Path.GetFileName(path), OpenRecentCommand));
        }
    }

    // ---- playback -----------------------------------------------------------------------------

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

        _skipTarget = null;
        _player.Seek(Math.Clamp(seconds, 0, DurationSeconds));
    }

    // ---- editing ------------------------------------------------------------------------------

    /// <summary>Segment under the pointer on the timeline, or -1. Kept current by the timeline control.</summary>
    [ObservableProperty]
    public partial int HoveredSegmentIndex { get; set; } = -1;

    /// <summary>Dissolves a region into its neighbours. Used by the timeline's context menu.</summary>
    [RelayCommand]
    private void DeleteSegment(int index)
    {
        if (Segments is null || index < 0 || index >= Segments.Count)
        {
            return;
        }

        Edit(s => s.Dissolve(index));
    }

    /// <summary>Delete / Backspace: acts on the region under the pointer.</summary>
    [RelayCommand]
    private void DeleteHoveredSegment() => DeleteSegment(HoveredSegmentIndex);

    [RelayCommand]
    private void ToggleSegment(int index)
    {
        if (Segments is null || index < 0 || index >= Segments.Count)
        {
            return;
        }

        Edit(s => s.Toggle(index));
    }

    /// <summary>A whole drag is one undo step: the snapshot is taken at Begin and recorded at End.</summary>
    [RelayCommand]
    private void MoveBoundary(BoundaryMove move)
    {
        if (Segments is null || move.BoundaryIndex < 1 || move.BoundaryIndex >= Segments.Count)
        {
            return;
        }

        switch (move.Phase)
        {
            case BoundaryDragPhase.Begin:
                _dragSnapshot = Segments.Segments.ToArray();
                break;

            case BoundaryDragPhase.Update:
                Segments.MoveBoundary(move.BoundaryIndex, move.Time);
                break;

            case BoundaryDragPhase.End:
                Segments.MoveBoundary(move.BoundaryIndex, move.Time);
                if (_dragSnapshot is { } before && !before.SequenceEqual(Segments.Segments))
                {
                    _history.Push(before);
                }

                _dragSnapshot = null;
                break;
        }
    }

    /// <summary>
    /// A range the user drew on the timeline becomes a section with the opposite state of the
    /// segment where the drag started: drawing over kept footage cuts it, drawing inside a cut
    /// restores that part.
    /// </summary>
    [RelayCommand]
    private void CreateSection(SectionRange range)
    {
        if (Segments is null)
        {
            return;
        }

        var anchorIndex = Segments.IndexAt(range.Anchor);
        if (anchorIndex < 0)
        {
            return;
        }

        var enabled = !Segments.Segments[anchorIndex].Enabled;
        Edit(s => s.SetRange(range.Start, range.End, enabled));
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Segments is null || !_history.CanUndo)
        {
            return;
        }

        _dragSnapshot = null;
        Segments.Restore(_history.Undo(Segments.Segments.ToArray()));
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (Segments is null || !_history.CanRedo)
        {
            return;
        }

        _dragSnapshot = null;
        Segments.Restore(_history.Redo(Segments.Segments.ToArray()));
    }

    private bool CanUndo => Segments is not null && !IsBusy && _history.CanUndo;

    private bool CanRedo => Segments is not null && !IsBusy && _history.CanRedo;

    /// <summary>Runs one edit as one undo step. Edits that change nothing leave no step behind.</summary>
    private void Edit(Action<SegmentList> edit)
    {
        if (Segments is null)
        {
            return;
        }

        var before = Segments.Segments.ToArray();
        edit(Segments);
        if (!before.SequenceEqual(Segments.Segments))
        {
            _history.Push(before);
        }
    }

    [RelayCommand]
    private async Task ShowPreferencesAsync()
    {
        var preferences = new PreferencesViewModel(_settings, _models);
        await _dialogs.ShowPreferencesAsync(preferences);
        _history.Limit = _settings.Current.UndoHistoryLimit;
    }

    /// <summary>
    /// Preview follows the edit: while playing, entering a removed segment jumps to the start of the
    /// next kept one. The seek visibly hitches; that is accepted for the MVP (see CLAUDE.md). When
    /// paused the playhead may sit anywhere so the user can inspect a cut.
    /// </summary>
    private void OnPlayerPosition(double seconds)
    {
        PositionSeconds = seconds;
        CurrentWordIndex = TranscriptView.IndexAtTime(Transcript, seconds);

        // Seek raises PositionChanged synchronously, so this handler can be re-entered by its own
        // seek. Never act on a position while a skip is already in progress: an unguarded loop here
        // once overflowed the stack.
        if (_skipping || !_player.IsPlaying || Segments is null)
        {
            return;
        }

        var index = Segments.IndexAt(seconds);
        if (index < 0 || Segments.Segments[index].Enabled)
        {
            _skipTarget = null;
            return;
        }

        // VLC keeps reporting a few stale positions inside the cut after a seek. If we already
        // asked for a jump past this point, wait for it to land rather than seeking again.
        if (_skipTarget is { } target && seconds < target)
        {
            return;
        }

        _skipping = true;
        try
        {
            for (var i = index + 1; i < Segments.Count; i++)
            {
                if (Segments.Segments[i].Enabled)
                {
                    _skipTarget = Segments.Segments[i].Start;
                    _player.Seek(_skipTarget.Value);
                    return;
                }
            }

            // Nothing kept after this point: the edited video is over.
            _skipTarget = null;
            _player.Pause();
            _player.Seek(DurationSeconds);
        }
        finally
        {
            _skipping = false;
        }
    }

    // ---- detection ----------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task DetectSilenceAsync()
    {
        if (Project is null || Segments is null)
        {
            return;
        }

        var settings = CurrentSettings;
        ErrorMessage = null;
        BeginBusy("Detecting silence…");
        try
        {
            var ffmpeg = ResolveFfmpeg();
            var progress = new Progress<double>(p => BusyProgress = p);
            var spans = await new SilenceDetector(ffmpeg).DetectAsync(Project.Source.Path, DurationSeconds, settings, progress, CancellationToken.None);
            var cuts = SilenceCutPlanner.Plan(spans, settings, DurationSeconds);

            Edit(s => s.ReplaceAutoSegments(cuts));
            Project = Project with { Settings = settings };

            var removed = cuts.Sum(c => c.Duration);
            StatusMessage = cuts.Count == 0
                ? "No silence found with the current settings."
                : $"Detected {cuts.Count} silence{(cuts.Count == 1 ? "" : "s")}, {TimeFormat.Clock(removed)} removed.";
        }
        catch (Exception ex) when (ex is FfmpegNotFoundException or FfmpegException or IOException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Whisper word timing is coarser than a hand-placed boundary, so word edges get a wider snap window than a drag.</summary>
    private const double WordSnapWindowSeconds = 0.040;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RemoveFillerWordsAsync()
    {
        if (Project is null || Segments is null)
        {
            return;
        }

        if (!HasTranscript && !await TranscribeCoreAsync())
        {
            return;
        }

        var fillers = FillerDetector.Find(Transcript, _settings.Current.FillerWords);
        var spans = fillers.Select(w =>
        {
            var (start, end) = SnapWordSpan(w.Start, w.End);
            return new FillerSpan(start, end, FillerDetector.Normalize(w.Text));
        });
        var cuts = FillerCutPlanner.Plan(spans, Segments.Segments, CurrentSettings, DurationSeconds);

        Edit(s => s.ApplyFillerCuts(cuts));
        IsTranscriptOpen = true;

        var removed = cuts.Sum(c => c.Duration);
        StatusMessage = cuts.Count switch
        {
            0 when fillers.Count == 0 => "No filler words found.",
            0 => "Every filler word is already cut.",
            1 => $"Removed 1 filler word, {TimeFormat.Clock(removed)} cut.",
            _ => $"Removed {cuts.Count} filler words, {TimeFormat.Clock(removed)} cut.",
        };
    }

    /// <summary>Snaps both edges of a word to the quietest nearby waveform bucket, falling back to the raw edges if snapping would collapse the word.</summary>
    private (double Start, double End) SnapWordSpan(double start, double end)
    {
        if (Waveform is not { } waveform)
        {
            return (start, end);
        }

        var snappedStart = waveform.SnapToZeroCrossing(start, WordSnapWindowSeconds);
        var snappedEnd = waveform.SnapToZeroCrossing(end, WordSnapWindowSeconds);
        return snappedEnd - snappedStart < 0.02 ? (start, end) : (snappedStart, snappedEnd);
    }

    // ---- export -------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ExportAsync()
    {
        if (Project is null || Segments is null)
        {
            return;
        }

        if (!Segments.Segments.Any(s => s.Enabled))
        {
            ErrorMessage = "Every segment is removed; there is nothing to export.";
            return;
        }

        FfmpegLocation ffmpeg;
        try
        {
            ffmpeg = ResolveFfmpeg();
        }
        catch (FfmpegNotFoundException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        _player.Pause();
        var snapshot = Project with { Segments = Segments.Segments.ToList(), Settings = CurrentSettings };
        var export = new ExportViewModel(snapshot, snapshot.Segments, ffmpeg, _temp, _files);
        await _dialogs.ShowExportAsync(export);
        if (export.IsDone)
        {
            StatusMessage = $"Exported {export.OutputFileName}.";
        }
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

    private void BeginBusy(string message, bool cancellable = false)
    {
        BusyMessage = message;
        BusyProgress = double.NaN;
        _busyCts = cancellable ? new CancellationTokenSource() : null;
        CanCancelBusy = cancellable;
        IsBusy = true;
    }

    private void EndBusy()
    {
        IsBusy = false;
        CanCancelBusy = false;
        _busyCts?.Dispose();
        _busyCts = null;
        BusyMessage = null;
        BusyProgress = double.NaN;
    }
}
