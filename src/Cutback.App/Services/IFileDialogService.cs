namespace Cutback.App.Services;

/// <summary>File pickers, abstracted so view models never touch Avalonia's storage APIs.</summary>
public interface IFileDialogService
{
    /// <returns>The chosen path, or null if the user cancelled.</returns>
    Task<string?> PickVideoToOpenAsync();

    /// <returns>The chosen path, or null if the user cancelled.</returns>
    Task<string?> PickProjectToOpenAsync();

    /// <param name="suggestedName">Default file name, without directory.</param>
    /// <returns>The chosen path, or null if the user cancelled.</returns>
    Task<string?> PickProjectToSaveAsync(string suggestedName);

    /// <param name="suggestedName">Default file name, without directory.</param>
    /// <returns>The chosen path, or null if the user cancelled.</returns>
    Task<string?> PickExportTargetAsync(string suggestedName);

    /// <param name="suggestedName">Default file name, without directory or extension.</param>
    /// <returns>The chosen <c>.txt</c> or <c>.srt</c> path, or null if the user cancelled.</returns>
    Task<string?> PickTranscriptTargetAsync(string suggestedName);
}
