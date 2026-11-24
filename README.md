# Clay

Clay is a natural-language calculator for Windows, built with WinUI 3 and .NET 10. Type expressions on the left and see one result per line on the right.

See the [changelog](CHANGELOG.md) for the current version and release history.

## Features

- Arithmetic, bitwise operators (`~`, `<<`, `>>`, `&`, `|`, `xor`/`^^`), percentages, variables (`=` or `:=`), labels, and comments (full-line `//` or `#`, inline `#`)
- Word and symbol operator aliases (`plus`, `minus`, `times`, `per`, `rem`, `mod`/`%%`, `div`/`\`/`//`, `**`)
- Units and conversions for length, mass, volume, time, temperature, data (including `b?byte` binary auto-prefix), speed, area, angle, energy, power, and pressure, plus `to base` SI expansion and reciprocal conversions (`m/s to s/m`)
- Unit arithmetic that combines and cancels units, such as `5 m/s * 4 s` (`20 m`), `2 kW * 3 h` (`6 kWh`), and `100 J / 2 s` (`50 W`)
- Optional physics units (force, frequency, current, voltage, and resistance) and optional **Ignore spaces in numbers** (`123 456 789`)
- Currency conversion with built-in fallback rates and optional live rates from [open.er-api.com](https://open.er-api.com/)
- Number base conversions: `bin`, `bin4`–`bin64`, `oct`, `hex`, `base <2..36>`, `float` (IEEE-754 32-bit), `roman`, and `sexa`
- Common mathematical and geometry functions, plus aggregates such as `sum`, `average`, and `prev`
- Exact arithmetic, symbolic algebra, equation solving, calculus, statistics, matrices and vectors, complex numbers, uncertainty intervals, and dates
- Lines that start with `+`, `*`, `/`, or `^` continue from the previous result (`+5` is `prev + 5`); a leading `-` is a negative number
- Plain-text open, save, save as, and autosave support

Example:

```text
# Budget
rent = 1200
utilities = 180 + 65
Groceries: 340 + 20%
Total

# Conversions
10 km in miles
72 °F in °C
$100 in EUR
255 in hex

# Unit arithmetic
5 m/s * 4 s
100 km / 2 h
60 miles per hour * 90 min
$15 per hour * 40 h
9.8 m/s^2 in ft/s^2
```

## Unit arithmetic

Multiplying, dividing, and raising values with units combines their units: `100 km / 2 h` is `50 km/h`, `3 m * 4 m` is `12 m²`, and `sqrt(9 m²)` is `3 m`. Units of the same kind cancel, so `5 m/s * 4 s` is `20 m`. When the two sides use different units of the same kind, the result uses the left unit: `2 m * 3 ft` is `1.8288 m²`, and `60 mph * 30 min` is `30 mi`. Results that match a named unit use that name (`2 kW * 3 h` is `6 kWh`, `100 J / 2 s` is `50 W`); other combinations are shown as compounds such as `kg·m/s²`. Use `in` to express a result in another unit.

Write compound units without spaces (`m/s`, `m/s²`, `m/s^2`, `kg·m/s²`) or with `per` (`60 miles per hour`, `$15 per hour`). A whole-number power after a unit applies to that unit, with or without spaces, so `5 m^2` is `5 m²` and `5 m/s ^ 2` is `5 m/s²`, while `(5 m)^2` is `25 m²`. Unit powers go up to 20, units can be at most 1e300 times larger or smaller than their base units, and exponents can't have units. Unit arithmetic that would overflow or underflow reports an error instead of `∞` or `0`. Absolute temperatures (°C and °F) can't be multiplied, divided by other units, or raised to powers; use kelvin for those.

Turn on **Settings → Physics units** to add newtons (`N`, `kN`, `lbf`), hertz (`Hz`, `kHz`, `MHz`, `GHz`), amperes (`A`, `mA`), volts (`V`, `mV`, `kV`), and ohms (`Ω`, `kΩ`). With the option on, results are also named with these units (`2 kg * 3 m/s^2` is `6 N`, `12 V / 2 Ω` is `6 A`), and their symbols are reserved; with it off, names such as `N` and `V` remain available as variables. Physics symbols are case-sensitive.

## Getting Clay for Windows

Clay includes WiX-based installer packaging. Building the solution in the **Release** configuration also builds the installer for the selected platform, either from Visual Studio (Build Solution) or from the command line:

```powershell
dotnet build Installer\Clay.Bundle.wixproj -c Release -p:Platform=x64
```

This creates:

- `ClaySetup.exe` (standalone bootstrapper EXE) in `dist\installer\<platform>\` (for example `dist\installer\x64\`)
- `ClayInstaller.msi` (MSI package) in `Installer\bin\<platform>\Release\`

The Release build publishes the app into `publish\win-<arch>` first and packages that folder, so no separate publish step is needed. Debug builds skip the installer.

Installers are provided for x64 and ARM64; x86 is available as a portable build. The MSI installs Clay to `%ProgramFiles%\Clay` and creates Start Menu/Desktop shortcuts.

## Requirements to Edit

- Windows 10 version 1809 or later, or Windows 11
- .NET 10 SDK
- Visual Studio with the Windows application development workload, if building from the IDE
- To load the installer projects in Visual Studio: the [HeatWave for VS2022](https://marketplace.visualstudio.com/) extension (WiX v4 is restored from NuGet, so command-line builds need nothing extra)

## Build and run

From the repository root:

```powershell
dotnet run --project . -p:Platform=x64
```

Alternatively, open `ClayCalculator.slnx` or `Clay.UI.csproj` in Visual Studio, select `x64`, `x86`, or `ARM64`, and press F5. The application is unpackaged and self-contained, so it does not require a separate Windows App SDK runtime installation.

## Tests

```powershell
dotnet test Tests\Clay.Tests      # engine and app-logic tests
dotnet test Tests\Clay.UITests    # UI smoke tests; builds and launches Clay.exe
```

The UI tests open real Clay windows, so run them on an interactive desktop and avoid using the mouse or keyboard while they run. Each test uses a temporary data folder. Set `CLAY_EXE` to test a different build of `Clay.exe`. The UI tests skip themselves in CI (when the `CI` environment variable is set) and when there is no interactive desktop; they're also tagged `Category=UI`, so `--filter "Category!=UI"` excludes them.

## Versioning

The version is defined in one place: `VersionPrefix` in `Directory.Build.props`. The assembly and file versions, the MSI, the setup EXE, and the version shown in the title bar all come from it. To release a new version, change that value, update [CHANGELOG.md](CHANGELOG.md), and run the Release workflow.

## Portable builds

Clay can be distributed as a self-contained portable folder. The folder includes `Clay.exe` and the native/runtime files required by WinUI 3; copy the complete folder to another compatible Windows machine rather than copying only the executable.

From the repository root, publish an x64 build with:

```powershell
dotnet publish .\Clay.UI.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish\win-x64
```

For other architectures, use `-p:Platform=x86 -r win-x86` or `-p:Platform=ARM64 -r win-arm64` (and a matching output folder). Run `Clay.exe` from the complete output folder. These builds are self-contained and do not require the .NET runtime or a separately installed Windows App SDK runtime.

## GitHub release publishing

A GitHub Actions workflow (`.github/workflows/release.yml`) builds portable archives for x64, x86, and ARM64 and installers for x64 and ARM64, then creates a GitHub release and tag. Release notes come from the matching version section of [CHANGELOG.md](CHANGELOG.md).

- Run the workflow manually with **workflow_dispatch** after updating the version and changelog.

## Project structure

- `Engine/` — standalone `Clay.Engine` (`net10.0`) class library with lexer, parser, values, units, and evaluation logic
- `Clay.UI.csproj` / `MainWindow.xaml(.cs)` — WinUI front-end, persistence, and exchange-rate refresh
- `Tests/Clay.Tests/` — xUnit test suite including ported Qalculate `.batch` tests
- `Tests/Clay.UITests/` — FlaUI smoke tests that launch `Clay.exe` and drive it through UI Automation
- `Installer/` — WiX projects (`Clay.Installer.wixproj` for the MSI, `Clay.Bundle.wixproj` for the setup EXE)
- `Directory.Build.props` — shared version number for the app and installers
- `Icon/icon.ico` and `Icon/icon512.png` — exported application icons

Editable icon design files are intentionally excluded from the repository. The exported icons are included because they are required by the application.

The calculator engine has no WinUI dependency, so `Tests/Clay.Tests` (`net10.0`) tests it through a plain project reference.

## Data and network behavior

Documents autosave to `%LOCALAPPDATA%\Clay\document.txt`. Open and Save read and write plain text files; Save writes to the current file, while Save as chooses a new file. Display settings persist in `%LOCALAPPDATA%\Clay\settings.json`.

On startup, Clay requests current exchange rates from `https://open.er-api.com/v6/latest/USD`. The status line identifies live rates, cached rates, or built-in approximate fallback rates and reports refresh failures. Successful rates are cached in `%LOCALAPPDATA%\Clay\rates.json` for offline use. Unexpected application errors are logged to `%LOCALAPPDATA%\Clay\errors.log`.

Set the `CLAY_DATA_DIR` environment variable to store the autosave, settings, rate cache, and error log in a different folder. The UI tests use this so they never touch your own documents.

Unknown variable names are reported as errors, except single lowercase letters such as `x` and `y`, which stay symbolic until assigned (`x + 1` shows `x + 1`). Keywords, constants, function names, scale names, and units are reserved and cannot be assigned as variables; this includes common single letters that are unit symbols or constants, such as `m`, `s`, `g`, `h`, `d`, `l`, `c`, and `e`. `what is` is a supported shortcut.

Block `total` and `average` include values in lists and reject incompatible units. Adding or subtracting two temperatures treats the right operand as a temperature difference in its unit and expresses the result in the left operand's unit. In `sum` and `avg`, mixed percentages are fractions (for example, `sum(100, 10%)` is `100.1`); all-percent inputs preserve percentage display.

Temperature values in `sum` and `avg` are converted as absolute temperatures to the first temperature unit before aggregation, matching block aggregates.

Use `mod` for remainders with variables or parenthesized expressions (for example, `20 mod (3)`); `%` before those expressions remains a percentage followed by implicit multiplication. A numeric right operand can also use `%`, as in `5 % 3`.

## License

Clay is licensed under the GNU General Public License, version 3. See [LICENSE](LICENSE).