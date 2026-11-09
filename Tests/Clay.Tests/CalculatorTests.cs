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

    [Fact]
    public void UnknownWordsRemainErrors()
    {
        var result = Assert.Single(new Calculator().Evaluate("Buy milk"));
        Assert.Equal(LineKind.Error, result.Kind);
        Assert.Contains("Unknown variable", result.Text);
    }
}
