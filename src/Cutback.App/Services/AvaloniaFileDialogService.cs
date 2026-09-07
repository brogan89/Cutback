using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Cutback.App.Services;

public sealed class AvaloniaFileDialogService : IFileDialogService
{
    public static readonly string[] VideoExtensions = ["mp4", "mov", "mkv", "webm", "m4v", "avi", "mts", "m2ts", "ts", "wmv", "flv"];

    private static readonly FilePickerFileType VideoType = new("Video files")
    {
        Patterns = VideoExtensions.Select(e => "*." + e).ToArray(),
        AppleUniformTypeIdentifiers = ["public.movie"],
        MimeTypes = ["video/*"],
    };

    private static readonly FilePickerFileType ProjectType = new("Cutback projects")
    {
        Patterns = ["*.cutback"],
        MimeTypes = ["application/json"],
    };

    private readonly Window _owner;

    public AvaloniaFileDialogService(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public async Task<string?> PickVideoToOpenAsync()
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = [VideoType, FilePickerFileTypes.All],
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProjectToOpenAsync()
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            AllowMultiple = false,
            FileTypeFilter = [ProjectType, FilePickerFileTypes.All],
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProjectToSaveAsync(string suggestedName)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            SuggestedFileName = suggestedName,
            DefaultExtension = "cutback",
            FileTypeChoices = [ProjectType],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickExportTargetAsync(string suggestedName)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export video",
            SuggestedFileName = suggestedName,
            DefaultExtension = "mp4",
            FileTypeChoices =
            [
                new FilePickerFileType("MP4") { Patterns = ["*.mp4"] },
                new FilePickerFileType("QuickTime") { Patterns = ["*.mov"] },
                new FilePickerFileType("Matroska") { Patterns = ["*.mkv"] },
                new FilePickerFileType("WebM") { Patterns = ["*.webm"] },
            ],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickTranscriptTargetAsync(string suggestedName)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export transcript",
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Plain text") { Patterns = ["*.txt"] },
                new FilePickerFileType("SubRip subtitles") { Patterns = ["*.srt"] },
            ],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }
}
