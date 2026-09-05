namespace Cutback.App.Services;

/// <summary>Modal prompts, abstracted so view models never touch Avalonia windows.</summary>
public interface IDialogService
{
    /// <summary>"Save changes to X?" with Save / Don't Save / Cancel.</summary>
    Task<SaveChoice> ConfirmSaveChangesAsync(string projectName);
}
