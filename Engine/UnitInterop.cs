using Clay.Engine;
using Point85.Caliper.UnitOfMeasure;
using System.Globalization;

public static class SharedUnits
{
	public static readonly Point85.Caliper.UnitOfMeasure.UnitOfMeasure NoUnit =
		MeasurementSystem.GetSystem().GetUOM(Unit.ONE);
	public static readonly Point85.Caliper.UnitOfMeasure.UnitOfMeasure PercentUnit =
	MeasurementSystem.GetSystem().GetUOM(Unit.PERCENT);
}

public class Value
{
	public IReadOnlyList<double> Amounts { get; }
	public Point85.Caliper.UnitOfMeasure.UnitOfMeasure UOM { get; }

	public bool IsPercent => UOM == SharedUnits.PercentUnit;

	/// <summary>True for a real physical/currency unit (not unitless, not percent).</summary>
	public bool HasUnit => UOM != null && !ReferenceEquals(UOM, SharedUnits.NoUnit) && !IsPercent;

	/// <summary>Optional display override, e.g. "0xFF" for hex/bin/oct results.</summary>
	public string? Text { get; set; }

	// Note: If your version uses a different name, it might be Unit.PERCENTAGE or Unit.ONE scaled


	// 1. Your original constructor (for creating values from raw Units)
	public Value(IEnumerable<double> amounts, Unit unit)
	{
		Amounts = amounts?.ToList() ?? throw new ArgumentNullException(nameof(amounts));
		UOM = MeasurementSystem.GetSystem().GetUOM(unit);
	}
	public Value(object amount, Unit? unit = null)
	{
		// 1. Convert any incoming numeric type safely to a double
		double numericValue = Convert.ToDouble(amount, System.Globalization.CultureInfo.InvariantCulture);
		Amounts = new[] { numericValue };

		// 2. Resolve the unit if provided, otherwise default to NoUnit
		UOM = unit.HasValue
			? MeasurementSystem.GetSystem().GetUOM(unit.Value)
			: SharedUnits.NoUnit;
	}


	public Value(double amount, Point85.Caliper.UnitOfMeasure.UnitOfMeasure unitOfMeasure)
	{
		Amounts = new[] { amount };
		UOM = unitOfMeasure ?? throw new ArgumentNullException(nameof(unitOfMeasure));
	}

	// 2. ADD THIS OVERLOAD: For creating values from an existing UnitOfMeasure object
	public Value(IEnumerable<double> amounts, Point85.Caliper.UnitOfMeasure.UnitOfMeasure unitOfMeasure)
	{
		Amounts = amounts?.ToList() ?? throw new ArgumentNullException(nameof(amounts));
		UOM = unitOfMeasure ?? throw new ArgumentNullException(nameof(unitOfMeasure));
	}

	public Value(IEnumerable<double> amounts)
	{
		Amounts = amounts?.ToList() ?? throw new ArgumentNullException(nameof(amounts));
		UOM = SharedUnits.NoUnit;
	}

	public double Number => Amounts.Count == 1 ? Amounts[0] : throw new EvalException("Expected a single number, not a list");
	public bool IsArray => Amounts.Count > 1;

	public static Value List(IEnumerable<Value> elements)
	{
		if (elements == null) throw new ArgumentNullException(nameof(elements));

		// Unpack the underlying raw double sequences from each element in the collection
		var flattenedAmounts = elements.SelectMany(v => v.Amounts);

		var targetUnit = elements.FirstOrDefault()?.UOM ?? SharedUnits.NoUnit;

		return new Value(flattenedAmounts, targetUnit);
	}
	public double Frac => new Quantity(Amounts[0], UOM)
	.Convert(SharedUnits.NoUnit)
	.Amount;


	public IEnumerable<Quantity> ToQuantities()
	{
		return Amounts.Select(a => new Quantity(a, UOM));
	}

	public static Value Pct(double amount)
	{
		return new Value([amount], SharedUnits.PercentUnit);
	}

	// Vector factory method for a list of percentages
	public static Value Pct(IEnumerable<double> amounts)
	{
		return new Value(amounts, SharedUnits.PercentUnit);
	}
	public override string ToString()
	{
		// If the vector contains no items, return an empty bracket layout
		if (Amounts == null || Amounts.Count == 0)
			return "[]";

		// 1. If it's a single scalar value, format it cleanly with its unit
		if (Amounts.Count == 1)
		{
			return Formatter.Format(this, decimals: 6, group: false);
		}

		// 2. If it's a vector list, map each inner number to a standalone scalar Value
		// so that Formatter.Format can output them as a clean, unified array string.
		var formattedElements = Amounts.Select(amount =>
			Formatter.Format(new Value([amount], UOM), decimals: 6, group: false)
		);

		return "[" + string.Join(", ", formattedElements) + "]";
	}


}


public static class Ops
{
	// ------------------------------------------------------------ list helpers
	public static IEnumerable<Value> Flatten(Value v)
	{
		// If the wrapper value holds multiple scalar elements under Amounts,
		// unpack each individual double into its own standalone single-item Value container
		return v.Amounts.Select(amount => new Value([amount], v.UOM));
	}

	/// <summary>Applies f to every element inside v by elevating them to scalar Values.</summary>
	public static Value Map(Value v, Func<Value, Value> f)
	{
		// 1. Temporarily elevate each raw double into a standalone scalar Value
		var scalarValues = v.Amounts.Select(amount => new Value([amount], v.UOM));

		// 2. Apply the custom transformation function to each scalar Value
		var processedValues = scalarValues.Select(scalar => f(scalar));

		// 3. Flatten the evaluated results back down into a single double array
		var flatAmounts = processedValues.SelectMany(result => result.Amounts);

		// 4. Return the consolidated new vector container
		return new Value(flatAmounts, v.UOM);
	}


	/// <summary>Applies f pairwise; a scalar is paired with every item of a list.</summary>
	/// <summary>Applies f pairwise; a scalar is paired with every item of a list.</summary>
	public static Value Broadcast(Value a, Value b, Func<Value, Value, Value> f)
	{
		// Case 1: Both values are vectors/lists
		if (a.Amounts.Count > 1 && b.Amounts.Count > 1)
		{
			if (a.Amounts.Count != b.Amounts.Count)
				throw new EvalException($"Lists differ in length ({a.Amounts.Count} vs {b.Amounts.Count})");

			// Wrap each raw double item back into a single-element Value container with its unit
			var zippedResults = a.Amounts.Zip(b.Amounts, (x, y) =>
				f(new Value([x], a.UOM), new Value([y], b.UOM))
			);

			return Value.List(zippedResults);
		}

		// Case 2: Only 'a' is a vector; pair each item of 'a' with the scalar 'b'
		if (a.Amounts.Count > 1)
		{
			var mappedResults = a.Amounts.Select(x =>
				f(new Value([x], a.UOM), b)
			);

			return Value.List(mappedResults);
		}

		// Case 3: Only 'b' is a vector; pair the scalar 'a' with each item of 'b'
		if (b.Amounts.Count > 1)
		{
			var mappedResults = b.Amounts.Select(y =>
				f(a, new Value([y], b.UOM))
			);

			return Value.List(mappedResults);
		}

		// Case 4: Both are single scalar values
		return f(a, b);
	}


	/// <summary>Zero-based indexing; negative indexes count from the end.</summary>
	public static Value Index(Value list, Value index)
	{
		// 1. Verify the index parameter contains exactly ONE scalar number
		if (index.Amounts.Count != 1)
			throw new EvalException("Index must be a single number");

		double d = index.Frac;
		if (d != Math.Floor(d))
			throw new EvalException("Index must be a whole number");

		int n = list.Amounts.Count;
		if (Math.Abs(d) > n)
			throw new EvalException($"Index {d} is out of range (list has {n} items)");

		int i = (int)d;
		if (i < 0) i += n;

		if (i < 0 || i >= n)
			throw new EvalException($"Index {d} is out of range (list has {n} items)");

		// 2. Wrap the isolated element in brackets [ ] to pass it as an IEnumerable<double>
		return new Value([list.Amounts[i]], list.UOM);
	}


	// ------------------------------------------------------------------ units

	/// <summary>Expresses v in the target unit's terms. Unitless values are taken as already in it.</summary>
	// 1. Add this new overload that accepts a UnitOfMeasure object directly
	public static double In(Value v, Point85.Caliper.UnitOfMeasure.UnitOfMeasure targetUom)
	{
		if (v.UOM == null || ReferenceEquals(v.UOM, SharedUnits.NoUnit))
			return v.Frac;

		if (ReferenceEquals(v.UOM, targetUom))
			return v.Frac;

		try
		{
			return new Quantity(v.Frac, v.UOM).Convert(targetUom).Amount;
		}
		catch (Exception ex)
		{
			throw new EvalException($"Unit conversion mismatch: {ex.Message}" + ex);
		}
	}

	// 2. Update your existing enum-based method to route through the new overload
	public static double In(Value v, Unit target)
	{
		var targetUom = MeasurementSystem.GetSystem().GetUOM(target);
		return In(v, targetUom);
	}


	public static Value ConvertTo(Value v, Unit target) =>
		ConvertTo(v, MeasurementSystem.GetSystem().GetUOM(target));

	public static Value ConvertTo(Value v, Point85.Caliper.UnitOfMeasure.UnitOfMeasure targetUom)
	{
		try
		{
			// Map across all underlying array amounts. 
			// Caliper checks compatibility on the very first element block.
			var convertedAmounts = v.Amounts.Select(amount =>
				v.UOM == null || ReferenceEquals(v.UOM, SharedUnits.NoUnit)
					? amount
					: new Quantity(amount, v.UOM).Convert(targetUom).Amount
			).ToList();

			return new Value(convertedAmounts, targetUom);
		}
		catch (Exception ex)
		{
			throw new EvalException($"Dimension mismatch or conversion error: {ex.Message}" + ex);
		}
	}


	/// <summary>Gives unitless numbers (inside lists too) the unit; values that already have one are kept.</summary>
	public static Value AttachUnit(Value v, Unit unit)
	{
		// If it's already got a valid physical unit (not NoUnit), leave it as-is
		if (v.UOM != null && !ReferenceEquals(v.UOM, SharedUnits.NoUnit))
			return v;

		var targetUom = MeasurementSystem.GetSystem().GetUOM(unit);
		return new Value(v.Amounts, targetUom);
	}


	// ------------------------------------------------------------- arithmetic

	public static Value Add(Value a, Value b, int sign) =>
		Broadcast(a, b, (x, y) => AddScalar(x, y, sign));

	public static Value Mul(Value a, Value b) => Broadcast(a, b, MulScalar);
	public static Value Div(Value a, Value b) => Broadcast(a, b, DivScalar);
	public static Value Mod(Value a, Value b) => Broadcast(a, b, ModScalar);
	public static Value Pow(Value a, Value b) => Broadcast(a, b, PowScalar);
	public static Value Factorial(Value v) => Map(v, FactorialScalar);

	private static Value AddScalar(Value a, Value b, int sign)
	{
		// 1. Handle percentages (assuming IsPercent checks if UnitOfMeasure is a percentage unit)
		if (a.IsPercent && b.IsPercent)
			return Value.Pct(a.Frac + sign * b.Frac);

		if (b.IsPercent)
			return new Value([a.Frac * (1 + sign * b.Frac / 100)], a.UOM);

		// Helper checks to see if an element is unitless
		bool aUnitless = a.UOM == null || ReferenceEquals(a.UOM, SharedUnits.NoUnit);
		bool bUnitless = b.UOM == null || ReferenceEquals(b.UOM, SharedUnits.NoUnit);

		// 2. Both are unitless
		if (aUnitless && bUnitless)
			return new Value([a.Frac + sign * b.Frac], SharedUnits.NoUnit);

		// 3. One or both have physical units
		var targetUom = !aUnitless ? a.UOM : b.UOM;

		try
		{
			double aConverted = aUnitless ? a.Frac : new Quantity(a.Frac, a.UOM).Convert(targetUom).Amount;
			double bConverted = bUnitless ? b.Frac : new Quantity(b.Frac, b.UOM).Convert(targetUom).Amount;

			return new Value([aConverted + sign * bConverted], targetUom);
		}
		catch (Exception ex)
		{
			throw new EvalException($"Can't add/subtract incompatible dimensions: {ex.Message}" + ex);
		}
	}

	private static Value MulScalar(Value a, Value b)
	{
		bool aHasUnit = a.UOM != null && !ReferenceEquals(a.UOM, SharedUnits.NoUnit);
		bool bHasUnit = b.UOM != null && !ReferenceEquals(b.UOM, SharedUnits.NoUnit);

		if (aHasUnit && bHasUnit)
			throw new EvalException("Multiplying two physical units together is not supported directly.");

		var resultingUnit = aHasUnit ? a.UOM : b.UOM;
		return new Value([a.Frac * b.Frac], resultingUnit);
	}

	private static Value DivScalar(Value a, Value b)
	{
		if (b.Frac == 0) throw new EvalException("Division by zero");

		bool aHasUnit = a.UOM != null && !ReferenceEquals(a.UOM, SharedUnits.NoUnit);
		bool bHasUnit = b.UOM != null && !ReferenceEquals(b.UOM, SharedUnits.NoUnit);

		if (aHasUnit && bHasUnit)
		{
			try
			{
				// Convert b to a's unit system to cancel out and yield a unitless number
				double bInA = new Quantity(b.Frac, b.UOM).Convert(a.UOM).Amount;
				return new Value([a.Frac / bInA], SharedUnits.NoUnit);
			}
			catch (Exception ex)
			{
				throw new EvalException($"Can't divide incompatible dimensions {a.UOM.Symbol} by {b.UOM.Symbol}" + ex);
			}
		}

		if (bHasUnit)
			throw new EvalException("Can't divide a scalar value by a physical unit metric");

		return new Value([a.Frac / b.Frac], a.UOM);
	}

	private static Value ModScalar(Value a, Value b)
	{
		if (b.Frac == 0) throw new EvalException("Division by zero");
		return new Value([a.Frac % b.Frac], a.UOM);
	}

	private static Value PowScalar(Value a, Value b)
	{
		double e = b.Frac;
		bool hasUnit = a.UOM != null && !ReferenceEquals(a.UOM, SharedUnits.NoUnit);

		if (hasUnit && e != 1)
			throw new EvalException("Powers of physical units are not supported");

		return new Value([Math.Pow(a.Frac, e)], a.UOM);
	}


	private static Value FactorialScalar(Value v)
	{
		double n = v.Frac;
		if (n < 0 || n != Math.Floor(n) || n > 170)
			throw new EvalException("Factorial needs a whole number from 0 to 170");
		double r = 1;
		for (int i = 2; i <= n; i++) r *= i;
		return new Value([r], SharedUnits.NoUnit);
	}

}

public static class Formatter
{
	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
	public static string Format(Value v, int decimals, bool group)
	{
		// 1. Case A: It's an empty vector
		if (v.Amounts == null || v.Amounts.Count == 0)
			return "[]";

		// 2. Case B: It's a multi-element vector list. Unpack and join them.
		if (v.Amounts.Count > 1)
		{
			var formattedElements = v.Amounts.Select(amount =>
				Format(new Value([amount], v.UOM), decimals, group)
			);
			return "[" + string.Join(", ", formattedElements) + "]";
		}

		if (v.Text != null) return v.Text;

		// 3. Case C: It's a single scalar value. Extract its isolated raw amount.
		double rawNumber = v.Frac; // pulls the first item out of Amounts safely

		// 4. Handle percentage presentation
		if (v.IsPercent)
			return Number(rawNumber, decimals, group) + "%";

		// 5. Handle unitless presentation
		var u = v.UOM;
		if (u == null || ReferenceEquals(u, SharedUnits.NoUnit))
			return Number(rawNumber, decimals, group);

		// 6. Handle currency presentation (Caliper currency check pattern)
		// Note: Caliper currencies typically use a boolean property or a currency category check
		if (UnitTable.IsCurrency(u))
		{
			// Default to 2 decimals for currency if Caliper doesn't expose a dynamic precision property
			double rounded = Math.Round(Math.Abs(rawNumber), 2, MidpointRounding.AwayFromZero);
			string body = rounded.ToString(Pattern(2, group, fixedDecimals: true), Inv);
			string sign = rawNumber < 0 && rounded > 0 ? "-" : "";

			return sign + u.Symbol + body;
		}

		// 7. Handle standard physics measurement unit strings
		string n = Number(rawNumber, decimals, group);
		return u.Symbol == "°" ? n + "°" : n + " " + u.Symbol;
	}


	public static string Number(double x, int decimals, bool group)
	{
		if (double.IsNaN(x)) return "NaN";
		if (double.IsInfinity(x)) return x > 0 ? "∞" : "-∞";
		if (x == 0) return "0";

		double r = Math.Round(x, Math.Min(decimals, 15), MidpointRounding.AwayFromZero);
		if (r == 0) return "0";

		double abs = Math.Abs(x);
		if (abs >= 1e15 || abs < 1e-6) return x.ToString("0.######E+0", Inv);

		return r.ToString(Pattern(decimals, group, fixedDecimals: false), Inv);
	}

	private static string Pattern(int decimals, bool group, bool fixedDecimals)
	{
		string whole = group ? "#,##0" : "0";
		if (decimals <= 0) return whole;
		return whole + "." + new string(fixedDecimals ? '0' : '#', decimals);
	}
}