# Third-party notices

Cutback is licensed under GPL-3.0-only. The dependencies below are compatible with that licence.
Keep this file current: every new package must be added here with its licence before it is merged.

## NuGet packages

| Package | Version | Licence | Used by |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Diagnostics | 11.3.20 | MIT | Cutback.App |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | Cutback.App |
| FFMpegCore | 5.4.0 | MIT | Cutback.Media |
| Whisper.net | 1.9.1 | MIT | Cutback.Analysis |
| Whisper.net.Runtime (bundles whisper.cpp and ggml) | 1.9.1 | MIT (whisper.cpp MIT, ggml MIT) | Cutback.Analysis |
| Whisper.net.Runtime.Metal | 1.9.1 | MIT | Cutback.Analysis, pulled in transitively by Whisper.net.Runtime; used on macOS |
| Microsoft.Extensions.AI.Abstractions (transitive via Whisper.net) | 10.2.0 | MIT | Cutback.Analysis |
| LibVLCSharp, LibVLCSharp.Avalonia | 3.10.1 | LGPL-2.1-or-later | Cutback.App |
| VideoLAN.LibVLC.Windows | 3.0.23.1 | LGPL-2.1-or-later (libVLC) | Cutback.App, Windows only |
| SkiaSharp (transitive via Avalonia.Skia) | 2.88.9 | MIT | Cutback.App |
| xunit, xunit.runner.visualstudio | 2.9.3 / 4.0.0 | Apache-2.0 | tests |
| Microsoft.NET.Test.Sdk | 18.9.0 | MIT | tests |
| FluentAssertions | 7.2.2 | Apache-2.0 | tests |

Test-only packages are not distributed with the application.

## External programs

| Program | Licence | Notes |
|---|---|---|
| FFmpeg (ffmpeg, ffprobe) | GPL-2.0-or-later when built with libx264 | Invoked as a separate process. Not bundled; installed by the user. |
| libVLC | LGPL-2.1-or-later | Loaded at runtime for video preview. Bundled on Windows; VLC.app on macOS; system package on Linux. |

## Downloaded at runtime

| Asset | Licence | Notes |
|---|---|---|
| OpenAI Whisper ggml model weights (`ggml-*.en.bin`) | MIT | Fetched from Hugging Face on first use into the per-user model cache. Not bundled with the application. |

## Fonts

The application uses the platform's default UI fonts. No fonts are bundled.
