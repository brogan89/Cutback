using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Cutback.App.Playback;

/// <summary>
/// Finds the native libVLC and initialises LibVLCSharp once at startup.
/// </summary>
/// <remarks>
/// Windows: the VideoLAN.LibVLC.Windows package copies libvlc next to the executable and
/// <see cref="LibVLCSharp.Shared.Core.Initialize(string)"/> with no path finds it.
/// macOS: the NuGet package is x86_64-only and incomplete, so the installed VLC.app is used.
/// Linux: the system libvlc package is used.
/// </remarks>
public static class LibVlcLocator
{
    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);

    private static readonly string[] MacVlcAppPaths =
    [
        "/Applications/VLC.app",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "VLC.app"),
    ];

    /// <summary>Calls <see cref="LibVLCSharp.Shared.Core.Initialize(string)"/> with the right directory for this platform.</summary>
    /// <exception cref="LibVlcNotFoundException">libVLC is not installed. The message names the install step.</exception>
    public static void Initialize()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var libDir = MacVlcAppPaths
                    .Select(app => Path.Combine(app, "Contents", "MacOS", "lib"))
                    .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "libvlc.dylib")))
                    ?? throw new LibVlcNotFoundException(
                        "Video preview needs VLC, but VLC.app was not found in /Applications.\n\n"
                        + "Install it with:\n    brew install --cask vlc\n\nor from https://www.videolan.org/, then restart Cutback.");

                // libvlccore cannot find VLC.app's plugins from inside another process, so tell it.
                // Environment.SetEnvironmentVariable only updates the managed copy on Unix; the
                // native getenv() needs a real setenv().
                var plugins = Path.Combine(Path.GetDirectoryName(libDir)!, "plugins");
                if (setenv("VLC_PLUGIN_PATH", plugins, 1) != 0)
                {
                    throw new LibVlcNotFoundException($"Could not set VLC_PLUGIN_PATH to \"{plugins}\".");
                }

                LibVLCSharp.Shared.Core.Initialize(libDir);
                return;
            }

            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (VLCException ex)
        {
            var hint = OperatingSystem.IsWindows()
                ? "The bundled libvlc could not be loaded. Reinstalling Cutback should fix this."
                : "Install it with your package manager, for example:\n    sudo apt install libvlc-dev vlc-plugin-base\n\nthen restart Cutback.";
            throw new LibVlcNotFoundException($"Video preview needs libVLC, which could not be loaded.\n\n{hint}\n\n{ex.Message}", ex);
        }
    }
}
