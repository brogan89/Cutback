using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cutback.Core.Models;

namespace Cutback.Core.Projects;

/// <summary>Reads and writes <c>.cutback</c> project files.</summary>
public static class ProjectSerializer
{
    /// <summary>Schema version written by this build. Bump alongside a new <see cref="ProjectMigrator"/> step.</summary>
    public const int CurrentVersion = 1;

    public const string FileExtension = ".cutback";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize(CutbackProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var file = ToFile(project);
        return JsonSerializer.Serialize(file, ProjectJsonContext.Default.ProjectFile);
    }

    /// <exception cref="ProjectFormatException">The text is not a readable Cutback project.</exception>
    /// <exception cref="InvalidPartitionException">The segments in the file do not form a valid partition.</exception>
    public static CutbackProject Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: ParseOptions) as JsonObject
                ?? throw new ProjectFormatException("The project file does not contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException("The project file is not valid JSON.", ex);
        }

        ProjectMigrator.Default.Migrate(root);

        ProjectFile file;
        try
        {
            file = root.Deserialize(ProjectJsonContext.Default.ProjectFile)
                ?? throw new ProjectFormatException("The project file deserialised to nothing.");
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException($"The project file has an unexpected shape: {ex.Message}", ex);
        }

        if (file.Source is null || file.Segments is null || file.Settings is null)
        {
            throw new ProjectFormatException("The project file is missing one of: source, segments, settings.");
        }

        return new CutbackProject(file.Source, file.Segments, file.Transcript ?? [], file.Settings);
    }

    public static async Task SaveAsync(CutbackProject project, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var json = Serialize(project);

        // Write to a sibling temp file and move it into place so a crash mid-write never leaves a
        // truncated project on disk.
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <exception cref="ProjectFormatException">The file is not a readable Cutback project.</exception>
    /// <exception cref="InvalidPartitionException">The segments in the file do not form a valid partition.</exception>
    public static async Task<CutbackProject> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(json);
    }

    private static ProjectFile ToFile(CutbackProject project) => new(
        CurrentVersion,
        project.Source,
        project.Segments.ToList(),
        project.Transcript.ToList(),
        project.Settings);
}
