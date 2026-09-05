namespace Cutback.Media.Export;

public enum ExportMode
{
    /// <summary>Re-encode with libx264 / aac. Frame-accurate cuts. Slow.</summary>
    Precise,

    /// <summary>Stream copy with cuts snapped to keyframes. Fast, but boundaries are approximate.</summary>
    Fast,
}
