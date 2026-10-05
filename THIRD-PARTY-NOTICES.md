# Third-party notices

Deduplicator uses third-party software. The authoritative license texts are shipped by the corresponding packages and upstream projects; release archives should include this notice and any license text required by the exact binaries being distributed.

| Component | License / notice | Project |
|---|---|---|
| .NET runtime and Microsoft.Extensions / Microsoft.Data.Sqlite | MIT | https://github.com/dotnet/runtime |
| Avalonia UI | MIT | https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| SQLite | Public Domain | https://sqlite.org/copyright.html |
| SQLitePCLRaw | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SixLabors.ImageSharp 3.x | Six Labors Split License 1.0; Apache-2.0 eligibility conditions or a commercial license apply | https://github.com/SixLabors/ImageSharp |
| CoenM.ImageHash | MIT | https://github.com/coenm/ImageHash |
| SkiaSharp | MIT | https://github.com/mono/SkiaSharp |
| Tmds.DBus.Protocol | MIT | https://github.com/tmds/Tmds.DBus |
| Chromaprint / fpcalc | LGPL-2.1-or-later; the selected binary can also include FFmpeg components with additional obligations | https://acoustid.org/chromaprint |

## fpcalc release requirement

`fpcalc.exe` is an optional external binary and is not tracked by this repository. Before distributing it, record its upstream version and SHA-256 hash, verify its Authenticode status, and include the Chromaprint and FFmpeg license material required by that particular build. The locally tested binary reports `fpcalc 1.6.0` with FFmpeg libraries; this statement is not a substitute for verifying the binary used in a future release.

## ImageSharp commercial-use check

ImageSharp is a direct dependency. The release owner must confirm that the project's use satisfies the Apache-2.0 eligibility conditions in the Six Labors Split License or obtain an appropriate commercial license.
