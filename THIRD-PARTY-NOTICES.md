# Third-party notices

PIXORA Capture Studio is built on the following components. They are restored from NuGet at build
time and redistributed inside the installer under their own licences.

| Component | Use in PIXORA | Licence |
|---|---|---|
| Windows App SDK (WinUI 3, Foundation, Interactive Experiences) | UI framework, windowing | Microsoft Software License Terms (redistributable) |
| .NET 10 runtime (self-contained) | Runtime | MIT |
| Microsoft.Windows.SDK.NET / CsWinRT projections | Windows API access (capture, media, storage) | MIT |
| SkiaSharp (+ native Skia) | Image rendering, annotation, blur/pixelate, PNG/JPEG/WebP encoding | MIT (Skia: BSD-3-Clause) |
| NAudio 3 | WASAPI loopback and microphone capture, mixing | MIT |
| Microsoft.Data.Sqlite (+ SQLitePCLRaw, SQLite) | Media library index | MIT (SQLitePCLRaw: Apache-2.0; SQLite: public domain) |
| CommunityToolkit.Mvvm | MVVM view models | MIT |
| Microsoft.Extensions.DependencyInjection | Service composition | MIT |
| xUnit, Microsoft.NET.Test.Sdk | Tests only (not shipped) | Apache-2.0 / MIT |
| NSIS | Installer builder (not shipped as a library) | zlib/libpng |

Video encoding uses the H.264/AAC encoders that ship with Windows (Media Foundation); no FFmpeg or
other codec binaries are distributed.

Icons in the UI use the Segoe Fluent Icons / Segoe MDL2 Assets fonts that ship with Windows.
