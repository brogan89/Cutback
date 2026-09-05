using System.Text.Json;

namespace Cutback.App.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in the per-user application data folder.</summary>
public sealed class AppSettingsStore
{
    public const int MaxRecentFiles = 10;

    private readonly string _path;

    public AppSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cutback", "settings.json"))
    {
    }

    public AppSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public AppSettings Current { get; private set; } = new();

    /// <summary>Reads settings from disk. A missing or unreadable file yields defaults; settings are never worth a crash.</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                Current = JsonSerializer.Deserialize(File.ReadAllText(_path), AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Current = new AppSettings();
        }

        return Current;
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Current = change(Current);
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, AppSettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a settings write is not worth interrupting the user.
        }
    }
}
