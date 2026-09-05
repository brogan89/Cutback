namespace Cutback.App.Playback;

/// <summary>The native libVLC library could not be found. The message tells the user how to install it.</summary>
public sealed class LibVlcNotFoundException : Exception
{
    public LibVlcNotFoundException(string message)
        : base(message)
    {
    }

    public LibVlcNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
