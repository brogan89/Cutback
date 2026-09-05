using System.Runtime.InteropServices;

namespace Cutback.Media.Tests;

public sealed class FfmpegLocatorTests
{
    private static FfmpegLocator Locator(
        OSPlatform platform,
        IEnumerable<string> existingFiles,
        string? userSetting = null,
        string? path = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var files = new HashSet<string>(existingFiles, StringComparer.Ordinal);
        return new FfmpegLocator(
            platform,
            fileExists: files.Contains,
            userConfiguredPath: userSetting,
            pathVariable: path,
            environment: env ?? new Dictionary<string, string>());
    }

    // ---- resolution order ---------------------------------------------------------------------

    [Fact]
    public void User_setting_pointing_at_the_binary_wins_over_PATH()
    {
        var locator = Locator(
            OSPlatform.OSX,
            ["/custom/ffmpeg", "/custom/ffprobe", "/usr/local/bin/ffmpeg", "/usr/local/bin/ffprobe"],
            userSetting: "/custom/ffmpeg",
            path: "/usr/local/bin");

        var location = locator.Locate();

        location.FfmpegPath.Should().Be("/custom/ffmpeg");
        location.FfprobePath.Should().Be("/custom/ffprobe");
        location.Source.Should().Be(FfmpegLocationSource.UserSetting);
    }

    [Fact]
    public void User_setting_may_be_the_directory_containing_the_binaries()
    {
        var locator = Locator(OSPlatform.Linux, ["/custom/ffmpeg", "/custom/ffprobe"], userSetting: "/custom");

        var location = locator.Locate();

        location.FfmpegPath.Should().Be("/custom/ffmpeg");
        location.Directory.Should().Be("/custom");
    }

    [Fact]
    public void A_stale_user_setting_falls_through_to_PATH()
    {
        var locator = Locator(
            OSPlatform.Linux,
            ["/usr/bin/ffmpeg", "/usr/bin/ffprobe"],
            userSetting: "/gone/ffmpeg",
            path: "/usr/bin");

        var location = locator.Locate();

        location.FfmpegPath.Should().Be("/usr/bin/ffmpeg");
        location.Source.Should().Be(FfmpegLocationSource.Path);
    }

    [Fact]
    public void PATH_is_searched_left_to_right()
    {
        var locator = Locator(
            OSPlatform.Linux,
            ["/a/ffmpeg", "/a/ffprobe", "/b/ffmpeg", "/b/ffprobe"],
            path: "/b:/a");

        locator.Locate().FfmpegPath.Should().Be("/b/ffmpeg");
    }

    [Fact]
    public void PATH_uses_the_platform_separator()
    {
        var locator = Locator(
            OSPlatform.Windows,
            [@"C:\tools\ffmpeg.exe", @"C:\tools\ffprobe.exe"],
            path: @"C:\Windows;C:\tools");

        locator.Locate().FfmpegPath.Should().Be(@"C:\tools\ffmpeg.exe");
    }

    [Fact]
    public void Common_locations_are_searched_after_PATH()
    {
        var locator = Locator(
            OSPlatform.OSX,
            ["/opt/homebrew/bin/ffmpeg", "/opt/homebrew/bin/ffprobe"],
            path: "/usr/bin:/bin");

        var location = locator.Locate();

        location.FfmpegPath.Should().Be("/opt/homebrew/bin/ffmpeg");
        location.Source.Should().Be(FfmpegLocationSource.CommonLocation);
    }

    [Fact]
    public void Windows_common_locations_include_winget_and_program_files()
    {
        var env = new Dictionary<string, string>
        {
            ["ProgramFiles"] = @"C:\Program Files",
            ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
        };
        var locator = Locator(
            OSPlatform.Windows,
            [@"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe", @"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\ffprobe.exe"],
            env: env);

        locator.Locate().FfmpegPath.Should().Be(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe");
    }

    [Fact]
    public void Linux_common_locations_include_usr_bin()
    {
        var locator = Locator(OSPlatform.Linux, ["/usr/bin/ffmpeg", "/usr/bin/ffprobe"]);

        locator.Locate().FfmpegPath.Should().Be("/usr/bin/ffmpeg");
    }

    // ---- ffprobe is required ------------------------------------------------------------------

    [Fact]
    public void A_location_with_ffmpeg_but_no_ffprobe_is_skipped()
    {
        var locator = Locator(
            OSPlatform.Linux,
            ["/a/ffmpeg", "/b/ffmpeg", "/b/ffprobe"],
            path: "/a:/b");

        locator.Locate().Directory.Should().Be("/b");
    }

    [Fact]
    public void Ffmpeg_without_ffprobe_anywhere_reports_the_missing_ffprobe()
    {
        var locator = Locator(OSPlatform.Linux, ["/usr/bin/ffmpeg"], path: "/usr/bin");

        var act = () => locator.Locate();

        act.Should().Throw<FfmpegNotFoundException>().WithMessage("*ffprobe*/usr/bin*");
    }

    // ---- not found ----------------------------------------------------------------------------

    [Fact]
    public void TryLocate_returns_null_when_nothing_is_found()
    {
        var locator = Locator(OSPlatform.Linux, [], path: "/nowhere");

        locator.TryLocate().Should().BeNull();
    }

    [Theory]
    [InlineData("Windows", "winget install")]
    [InlineData("OSX", "brew install ffmpeg")]
    [InlineData("Linux", "apt install ffmpeg")]
    public void Not_found_message_names_the_platform_install_command(string platformName, string expectedHint)
    {
        var platform = OSPlatform.Create(platformName.ToUpperInvariant());
        var locator = Locator(platform, [], path: "/nowhere");

        var act = () => locator.Locate();

        act.Should().Throw<FfmpegNotFoundException>()
            .Which.Message.Should().Contain(expectedHint).And.Contain("ffmpeg");
    }

    [Fact]
    public void Not_found_message_mentions_the_setting_when_one_was_configured()
    {
        var locator = Locator(OSPlatform.OSX, [], userSetting: "/old/ffmpeg", path: "/nowhere");

        var act = () => locator.Locate();

        act.Should().Throw<FfmpegNotFoundException>().WithMessage("*/old/ffmpeg*");
    }

    // ---- defaults -----------------------------------------------------------------------------

    [Fact]
    public void Default_constructor_uses_the_current_process_environment()
    {
        // Only checks it does not throw and picks the right binary name for this OS.
        var locator = new FfmpegLocator();

        locator.ExecutableName("ffmpeg").Should().Be(OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
    }
}
