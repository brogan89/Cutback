using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cutback.App.Services;

namespace Cutback.App.ViewModels;

/// <summary>Drives the Preferences window. Edits are staged and written to <see cref="AppSettingsStore"/> on OK.</summary>
public sealed partial class PreferencesViewModel : ViewModelBase
{
    private readonly AppSettingsStore _settings;

    public PreferencesViewModel(AppSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        UndoHistoryLimit = settings.Current.UndoHistoryLimit;
    }

    /// <summary>Raised when the window should close, whether the changes were kept or not.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>How many edits can be undone. See <see cref="AppSettings.UndoHistoryLimit"/>.</summary>
    [ObservableProperty]
    public partial int UndoHistoryLimit { get; set; }

    [RelayCommand]
    private void Save()
    {
        var limit = Math.Clamp(UndoHistoryLimit, AppSettings.MinUndoHistoryLimit, AppSettings.MaxUndoHistoryLimit);
        _settings.Update(s => s with { UndoHistoryLimit = limit });
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
