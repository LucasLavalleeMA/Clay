# Clay

Clay is a natural-language calculator for Windows, built with WinUI 3 and .NET 8. Type expressions on the left and see one result per line on the right.

See the [changelog](CHANGELOG.md) for the current version and release history.

## Features

- Arithmetic, percentages, variables, labels, and comments
- Units and conversions for length, mass, volume, time, temperature, data, speed, area, angle, energy, power, and pressure
- Unit arithmetic that combines and cancels units, such as `5 m/s * 4 s` (`20 m`) and `2 kW * 3 h` (`6 kWh`)
- Optional physics units (force, frequency, current, voltage, and resistance)
- Currency conversion with built-in fallback rates and optional live rates from [open.er-api.com](https://open.er-api.com/)
- Decimal, binary, octal, and hexadecimal number bases
- Common mathematical functions and aggregates such as `sum`, `average`, and `prev`
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

This creates, under `dist\installer\<platform>\` (for example `dist\installer\x64\`):

- `ClayInstaller.msi` (MSI package)
- `ClaySetup.exe` (standalone bootstrapper EXE)

The Release build publishes the app into `publish\win-<arch>` first and packages that folder, so no separate publish step is needed. Debug builds skip the installer.

`.\publish-installer.ps1` remains available for scripted builds.

The MSI installs Clay to `%ProgramFiles%\Clay` and creates Start Menu/Desktop shortcuts.

## Requirements to Edit

- Windows 10 version 1809 or later, or Windows 11
- .NET 8 SDK
- Visual Studio with the Windows application development workload, if building from the IDE
- To load the installer projects in Visual Studio: the [HeatWave for VS2022](https://marketplace.visualstudio.com/) extension (WiX v4 is restored from NuGet, so command-line builds need nothing extra)

## Build and run

From the repository root:

```powershell
dotnet run --project . -p:Platform=x64
```

Alternatively, open `ClayCalculator.slnx` or `Clay.csproj` in Visual Studio, select `x64`, `x86`, or `ARM64`, and press F5. The application is unpackaged and self-contained, so it does not require a separate Windows App SDK runtime installation.

## Versioning

The version is defined in one place: `VersionPrefix` in `Directory.Build.props`. The assembly and file versions, the MSI, the setup EXE, and the version shown in the title bar all come from it. To release a new version, change that value, update [CHANGELOG.md](CHANGELOG.md), and tag the release.

## Portable builds

Clay can be distributed as a self-contained portable folder. The folder includes `Clay.exe` and the native/runtime files required by WinUI 3; copy the complete folder to another compatible Windows machine rather than copying only the executable.

From PowerShell, publish the default x64 build with:

```powershell
.\publish-portable.ps1
```

To publish another supported architecture:

```powershell
.\publish-portable.ps1 -Architecture x86
.\publish-portable.ps1 -Architecture arm64
```

The output is written to `publish\win-x64`, `publish\win-x86`, or `publish\win-arm64`. Run `Clay.exe` from the complete output folder. These builds are self-contained and do not require the .NET 8 runtime or a separately installed Windows App SDK runtime.

## GitHub release publishing

A GitHub Actions workflow (`.github/workflows/release-installer.yml`) builds and uploads installer artifacts to Releases.

- Push a tag like `v1.0.1` to build and publish `ClayInstaller.msi` and `ClaySetup.exe`
- Or run the workflow manually with **workflow_dispatch**

## Project structure

- `Engine/` — calculator lexer, parser, values, units, and evaluation logic
- `MainWindow.xaml(.cs)` — WinUI interface, persistence, and exchange-rate refresh
- `Installer/` — WiX projects (`Clay.Installer.wixproj` for the MSI, `Clay.Bundle.wixproj` for the setup EXE)
- `Directory.Build.props` — shared version number for the app and installers
- `Icon/icon.ico` and `Icon/icon512.png` — exported application icons

Editable icon design files are intentionally excluded from the repository. The exported icons are included because they are required by the application.

The calculator engine has no WinUI dependency and can be tested from a plain `net8.0` test project by linking the files in `Engine/`.

## Data and network behavior

Documents autosave to `%LOCALAPPDATA%\Clay\document.txt`. Open and Save read and write plain text files; Save writes to the current file, while Save as chooses a new file. Display settings persist in `%LOCALAPPDATA%\Clay\settings.json`.

On startup, Clay requests current exchange rates from `https://open.er-api.com/v6/latest/USD`. The status line identifies live rates, cached rates, or built-in approximate fallback rates and reports refresh failures. Successful rates are cached in `%LOCALAPPDATA%\Clay\rates.json` for offline use. Unexpected application errors are logged to `%LOCALAPPDATA%\Clay\errors.log`.

Unknown variable names are reported as errors. Keywords, constants, function names, scale names, and units are reserved and cannot be assigned as variables. `what is` is a supported shortcut.

Block `total` and `average` include values in lists and reject incompatible units. Adding or subtracting two temperatures treats the right operand as a temperature difference in its unit and expresses the result in the left operand's unit. In `sum` and `avg`, mixed percentages are fractions (for example, `sum(100, 10%)` is `100.1`); all-percent inputs preserve percentage display.

Temperature values in `sum` and `avg` are converted as absolute temperatures to the first temperature unit before aggregation, matching block aggregates.

Use `mod` for remainders with variables or parenthesized expressions (for example, `20 mod (3)`); `%` before those expressions remains a percentage followed by implicit multiplication. A numeric right operand can also use `%`, as in `5 % 3`.

## License

Clay is licensed under the GNU General Public License, version 3. See [LICENSE](LICENSE).