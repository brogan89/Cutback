using System.Text.Json;
using Cutback.Analysis;
using Cutback.Core.Detection;
using Cutback.Core.Models;

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

        Current = Sanitize(Current);
        return Current;
    }

    /// <summary>A hand-edited file must not produce values the UI cannot represent.</summary>
    private static AppSettings Sanitize(AppSettings settings) => settings with
    {
        UndoHistoryLimit = Math.Clamp(settings.UndoHistoryLimit, AppSettings.MinUndoHistoryLimit, AppSettings.MaxUndoHistoryLimit),
        RecentFiles = settings.RecentFiles ?? [],
        WhisperModel = WhisperModelInfo.For(WhisperModelInfo.Parse(settings.WhisperModel)).Key,
        FillerWords = SanitizeFillerWords(settings.FillerWords),
        // Math.Clamp(NaN, ...) is NaN, and NaN would go straight into an ffmpeg filter string.
        SilenceThresholdDb = double.IsFinite(settings.SilenceThresholdDb)
            ? Math.Clamp(settings.SilenceThresholdDb, AppSettings.MinSilenceThresholdDb, AppSettings.MaxSilenceThresholdDb)
            : DetectionSettings.Default.SilenceThresholdDb,
        MinSilenceMs = Math.Clamp(settings.MinSilenceMs, AppSettings.MinMinSilenceMs, AppSettings.MaxMinSilenceMs),
        PaddingMs = Math.Clamp(settings.PaddingMs, AppSettings.MinPaddingMs, AppSettings.MaxPaddingMs),
        MinKeepMs = Math.Clamp(settings.MinKeepMs, AppSettings.MinMinKeepMs, AppSettings.MaxMinKeepMs),
    };

    /// <summary>Normalised, de-duplicated filler words, or the defaults when nothing usable remains.</summary>
    private static IReadOnlyList<string> SanitizeFillerWords(IReadOnlyList<string>? words)
    {
        var cleaned = (words ?? [])
            .Select(FillerDetector.Normalize)
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return cleaned.Count > 0 ? cleaned : FillerDetector.DefaultWords;
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
