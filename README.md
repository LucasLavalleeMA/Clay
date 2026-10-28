# Clay

Clay is a Numi-style natural-language calculator for Windows, built with WinUI 3 and .NET 8. Type expressions on the left and see one result per line on the right.

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

## Build and run

From the repository root:

```powershell
dotnet run --project . -p:Platform=x64
```

Alternatively, open `Clay.csproj` in Visual Studio, select `x64`, `x86`, or `ARM64`, and press F5. The application is unpackaged and self-contained, so it does not require a separate Windows App SDK runtime installation.

## Project structure

- `Engine/` — calculator lexer, parser, values, units, and evaluation logic
- `MainWindow.xaml(.cs)` — WinUI interface, persistence, and exchange-rate refresh
- `Icon/icon.ico` and `Icon/icon512.png` — exported application icons

Editable icon design files are intentionally excluded from the repository. The exported icons are included because they are required by the application.

The calculator engine has no WinUI dependency and can be tested from a plain `net8.0` test project by linking the files in `Engine/`.

## Data and network behavior

Documents autosave to `%LOCALAPPDATA%\Clay\document.txt`. Open and Save read and write plain text files. On startup, Clay requests current exchange rates from `https://open.er-api.com/v6/latest/USD`; if the request fails, built-in approximate rates are used.

## License

Clay is licensed under the GNU General Public License, version 3. See [LICENSE](LICENSE).
