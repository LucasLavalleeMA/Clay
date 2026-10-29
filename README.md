# Clay

Clay is a natural-language calculator for Windows, built with WinUI 3 and .NET 8. Type expressions on the left and see one result per line on the right.

See the [changelog](CHANGELOG.md) for the current version and release history.

## Features

- Arithmetic, percentages, variables, labels, and comments
- Units and conversions for length, mass, volume, time, temperature, data, speed, area, angle, energy, power, and pressure
- Currency conversion with built-in fallback rates and optional live rates from [open.er-api.com](https://open.er-api.com/)
- Decimal, binary, and hexadecimal number bases
- Common mathematical functions and aggregates such as `sum`, `average`, and `prev`
- Plain-text open, save, and autosave support

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
```

## Requirements

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

## MSI installer and setup EXE

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

Documents autosave to `%LOCALAPPDATA%\Clay\document.txt`. Open and Save read and write plain text files. On startup, Clay requests current exchange rates from `https://open.er-api.com/v6/latest/USD`; if the request fails, built-in approximate rates are used.

## License

Clay is licensed under the GNU General Public License, version 3. See [LICENSE](LICENSE).