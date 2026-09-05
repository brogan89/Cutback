namespace Cutback.Media;

/// <summary>ffprobe ran but the file could not be understood as a video Cutback can edit.</summary>
public sealed class MediaProbeException : Exception
{
    public MediaProbeException(string message)
        : base(message)
    {
    }

    public MediaProbeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
