# Changelog

All notable changes to Clay are documented here.

## [1.0.1] - 2026-10-29

### Added

- WiX-based installer packaging: an MSI package and a `ClaySetup.exe` bootstrapper that installs Clay to `%ProgramFiles%\Clay` with Start Menu and Desktop shortcuts
- Version number shown in the title bar

### Changed

- The app version is now defined once in `Directory.Build.props` and shared by the application, the MSI, and the setup EXE
- Building the solution in Release now also builds the installer
- Installer output is grouped under `dist\installer\<platform>\`, and the `.wixpdb` is no longer generated

### Fixed

- The word `is` now correctly converts to the `=` symbol

## [1.0.0] - Initial release

Clay's first release includes:

- Natural-language arithmetic with percentages, variables, labels, comments, and aggregates
- Unit conversions across length, mass, volume, time, temperature, data, speed, area, angle, energy, power, and pressure
- Currency conversion with live exchange-rate refresh and built-in offline fallback rates
- Decimal, binary, and hexadecimal number support
- Mathematical functions including trigonometry, logarithms, rounding, powers, and factorials
- Plain-text open, save, and local autosave support
- Windows desktop UI built with WinUI 3 and .NET 8

[1.0.1]: https://github.com/LucasLavalleeMA/Clay/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/LucasLavalleeMA/Clay/releases/tag/v1.0.0