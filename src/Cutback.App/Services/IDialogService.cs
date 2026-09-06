namespace Cutback.App.Services;

/// <summary>Modal prompts, abstracted so view models never touch Avalonia windows.</summary>
public interface IDialogService
{
    /// <summary>"Save changes to X?" with Save / Don't Save / Cancel.</summary>
    Task<SaveChoice> ConfirmSaveChangesAsync(string projectName);

    /// <summary>Shows the export dialog modally and returns when it closes.</summary>
    Task ShowExportAsync(ViewModels.ExportViewModel viewModel);

    /// <summary>Shows the preferences window modally and returns when it closes.</summary>
    Task ShowPreferencesAsync(ViewModels.PreferencesViewModel viewModel);
}
