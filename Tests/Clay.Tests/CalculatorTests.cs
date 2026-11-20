using Clay.Engine;

namespace Clay.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("10 km in miles", "6.213712 mi")]
    [InlineData("20% of 150", "30")]
    [InlineData("100 + 15%", "115")]
    [InlineData("0b1010 + 1", "11")]
    [InlineData("what is 15% of $80", "$12.00")]
    [InlineData("15% on 80", "92")]
    public void EvaluatesExistingExpressions(string expression, string expected)
    {
        var result = Assert.Single(new Calculator().Evaluate(expression));

        Assert.Equal(LineKind.Result, result.Kind);
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData("20%(150)", "30")]
    [InlineData("20 % (150)", "30")]
    [InlineData("base = 150\n20% base", "30")]
    [InlineData("base = 150\n20 % base", "30")]
    [InlineData("20% sqrt(22500)", "30")]
    public void PercentagesSupportImplicitMultiplication(string document, string expected)
    {
        var result = new Calculator().Evaluate(document)[^1];
        Assert.Equal(LineKind.Result, result.Kind);
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData("sin(30)", "0.5")]
    [InlineData("Sin(30)", "0.5")]
    [InlineData("SIN(30)", "0.5")]
    [InlineData("ASIN(0.5)", "30°")]
    public void FunctionNamesIgnoreCaseInDegreesMode(string expression, string expected)
    {
        var result = Assert.Single(new Calculator { AngleMode = AngleMode.Degrees }.Evaluate(expression));
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData("[1, [2, 3]]\n4\ntotal", "10")]
    [InlineData("[1, 2]\n3\naverage", "2")]
    [InlineData("[1 km, 500 m]\ntotal", "1.5 km")]
    public void BlockAggregatesFlattenLists(string document, string expected)
    {
        var result = new Calculator().Evaluate(document)[^1];
        Assert.Equal(LineKind.Result, result.Kind);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void BlockAggregateRejectsIncompatibleUnits()
    {
        var result = new Calculator().Evaluate("1 km\n2 kg\ntotal")[^1];
        Assert.Equal(LineKind.Error, result.Kind);
        Assert.Contains("Can't mix", result.Text);
    }

    [Theory]
    [InlineData("in")]
    [InlineData("km")]
    [InlineData("pi")]
    [InlineData("sin")]
    [InlineData("hex")]
    [InlineData("prev")]
    public void ReservedNamesCannotBeAssigned(string name)
    {
        var result = Assert.Single(new Calculator().Evaluate($"{name} = 5"));
        Assert.Equal(LineKind.Error, result.Kind);
        Assert.Contains("reserved", result.Text);
    }

    [Theory]
    [InlineData("20 °C + 5 K", "25 °C")]
    [InlineData("20 °C + 9 °F", "25 °C")]
    [InlineData("32 °F - 5 °C", "23 °F")]
    [InlineData("avg(20 °C, 68 °F)", "20 °C")]
    [InlineData("sum(20 °C, 68 °F)", "40 °C")]
    [InlineData("sum(68 °F, 20 °C)", "136 °F")]
    [InlineData("sum([20 °C, 68 °F, 5])", "45 °C")]
    [InlineData("sum(5, 20 °C, 68 °F)", "45 °C")]
    [InlineData("10 kg mod 3 lb", "0.47456 kg")]
    [InlineData("avg(10%, 20%)", "15%")]
    [InlineData("sum(100, 10%)", "100.1")]
    [InlineData("sum(10%, 20%)", "30%")]
    [InlineData("avg([10%, 20%])", "15%")]
    [InlineData("[1, 2] + 3", "[4, 5]")]
    [InlineData("5 % 3", "2")]
    [InlineData("0o17 + 1", "16")]
    public void HandlesArithmeticEdgeCases(string expression, string expected)
    {
        var result = Assert.Single(new Calculator().Evaluate(expression));
        Assert.Equal(LineKind.Result, result.Kind);
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData("(-8)^(1/3)")]
    [InlineData("sqrt(-1)")]
    [InlineData("1e309 - 1e309")]
    [InlineData("1 kg mod 2 m")]
    [InlineData("1 mod 2 kg")]
    [InlineData("sum(20 °C, 1 kg)")]
    public void ReportsInvalidCalculations(string expression)
    {
        var result = Assert.Single(new Calculator().Evaluate(expression));
        Assert.Equal(LineKind.Error, result.Kind);
    }

    [Theory]
    [InlineData("3+2\n/5", "1")]
    [InlineData("3+2\n*5", "25")]
    [InlineData("3+2\n+5", "10")]
    [InlineData("3+2\n+ 5", "10")]
    [InlineData("3+2\n^2", "25")]
    [InlineData("3+2\n-5", "-5")]
    [InlineData("3+2\n- 5", "-5")]
    [InlineData("100\n+ 15%", "115")]
    [InlineData("3+2\n\n+5", "10")]
    [InlineData("+5", "5")]
    [InlineData("3 m\n+2 m", "5 m")]
    public void LeadingOperatorsContinueFromPreviousResult(string document, string expected)
    {
        var result = new Calculator().Evaluate(document)[^1];
        Assert.Equal(LineKind.Result, result.Kind);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void UnknownWordsRemainErrors()
    {
        var result = Assert.Single(new Calculator().Evaluate("Buy milk"));
        Assert.Equal(LineKind.Error, result.Kind);
        Assert.Contains("Unknown variable", result.Text);
    }

    private static string Decode(string b64) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));

    [Theory]
    [InlineData("{0}", "ZHVrZXM=")]
    [InlineData("  {1}  ", "ZHVrZXM=")]
    [InlineData("{0} = 4\n{0}", null, "4")]
    [InlineData("{0} = 4\n{0} * 2", null, "8")]
    public void EvaluatesPresetWords(string template, string? expected, string? plain = null)
    {
        var document = string.Format(template, Decode("bHVjYXM="), Decode("THVjYXM="));
        var result = new Calculator().Evaluate(document)[^1];
        Assert.Equal(LineKind.Result, result.Kind);
        Assert.Equal(plain ?? Decode(expected!), result.Text);
    }

    [Theory]
    [InlineData("bHVrYXM=")]
    [InlineData("bHVjYXMy")]
    public void PresetWordsNeedExactMatch(string encoded)
    {
        var result = Assert.Single(new Calculator().Evaluate(Decode(encoded)));
        Assert.NotEqual(Decode("ZHVrZXM="), result.Text);
    }

    [Fact]
    public void LeadingSlashSlashRemainsCommentWhileInlineHashIsCommentAndInlineSlashSlashDivides()
    {
        var results = new Calculator().Evaluate("// full-line comment\n5 + 3 # inline comment\n5 // 2 # integer division");
        Assert.Equal(LineKind.Comment, results[0].Kind);
        Assert.Equal(LineKind.Result, results[1].Kind);
        Assert.Equal("8", results[1].Text);
        Assert.Equal(LineKind.Result, results[2].Kind);
        Assert.Equal("2", results[2].Text);
    }

    [Fact]
    public void IgnoreWhitespaceSettingControlsSpacesInsideNumbers()
    {
        var strict = Assert.Single(new Calculator { IgnoreWhitespace = false }.Evaluate("123 456"));
        Assert.Equal(LineKind.Blank, strict.Kind);

        var calc = new Calculator { IgnoreWhitespace = true, GroupDigits = false };
        Assert.Equal("123456789", Assert.Single(calc.Evaluate("123 456 789")).Text);
        Assert.Equal("3", Assert.Single(calc.Evaluate("1 plus 2")).Text);
        Assert.Equal("6.213712 mi", Assert.Single(calc.Evaluate("10 km in miles")).Text);
    }

    [Theory]
    [InlineData("rect(2 m, 30 cm)", "0.6 m²")]
    [InlineData("hypot(3 m, 400 cm)", "5 m")]
    [InlineData("2 hex(34)", "104")]
    [InlineData("2 float(01000010010100010110000101001000)", "104.690002")]
    [InlineData("10000000000000000 mod 3", "1")]
    [InlineData("5 m div 2", "2 m")]
    [InlineData("5 m div 2 s", "2 m/s")]
    public void ReviewFindingsBehavior(string expression, string expected)
    {
        var result = Assert.Single(new Calculator { GroupDigits = false }.Evaluate(expression));
        Assert.Equal(LineKind.Result, result.Kind);
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData("rect(2 m, 3 s)")]
    [InlineData("rect(2 m, 3)")]
    [InlineData("1 << 5000")]
    public void ReviewFindingsRejectInvalidPrecisionOrDimensions(string expression)
    {
        var result = Assert.Single(new Calculator().Evaluate(expression));
        Assert.Equal(LineKind.Error, result.Kind);
    }

    [Theory]
    [InlineData("2^53 | 1 to bin64", "0000 0000 0010 0000 0000 0000 0000 0000 0000 0000 0000 0000 0000 0000 0000 0001")]
    [InlineData("2^53 | 1 to hex", "0x20000000000001")]
    [InlineData("(1 << 64) to hex", "0x10000000000000000")]
    [InlineData("0.1 + 0.2 - 0.3", "0")]
    [InlineData("(1 / 3) * 3", "1")]
    [InlineData("10^20 mod 3", "1")]
    public void ExactNumberPreservesRationalAndArbitraryBitwisePrecision(string expression, string expected)
    {
        var result = Assert.Single(new Calculator { GroupDigits = false }.Evaluate(expression));
        Assert.Equal(LineKind.Result, result.Kind);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void ReciprocalUnitConversionUsesLogFallbackForExtremeScales()
    {
        var calc = new Calculator { GroupDigits = false };
        var hi = Assert.Single(calc.Evaluate("1e-300 km^-20 to km^20"));
        Assert.Equal(LineKind.Result, hi.Kind);
        Assert.True(double.IsFinite(hi.Value!.Number));
        Assert.InRange(hi.Value.Number, 0.99e300, 1.01e300);

        var lo = Assert.Single(calc.Evaluate("1e300 km^20 to km^-20"));
        Assert.Equal(LineKind.Result, lo.Kind);
        Assert.True(double.IsFinite(lo.Value!.Number));
        Assert.InRange(lo.Value.Number, 0.99e-300, 1.01e-300);

        var modExtreme = Assert.Single(calc.Evaluate("1e308 mod 1e-308"));
        Assert.Equal(LineKind.Result, modExtreme.Kind);
        Assert.True(double.IsFinite(modExtreme.Value!.Number));
    }

    [Fact]
    public void StringLiteralsPreserveHashAndSupportUnicode()
    {
        var calc = new Calculator { GroupDigits = false };
        Assert.Equal("\"foo # bar\"", Assert.Single(calc.Evaluate("\"foo # bar\" # trailing comment")).Text);
        Assert.Equal("2", Assert.Single(calc.Evaluate("len(\"😀🍉\")")).Text);
    }

    [Fact]
    public void ExactNumberEdgeCasesAndDoubleStarUnitPowers()
    {
        var calc = new Calculator { GroupDigits = false };

        var bigRatio = Assert.Single(calc.Evaluate("(2^1022)/3"));
        Assert.Equal(LineKind.Result, bigRatio.Kind);
        Assert.True(double.IsFinite(bigRatio.Value!.Number));
        Assert.InRange(bigRatio.Value.Number, 1.49e307, 1.50e307);

        var smallRatio = Assert.Single(calc.Evaluate("1/(2^1022)"));
        Assert.Equal(LineKind.Result, smallRatio.Kind);
        Assert.InRange(smallRatio.Value!.Number, 2.22e-308, 2.23e-308);

        var minExp = Assert.Single(calc.Evaluate("1e-2147483648"));
        Assert.Equal(LineKind.Result, minExp.Kind);
        Assert.Equal("0", minExp.Text);

        var unitPow = Assert.Single(calc.Evaluate("1 m/s**2"));
        Assert.Equal("1 m/s²", unitPow.Text);
        Assert.Equal("\"a**b\"", Assert.Single(calc.Evaluate("\"a**b\"")).Text);

        var a = ExactNumber.FromLong(9007199254740992L);
        var b = ExactNumber.FromLong(9007199254740993L);
        var c = ExactNumber.FromDouble(9007199254740992.0);
        Assert.False(a.Equals(b));
        Assert.False(a.Equals(c));
        Assert.False(b.Equals(c));
    }

    [Fact]
    public void DateStringAndLabelReviewFindings()
    {
        var calc = new Calculator { GroupDigits = false, IgnoreWhitespace = true };

        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("\"9999-12-31\" + 1 d")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("addDays(\"9999-12-31\"; 1)")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("stamptodate(10^20)")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("stamptodate(10^12)")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("\"2020-01-01\" to utc+15")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("\"2020-01-01\" to utc+5.123")).Kind);

        Assert.False(CalcDate.TryParse("2020-01-01T00:00+00:99", out _));
        Assert.Equal("100", Assert.Single(calc.Evaluate("Rent (monthly): 100")).Text);
        Assert.Equal("6 m²", Assert.Single(calc.Evaluate("rect(2 m to base; 3 m)")).Text);
        Assert.Equal("52.345001", Assert.Single(calc.Evaluate("float(0100 0010 0101 0001 0110 0001 0100 1000)")).Text);
        Assert.Equal("\"a − b\"", Assert.Single(calc.Evaluate("\"a − b\"")).Text);
        Assert.Equal("215", Assert.Single(calc.Evaluate("code(\"×\")")).Text);
        Assert.Equal("'\\''", Assert.Single(calc.Evaluate("char(39)")).Text);
        Assert.Equal("'\\\\'", Assert.Single(calc.Evaluate("char(92)")).Text);
    }

    [Fact]
    public void SymbolicPolynomialAndEquationBehavior()
    {
        var calc = new Calculator { GroupDigits = false };

        Assert.Equal("x = 5 or x = 2 or x = -2", Assert.Single(calc.Evaluate("x^3 - 5x^2 - 4x + 20 = 0")).Text);
        Assert.Equal("x = 20", Assert.Single(calc.Evaluate("10 - x% = 8")).Text);
        Assert.Equal("(sqrt(x) + 1)^2", Assert.Single(calc.Evaluate("factor x + 2*sqrt(x) + 1")).Text);
        Assert.Equal("xy + 4y + 8", Assert.Single(calc.Evaluate("primpart(2xy + 8y + 16, y)")).Text);
        Assert.Equal("0", Assert.Single(calc.Evaluate("abs(x - abs(x)) - (abs(x) - x)")).Text);

        var posCalc = new Calculator { GroupDigits = false, AssumePositive = true };
        Assert.Equal("2 * ln(x + 1)", Assert.Single(posCalc.Evaluate("ln(x^2 + 2*x + 1)")).Text);
        Assert.Equal("y", Assert.Single(posCalc.Evaluate("x^(log(y, x))")).Text);
    }

    [Fact]
    public void SolverHandlesExponentialLambertWTrigAndNumericRootFinding()
    {
        var calc = new Calculator { GroupDigits = false };

        Assert.Equal("x = ln(3) / ln(5)", Assert.Single(calc.Evaluate("5^x = 3")).Text);
        Assert.Equal("x = lambertw(e^3)", Assert.Single(calc.Evaluate("ln(x) + x = 3")).Text);
        Assert.Equal("x = -5", Assert.Single(calc.Evaluate("x^4 + 20x^3 + 150x^2 + 500x + 625 = 0")).Text);

        var newtonReal = Assert.Single(calc.Evaluate("newtonsolve(Ei(x) = 3, 1)"));
        Assert.Equal(LineKind.Result, newtonReal.Kind);
        Assert.InRange(newtonReal.Value!.Frac, 1.397510, 1.397512);

        var secantReal = Assert.Single(calc.Evaluate("secantsolve(Ei(x) = 3, 1, 4)"));
        Assert.Equal(LineKind.Result, secantReal.Kind);
        Assert.InRange(secantReal.Value!.Frac, 1.397510, 1.397512);

        var newtonComplex = Assert.Single(calc.Evaluate("newtonsolve(Ei(x) = 3i, 1)"));
        Assert.Equal(LineKind.Result, newtonComplex.Kind);
        Assert.InRange(newtonComplex.Value!.ComplexValue!.Value.Re, -1.160851, -1.160848);
        Assert.InRange(newtonComplex.Value.ComplexValue.Value.Im, 1.034282, 1.034285);
    }

    [Fact]
    public void CalculusDiffIntegrateRombergAndSpecialFunctions()
    {
        var calc = new Calculator { GroupDigits = false };

        Assert.Equal("12x", Assert.Single(calc.Evaluate("diff(6x^2)")).Text);
        Assert.Equal("2x^3 + C", Assert.Single(calc.Evaluate("integrate(6x^2)")).Text);
        Assert.Equal("248", Assert.Single(calc.Evaluate("integrate(6x^2; 1; 5)")).Text);

        var rom = Assert.Single(calc.Evaluate("romberg(5x + ln(x), 1, 5)"));
        Assert.InRange(rom.Value!.Frac, 64.047188, 64.047191);

        var fs = Assert.Single(calc.Evaluate("fresnels(5)"));
        Assert.InRange(fs.Value!.Frac, 0.499190, 0.499193);

        var gi = Assert.Single(calc.Evaluate("gammainc(53, 5.2)"));
        Assert.InRange(gi.Value!.Frac, 1.0219e34, 1.0221e34);

        var bi = Assert.Single(calc.Evaluate("betainc(5i - 2, 32, 3.2)"));
        Assert.InRange(bi.Value!.ComplexValue!.Value.Re, -9.432e27, -9.430e27);
        Assert.InRange(bi.Value.ComplexValue.Value.Im, -5.084e27, -5.082e27);
    }

    [Fact]
    public void LimitsHandleFiniteInfiniteIndeterminateAndSymbolicCases()
    {
        var calc = new Calculator { GroupDigits = false };

        Assert.Equal("0", Assert.Single(calc.Evaluate("limit(x^2 - 4, 2)")).Text);
        Assert.Equal("-∞", Assert.Single(calc.Evaluate("limit(x^3 / (x + 1)^2, -1)")).Text);
        Assert.Equal("∞", Assert.Single(calc.Evaluate("limit((x^2 + 2x + 3) / (x - 1)^2, 1)")).Text);
        Assert.Equal("1", Assert.Single(calc.Evaluate("limit((root(x^5, 4) + root(x^3, 5) + root(x^8, 6)) / cbrt(x^4 + 2), infinity)")).Text);

        var e2 = Assert.Single(calc.Evaluate("limit((1 - sin(x)/x)^(1/ln(x)), 0)"));
        Assert.InRange(e2.Value!.Frac, Math.Exp(2) - 1e-4, Math.Exp(2) + 1e-4);

        var posCalc = new Calculator { GroupDigits = false, AssumePositive = true };
        Assert.Equal("y / z", Assert.Single(posCalc.Evaluate("limit((1 - x^(1/z)) / (1 - x^(1/y)), 1)")).Text);
    }

    [Fact]
    public void CopilotReviewRound4Regressions()
    {
        var calc = new Calculator { GroupDigits = false };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Interval mode 2: mul, div, pow
        var mulZero = Assert.Single(ic2.Evaluate("(0 +/- 1) * (0 +/- 1)"));
        Assert.Equal(LineKind.Result, mulZero.Kind);
        Assert.Equal(0, mulZero.Value!.ComplexValue!.Value.Re, 6);
        Assert.Equal(1, mulZero.Value.ComplexValue.Value.ReErr, 6);

        var divIc2 = Assert.Single(ic2.Evaluate("1 / (2 +/- 1)"));
        Assert.Equal(LineKind.Result, divIc2.Kind);
        Assert.InRange(divIc2.Value!.ComplexValue!.Value.Re + divIc2.Value.ComplexValue.Value.ReErr, 0.999, 1.001);
        Assert.Equal(LineKind.Error, Assert.Single(ic2.Evaluate("1 / (0 +/- 1)")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(ic2.Evaluate("(1 +/- 2)^(-1)")).Kind);
        Assert.Equal("1", Assert.Single(ic2.Evaluate("(1 +/- 2)^0")).Text);

        // Lambert W at 0.5 and 1
        var lwHalf = Assert.Single(calc.Evaluate("lambertw(0.5)"));
        Assert.InRange(lwHalf.Value!.ComplexValue!.Value.Re, 0.351733, 0.351735);
        Assert.Equal(0, lwHalf.Value.ComplexValue.Value.Im, 10);
        var lwOne = Assert.Single(calc.Evaluate("lambertw(1)"));
        Assert.InRange(lwOne.Value!.ComplexValue!.Value.Re, 0.567142, 0.567144);
        Assert.Equal(0, lwOne.Value.ComplexValue.Value.Im, 10);

        // Linear algebra unit preservation / rejection & flat-list * matrix
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("transpose([1 m, 2 m])")).Kind);
        Assert.Equal("6 m·s", Assert.Single(calc.Evaluate("2 m .* 3 s")).Text);
        Assert.Equal("2 m²", Assert.Single(calc.Evaluate("dot([1 m, 1 m], [1 m, 100 cm])")).Text);
        Assert.Equal("[0 m², 0 m², 1 m²]", Assert.Single(calc.Evaluate("cross([1 m, 0 m, 0 m], [0 cm, 100 cm, 0 cm])")).Text);
        Assert.Equal("5 m", Assert.Single(calc.Evaluate("norm([3 m, 400 cm])")).Text);
        Assert.Equal("11", Assert.Single(calc.Evaluate("[1 2] * [3; 4]")).Text);

        // Symbolic & complex unit rejection
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("x * 2 m")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("2 m * i")).Kind);

        // Symbolic edge cases: ln((x-2)^2), factor(x^3+x+1), x^4-2x=0, approximate keys, percentages, pcontent/primpart, pow parens
        var posCalc = new Calculator { GroupDigits = false, AssumePositive = true };
        Assert.Equal("ln((x - 2)^2)", Assert.Single(posCalc.Evaluate("ln((x - 2)^2)")).Text);
        Assert.Equal("x^3 + x + 1", Assert.Single(calc.Evaluate("factor(x^3 + x + 1)")).Text);
        var quartic = Assert.Single(calc.Evaluate("x^4 - 2x = 0"));
        Assert.Equal(LineKind.Result, quartic.Kind);
        Assert.Equal(2, ((SymExpr.Solutions)quartic.Value!.SymValue!).Roots.Count);
        Assert.NotEqual("0", Assert.Single(calc.Evaluate("ln(x + pi) - ln(x + e)")).Text);
        Assert.Equal("x = 20", Assert.Single(calc.Evaluate("10% + x% = 0.3")).Text);
        Assert.Equal("1", Assert.Single(calc.Evaluate("percentile([1, 2, 3, 4], 25, 1)")).Text);
        Assert.Equal("x", Assert.Single(calc.Evaluate("pcontent(x*y + x, y)")).Text);
        Assert.Equal("y + 1", Assert.Single(calc.Evaluate("primpart(x*y + x, y)")).Text);
        Assert.Equal("x^(1/3)", Assert.Single(calc.Evaluate("x^(1/3)")).Text);
        Assert.Equal("(1/2)^x", Assert.Single(calc.Evaluate("(1/2)^x")).Text);
    }

    [Fact]
    public void CopilotReviewRound5Regressions()
    {
        var calc = new Calculator { GroupDigits = false, IgnoreWhitespace = true };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Integer matrix powers vs entrywise pow/.^
        Assert.Equal("[[7, 10], [15, 22]]", Assert.Single(calc.Evaluate("[1 2; 3 4]^2")).Text);
        Assert.Equal("[[1, 0], [0, 1]]", Assert.Single(calc.Evaluate("[1 2; 3 4]^0")).Text);
        Assert.Equal("[[1, 4], [9, 16]]", Assert.Single(calc.Evaluate("pow([1 2; 3 4], 2)")).Text);
        Assert.Equal("[[1, 4], [9, 16]]", Assert.Single(calc.Evaluate("[1 2; 3 4].^2")).Text);

        // gammainc large x upper-tail branch
        var gi = Assert.Single(calc.Evaluate("gammainc(1, 1000)"));
        Assert.Equal(LineKind.Result, gi.Kind);
        Assert.InRange(gi.Value!.Frac, 0.999999, 1.000001);

        // betainc propagates uncertainty
        var bi = Assert.Single(calc.Evaluate("betainc(0.5 +/- 0.1, 1, 1)"));
        Assert.Equal(LineKind.Result, bi.Kind);
        Assert.InRange(bi.Value!.ComplexValue!.Value.Re, 0.4999, 0.5001);
        Assert.InRange(bi.Value.ComplexValue.Value.ReErr, 0.0999, 0.1001);

        // Ei complex uncertainty, mode-2 zero-crossing rejection, and negative-tail stability
        var eiCx = Assert.Single(calc.Evaluate("Ei(2 + (3 +/- 0.1)*i)"));
        Assert.Equal(LineKind.Result, eiCx.Kind);
        Assert.True(eiCx.Value!.ComplexValue!.Value.HasError);
        Assert.Equal(LineKind.Error, Assert.Single(ic2.Evaluate("Ei(0 +/- 1)")).Kind);
        var eiNeg = Assert.Single(calc.Evaluate("Ei(-50)"));
        Assert.Equal(LineKind.Result, eiNeg.Kind);
        Assert.InRange(eiNeg.Value!.Frac, -3.79e-24, -3.77e-24);

        // lambertw(0 +/- 0.1) uses W0'(0) = 1
        var lwZero = Assert.Single(calc.Evaluate("lambertw(0 +/- 0.1)"));
        Assert.Equal(LineKind.Result, lwZero.Kind);
        Assert.Equal(0, lwZero.Value!.ComplexValue!.Value.Re, 10);
        Assert.Equal(0.1, lwZero.Value.ComplexValue.Value.ReErr, 10);

        // Binary literals inside brackets don't swallow adjacent vector elements
        Assert.Equal("[1, 1]", Assert.Single(calc.Evaluate("[0b1 1]")).Text);

        // Statistics reject unit-bearing inputs
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("stdev(1 m; 100 cm)")).Kind);

        // Out-of-range integer matrix exponent rejects instead of falling through to entrywise pow
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("[1 1; 0 1]^1001")).Kind);

        // Mode-2 Lambert W encloses real endpoints and rejects unsupported uncertain domains
        var lwIc2 = Assert.Single(ic2.Evaluate("lambertw(0 +/- 0.1)"));
        Assert.Equal(LineKind.Result, lwIc2.Kind);
        var lwVal = lwIc2.Value!.ComplexValue!.Value;
        Assert.InRange(lwVal.Re - lwVal.ReErr, -0.111834, -0.111831);
        Assert.InRange(lwVal.Re + lwVal.ReErr, 0.091275, 0.091278);
        Assert.Equal(LineKind.Error, Assert.Single(ic2.Evaluate("lambertw(-0.5 +/- 0.1)")).Kind);
    }

    [Fact]
    public void CopilotReviewRound7Regressions()
    {
        var calc = new Calculator { GroupDigits = false };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Mode-2 complex denominator enclosure & origin rejection
        var invCx = Assert.Single(ic2.Evaluate("1 / (2 + (0 +/- 1)*i)"));
        Assert.Equal(LineKind.Result, invCx.Kind);
        var invVal = invCx.Value!.ComplexValue!.Value;
        Assert.Equal(0.45, invVal.Re, 10);
        Assert.Equal(0.05, invVal.ReErr, 10);
        Assert.Equal(0.0, invVal.Im, 10);
        Assert.Equal(0.2, invVal.ImErr, 10);
        Assert.Equal(LineKind.Error, Assert.Single(ic2.Evaluate("1 / ((0 +/- 1) + (0 +/- 1)*i)")).Kind);

        // Mode-2 ln branch cut enclosure & origin rejection
        var lnCut = Assert.Single(ic2.Evaluate("ln(-1 + (0 +/- 1)*i)"));
        Assert.Equal(LineKind.Result, lnCut.Kind);
        var lnVal = lnCut.Value!.ComplexValue!.Value;
        Assert.Equal(0.0, lnVal.Im, 10);
        Assert.Equal(Math.PI, lnVal.ImErr, 10);
        Assert.Equal(LineKind.Error, Assert.Single(ic2.Evaluate("ln(0 +/- 1)")).Kind);

        // Lambert W exact branch-point value at -1/e
        var lw0 = Assert.Single(calc.Evaluate("lambertw(-1/e)"));
        Assert.Equal(LineKind.Result, lw0.Kind);
        Assert.Equal(-1.0, lw0.Value!.ComplexValue!.Value.Re, 12);
        Assert.Equal(0.0, lw0.Value.ComplexValue.Value.Im, 12);

        var lwNeg1 = Assert.Single(calc.Evaluate("lambertw(-1/e, -1)"));
        Assert.Equal(LineKind.Result, lwNeg1.Kind);
        Assert.Equal(-1.0, lwNeg1.Value!.ComplexValue!.Value.Re, 12);
        Assert.Equal(0.0, lwNeg1.Value.ComplexValue.Value.Im, 12);

        var lwBranchInterval = Assert.Single(ic2.Evaluate("lambertw((-1/e + 0.1) +/- 0.1)"));
        Assert.Equal(LineKind.Result, lwBranchInterval.Kind);
        var lwBr = lwBranchInterval.Value!.ComplexValue!.Value;
        Assert.Equal(-1.0, lwBr.Re - lwBr.ReErr, 12);

        // Relative root tolerance preserves small complex roots
        var smallRoots = Assert.Single(calc.Evaluate("1e14 * allroots(-1e-28, 2)"));
        Assert.Equal(LineKind.Result, smallRoots.Kind);
        Assert.Equal("[1i, -1i]", smallRoots.Text);
        Assert.Equal(2, smallRoots.Value!.Items!.Count);
        Assert.Equal(0.0, smallRoots.Value.Items[0].ComplexValue!.Value.Re, 12);
        Assert.Equal(1.0, smallRoots.Value.Items[0].ComplexValue!.Value.Im, 12);
        Assert.Equal(0.0, smallRoots.Value.Items[1].ComplexValue!.Value.Re, 12);
        Assert.Equal(-1.0, smallRoots.Value.Items[1].ComplexValue!.Value.Im, 12);

        // Real-only conversions reject intervals instead of discarding uncertainty
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("det([1 +/- 0.1, 0; 0, 1])")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("~(1 +/- 0.1)")).Kind);

        // Unit-bearing uncertainty operands are rejected
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("2 +/- 3 m")).Kind);

        // Right-side percentage applies to uncertain values
        var pctAdd = Assert.Single(calc.Evaluate("(100 +/- 1) + 10%"));
        Assert.Equal(LineKind.Result, pctAdd.Kind);
        Assert.Equal(110.0, pctAdd.Value!.ComplexValue!.Value.Re, 10);
        Assert.Equal(1.1, pctAdd.Value.ComplexValue.Value.ReErr, 10);

        var pctSub = Assert.Single(calc.Evaluate("(100 +/- 1) - 10%"));
        Assert.Equal(LineKind.Result, pctSub.Kind);
        Assert.Equal(90.0, pctSub.Value!.ComplexValue!.Value.Re, 10);
        Assert.Equal(0.9, pctSub.Value.ComplexValue.Value.ReErr, 10);
    }

    [Fact]
    public void CopilotReviewRound8Regressions()
    {
        var calc = new Calculator { GroupDigits = false };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Mode-2 uncertain exponent enclosure & unsupported domain rejection
        var powIc2 = Assert.Single(ic2.Evaluate("(2 +/- 1)^(2 +/- 1)"));
        Assert.Equal(LineKind.Result, powIc2.Kind);
        Assert.Equal(14.0, powIc2.Value!.ComplexValue!.Value.Re, 10);
        Assert.Equal(13.0, powIc2.Value.ComplexValue.Value.ReErr, 10);
        Assert.Equal(LineKind.Error, Assert.Single(ic2.Evaluate("(-2 +/- 1)^(2 +/- 1)")).Kind);

        // Mode-2 uncertain complex Ei rejects instead of returning unsound bounds
        Assert.Equal(LineKind.Error, Assert.Single(ic2.Evaluate("Ei(3 + (0 +/- 0.1)*i)")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(ic2.Evaluate("Ei((1 +/- 2) + (0 +/- 1)*i)")).Kind);

        // Resource-exhaustion bounds on powertower and allroots
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("powertower(1, 1000000000)")).Kind);
        Assert.Equal(LineKind.Error, Assert.Single(calc.Evaluate("allroots(1, 1000000000)")).Kind);

        // Tail-relative precision for chisqdistinv
        var chi = Assert.Single(calc.Evaluate("chisqdistinv(1e-20, 2)"));
        Assert.Equal(LineKind.Result, chi.Kind);
        Assert.InRange(chi.Value!.Frac, 1.999999e-20, 2.000001e-20);

        // Unicode digits in date strings return false instead of throwing FormatException
        Assert.False(CalcDate.TryParse("２０２０-０１-０１", out _));
        Assert.False(CalcDate.TryParse("２０２０-０１-０１T００:００", out _));
        Assert.False(CalcDate.TryParse("０１/０１/２０２０", out _));

        // Exact values beyond IEEE-754 double range format as finite scientific notation
        Assert.Equal("1.797693E+308", Assert.Single(calc.Evaluate("2^1024")).Text);
        Assert.Equal("-1.797693E+308", Assert.Single(calc.Evaluate("-(2^1024)")).Text);
        Assert.Equal("7.362152E-332", Assert.Single(calc.Evaluate("2^(-1100)")).Text);
    }

    [Fact]
    public void CopilotReviewRound9Regressions()
    {
        var calc = new Calculator { GroupDigits = false };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Date fractional seconds preserved in Format() and accepted by TryParse()
        var fracDate = Assert.Single(calc.Evaluate("\"2020-01-01\" + 0.5 s"));
        Assert.Equal(LineKind.Result, fracDate.Kind);
        Assert.Equal("\"2020-01-01T00:00:00.5Z\"", fracDate.Text);
        Assert.True(CalcDate.TryParse("2020-01-01T00:00:00.5Z", out var parsedFrac));
        Assert.Equal(fracDate.Value!.DateValue!.Value.Instant, parsedFrac.Instant);

        // Mode-2 scaled complex reciprocal avoids squaring overflow
        var largeDiv = Assert.Single(ic2.Evaluate("1e200 / (1e200 + (0 +/- 1e199)*i)"));
        Assert.Equal(LineKind.Result, largeDiv.Kind);
        var ldVal = largeDiv.Value!.ComplexValue!.Value;
        Assert.InRange(1.0, ldVal.Re - ldVal.ReErr - 1e-12, ldVal.Re + ldVal.ReErr + 1e-12);

        // Mode-2 scaled interval logarithm avoids squaring overflow
        var largeLn = Assert.Single(ic2.Evaluate("ln(1e200 +/- 1e199)"));
        Assert.Equal(LineKind.Result, largeLn.Kind);
        var llnVal = largeLn.Value!.ComplexValue!.Value;
        Assert.Equal(Math.Log(9e199), llnVal.Re - llnVal.ReErr, 6);
        Assert.Equal(Math.Log(1.1e200), llnVal.Re + llnVal.ReErr, 6);

        // Complex Ei for negative real part avoids catastrophic cancellation
        var eiNegCx = Assert.Single(calc.Evaluate("Ei(-50 + i)"));
        Assert.Equal(LineKind.Result, eiNegCx.Kind);
        Assert.InRange(eiNegCx.Value!.ComplexValue!.Value.Re, -1e-20, 1e-20);
        Assert.InRange(eiNegCx.Value.ComplexValue.Value.Im, Math.PI - 1e-12, Math.PI + 1e-12);

        // lambertw(-1) converges via non-singular fallback seed
        var lwNeg1 = Assert.Single(calc.Evaluate("lambertw(-1)"));
        Assert.Equal(LineKind.Result, lwNeg1.Kind);
        Assert.InRange(lwNeg1.Value!.ComplexValue!.Value.Re, -0.318133, -0.318130);
        Assert.InRange(lwNeg1.Value.ComplexValue.Value.Im, 1.337234, 1.337237);

        // Large integer exponent parity preserved above 2^53
        Assert.Equal("-1", Assert.Single(calc.Evaluate("(-1)^(2^53 + 1)")).Text);
        Assert.Equal("1", Assert.Single(calc.Evaluate("(-1)^(2^53)")).Text);

        // Partial pivoting preserves accuracy for tiny approximate pivots
        var inv = Assert.Single(calc.Evaluate("inverse([1e-101, 1; 1, 1])"));
        Assert.Equal(LineKind.Result, inv.Kind);
        Assert.InRange(inv.Value!.Items![0].Items![0].Frac, -1.0001, -0.9999);

        // Lower-tail normal CDF preserves representable small probabilities
        var nd = Assert.Single(calc.Evaluate("normdist(-10, 0, 1, 1)"));
        Assert.Equal(LineKind.Result, nd.Kind);
        Assert.InRange(nd.Value!.Frac, 7.6198e-24, 7.6199e-24);

        // Relative noise suppression preserves small-scale polynomial fits
        var qf = Assert.Single(calc.Evaluate("quadraticfit([1e-13, 4e-13, 9e-13])"));
        Assert.Equal(LineKind.Result, qf.Kind);
        Assert.NotEqual("0", qf.Text);
        Assert.Contains("x^2", qf.Text);
    }
}
