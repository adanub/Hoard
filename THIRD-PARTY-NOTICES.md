# Third-party notices

Hoard is licensed under [GPL-3.0](LICENSE). It uses and, in one case, redistributes the following
third-party components, each of which remains under its own licence.

## Redistributed in release builds

### gallery-dl — GPL-2.0

Hoard bundles the [gallery-dl](https://github.com/mikf/gallery-dl) executable in its release archives and
runs it as a **separate process**; the two are distributed together but are not combined into a single
program. gallery-dl is copyright Mike Fährmann and contributors, licensed under the GNU General Public
Licence version 2. Its full licence text ships in `licences/gallery-dl-LICENSE.txt` inside each release
archive, and its source is available at <https://github.com/mikf/gallery-dl>.

Binaries are taken from [gdl-org/builds](https://github.com/gdl-org/builds).

## Referenced as dependencies

Resolved from NuGet at build time and not redistributed as source by this repository.

| Component | Licence |
| --- | --- |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (incl. `Avalonia.Desktop`, `Avalonia.Controls.ItemsRepeater`, `Avalonia.Fonts.Inter`, `AvaloniaUI.DiagnosticsSupport`) | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT |
| [Entity Framework Core](https://github.com/dotnet/efcore) (`Microsoft.EntityFrameworkCore.Sqlite`) | MIT |
| [Microsoft.Extensions.\*](https://github.com/dotnet/runtime) (DependencyInjection, Logging) | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | Apache-2.0 |
| [Serilog](https://github.com/serilog/serilog) (Extensions.Logging, Sinks.Console, Sinks.File) | Apache-2.0 |

The [Inter](https://rsms.me/inter/) typeface, bundled via `Avalonia.Fonts.Inter`, is licensed under the
SIL Open Font Licence 1.1.

## Design assets

Icons are [Lucide](https://lucide.dev) (ISC), embedded as path geometries in `Theme/Icons.axaml`.
The component styling takes [shadcn/ui](https://ui.shadcn.com) as a design *reference* only — no code or
assets from it are included.

The Hoard application icon (`assets/icon/`) is original work, part of this project, and covered by
[LICENSE](LICENSE).

## Not affiliated with Pinterest

Hoard is an independent tool. It is not affiliated with, endorsed by, sponsored by or connected to
Pinterest, Inc. "Pinterest" is a trademark of Pinterest, Inc., used here only to describe what the
software interoperates with.
