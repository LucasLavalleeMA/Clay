# Compound unit arithmetic

Goal: unit-aware multiplication, division, and powers, so that `5 m/s * 4 s` is `20 m`,
`100 km / 2 h` is `50 km/h`, and `2 kW * 3 h` is `6 kWh`.

## Decisions

- The unit system stays in-house (`Engine/`); no external units library such as CaliperSharp.
- When both operands have units of the same kind, the left (first declared) unit wins and the right
  operand is converted to it: `2 m * 3 ft` is `1.8288 m²`.
- Extra physics units (force, frequency, electrical) are opt-in through a **Physics units** setting.
  Compound arithmetic itself is always on.

## Design

### Dimensions (`Engine/Units.cs`)

- `Dim` becomes a value type holding integer exponents for length, mass, time, temperature, data,
  angle, currency, and electric current. The former categories are presets built from those
  exponents, for example `Speed = Length / Time` and `Energy = Mass · Length² / Time²`.
- Factors use one coherent base per dimension: meter, kilogram, second, kelvin, byte, radian, USD,
  ampere. Mass moves from gram to kilogram and volume from liter to cubic meter; conversions between
  existing units are unchanged.

### Units and terms

- A `Unit` is either a leaf (its own factor and dimension), a named derived unit with a definition
  (`km/h = km · h⁻¹`, `m² = m²`, `kWh = kW · h`), or a generated compound (`m/s²`).
- Every unit expands to leaf terms `(unit, power)`. A compound's factor and dimension are computed
  from its terms, so currency compounds follow live exchange rates.
- `UnitTable.Compose` normalizes terms back into a unit: cancelled terms disappear, a single leaf is
  returned as itself, a match against a named unit's definition or an alias (`J/s` → `W`) returns that
  unit, and anything else becomes a generated compound with a symbol such as `kg·m/s²` or `min⁻¹`.

### Arithmetic (`Engine/Value.cs`)

- Multiplying or dividing merges terms. A right-hand term is converted to a left-hand term when its
  dimension is an integer power of that term's dimension (`s` against `h`, `s` against `Hz`,
  `ha` against `m`), so terms cancel in the left operand's units. Across different powers this only
  happens when the power grows or cancels, so `l/km` and `km/l` stay rates.
- Dividing two values of the same dimension still gives a plain number, and everything cancelling
  gives a plain number.
- Powers and roots apply to unit exponents (`(3 m)^2 = 9 m²`, `sqrt(9 m²) = 3 m`); roots that leave
  fractional exponents are errors.
- Absolute temperatures (°C, °F) cannot be multiplied, divided by other units, or raised to powers.
- Exponents can't have units. Units are limited to sizes from 1e-300 to 1e300 of their base units, so
  unit factors are always finite. Multiplying, dividing, or raising units goes through logarithms when an
  intermediate would overflow, a zero coefficient stays zero, and a result that would itself be `∞` or
  `0` is an error. This includes scaling a unit value by a plain number (`1e200 m * 1e200`), and zero
  with a unit to a negative power (`(0 m)^-1`) is division by zero.

### Parsing (`Engine/Lexer.cs`, `Engine/Calculator.cs`)

- Unit expressions without spaces are single tokens: `m/s`, `m/s²`, `kg·m/s²`, `N·m`.
- `^n` after a unit applies to the unit, with or without spaces: `5 m^2` is `5 m²` and `5 m/s ^ 2`
  is `5 m/s²`. Unit powers are whole numbers up to 20, which keeps any single powered unit within
  about 1e±300 (unit factors run from 1e-6 to 1e15).
- Scale words don't start a unit expression, so `5k/s` is `5,000 s⁻¹`, not kelvin per second.
- A bare unit after `*`, `/`, or `per` means one of that unit: `5 m / s`, `60 miles per hour`,
  `$15 per hour`.
- Conversion targets accept the same forms: `in km/h`, `in ft/s^2`, `in miles per hour`.

### Physics units option

- `Calculator.PhysicsUnits` enables N, kN, lbf, Hz, kHz, MHz, GHz, A, mA, V, mV, kV, and Ω, plus
  naming results with them (`kg·m/s²` → `N`, `N·m` → `J`, `V·A` → `W`).
- Short symbols are case-sensitive so `A` doesn't match the word `a` and `C` stays Celsius.
- While the option is off, those names stay free for variables.
- The option is a **Physics units** toggle in the Settings menu and persists in `settings.json`
  (missing in older files means off).

## Progress

- [x] Create the `feat/compound-units` branch and this plan
- [x] Dimension vector and coherent base factors
- [x] Unit terms, definitions, aliases, and `Compose`
- [x] Multiplication, division, powers, and roots
- [x] Unit expressions in the lexer and parser (`/`, `·`, superscripts, `^n`, `per`, bare units)
- [x] Compound conversion targets
- [x] Compound formatting, including currency rates (`$15/h`)
- [x] Physics units table, scoping, and reserved names
- [x] Physics units setting in the UI and `settings.json`
- [x] Tests (`Tests/Clay.Tests/CompoundUnitTests.cs`, `AppDataTests.cs`)
- [x] README and CHANGELOG
- [ ] Manual check of the Settings toggle in the running app
- [ ] Owner review

## Known limitations

- Terms only cancel when one dimension is a whole power (±1 to ±3) of the other, so
  `10 J / 2 N` stays `J/N` unless physics units are on (then it is `5 m`).
- Across different powers, terms only join when the power grows or cancels, so rates stay as written:
  `5 l / 100 km` is `0.05 l/km` and `1 acre / 100 ft` is `0.01 acre/ft` (use `in ft` for `435.6 ft`).
- Typing a compound that has a name shows the name: `10 J/s` displays as `10 W`.
- With physics units off, `kN` still matches knots (`kn`) through case-insensitive lookup.
- Compound currency rates use the display decimals instead of the currency's fixed decimals
  (`$15/h`, `$0.1234/kWh`).
