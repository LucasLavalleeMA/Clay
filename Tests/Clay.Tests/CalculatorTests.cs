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
        var result = new Calculator().Evaluate(expression).ShouldHaveSingleItem();

        result.Kind.ShouldBe(LineKind.Result);
        result.Text.ShouldBe(expected);
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
        result.Kind.ShouldBe(LineKind.Result);
        result.Text.ShouldBe(expected);
    }

    [Theory]
    [InlineData("sin(30)", "0.5")]
    [InlineData("Sin(30)", "0.5")]
    [InlineData("SIN(30)", "0.5")]
    [InlineData("ASIN(0.5)", "30°")]
    public void FunctionNamesIgnoreCaseInDegreesMode(string expression, string expected)
    {
        var result = new Calculator { AngleMode = AngleMode.Degrees }.Evaluate(expression).ShouldHaveSingleItem();
        result.Text.ShouldBe(expected);
    }

    [Theory]
    [InlineData("[1, [2, 3]]\n4\ntotal", "10")]
    [InlineData("[1, 2]\n3\naverage", "2")]
    [InlineData("[1 km, 500 m]\ntotal", "1.5 km")]
    public void BlockAggregatesFlattenLists(string document, string expected)
    {
        var result = new Calculator().Evaluate(document)[^1];
        result.Kind.ShouldBe(LineKind.Result);
        result.Text.ShouldBe(expected);
    }

    [Fact]
    public void BlockAggregateRejectsIncompatibleUnits()
    {
        var result = new Calculator().Evaluate("1 km\n2 kg\ntotal")[^1];
        result.Kind.ShouldBe(LineKind.Error);
        result.Text.ShouldContain("Can't mix");
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
        var result = new Calculator().Evaluate($"{name} = 5").ShouldHaveSingleItem();
        result.Kind.ShouldBe(LineKind.Error);
        result.Text.ShouldContain("reserved");
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
        var result = new Calculator().Evaluate(expression).ShouldHaveSingleItem();
        result.Kind.ShouldBe(LineKind.Result);
        result.Text.ShouldBe(expected);
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
        var result = new Calculator().Evaluate(expression).ShouldHaveSingleItem();
        result.Kind.ShouldBe(LineKind.Error);
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
        result.Kind.ShouldBe(LineKind.Result);
        result.Text.ShouldBe(expected);
    }

    [Fact]
    public void UnknownWordsRemainErrors()
    {
        var result = new Calculator().Evaluate("Buy milk").ShouldHaveSingleItem();
        result.Kind.ShouldBe(LineKind.Error);
        result.Text.ShouldContain("Unknown variable");
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
        result.Kind.ShouldBe(LineKind.Result);
        result.Text.ShouldBe(plain ?? Decode(expected!));
    }

    [Theory]
    [InlineData("bHVrYXM=")]
    [InlineData("bHVjYXMy")]
    public void PresetWordsNeedExactMatch(string encoded)
    {
        var result = new Calculator().Evaluate(Decode(encoded)).ShouldHaveSingleItem();
        result.Text.ShouldNotBe(Decode("ZHVrZXM="));
    }

    [Fact]
    public void LeadingSlashSlashRemainsCommentWhileInlineHashIsCommentAndInlineSlashSlashDivides()
    {
        var results = new Calculator().Evaluate("// full-line comment\n5 + 3 # inline comment\n5 // 2 # integer division");
        results[0].Kind.ShouldBe(LineKind.Comment);
        results[1].Kind.ShouldBe(LineKind.Result);
        results[1].Text.ShouldBe("8");
        results[2].Kind.ShouldBe(LineKind.Result);
        results[2].Text.ShouldBe("2");
    }

    [Fact]
    public void IgnoreWhitespaceSettingControlsSpacesInsideNumbers()
    {
        var strict = new Calculator { IgnoreWhitespace = false }.Evaluate("123 456").ShouldHaveSingleItem();
        strict.Kind.ShouldBe(LineKind.Blank);

        var calc = new Calculator { IgnoreWhitespace = true, GroupDigits = false };
        calc.Evaluate("123 456 789").ShouldHaveSingleItem().Text.ShouldBe("123456789");
        calc.Evaluate("1 plus 2").ShouldHaveSingleItem().Text.ShouldBe("3");
        calc.Evaluate("10 km in miles").ShouldHaveSingleItem().Text.ShouldBe("6.213712 mi");
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
        var result = new Calculator { GroupDigits = false }.Evaluate(expression).ShouldHaveSingleItem();
        result.Kind.ShouldBe(LineKind.Result);
        result.Text.ShouldBe(expected);
    }

    [Theory]
    [InlineData("rect(2 m, 3 s)")]
    [InlineData("rect(2 m, 3)")]
    [InlineData("1 << 5000")]
    public void ReviewFindingsRejectInvalidPrecisionOrDimensions(string expression)
    {
        var result = new Calculator().Evaluate(expression).ShouldHaveSingleItem();
        result.Kind.ShouldBe(LineKind.Error);
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
        var result = new Calculator { GroupDigits = false }.Evaluate(expression).ShouldHaveSingleItem();
        result.Kind.ShouldBe(LineKind.Result);
        result.Text.ShouldBe(expected);
    }

    [Fact]
    public void ReciprocalUnitConversionUsesLogFallbackForExtremeScales()
    {
        var calc = new Calculator { GroupDigits = false };
        var hi = calc.Evaluate("1e-300 km^-20 to km^20").ShouldHaveSingleItem();
        hi.Kind.ShouldBe(LineKind.Result);
        double.IsFinite(hi.Value!.Number).ShouldBeTrue();
        hi.Value.Number.ShouldBeInRange(0.99e300, 1.01e300);

        var lo = calc.Evaluate("1e300 km^20 to km^-20").ShouldHaveSingleItem();
        lo.Kind.ShouldBe(LineKind.Result);
        double.IsFinite(lo.Value!.Number).ShouldBeTrue();
        lo.Value.Number.ShouldBeInRange(0.99e-300, 1.01e-300);

        var modExtreme = calc.Evaluate("1e308 mod 1e-308").ShouldHaveSingleItem();
        modExtreme.Kind.ShouldBe(LineKind.Result);
        double.IsFinite(modExtreme.Value!.Number).ShouldBeTrue();
    }

    [Fact]
    public void StringLiteralsPreserveHashAndSupportUnicode()
    {
        var calc = new Calculator { GroupDigits = false };
        calc.Evaluate("\"foo # bar\" # trailing comment").ShouldHaveSingleItem().Text.ShouldBe("\"foo # bar\"");
        calc.Evaluate("len(\"😀🍉\")").ShouldHaveSingleItem().Text.ShouldBe("2");
    }

    [Fact]
    public void ExactNumberEdgeCasesAndDoubleStarUnitPowers()
    {
        var calc = new Calculator { GroupDigits = false };

        var bigRatio = calc.Evaluate("(2^1022)/3").ShouldHaveSingleItem();
        bigRatio.Kind.ShouldBe(LineKind.Result);
        double.IsFinite(bigRatio.Value!.Number).ShouldBeTrue();
        bigRatio.Value.Number.ShouldBeInRange(1.49e307, 1.50e307);

        var smallRatio = calc.Evaluate("1/(2^1022)").ShouldHaveSingleItem();
        smallRatio.Kind.ShouldBe(LineKind.Result);
        smallRatio.Value!.Number.ShouldBeInRange(2.22e-308, 2.23e-308);

        var minExp = calc.Evaluate("1e-2147483648").ShouldHaveSingleItem();
        minExp.Kind.ShouldBe(LineKind.Result);
        minExp.Text.ShouldBe("0");

        var unitPow = calc.Evaluate("1 m/s**2").ShouldHaveSingleItem();
        unitPow.Text.ShouldBe("1 m/s²");
        calc.Evaluate("\"a**b\"").ShouldHaveSingleItem().Text.ShouldBe("\"a**b\"");

        var a = ExactNumber.FromLong(9007199254740992L);
        var b = ExactNumber.FromLong(9007199254740993L);
        var c = ExactNumber.FromDouble(9007199254740992.0);
        a.Equals(b).ShouldBeFalse();
        a.Equals(c).ShouldBeFalse();
        b.Equals(c).ShouldBeFalse();
    }

    [Fact]
    public void DateStringAndLabelReviewFindings()
    {
        var calc = new Calculator { GroupDigits = false, IgnoreWhitespace = true };

        calc.Evaluate("\"9999-12-31\" + 1 d").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        calc.Evaluate("addDays(\"9999-12-31\"; 1)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        calc.Evaluate("stamptodate(10^20)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        calc.Evaluate("stamptodate(10^12)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        calc.Evaluate("\"2020-01-01\" to utc+15").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        calc.Evaluate("\"2020-01-01\" to utc+5.123").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        CalcDate.TryParse("2020-01-01T00:00+00:99", out _).ShouldBeFalse();
        calc.Evaluate("Rent (monthly): 100").ShouldHaveSingleItem().Text.ShouldBe("100");
        calc.Evaluate("rect(2 m to base; 3 m)").ShouldHaveSingleItem().Text.ShouldBe("6 m²");
        calc.Evaluate("float(0100 0010 0101 0001 0110 0001 0100 1000)").ShouldHaveSingleItem().Text.ShouldBe("52.345001");
        calc.Evaluate("\"a − b\"").ShouldHaveSingleItem().Text.ShouldBe("\"a − b\"");
        calc.Evaluate("code(\"×\")").ShouldHaveSingleItem().Text.ShouldBe("215");
        calc.Evaluate("char(39)").ShouldHaveSingleItem().Text.ShouldBe("'\\''");
        calc.Evaluate("char(92)").ShouldHaveSingleItem().Text.ShouldBe("'\\\\'");
    }

    [Fact]
    public void SymbolicPolynomialAndEquationBehavior()
    {
        var calc = new Calculator { GroupDigits = false };

        calc.Evaluate("x^3 - 5x^2 - 4x + 20 = 0").ShouldHaveSingleItem().Text.ShouldBe("x = 5 or x = 2 or x = -2");
        calc.Evaluate("10 - x% = 8").ShouldHaveSingleItem().Text.ShouldBe("x = 20");
        calc.Evaluate("factor x + 2*sqrt(x) + 1").ShouldHaveSingleItem().Text.ShouldBe("(sqrt(x) + 1)^2");
        calc.Evaluate("primpart(2xy + 8y + 16, y)").ShouldHaveSingleItem().Text.ShouldBe("xy + 4y + 8");
        calc.Evaluate("abs(x - abs(x)) - (abs(x) - x)").ShouldHaveSingleItem().Text.ShouldBe("0");

        var posCalc = new Calculator { GroupDigits = false, AssumePositive = true };
        posCalc.Evaluate("ln(x^2 + 2*x + 1)").ShouldHaveSingleItem().Text.ShouldBe("2 * ln(x + 1)");
        posCalc.Evaluate("x^(log(y, x))").ShouldHaveSingleItem().Text.ShouldBe("y");
    }

    [Fact]
    public void SolverHandlesExponentialLambertWTrigAndNumericRootFinding()
    {
        var calc = new Calculator { GroupDigits = false };

        calc.Evaluate("5^x = 3").ShouldHaveSingleItem().Text.ShouldBe("x = ln(3) / ln(5)");
        calc.Evaluate("ln(x) + x = 3").ShouldHaveSingleItem().Text.ShouldBe("x = lambertw(e^3)");
        calc.Evaluate("x^4 + 20x^3 + 150x^2 + 500x + 625 = 0").ShouldHaveSingleItem().Text.ShouldBe("x = -5");

        var newtonReal = calc.Evaluate("newtonsolve(Ei(x) = 3, 1)").ShouldHaveSingleItem();
        newtonReal.Kind.ShouldBe(LineKind.Result);
        newtonReal.Value!.Frac.ShouldBeInRange(1.397510, 1.397512);

        var secantReal = calc.Evaluate("secantsolve(Ei(x) = 3, 1, 4)").ShouldHaveSingleItem();
        secantReal.Kind.ShouldBe(LineKind.Result);
        secantReal.Value!.Frac.ShouldBeInRange(1.397510, 1.397512);

        var newtonComplex = calc.Evaluate("newtonsolve(Ei(x) = 3i, 1)").ShouldHaveSingleItem();
        newtonComplex.Kind.ShouldBe(LineKind.Result);
        newtonComplex.Value!.ComplexValue!.Value.Re.ShouldBeInRange(-1.160851, -1.160848);
        newtonComplex.Value.ComplexValue.Value.Im.ShouldBeInRange(1.034282, 1.034285);
    }

    [Fact]
    public void CalculusDiffIntegrateRombergAndSpecialFunctions()
    {
        var calc = new Calculator { GroupDigits = false };

        calc.Evaluate("diff(6x^2)").ShouldHaveSingleItem().Text.ShouldBe("12x");
        calc.Evaluate("integrate(6x^2)").ShouldHaveSingleItem().Text.ShouldBe("2x^3 + C");
        calc.Evaluate("integrate(6x^2; 1; 5)").ShouldHaveSingleItem().Text.ShouldBe("248");

        var rom = calc.Evaluate("romberg(5x + ln(x), 1, 5)").ShouldHaveSingleItem();
        rom.Value!.Frac.ShouldBeInRange(64.047188, 64.047191);

        var fs = calc.Evaluate("fresnels(5)").ShouldHaveSingleItem();
        fs.Value!.Frac.ShouldBeInRange(0.499190, 0.499193);

        var gi = calc.Evaluate("gammainc(53, 5.2)").ShouldHaveSingleItem();
        gi.Value!.Frac.ShouldBeInRange(1.0219e34, 1.0221e34);

        var bi = calc.Evaluate("betainc(5i - 2, 32, 3.2)").ShouldHaveSingleItem();
        bi.Value!.ComplexValue!.Value.Re.ShouldBeInRange(-9.432e27, -9.430e27);
        bi.Value.ComplexValue.Value.Im.ShouldBeInRange(-5.084e27, -5.082e27);
    }

    [Fact]
    public void LimitsHandleFiniteInfiniteIndeterminateAndSymbolicCases()
    {
        var calc = new Calculator { GroupDigits = false };

        calc.Evaluate("limit(x^2 - 4, 2)").ShouldHaveSingleItem().Text.ShouldBe("0");
        calc.Evaluate("limit(x^3 / (x + 1)^2, -1)").ShouldHaveSingleItem().Text.ShouldBe("-∞");
        calc.Evaluate("limit((x^2 + 2x + 3) / (x - 1)^2, 1)").ShouldHaveSingleItem().Text.ShouldBe("∞");
        calc.Evaluate("limit((root(x^5, 4) + root(x^3, 5) + root(x^8, 6)) / cbrt(x^4 + 2), infinity)").ShouldHaveSingleItem().Text.ShouldBe("1");

        var e2 = calc.Evaluate("limit((1 - sin(x)/x)^(1/ln(x)), 0)").ShouldHaveSingleItem();
        e2.Value!.Frac.ShouldBeInRange(Math.Exp(2) - 1e-4, Math.Exp(2) + 1e-4);

        var posCalc = new Calculator { GroupDigits = false, AssumePositive = true };
        posCalc.Evaluate("limit((1 - x^(1/z)) / (1 - x^(1/y)), 1)").ShouldHaveSingleItem().Text.ShouldBe("y / z");
    }

    [Fact]
    public void CopilotReviewRound4Regressions()
    {
        var calc = new Calculator { GroupDigits = false };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Interval mode 2: mul, div, pow
        var mulZero = ic2.Evaluate("(0 +/- 1) * (0 +/- 1)").ShouldHaveSingleItem();
        mulZero.Kind.ShouldBe(LineKind.Result);
        mulZero.Value!.ComplexValue!.Value.Re.ShouldBe(0, 0.5e-6);
        mulZero.Value.ComplexValue.Value.ReErr.ShouldBe(1, 0.5e-6);

        var divIc2 = ic2.Evaluate("1 / (2 +/- 1)").ShouldHaveSingleItem();
        divIc2.Kind.ShouldBe(LineKind.Result);
        (divIc2.Value!.ComplexValue!.Value.Re + divIc2.Value.ComplexValue.Value.ReErr).ShouldBeInRange(0.999, 1.001);
        ic2.Evaluate("1 / (0 +/- 1)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        ic2.Evaluate("(1 +/- 2)^(-1)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        ic2.Evaluate("(1 +/- 2)^0").ShouldHaveSingleItem().Text.ShouldBe("1");

        // Lambert W at 0.5 and 1
        var lwHalf = calc.Evaluate("lambertw(0.5)").ShouldHaveSingleItem();
        lwHalf.Value!.ComplexValue!.Value.Re.ShouldBeInRange(0.351733, 0.351735);
        lwHalf.Value.ComplexValue.Value.Im.ShouldBe(0, 0.5e-10);
        var lwOne = calc.Evaluate("lambertw(1)").ShouldHaveSingleItem();
        lwOne.Value!.ComplexValue!.Value.Re.ShouldBeInRange(0.567142, 0.567144);
        lwOne.Value.ComplexValue.Value.Im.ShouldBe(0, 0.5e-10);

        // Linear algebra unit preservation / rejection & flat-list * matrix
        calc.Evaluate("transpose([1 m, 2 m])").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        calc.Evaluate("2 m .* 3 s").ShouldHaveSingleItem().Text.ShouldBe("6 m·s");
        calc.Evaluate("dot([1 m, 1 m], [1 m, 100 cm])").ShouldHaveSingleItem().Text.ShouldBe("2 m²");
        calc.Evaluate("cross([1 m, 0 m, 0 m], [0 cm, 100 cm, 0 cm])").ShouldHaveSingleItem().Text.ShouldBe("[0 m², 0 m², 1 m²]");
        calc.Evaluate("norm([3 m, 400 cm])").ShouldHaveSingleItem().Text.ShouldBe("5 m");
        calc.Evaluate("[1 2] * [3; 4]").ShouldHaveSingleItem().Text.ShouldBe("11");

        // Symbolic & complex unit rejection
        calc.Evaluate("x * 2 m").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        calc.Evaluate("2 m * i").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Symbolic edge cases: ln((x-2)^2), factor(x^3+x+1), x^4-2x=0, approximate keys, percentages, pcontent/primpart, pow parens
        var posCalc = new Calculator { GroupDigits = false, AssumePositive = true };
        posCalc.Evaluate("ln((x - 2)^2)").ShouldHaveSingleItem().Text.ShouldBe("ln((x - 2)^2)");
        calc.Evaluate("factor(x^3 + x + 1)").ShouldHaveSingleItem().Text.ShouldBe("x^3 + x + 1");
        var quartic = calc.Evaluate("x^4 - 2x = 0").ShouldHaveSingleItem();
        quartic.Kind.ShouldBe(LineKind.Result);
        ((SymExpr.Solutions)quartic.Value!.SymValue!).Roots.Count.ShouldBe(2);
        calc.Evaluate("ln(x + pi) - ln(x + e)").ShouldHaveSingleItem().Text.ShouldNotBe("0");
        calc.Evaluate("10% + x% = 0.3").ShouldHaveSingleItem().Text.ShouldBe("x = 20");
        calc.Evaluate("percentile([1, 2, 3, 4], 25, 1)").ShouldHaveSingleItem().Text.ShouldBe("1");
        calc.Evaluate("pcontent(x*y + x, y)").ShouldHaveSingleItem().Text.ShouldBe("x");
        calc.Evaluate("primpart(x*y + x, y)").ShouldHaveSingleItem().Text.ShouldBe("y + 1");
        calc.Evaluate("x^(1/3)").ShouldHaveSingleItem().Text.ShouldBe("x^(1/3)");
        calc.Evaluate("(1/2)^x").ShouldHaveSingleItem().Text.ShouldBe("(1/2)^x");
    }

    [Fact]
    public void CopilotReviewRound5Regressions()
    {
        var calc = new Calculator { GroupDigits = false, IgnoreWhitespace = true };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Integer matrix powers vs entrywise pow/.^
        calc.Evaluate("[1 2; 3 4]^2").ShouldHaveSingleItem().Text.ShouldBe("[[7, 10], [15, 22]]");
        calc.Evaluate("[1 2; 3 4]^0").ShouldHaveSingleItem().Text.ShouldBe("[[1, 0], [0, 1]]");
        calc.Evaluate("pow([1 2; 3 4], 2)").ShouldHaveSingleItem().Text.ShouldBe("[[1, 4], [9, 16]]");
        calc.Evaluate("[1 2; 3 4].^2").ShouldHaveSingleItem().Text.ShouldBe("[[1, 4], [9, 16]]");

        // gammainc large x upper-tail branch
        var gi = calc.Evaluate("gammainc(1, 1000)").ShouldHaveSingleItem();
        gi.Kind.ShouldBe(LineKind.Result);
        gi.Value!.Frac.ShouldBeInRange(0.999999, 1.000001);

        // betainc propagates uncertainty
        var bi = calc.Evaluate("betainc(0.5 +/- 0.1, 1, 1)").ShouldHaveSingleItem();
        bi.Kind.ShouldBe(LineKind.Result);
        bi.Value!.ComplexValue!.Value.Re.ShouldBeInRange(0.4999, 0.5001);
        bi.Value.ComplexValue.Value.ReErr.ShouldBeInRange(0.0999, 0.1001);

        // Ei complex uncertainty, mode-2 zero-crossing rejection, and negative-tail stability
        var eiCx = calc.Evaluate("Ei(2 + (3 +/- 0.1)*i)").ShouldHaveSingleItem();
        eiCx.Kind.ShouldBe(LineKind.Result);
        eiCx.Value!.ComplexValue!.Value.HasError.ShouldBeTrue();
        ic2.Evaluate("Ei(0 +/- 1)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        var eiNeg = calc.Evaluate("Ei(-50)").ShouldHaveSingleItem();
        eiNeg.Kind.ShouldBe(LineKind.Result);
        eiNeg.Value!.Frac.ShouldBeInRange(-3.79e-24, -3.77e-24);

        // lambertw(0 +/- 0.1) uses W0'(0) = 1
        var lwZero = calc.Evaluate("lambertw(0 +/- 0.1)").ShouldHaveSingleItem();
        lwZero.Kind.ShouldBe(LineKind.Result);
        lwZero.Value!.ComplexValue!.Value.Re.ShouldBe(0, 0.5e-10);
        lwZero.Value.ComplexValue.Value.ReErr.ShouldBe(0.1, 0.5e-10);

        // Binary literals inside brackets don't swallow adjacent vector elements
        calc.Evaluate("[0b1 1]").ShouldHaveSingleItem().Text.ShouldBe("[1, 1]");

        // Statistics reject unit-bearing inputs
        calc.Evaluate("stdev(1 m; 100 cm)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Out-of-range integer matrix exponent rejects instead of falling through to entrywise pow
        calc.Evaluate("[1 1; 0 1]^1001").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Mode-2 Lambert W encloses real endpoints and rejects unsupported uncertain domains
        var lwIc2 = ic2.Evaluate("lambertw(0 +/- 0.1)").ShouldHaveSingleItem();
        lwIc2.Kind.ShouldBe(LineKind.Result);
        var lwVal = lwIc2.Value!.ComplexValue!.Value;
        (lwVal.Re - lwVal.ReErr).ShouldBeInRange(-0.111834, -0.111831);
        (lwVal.Re + lwVal.ReErr).ShouldBeInRange(0.091275, 0.091278);
        ic2.Evaluate("lambertw(-0.5 +/- 0.1)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
    }

    [Fact]
    public void CopilotReviewRound7Regressions()
    {
        var calc = new Calculator { GroupDigits = false };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Mode-2 complex denominator enclosure & origin rejection
        var invCx = ic2.Evaluate("1 / (2 + (0 +/- 1)*i)").ShouldHaveSingleItem();
        invCx.Kind.ShouldBe(LineKind.Result);
        var invVal = invCx.Value!.ComplexValue!.Value;
        invVal.Re.ShouldBe(0.45, 0.5e-10);
        invVal.ReErr.ShouldBe(0.05, 0.5e-10);
        invVal.Im.ShouldBe(0.0, 0.5e-10);
        invVal.ImErr.ShouldBe(0.2, 0.5e-10);
        ic2.Evaluate("1 / ((0 +/- 1) + (0 +/- 1)*i)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Mode-2 ln branch cut enclosure & origin rejection
        var lnCut = ic2.Evaluate("ln(-1 + (0 +/- 1)*i)").ShouldHaveSingleItem();
        lnCut.Kind.ShouldBe(LineKind.Result);
        var lnVal = lnCut.Value!.ComplexValue!.Value;
        lnVal.Im.ShouldBe(0.0, 0.5e-10);
        lnVal.ImErr.ShouldBe(Math.PI, 0.5e-10);
        ic2.Evaluate("ln(0 +/- 1)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Lambert W exact branch-point value at -1/e
        var lw0 = calc.Evaluate("lambertw(-1/e)").ShouldHaveSingleItem();
        lw0.Kind.ShouldBe(LineKind.Result);
        lw0.Value!.ComplexValue!.Value.Re.ShouldBe(-1.0, 0.5e-12);
        lw0.Value.ComplexValue.Value.Im.ShouldBe(0.0, 0.5e-12);

        var lwNeg1 = calc.Evaluate("lambertw(-1/e, -1)").ShouldHaveSingleItem();
        lwNeg1.Kind.ShouldBe(LineKind.Result);
        lwNeg1.Value!.ComplexValue!.Value.Re.ShouldBe(-1.0, 0.5e-12);
        lwNeg1.Value.ComplexValue.Value.Im.ShouldBe(0.0, 0.5e-12);

        var lwBranchInterval = ic2.Evaluate("lambertw((-1/e + 0.1) +/- 0.1)").ShouldHaveSingleItem();
        lwBranchInterval.Kind.ShouldBe(LineKind.Result);
        var lwBr = lwBranchInterval.Value!.ComplexValue!.Value;
        (lwBr.Re - lwBr.ReErr).ShouldBe(-1.0, 0.5e-12);

        // Relative root tolerance preserves small complex roots
        var smallRoots = calc.Evaluate("1e14 * allroots(-1e-28, 2)").ShouldHaveSingleItem();
        smallRoots.Kind.ShouldBe(LineKind.Result);
        smallRoots.Text.ShouldBe("[1i, -1i]");
        smallRoots.Value!.Items!.Count.ShouldBe(2);
        smallRoots.Value.Items[0].ComplexValue!.Value.Re.ShouldBe(0.0, 0.5e-12);
        smallRoots.Value.Items[0].ComplexValue!.Value.Im.ShouldBe(1.0, 0.5e-12);
        smallRoots.Value.Items[1].ComplexValue!.Value.Re.ShouldBe(0.0, 0.5e-12);
        smallRoots.Value.Items[1].ComplexValue!.Value.Im.ShouldBe(-1.0, 0.5e-12);

        // Real-only conversions reject intervals instead of discarding uncertainty
        calc.Evaluate("det([1 +/- 0.1, 0; 0, 1])").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        calc.Evaluate("~(1 +/- 0.1)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Unit-bearing uncertainty operands are rejected
        calc.Evaluate("2 +/- 3 m").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Right-side percentage applies to uncertain values
        var pctAdd = calc.Evaluate("(100 +/- 1) + 10%").ShouldHaveSingleItem();
        pctAdd.Kind.ShouldBe(LineKind.Result);
        pctAdd.Value!.ComplexValue!.Value.Re.ShouldBe(110.0, 0.5e-10);
        pctAdd.Value.ComplexValue.Value.ReErr.ShouldBe(1.1, 0.5e-10);

        var pctSub = calc.Evaluate("(100 +/- 1) - 10%").ShouldHaveSingleItem();
        pctSub.Kind.ShouldBe(LineKind.Result);
        pctSub.Value!.ComplexValue!.Value.Re.ShouldBe(90.0, 0.5e-10);
        pctSub.Value.ComplexValue.Value.ReErr.ShouldBe(0.9, 0.5e-10);
    }

    [Fact]
    public void CopilotReviewRound8Regressions()
    {
        var calc = new Calculator { GroupDigits = false };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Mode-2 uncertain exponent enclosure & unsupported domain rejection
        var powIc2 = ic2.Evaluate("(2 +/- 1)^(2 +/- 1)").ShouldHaveSingleItem();
        powIc2.Kind.ShouldBe(LineKind.Result);
        powIc2.Value!.ComplexValue!.Value.Re.ShouldBe(14.0, 0.5e-10);
        powIc2.Value.ComplexValue.Value.ReErr.ShouldBe(13.0, 0.5e-10);
        ic2.Evaluate("(-2 +/- 1)^(2 +/- 1)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Mode-2 uncertain complex Ei rejects instead of returning unsound bounds
        ic2.Evaluate("Ei(3 + (0 +/- 0.1)*i)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        ic2.Evaluate("Ei((1 +/- 2) + (0 +/- 1)*i)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Resource-exhaustion bounds on powertower and allroots
        calc.Evaluate("powertower(1, 1000000000)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);
        calc.Evaluate("allroots(1, 1000000000)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Tail-relative precision for chisqdistinv
        var chi = calc.Evaluate("chisqdistinv(1e-20, 2)").ShouldHaveSingleItem();
        chi.Kind.ShouldBe(LineKind.Result);
        chi.Value!.Frac.ShouldBeInRange(1.999999e-20, 2.000001e-20);

        // Unicode digits in date strings return false instead of throwing FormatException
        CalcDate.TryParse("２０２０-０１-０１", out _).ShouldBeFalse();
        CalcDate.TryParse("２０２０-０１-０１T００:００", out _).ShouldBeFalse();
        CalcDate.TryParse("０１/０１/２０２０", out _).ShouldBeFalse();

        // Exact values beyond IEEE-754 double range format as finite scientific notation
        calc.Evaluate("2^1024").ShouldHaveSingleItem().Text.ShouldBe("1.797693E+308");
        calc.Evaluate("-(2^1024)").ShouldHaveSingleItem().Text.ShouldBe("-1.797693E+308");
        calc.Evaluate("2^(-1100)").ShouldHaveSingleItem().Text.ShouldBe("7.362152E-332");
    }

    [Fact]
    public void CopilotReviewRound9Regressions()
    {
        var calc = new Calculator { GroupDigits = false };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Date fractional seconds preserved in Format() and accepted by TryParse()
        var fracDate = calc.Evaluate("\"2020-01-01\" + 0.5 s").ShouldHaveSingleItem();
        fracDate.Kind.ShouldBe(LineKind.Result);
        fracDate.Text.ShouldBe("\"2020-01-01T00:00:00.5Z\"");
        CalcDate.TryParse("2020-01-01T00:00:00.5Z", out var parsedFrac).ShouldBeTrue();
        parsedFrac.Instant.ShouldBe(fracDate.Value!.DateValue!.Value.Instant);

        // Mode-2 scaled complex reciprocal avoids squaring overflow
        var largeDiv = ic2.Evaluate("1e200 / (1e200 + (0 +/- 1e199)*i)").ShouldHaveSingleItem();
        largeDiv.Kind.ShouldBe(LineKind.Result);
        var ldVal = largeDiv.Value!.ComplexValue!.Value;
        1.0.ShouldBeInRange(ldVal.Re - ldVal.ReErr - 1e-12, ldVal.Re + ldVal.ReErr + 1e-12);

        // Mode-2 scaled interval logarithm avoids squaring overflow
        var largeLn = ic2.Evaluate("ln(1e200 +/- 1e199)").ShouldHaveSingleItem();
        largeLn.Kind.ShouldBe(LineKind.Result);
        var llnVal = largeLn.Value!.ComplexValue!.Value;
        (llnVal.Re - llnVal.ReErr).ShouldBe(Math.Log(9e199), 0.5e-6);
        (llnVal.Re + llnVal.ReErr).ShouldBe(Math.Log(1.1e200), 0.5e-6);

        // Complex Ei for negative real part avoids catastrophic cancellation
        var eiNegCx = calc.Evaluate("Ei(-50 + i)").ShouldHaveSingleItem();
        eiNegCx.Kind.ShouldBe(LineKind.Result);
        eiNegCx.Value!.ComplexValue!.Value.Re.ShouldBeInRange(-1e-20, 1e-20);
        eiNegCx.Value.ComplexValue.Value.Im.ShouldBeInRange(Math.PI - 1e-12, Math.PI + 1e-12);

        // lambertw(-1) converges via non-singular fallback seed
        var lwNeg1 = calc.Evaluate("lambertw(-1)").ShouldHaveSingleItem();
        lwNeg1.Kind.ShouldBe(LineKind.Result);
        lwNeg1.Value!.ComplexValue!.Value.Re.ShouldBeInRange(-0.318133, -0.318130);
        lwNeg1.Value.ComplexValue.Value.Im.ShouldBeInRange(1.337234, 1.337237);

        // Large integer exponent parity preserved above 2^53
        calc.Evaluate("(-1)^(2^53 + 1)").ShouldHaveSingleItem().Text.ShouldBe("-1");
        calc.Evaluate("(-1)^(2^53)").ShouldHaveSingleItem().Text.ShouldBe("1");

        // Partial pivoting preserves accuracy for tiny approximate pivots
        var inv = calc.Evaluate("inverse([1e-101, 1; 1, 1])").ShouldHaveSingleItem();
        inv.Kind.ShouldBe(LineKind.Result);
        inv.Value!.Items![0].Items![0].Frac.ShouldBeInRange(-1.0001, -0.9999);

        // Lower-tail normal CDF preserves representable small probabilities
        var nd = calc.Evaluate("normdist(-10, 0, 1, 1)").ShouldHaveSingleItem();
        nd.Kind.ShouldBe(LineKind.Result);
        nd.Value!.Frac.ShouldBeInRange(7.6198e-24, 7.6199e-24);

        // Relative noise suppression preserves small-scale polynomial fits
        var qf = calc.Evaluate("quadraticfit([1e-13, 4e-13, 9e-13])").ShouldHaveSingleItem();
        qf.Kind.ShouldBe(LineKind.Result);
        qf.Text.ShouldNotBe("0");
        qf.Text.ShouldContain("x^2");
    }

    [Fact]
    public void UpstreamPr5ReviewRegressions()
    {
        var calc = new Calculator { GroupDigits = false };
        var ic2 = new Calculator { GroupDigits = false, IntervalMode = 2 };

        // Mode-2 betainc encloses monotone real intervals and rejects unsupported domains
        var biIc2 = ic2.Evaluate("betainc(0.5 +/- 0.4, 2, 1)").ShouldHaveSingleItem();
        biIc2.Kind.ShouldBe(LineKind.Result);
        var biVal = biIc2.Value!.ComplexValue!.Value;
        biVal.Re.ShouldBe(0.41, 0.5e-10);
        biVal.ReErr.ShouldBe(0.40, 0.5e-10);
        ic2.Evaluate("betainc(0.5 + (0 +/- 0.1)*i, 2, 1)").ShouldHaveSingleItem().Kind.ShouldBe(LineKind.Error);

        // Large Fresnel arguments use bounded asymptotic expansion
        var fsLarge = calc.Evaluate("fresnels(10000)").ShouldHaveSingleItem();
        fsLarge.Kind.ShouldBe(LineKind.Result);
        fsLarge.Value!.Frac.ShouldBeInRange(0.4999, 0.5001);

        // Incomplete beta resolves endpoint singularity for a < 1
        var biSing = calc.Evaluate("betainc(0.5, 0.1, 1)").ShouldHaveSingleItem();
        biSing.Kind.ShouldBe(LineKind.Result);
        biSing.Value!.Frac.ShouldBe(Math.Pow(0.5, 0.1), 0.5e-10);

        // Numerical integration supports inverse-trig functions
        var intAtan = calc.Evaluate("integrate(atan(x), 0, 1)").ShouldHaveSingleItem();
        intAtan.Kind.ShouldBe(LineKind.Result);
        intAtan.Value!.Frac.ShouldBe(Math.PI / 4.0 - Math.Log(2.0) / 2.0, 0.5e-6);
    }
}
