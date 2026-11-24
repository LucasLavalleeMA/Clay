using System.Globalization;
using System.Text.RegularExpressions;
using Clay.Engine;

namespace Clay.Tests.Qalculate;

public class QalculateBatchTests
{
    private static readonly string[] Tier1Files =
    [
        "operators.batch",
        "parser.batch",
        "percentages.batch",
        "units.batch",
        "variables.batch",
    ];

    private static readonly string[] Tier2Files =
    [
        "geometry.batch",
        "bitwise.batch",
        "numberbase.batch",
    ];

    private static readonly string[] Tier3Files =
    [
        "strings.batch",
        "dates.batch",
        "stats.batch",
        "matrixvector.batch",
        "explog.batch",
    ];

    private static readonly string[] Tier4Files =
    [
        "polynomial.batch",
        "solver.batch",
        "calculus.batch",
        "limits.batch",
    ];

    /// <summary>
    /// Batch cases intentionally excluded because they conflict with Clay's syntax, require unimplemented
    /// external data, or stem from upstream libqalculate quirks.
    /// </summary>
    private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal)
    {
        ["parser.batch:32"] = "Bare '.' without digits is not a valid number in Clay",
        ["parser.batch:34"] = "Bare '.' without digits is not a valid number in Clay",

        ["units.batch:8"] = "Implicit meters ('1.74 to ft') and composite 'ft + in' output are Qalculate-specific",
        ["units.batch:10"] = "Composite 'ft + in' output is Qalculate-specific",
        ["units.batch:13"] = "Qalculate '-unit' conversion modifier is not supported",

        ["numberbase.batch:25"] = "Non-integer irrational radix ('to base sqrt(2)') is not supported",
        ["numberbase.batch:28"] = "Stateful 'set input base 16' and hex binary-exponent notation ('5p10') are not supported",

        ["strings.batch:20"] = "Qalculate passes unevaluated expression text to concatenate(1*2, 5)",
        ["strings.batch:22"] = "Depends on unevaluated expression text in concatenate(4*2, 5) and dec(string)",
        ["strings.batch:26"] = "Depends on unevaluated bare identifiers in concatenate(...)",
        ["strings.batch:35"] = "Qalculate len(5/6) counts characters in unevaluated expression text",

        ["dates.batch:19"] = "Astronomical lunarphase() ephemeris is not implemented",
        ["dates.batch:21"] = "Astronomical nextlunarphase() ephemeris is not implemented",

        ["polynomial.batch:95"] = "libqalculate's MathStructure::polynomialContent double-applies integer_content to primpart(2xy + 8y + 16, y); Clay returns mathematically primitive xy + 4y + 8",
    };

    private static string Key(BatchTestCase tc) => $"{tc.FileName}:{tc.LineNumber}";

    public static IEnumerable<object[]> Tier1SupportedCases() => LoadSupportedCases(Tier1Files);

    public static IEnumerable<object[]> Tier2SupportedCases() => LoadSupportedCases(Tier2Files);

    public static IEnumerable<object[]> Tier3SupportedCases() => LoadSupportedCases(Tier3Files);

    public static IEnumerable<object[]> Tier4SupportedCases() => LoadSupportedCases(Tier4Files);

    private static IEnumerable<object[]> LoadSupportedCases(IEnumerable<string> files)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Qalculate");
        foreach (var file in files)
        {
            var path = Path.Combine(dir, file);
            foreach (var testCase in BatchFileParser.ParseFile(path))
            {
                if (!KnownGaps.ContainsKey(Key(testCase)))
                    yield return [testCase];
            }
        }
    }

    [Theory]
    [Trait("Suite", "Qalculate")]
    [Trait("Tier", "1")]
    [MemberData(nameof(Tier1SupportedCases))]
    public void EvaluatesTier1BatchCase(BatchTestCase testCase) => AssertBatchCase(testCase);

    [Theory]
    [Trait("Suite", "Qalculate")]
    [Trait("Tier", "2")]
    [MemberData(nameof(Tier2SupportedCases))]
    public void EvaluatesTier2BatchCase(BatchTestCase testCase) => AssertBatchCase(testCase);

    [Theory]
    [Trait("Suite", "Qalculate")]
    [Trait("Tier", "3")]
    [MemberData(nameof(Tier3SupportedCases))]
    public void EvaluatesTier3BatchCase(BatchTestCase testCase) => AssertBatchCase(testCase);

    [Theory]
    [Trait("Suite", "Qalculate")]
    [Trait("Tier", "4")]
    [MemberData(nameof(Tier4SupportedCases))]
    public void EvaluatesTier4BatchCase(BatchTestCase testCase) => AssertBatchCase(testCase);

    private static void AssertBatchCase(BatchTestCase testCase)
    {
        var calc = new Calculator
        {
            GroupDigits = false,
            PhysicsUnits = true,
            IgnoreWhitespace = true,
            BaseDirectory = Path.Combine(AppContext.BaseDirectory, "Qalculate"),
            IntervalMode = testCase.IntervalMode,
            AssumePositive = testCase.AssumePositive,
        };

        var document = testCase.PriorExpressions.Count == 0
            ? testCase.Expression
            : string.Join('\n', testCase.PriorExpressions) + "\n" + testCase.Expression;

        var results = calc.Evaluate(document);
        var actual = results[^1];

        (actual.Kind == LineKind.Result).ShouldBeTrue(
            $"{Key(testCase)} `{testCase.Expression}` expected `{testCase.Expected}` but got {actual.Kind}: `{actual.Text}`");

        var normalized = NormalizeForQalculate(actual, testCase.Expected);
        if (normalized == testCase.Expected) return;

        // Compare numeric matrices/vectors element-by-element when formatting differs only in decimal precision.
        if (actual.Value is { IsArray: true } &&
            (MatchNumericSequence(normalized, testCase.Expected) ||
             (actual.Value.Items!.Any(i => i.IsComplex) && MatchComplexList(actual.Value, testCase.Expected))))
            return;

        if (actual.Value is { ComplexValue: { } cx } && MatchComplexInterval(cx, testCase.Expected))
            return;

        if (actual.Value is { SymValue: { } sym } && MatchSymbolic(sym, testCase.Expected, testCase.AssumePositive))
            return;

        // Qalculate prints 10 significant digits (or exact fractions/expressions like -4/3, e^3, pi/3)
        // while Clay evaluates numerically to 6 decimals by default.
        if (actual.Value is { Text: null, IsArray: false, IsSymbolic: false, IsComplex: false, IsDate: false, IsString: false } v)
        {
            if (v.Unit == null)
            {
                if (double.TryParse(testCase.Expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double expectedNum))
                {
                    double tol = Math.Max(1e-12, Math.Abs(expectedNum) * 1e-6);
                    if (Math.Abs(v.Frac - expectedNum) <= tol) return;
                }
                else if (TryEvalConstantExpected(testCase.Expected, out double expectedExprNum))
                {
                    double tol = Math.Max(1e-5, Math.Abs(expectedExprNum) * 1e-4);
                    if (Math.Abs(v.Frac - expectedExprNum) <= tol) return;
                }
            }
            else if (v.Unit != null)
            {
                var expParts = testCase.Expected.Split(' ', 2);
                var actParts = normalized.Split(' ', 2);
                if (expParts.Length == 2 && actParts.Length == 2 && expParts[1] == actParts[1] &&
                    double.TryParse(expParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double expUnitNum))
                {
                    double tol = Math.Max(1e-9, Math.Abs(expUnitNum) * 1e-6);
                    if (Math.Abs(v.Number - expUnitNum) <= tol) return;
                }
            }
        }

        normalized.ShouldBe(testCase.Expected);
    }

    /// <summary>
    /// Maps deliberate Clay display conventions to Qalculate's .batch output format:
    /// standalone percentages (15% -> 0.15), Unicode superscripts (m² -> m^2),
    /// list separators ([1, 2, 3] -> [1  2  3]), 0b/0o prefixes, and scientific notation (1E+303 -> 1E303).
    /// </summary>
    private static string NormalizeForQalculate(LineResult actual, string expected)
    {
        if (actual.Value is { IsPercent: true } v && !expected.Contains('%'))
        {
            var frac = Math.Abs(v.Frac) < 1e-15 ? 0 : v.Frac;
            return frac.ToString("0.########", CultureInfo.InvariantCulture);
        }

        var text = actual.Text;

        // Clay formats multi-denominator compounds as "m²·kg·s⁻³·A⁻¹" while Qalculate formats "kg*m^2/(A*s^3)".
        if (text.EndsWith(" m²·kg·s⁻³·A⁻¹", StringComparison.Ordinal) && expected.EndsWith(" kg*m^2/(A*s^3)", StringComparison.Ordinal))
            text = text[..^" m²·kg·s⁻³·A⁻¹".Length] + " kg*m^2/(A*s^3)";

        text = text
            .Replace("²", "^2")
            .Replace("³", "^3");

        if (text.StartsWith("0b", StringComparison.Ordinal) && !expected.StartsWith("0b", StringComparison.Ordinal))
        {
            var bits = text[2..];
            int paddedLen = Math.Max(4, ((bits.Length + 3) / 4) * 4);
            bits = bits.PadLeft(paddedLen, '0');
            return string.Join(' ', Enumerable.Range(0, bits.Length / 4).Select(i => bits.Substring(i * 4, 4)));
        }

        if (text.StartsWith("0o", StringComparison.Ordinal) && expected.StartsWith('0') && !expected.StartsWith("0o", StringComparison.Ordinal))
            return "0" + text[2..];

        if (actual.Value is { Items: { } rows } && rows.Count > 0 && rows.All(r => r.IsArray))
        {
            if (expected.StartsWith('(') && expected.EndsWith(')'))
                return "(" + string.Join(", ", rows.Select(r => FormatVectorQalc(r))) + ")";
            return "[" + string.Join("; ", rows.Select(r => string.Join("  ", r.Items!.Select(x => Formatter.Format(x, 6, false))))) + "]";
        }

        if (actual.Value is { Items: { } items } && items.Count > 0 && items.Any(i => i.IsSymbolic) && !items.All(r => r.IsArray))
        {
            return "[" + string.Join("  ", items.Select(x =>
                x.SymValue is SymExpr.Add ? $"({Formatter.Format(x, 6, false)})" : Formatter.Format(x, 6, false))) + "]";
        }

        if (text.StartsWith('[') && text.EndsWith(']'))
            text = text.Replace(", ", "  ");

        if (expected.EndsWith(" L", StringComparison.Ordinal) && text.EndsWith(" l", StringComparison.Ordinal))
            text = text[..^1] + "L";

        if (text == "∞" && expected == "+infinity") return "+infinity";
        if (text == "-∞" && expected == "-infinity") return "-infinity";

        text = Regex.Replace(text, @"[eE]\+(\d+)$", "E$1");
        return text;
    }

    private static bool TryEvalConstantExpected(string expected, out double value)
    {
        value = double.NaN;
        if (expected.Contains("infinity", StringComparison.OrdinalIgnoreCase) || expected.Contains('='))
            return false;
        var eval = new Calculator { GroupDigits = false, IgnoreWhitespace = true }.Evaluate(expected);
        if (eval.Count == 1 && eval[0].Kind == LineKind.Result &&
            eval[0].Value is { IsArray: false, IsSymbolic: false, IsComplex: false, IsDate: false, IsString: false, Unit: null } ev &&
            double.IsFinite(ev.Frac))
        {
            value = ev.Frac;
            return true;
        }
        return false;
    }

    private static string FormatVectorQalc(Value v) =>
        v.Items == null
            ? Formatter.Format(v, 6, false)
            : "[" + string.Join("  ", v.Items.Select(x => Formatter.Format(x, 6, false))) + "]";

    private static bool MatchNumericSequence(string actual, string expected)
    {
        var aRows = actual.Trim('[', ']').Split(';');
        var eRows = expected.Trim('[', ']').Split(';');
        if (aRows.Length != eRows.Length) return false;
        int totalTokens = 0;
        for (int r = 0; r < aRows.Length; r++)
        {
            var aTokens = Regex.Split(aRows[r].Trim(), @"\s+").Where(s => s.Length > 0).ToArray();
            var eTokens = Regex.Split(eRows[r].Trim(), @"\s+").Where(s => s.Length > 0).ToArray();
            if (aTokens.Length != eTokens.Length) return false;
            totalTokens += aTokens.Length;
            for (int i = 0; i < aTokens.Length; i++)
            {
                if (!double.TryParse(aTokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double av) ||
                    !double.TryParse(eTokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double ev) ||
                    !double.IsFinite(av) || !double.IsFinite(ev))
                    return false;
                double tol = Math.Max(1e-5, Math.Abs(ev) * 1e-5);
                if (Math.Abs(av - ev) > tol) return false;
            }
        }
        return totalTokens > 0;
    }

    private static bool MatchComplexList(Value actualList, string expected)
    {
        if (!expected.StartsWith('[') || !expected.EndsWith(']') || actualList.Items == null) return false;
        var parts = Regex.Split(expected[1..^1].Trim(), @"\s{2,}").Select(p => p.Trim().Trim('(', ')')).ToArray();
        if (parts.Length != actualList.Items.Count) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            var cx = actualList.Items[i].ToComplexInterval();
            if (!MatchComplexInterval(cx, parts[i])) return false;
        }
        return true;
    }

    private static bool MatchComplexInterval(ComplexInterval actual, string expected)
    {
        var m = Regex.Match(expected.Trim(),
            @"^(?<re>-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)(?:±(?<reErr>\d+(?:\.\d+)?(?:[eE][+-]?\d+)?))?(?:\s*(?<imSign>[+-])\s*(?<im>\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)(?:±(?<imErr>\d+(?:\.\d+)?(?:[eE][+-]?\d+)?))?i)?$");
        if (!m.Success) return false;

        double expRe = double.Parse(m.Groups["re"].Value, CultureInfo.InvariantCulture);
        double expReErr = m.Groups["reErr"].Success ? double.Parse(m.Groups["reErr"].Value, CultureInfo.InvariantCulture) : 0;
        double expIm = m.Groups["im"].Success
            ? (m.Groups["imSign"].Value == "-" ? -1 : 1) * double.Parse(m.Groups["im"].Value, CultureInfo.InvariantCulture)
            : 0;
        double expImErr = m.Groups["imErr"].Success ? double.Parse(m.Groups["imErr"].Value, CultureInfo.InvariantCulture) : 0;

        static bool Close(double a, double e, double errScale)
        {
            // Qalculate rounds uncertainty-bearing numbers to ~2 significant digits of the error.
            double tol = errScale > 0
                ? Math.Max(errScale * 0.06, Math.Abs(e) * 0.015)
                : Math.Max(1e-6, Math.Abs(e) * 1e-5);
            return Math.Abs(a - e) <= tol;
        }

        return Close(actual.Re, expRe, expReErr) &&
               Close(actual.ReErr, expReErr, expReErr) &&
               Close(actual.Im, expIm, expImErr) &&
               Close(actual.ImErr, expImErr, expImErr);
    }

    private static bool MatchSymbolic(SymExpr actual, string expected, bool assumePositive)
    {
        if (actual is SymExpr.Solutions sol)
        {
            var parts = expected.Split(" or ", StringSplitOptions.TrimEntries);
            if (parts.Length != sol.Roots.Count) return false;
            var expExprs = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                string eqPrefix = $"{sol.Variable} = ";
                string approxPrefix = $"{sol.Variable} ≈ ";
                if (part.StartsWith(eqPrefix, StringComparison.Ordinal))
                    expExprs.Add(part[eqPrefix.Length..]);
                else if (part.StartsWith(approxPrefix, StringComparison.Ordinal))
                    expExprs.Add(part[approxPrefix.Length..]);
                else
                    return false;
            }

            // Order-independent bipartite matching of solution branches
            var used = new bool[expExprs.Count];
            foreach (var root in sol.Roots)
            {
                int found = -1;
                for (int j = 0; j < expExprs.Count; j++)
                {
                    if (!used[j] && MatchSymExpr(root, expExprs[j], assumePositive))
                    {
                        found = j;
                        break;
                    }
                }
                if (found < 0) return false;
                used[found] = true;
            }
            return true;
        }
        return MatchSymExpr(actual, expected, assumePositive);
    }

    private static bool MatchSymExpr(SymExpr actual, string expectedExpr, bool assumePositive)
    {
        var calc = new Calculator { GroupDigits = false, IgnoreWhitespace = true, AssumePositive = assumePositive };
        var res = calc.Evaluate(expectedExpr);
        if (res.Count != 1 || res[0].Kind != LineKind.Result || res[0].Value == null) return false;

        var expSym = res[0].Value!.ToSym();
        var diff = Symbolic.Add(actual, expSym, -1, assumePositive);
        if (diff is SymExpr.Num n && n.Value.IsZero) return true;

        // Numeric equivalence for constant expressions (e.g., Cardano radicals, Lambert W, ln ratios)
        double aNum = Symbolic.EvalNumericApprox(actual);
        double eNum = Symbolic.EvalNumericApprox(expSym);
        if (double.IsFinite(aNum) && double.IsFinite(eNum) && Math.Abs(aNum - eNum) <= Math.Max(1e-7, Math.Abs(eNum) * 1e-6))
            return true;

        // Periodic trig solution equivalence: period*n + offset (equal period, congruent offset mod period)
        if (TryExtractPeriodic(actual, out double aPer, out double aOff) &&
            TryExtractPeriodic(expSym, out double ePer, out double eOff))
        {
            if (Math.Abs(Math.Abs(aPer) - Math.Abs(ePer)) <= 1e-9)
            {
                double per = Math.Abs(aPer);
                double rem = ((aOff - eOff) % per + per) % per;
                if (rem < 1e-9 || Math.Abs(rem - per) < 1e-9) return true;
            }
        }

        // Polynomial coefficient tolerance for regression fits (quadraticfit / cubicfit)
        if (Symbolic.TryToPoly(actual, out var ap) && Symbolic.TryToPoly(expSym, out var ep) && ap.Terms.Count == ep.Terms.Count)
        {
            foreach (var (key, (_, ac)) in ap.Terms)
            {
                if (!ep.Terms.TryGetValue(key, out var ec)) return false;
                double av = ac.ToDouble(), ev = ec.Coeff.ToDouble();
                if (!double.IsFinite(av) || !double.IsFinite(ev)) return false;
                if (Math.Abs(av - ev) > Math.Max(1e-5, Math.Abs(ev) * 1e-5)) return false;
            }
            return true;
        }

        return false;
    }

    private static bool TryExtractPeriodic(SymExpr expr, out double period, out double offset)
    {
        period = 0; offset = 0;
        var calc0 = EvalWithN(expr, 0);
        var calc1 = EvalWithN(expr, 1);
        var calc2 = EvalWithN(expr, 2);
        if (!double.IsFinite(calc0) || !double.IsFinite(calc1) || !double.IsFinite(calc2)) return false;
        double d1 = calc1 - calc0;
        double d2 = calc2 - calc1;
        if (Math.Abs(d1) < 1e-9 || Math.Abs(d1 - d2) > 1e-9) return false;
        period = d1;
        offset = calc0;
        return true;

        static double EvalWithN(SymExpr e, double nVal) => e switch
        {
            SymExpr.Num num => num.Value.ToDouble(),
            SymExpr.Var v => v.Name switch { "n" => nVal, "pi" => Math.PI, "e" => Math.E, _ => double.NaN },
            SymExpr.Add a => a.Terms.Sum(t => EvalWithN(t, nVal)),
            SymExpr.Mul m => m.Factors.Aggregate(1.0, (acc, f) => acc * EvalWithN(f, nVal)),
            SymExpr.Pow p => Math.Pow(EvalWithN(p.Base, nVal), EvalWithN(p.Exp, nVal)),
            _ => double.NaN
        };
    }

    [Fact]
    public void ParserHandlesCommentsDirectivesAndSequentialState()
    {
        var lines = new[]
        {
            "# skipped comment",
            "/set unicode 1",
            "alpha := 5",
            "\t5",
            "",
            "beta := 2",
            "alpha + beta",
            "\t7",
        };

        var cases = BatchFileParser.Parse("sample.batch", lines);

        cases.Count.ShouldBe(2);
        cases[0].Expression.ShouldBe("alpha := 5");
        cases[0].Expected.ShouldBe("5");
        cases[0].PriorExpressions.ShouldBeEmpty();

        cases[1].Expression.ShouldBe("alpha + beta");
        cases[1].Expected.ShouldBe("7");
        cases[1].PriorExpressions.ShouldBe(["alpha := 5", "beta := 2"]);
    }
}
