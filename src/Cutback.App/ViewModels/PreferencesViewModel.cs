using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cutback.Analysis;
using Cutback.App.Services;
using Cutback.Core.Detection;

namespace Cutback.App.ViewModels;

/// <summary>One entry in the speech-model dropdown.</summary>
public sealed record SpeechModelOption(WhisperModel Model, string Label);

/// <summary>Drives the Preferences window. Edits are staged and written to <see cref="AppSettingsStore"/> on OK.</summary>
public sealed partial class PreferencesViewModel : ViewModelBase
{
    private readonly AppSettingsStore _settings;

    public PreferencesViewModel(AppSettingsStore settings, ModelStore models)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(models);
        _settings = settings;
        UndoHistoryLimit = settings.Current.UndoHistoryLimit;

        SpeechModels = WhisperModelInfo.All
            .Select(i => new SpeechModelOption(i.Model, i.DisplayName + (models.IsDownloaded(i.Model) ? ", downloaded" : "")))
            .ToList();
        var current = WhisperModelInfo.Parse(settings.Current.WhisperModel);
        SelectedSpeechModel = SpeechModels.First(o => o.Model == current);
        FillerWordsText = string.Join(", ", settings.Current.FillerWords);
    }

    /// <summary>Raised when the window should close, whether the changes were kept or not.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>How many edits can be undone. See <see cref="AppSettings.UndoHistoryLimit"/>.</summary>
    [ObservableProperty]
    public partial int UndoHistoryLimit { get; set; }

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

        _settings.Update(s => s with
        {
            UndoHistoryLimit = limit,
            WhisperModel = WhisperModelInfo.For(model).Key,
            FillerWords = fillers.Count > 0 ? fillers : FillerDetector.DefaultWords,
        });
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
