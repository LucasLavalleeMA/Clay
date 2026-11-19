# Unit arithmetic edge cases

Inputs probed while reviewing compound unit arithmetic (#2), with the results `main` gives. Use it as a
quick reference for intended behavior and as a checklist when changing `Engine/Units.cs`,
`Engine/Value.cs` or the unit parsing in `Engine/Lexer.cs` and `Engine/Calculator.cs`. Most rows are also
covered by `Tests/Clay.Tests/CompoundUnitTests.cs`.

- Each input is evaluated on its own; `↵` separates lines, and the result is the last line's.
- Sections use physics units off unless the heading says otherwise.
- Values below 1e-6 can display as `0` (the formatter's rounding): `(1e-36 ha)^9.5` is really `1e-304 m¹⁹`.
- The limits behind the "Numeric range" rows are described at the top of the arithmetic section in
  `Engine/Value.cs` and in `docs/plans/compound-units.md`.

## Compound arithmetic

| Input | Result |
| --- | --- |
| `5 m/s * 4 s` | `20 m` |
| `60 miles per hour * 2 h` | `120 mi` |
| `$15 per hour * 40 h` | `$600.00` |
| `10 kWh * $0.12/kWh` | `$1.20` |
| `5 km / 2 h` | `2.5 km/h` |
| `100 km / 8 l` | `12.5 km/l` |
| `5 m/s in km/h` | `18 km/h` |
| `9.81 m/s^2 * 2 s` | `19.62 m/s` |
| `5 m * 1 ft` | `1.524 m²` |
| `5 m·ft` | `5 m·ft` |
| `5 Wh/km` | `5 Wh/km` |
| `1 acre / 100 ft` | `0.01 acre/ft` |
| `5 J/s` | `5 W` |

## Powers and roots

| Input | Result |
| --- | --- |
| `5 m^2` | `5 m²` |
| `5 m ^ 2` | `5 m²` |
| `5 m/s ^ 2` | `5 m/s²` |
| `(5 m)^2` | `25 m²` |
| `5 m^(2)` | `25 m²` |
| `x = 2` ↵ `5 m^x` | `25 m²` |
| `5 m / s^(2)` | `5 m/s²` |
| `5 kg per m^(1+1)` | `5 kg/m²` |
| `2 * m⁰^(2)` | `2` |
| `sqrt(9 m²)` | `3 m` |
| `sqrt(1 ha)` | `100 m` |
| `sqrt(100 J/kg)` | `10 m/s` |
| `cbrt(-8 m³)` | `-2 m` |
| `9 m²^0.5` | Error: Unit powers must be whole numbers; use parentheses to raise the whole quantity |
| `1 m/s^2.5` | Error: Unit powers must be whole numbers; use parentheses to raise the whole quantity |
| `5 m^20` | `5 m²⁰` |
| `5 m^21` | Error: Unit powers can't be larger than 20 |
| `5 m^20 * 5 m` | Error: Unit powers can't be larger than 20 |
| `5 m^1e10` | Error: Unit powers can't be larger than 20 |
| `5 s^65536^65536` | Error: Unit powers can't be larger than 20 |
| `1 m⁻²¹⁴⁷⁴⁸³⁶⁴⁸` | Error: Unknown variable 'm⁻²¹⁴⁷⁴⁸³⁶⁴⁸' |

## Parsing: scale words, inches, per, currency

| Input | Result |
| --- | --- |
| `5k/s` | `5,000 s⁻¹` |
| `$5M/yr` | `$5,000,000/yr` |
| `5 in per s` | `5 in/s` |
| `1 m in cm per s^0` | `100 cm` |
| `1 s^-1 in m^0 per s` | `1 s⁻¹` |
| `1 s^-1 in m per m per s` | `1 s⁻¹` |
| `5 kWh/$` | `5 kWh/USD` |
| `5 kWh / $2` | `2.5 kWh/USD` |
| `5 $^2` | `5 USD²` |
| `10 kWh/USD in kWh/$` | `10 kWh/USD` |
| `m⁰ = 3` | Error: 'm⁰' is a reserved name |

## Rejected combinations

| Input | Result |
| --- | --- |
| `20 °C * 2 m` | Error: Can't multiply °C by m |
| `2 m / 20 °C` | Error: Can't divide m by °C |
| `(2 °C)^2` | Error: Powers of °C aren't supported |
| `2 m + 3 s` | Error: Can't mix s and m |
| `5 m/s in km` | Error: Can't mix m/s and km |
| `sqrt(2 m)` | Error: Can't raise m to that power |
| `(3 m)^(2 s)` | Error: An exponent can't have a unit |
| `5 m / s^(1 s)` | Error: An exponent can't have a unit |
| `10^(3 km)` | Error: An exponent can't have a unit |

## Same-dimension division

| Input | Result |
| --- | --- |
| `10 km/h / 5 km/h` | `2` |
| `1 kWh / 1 J` | `3,600,000` |
| `20 °C / 10 °C` | `2` |
| `68 °F / 20 °C` | `1` |
| `0 m / 5 m` | `0` |

## Numeric range

| Input | Result |
| --- | --- |
| `1 mi^20 in km^20` | `13,582.437148 km²⁰` |
| `1e10 PB^20 in TB^20` | `1E+70 TB²⁰` |
| `1e308 bit^20 / 1e-9 PB^20` | `0.086736` |
| `1e-300 PB^20 / 1e-300 B^20` | `1E+300` |
| `1e-300 PB^20 / 1 bit^20` | `1.152921E+18` |
| `1e200 mm^20 * 1e200 km^-19` | `1E+286 mm` |
| `sqrt(1e300 PB^19·B)` | `3.162278E+292 B¹⁰` |
| `(1e-36 ha)^9.5` | `0 m¹⁹` |
| `1 PB^20 / 1 bit^20` | Error: That result is too large or too small |
| `1e200 m * 1e200 m` | Error: That result is too large or too small |
| `1e-200 m * 1e-200 m` | Error: That result is too large or too small |
| `(1e200 m)^2` | Error: That result is too large or too small |
| `1e200 m * 1e200` | Error: That result is too large or too small |
| `1e-200 m / 1e200` | Error: That result is too large or too small |
| `1e200 * 1e200` | `∞` |
| `0 PB^20 * 1 bit^-20` | `0` |
| `(0 m)^2` | `0 m²` |
| `(0 m)^-1` | Error: Division by zero |
| `5 µm^20/PB^20` | Error: That unit is too large or too small |
| `1e-300 PB^20·TB^20 / 1 B^20·KB^20` | Error: Unit powers can't be larger than 20 |
| `1 km + 1e-320 m` | `1 km` |

## Physics symbols with physics units off

| Input | Result |
| --- | --- |
| `N = 5` ↵ `N * 2` | `10` |
| `V = 3` ↵ `2 V` | `6` |
| `5 kN` | `5 kn` |

## Physics units on

| Input | Result |
| --- | --- |
| `5 N * 2 m` | `10 J` |
| `2 kg * 3 m/s^2` | `6 N` |
| `12 V / 2 Ω` | `6 A` |
| `2 A * 3 V` | `6 W` |
| `100 W / 2 V` | `50 A` |
| `60 Hz * 2 s` | `120` |
| `10 J / 2 N` | `5 m` |
| `10 N / 2 m²` | `5 Pa` |
| `1 kN in N` | `1,000 N` |
| `1 kg·m/s² in N` | `1 N` |
| `5 kN` | `5 kN` |
| `5 k/s` | `5,000 Hz` |
| `v = 3` ↵ `V * 2` | (no result) |
