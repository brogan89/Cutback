using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cutback.App.Services;
using Cutback.Core.Models;
using Cutback.Media;
using Cutback.Media.Export;

namespace Cutback.App.ViewModels;

/// <summary>Drives the export dialog: choose a target and mode, run, watch progress, cancel.</summary>
public sealed partial class ExportViewModel : ViewModelBase
{
    private readonly CutbackProject _project;
    private readonly IReadOnlyList<Segment> _segments;
    private readonly Exporter _exporter;
    private readonly IFileDialogService _files;
    private CancellationTokenSource? _cts;

    public ExportViewModel(CutbackProject project, IReadOnlyList<Segment> segments, FfmpegLocation ffmpeg, TempSession temp, IFileDialogService files)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(temp);
        ArgumentNullException.ThrowIfNull(files);
        _project = project;
        _segments = segments;
        _exporter = new Exporter(ffmpeg, temp);
        _files = files;

        OutputPath = SuggestOutputPath(project.Source.Path);
        KeptSeconds = FilterGraphBuilder.KeptDuration(segments);
        CutCount = segments.Count(s => !s.Enabled);
    }

    /// <summary>Raised when the dialog should close (finished, or cancelled before starting).</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(OutputFileName), nameof(FastModeAvailable))]
    public partial string OutputPath { get; set; }

    public string OutputFileName => Path.GetFileName(OutputPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrecise))]
    public partial bool IsFast { get; set; }

    public bool IsPrecise
    {
        get => !IsFast;
        set => IsFast = !value;
    }

    /// <summary>Fast mode copies the source codecs, which WebM cannot hold.</summary>
    public bool FastModeAvailable => !string.Equals(Path.GetExtension(OutputPath), ".webm", StringComparison.OrdinalIgnoreCase);

    public double KeptSeconds { get; }

    public int CutCount { get; }

    public string Summary =>
        $"{TimeFormat.Clock(KeptSeconds)} kept of {TimeFormat.Clock(_project.Source.DurationSeconds)}, {CutCount} cut{(CutCount == 1 ? "" : "s")} removed.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(BrowseCommand))]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string? Stage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    public partial bool IsDone { get; private set; }

    public string CancelLabel => IsExporting ? "Cancel" : IsDone ? "Close" : "Cancel";

    private bool CanExport => !IsExporting && !IsDone && !string.IsNullOrWhiteSpace(OutputPath);

    private bool CanBrowse => !IsExporting && !IsDone;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseAsync()
    {
        // Stem only; the picker adds the extension (see MainWindowViewModel.SaveProjectAsAsync).
        var picked = await _files.PickExportTargetAsync(Path.GetFileNameWithoutExtension(OutputPath));
        if (picked is not null)
        {
            OutputPath = CollapseDoubledExtension(picked);
            if (!FastModeAvailable)
            {
                IsFast = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        ErrorMessage = null;
        Progress = 0;
        Stage = "Starting…";
        IsExporting = true;
        _cts = new CancellationTokenSource();
        try
        {
            var request = new ExportRequest(_project.Source.Path, _segments, _project.Source.DurationSeconds, OutputPath, IsFast ? ExportMode.Fast : ExportMode.Precise);
            var progress = new Progress<ExportProgress>(p =>
            {
                Progress = p.Fraction;
                Stage = p.Stage;
            });
            await _exporter.ExportAsync(request, progress, _cts.Token);
            Progress = 1;
            Stage = $"Exported {OutputFileName}.";
            IsDone = true;
        }
        catch (OperationCanceledException)
        {
            Stage = "Cancelled.";
            Progress = 0;
        }
        catch (Exception ex) when (ex is FfmpegException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            Stage = null;
        }
        finally
        {
            IsExporting = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Cancel a running export, or close the dialog if nothing is running.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsExporting)
        {
            _cts?.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by the window when the user tries to close it; false while an export runs.</summary>
    public bool CanClose => !IsExporting;

    /// <summary>"name.mp4.mp4" from a picker becomes "name.mp4".</summary>
    private static string CollapseDoubledExtension(string path)
    {
        var ext = Path.GetExtension(path);
        while (ext.Length > 0 && path.EndsWith(ext + ext, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^ext.Length];
        }

        return path;
    }

    /// <summary>"recording-cut.mp4" next to the source, numbered if that already exists.</summary>
    private static string SuggestOutputPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourcePath) + "-cut";
        var candidate = Path.Combine(dir, stem + ".mp4");
        for (var i = 2; File.Exists(candidate) && i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{stem}-{i}.mp4");
        }

        return candidate;
    }
}
