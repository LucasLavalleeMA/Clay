using System;
using System.Collections.Generic;
using Point85.Caliper.UnitOfMeasure;

namespace Clay.Engine
{
	/// <summary>
	/// Name/symbol -> CaliperSharp UnitOfMeasure lookup. Units come straight from
	/// MeasurementSystem (Unit enum, or Prefix + base unit); nothing is hand-defined.
	/// </summary>
	public static class UnitTable
	{
		private static readonly Dictionary<string, UnitOfMeasure> Exact = new(StringComparer.Ordinal);
		private static readonly Dictionary<string, UnitOfMeasure> Loose = new(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, UnitOfMeasure> Fiat = new(StringComparer.OrdinalIgnoreCase);

		private static readonly MeasurementSystem Sys = MeasurementSystem.GetSystem();

		public static UnitOfMeasure? Lookup(string name) =>
			Exact.TryGetValue(name, out var u) ? u : Loose.GetValueOrDefault(name);

		public static bool IsCurrency(UnitOfMeasure u) => Fiat.ContainsValue(u);

		public static UnitOfMeasure Radian => Sys.GetUOM(Unit.RADIAN);
		public static UnitOfMeasure Inch => Sys.GetUOM(Unit.INCH);

		// Predefined unit from the Unit enum.
		private static void Def(Unit unit, params string[] names) =>
			Register(Sys.GetUOM(unit), names);

		// Prefixed unit (kilo + metre, milli + gram, ...) built by Caliper itself.
		private static void Def(Prefix prefix, Unit baseUnit, params string[] names) =>
	Register(Sys.GetUOM(prefix, baseUnit), names);

		private static void Register(UnitOfMeasure unit, params string[] names)
		{
			foreach (var n in names)
			{
				Exact.TryAdd(n, unit);
				Loose.TryAdd(n, unit);
			}
		}

		static UnitTable()
		{
			// Length
			Def(Unit.METRE, "m", "meter", "meters", "metre", "metres");
			Def(Prefix.KILO, Unit.METRE, "km", "kilometer", "kilometers", "kilometre", "kilometres");
			Def(Prefix.CENTI, Unit.METRE, "cm", "centimeter", "centimeters", "centimetre", "centimetres");
			Def(Prefix.MILLI, Unit.METRE, "mm", "millimeter", "millimeters", "millimetre", "millimetres");
			Def(Prefix.MICRO, Unit.METRE, "µm", "um", "micrometer", "micrometers");
			Def(Unit.MILE, "mi", "mile", "miles");
			Def(Unit.YARD, "yd", "yard", "yards");
			Def(Unit.FOOT, "ft", "foot", "feet");
			Def(Unit.INCH, "in", "inch", "inches");
			Def(Unit.NAUTICAL_MILE, "nmi", "nauticalmile", "nauticalmiles");

			// Mass
			Def(Unit.GRAM, "g", "gram", "grams");
			Def(Unit.KILOGRAM, "kg", "kilogram", "kilograms", "kilo", "kilos");
			Def(Prefix.MILLI, Unit.GRAM, "mg", "milligram", "milligrams");
			Def(Unit.POUND_MASS, "lb", "lbs", "pound", "pounds");
			Def(Unit.OUNCE, "oz", "ounce", "ounces");
			Def(Unit.TONNE, "t", "tonne", "tonnes");

			// Volume
			Def(Unit.LITRE, "l", "liter", "liters", "litre", "litres");
			Def(Prefix.MILLI, Unit.LITRE, "ml", "milliliter", "milliliters", "millilitre", "millilitres");
			Def(Unit.US_GALLON, "gal", "gallon", "gallons");
			Def(Unit.US_QUART, "qt", "quart", "quarts");
			Def(Unit.US_PINT, "pt", "pint", "pints");
			Def(Unit.US_CUP, "cup", "cups");
			Def(Unit.US_FLUID_OUNCE, "fl oz", "floz");
			Def(Unit.US_TABLESPOON, "tbsp", "tablespoon", "tablespoons");
			Def(Unit.US_TEASPOON, "tsp", "teaspoon", "teaspoons");
			Def(Unit.CUBIC_METRE, "m³", "m3");

			// Time
			Def(Prefix.MILLI, Unit.SECOND, "ms", "millisecond", "milliseconds");
			Def(Unit.SECOND, "s", "sec", "secs", "second", "seconds");
			Def(Unit.MINUTE, "min", "mins", "minute", "minutes");
			Def(Unit.HOUR, "h", "hr", "hrs", "hour", "hours");
			Def(Unit.DAY, "d", "day", "days");
			Def(Unit.WEEK, "wk", "week", "weeks");
			Def(Unit.JULIAN_YEAR, "yr", "yrs", "year", "years");

			// Temperature
			Def(Unit.CELSIUS, "°C", "C", "celsius", "degC");
			Def(Unit.FAHRENHEIT, "°F", "F", "fahrenheit", "degF");
			Def(Unit.KELVIN, "K", "kelvin");

			// Data
			Def(Unit.BIT, "bit", "bits");
			Def(Unit.BYTE, "B", "byte", "bytes");
			Def(Prefix.KILO, Unit.BYTE, "KB", "kilobyte", "kilobytes");
			Def(Prefix.MEGA, Unit.BYTE, "MB", "megabyte", "megabytes");

			// Speed
			Def(Unit.METRE_PER_SEC, "m/s", "mps");
			Register(Sys.CreateQuotientUOM(Sys.GetUOM(Prefix.KILO, Unit.METRE), Sys.GetUOM(Unit.HOUR)), "km/h", "kph", "kmh");
			Def(Unit.MILES_PER_HOUR, "mph", "mi/h");
			Def(Unit.KNOT, "kn", "knot", "knots");

			// Area
			Def(Unit.SQUARE_METRE, "m²", "m2", "sqm");
			Def(Unit.HECTARE, "ha", "hectare", "hectares");
			Def(Unit.ACRE, "acre", "acres");
			Def(Unit.SQUARE_FOOT, "ft²", "ft2", "sqft");

			// Angle
			Def(Unit.RADIAN, "rad", "radian", "radians");
			Def(Unit.DEGREE, "°", "deg", "degree", "degrees");

			// Energy & Power
			Def(Unit.JOULE, "J", "joule", "joules");
			Def(Prefix.KILO, Unit.JOULE, "kJ");
			Def(Unit.CALORIE, "cal", "calorie", "calories");
			Def(Prefix.KILO, Unit.CALORIE, "kcal");
			Def(Unit.WATT_HOUR, "Wh");
			Def(Prefix.KILO, Unit.WATT_HOUR, "kWh");
			Def(Unit.WATT, "W", "watt", "watts");
			Def(Prefix.KILO, Unit.WATT, "kW");
			Def(Unit.HP, "hp", "horsepower");

			// Pressure
			Def(Unit.PASCAL, "Pa");
			Def(Prefix.KILO, Unit.PASCAL, "kPa");
			Def(Unit.BAR, "bar");
			Def(Unit.PSI, "psi");
			Def(Unit.ATMOSPHERE, "atm");

			// Currencies
			Cur(Unit.US_DOLLAR, "USD", "$", "dollar", "dollars");
			Cur(Unit.EURO, "EUR", "€", "euro", "euros");
			Cur(Unit.YUAN, "CNY", "yuan", "rmb");
		}

		private static void Cur(Unit unit, string code, params string[] extra)
		{
			var uom = Sys.GetUOM(unit);
			Fiat[code] = uom;
			Register(uom, [code, .. extra]);
		}
	}
}