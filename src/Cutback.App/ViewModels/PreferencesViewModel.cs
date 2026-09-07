using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cutback.Analysis;
using Cutback.App.Services;
using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.App.ViewModels;

/// <summary>One entry in the speech-model dropdown.</summary>
public sealed record SpeechModelOption(WhisperModel Model, string Label);

/// <summary>Drives the Preferences window. Edits are staged and written to <see cref="AppSettingsStore"/> on OK.</summary>
public sealed partial class PreferencesViewModel : ViewModelBase
{
    /// <summary>Tab indices, matching the order of the TabItems in PreferencesWindow.axaml.</summary>
    public const int GeneralTab = 0;
    public const int DetectionTab = 1;
    public const int TranscriptionTab = 2;

    private readonly AppSettingsStore _settings;

    public PreferencesViewModel(AppSettingsStore settings, ModelStore models)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(models);
        _settings = settings;
        UndoHistoryLimit = settings.Current.UndoHistoryLimit;
        SilenceThresholdDb = settings.Current.SilenceThresholdDb;
        MinSilenceMs = settings.Current.MinSilenceMs;
        PaddingMs = settings.Current.PaddingMs;
        MinKeepMs = settings.Current.MinKeepMs;

        SpeechModels = WhisperModelInfo.All
            .Select(i => new SpeechModelOption(i.Model, i.DisplayName + (models.IsDownloaded(i.Model) ? ", downloaded" : "")))
            .ToList();
        var current = WhisperModelInfo.Parse(settings.Current.WhisperModel);
        SelectedSpeechModel = SpeechModels.First(o => o.Model == current);
        FillerWordsText = string.Join(", ", settings.Current.FillerWords);
    }

    /// <summary>Raised when the window should close, whether the changes were kept or not.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Which tab the window opens on. See <see cref="GeneralTab"/> and friends.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; } = GeneralTab;

    /// <summary>How many edits can be undone. See <see cref="AppSettings.UndoHistoryLimit"/>.</summary>
    [ObservableProperty]
    public partial int UndoHistoryLimit { get; set; }

    // Silence detection. Bounds live on AppSettings and are mirrored by the NumericUpDowns.

    [ObservableProperty]
    public partial double SilenceThresholdDb { get; set; }

    [ObservableProperty]
    public partial int MinSilenceMs { get; set; }

    [ObservableProperty]
    public partial int PaddingMs { get; set; }

    [ObservableProperty]
    public partial int MinKeepMs { get; set; }

    /// <summary>Puts the four detection fields back to <see cref="DetectionSettings.Default"/>. Nothing is saved until OK.</summary>
    [RelayCommand]
    private void ResetDetection()
    {
        var d = DetectionSettings.Default;
        SilenceThresholdDb = d.SilenceThresholdDb;
        MinSilenceMs = d.MinSilenceMs;
        PaddingMs = d.PaddingMs;
        MinKeepMs = d.MinKeepMs;
    }

    public IReadOnlyList<SpeechModelOption> SpeechModels { get; }

    [ObservableProperty]
    public partial SpeechModelOption? SelectedSpeechModel { get; set; }

    /// <summary>Comma-separated. Blank entries are dropped; an empty list falls back to the default.</summary>
    [ObservableProperty]
    public partial string FillerWordsText { get; set; }

    [RelayCommand]
    private void Save()
    {
        var limit = Math.Clamp(UndoHistoryLimit, AppSettings.MinUndoHistoryLimit, AppSettings.MaxUndoHistoryLimit);
        var model = SelectedSpeechModel?.Model ?? WhisperModel.BaseEn;
        var fillers = FillerWordsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(FillerDetector.Normalize)
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var threshold = double.IsFinite(SilenceThresholdDb)
            ? Math.Clamp(SilenceThresholdDb, AppSettings.MinSilenceThresholdDb, AppSettings.MaxSilenceThresholdDb)
            : DetectionSettings.Default.SilenceThresholdDb;

        _settings.Update(s => s with
        {
            UndoHistoryLimit = limit,
            WhisperModel = WhisperModelInfo.For(model).Key,
            FillerWords = fillers.Count > 0 ? fillers : FillerDetector.DefaultWords,
            SilenceThresholdDb = threshold,
            MinSilenceMs = Math.Clamp(MinSilenceMs, AppSettings.MinMinSilenceMs, AppSettings.MaxMinSilenceMs),
            PaddingMs = Math.Clamp(PaddingMs, AppSettings.MinPaddingMs, AppSettings.MaxPaddingMs),
            MinKeepMs = Math.Clamp(MinKeepMs, AppSettings.MinMinKeepMs, AppSettings.MaxMinKeepMs),
        });
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
