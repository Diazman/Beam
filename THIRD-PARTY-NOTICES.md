# Third-party notices

Beam includes or depends on the following components.

| Component | License | Use |
|---|---|---|
| [.NET runtime and libraries](https://github.com/dotnet/runtime) | MIT | Runtime (bundled in the self-contained build) |
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) | MIT | UI framework |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | MIT | Rendering (via Avalonia) |
| [QRCoder](https://github.com/codebude/QRCoder) | MIT | QR codes on the Phone page |
| [Inter font](https://github.com/rsms/inter) (via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | UI font on non-Windows platforms |
| [Material Design Icons](https://github.com/google/material-design-icons) | Apache License 2.0 | Icon shapes in `src/Beam.UI/Styles/Icons.axaml` |
| [Windows SDK projection (CsWinRT)](https://github.com/microsoft/CsWinRT) | MIT | Windows toast notifications |
| System.Security.Cryptography.ProtectedData | MIT | Protecting the device key with DPAPI |

Test-only: xUnit (Apache 2.0), Microsoft.NET.Test.Sdk (MIT), Avalonia.Headless (MIT).
