using Clay.Engine;

namespace Clay.Tests;

public sealed class QalcDocumentEvaluatorTests
{
    [Fact]
    public async Task EvaluatesDependentVariablesFromTopToBottomAndResetsBetweenPasses()
    {
        using var calculator = new QalcDocumentEvaluator();

        var firstPass = await calculator.EvaluateAsync("subtotal = 3\ntotal = subtotal * 4\ntotal");
        Assert.Equal(["3", "12", "15"], firstPass.Select(result => result.Text));

        var secondPass = await calculator.EvaluateAsync("subtotal = 5\ntotal = subtotal * 4\ntotal");
        Assert.Equal(["5", "20", "25"], secondPass.Select(result => result.Text));
    }

    [Fact]
    public async Task ReorderingLinesDoesNotReuseAssignmentsFromAnEarlierPass()
    {
        using var calculator = new QalcDocumentEvaluator();
        await calculator.EvaluateAsync("subtotal = 5\ntotal = subtotal * 4");

        var reordered = await calculator.EvaluateAsync("total = subtotal * 4\nsubtotal = 5");

        Assert.NotEqual("20", reordered[0].Text);
        Assert.Equal(LineKind.Result, reordered[1].Kind);
    }

    [Fact]
    public async Task InjectsPreviousResultForDependentLines()
    {
        using var calculator = new QalcDocumentEvaluator();

        var results = await calculator.EvaluateAsync("2 + 3\nprev * 2");

        Assert.Equal("5", results[0].Text);
        Assert.Equal("10", results[1].Text);
    }

    [Theory]
    [InlineData("1\n2\nTotal", "3")]
    [InlineData("1\n2\nsum", "3")]
    [InlineData("2\n4\naverage", "3")]
    [InlineData("1 km\n500 m\nTotal", "1.5 km")]
    public async Task EvaluatesBlockAggregates(string document, string expected)
    {
        using var calculator = new QalcDocumentEvaluator();

        var results = await calculator.EvaluateAsync(document);

        Assert.True(results[^1].Kind == LineKind.Result, $"{results[^1].Kind}: {results[^1].Text}");
        Assert.Equal(expected, results[^1].Text);
    }

    [Fact]
    public async Task ResetsAggregatesAtBlankLinesAndHeadings()
    {
        using var calculator = new QalcDocumentEvaluator();

        var results = await calculator.EvaluateAsync("1\n\n2\nTotal\n# next\n3\nTotal");

        Assert.Equal(LineKind.Blank, results[1].Kind);
        Assert.Equal(LineKind.Result, results[3].Kind);
        Assert.Equal("2", results[3].Text);
        Assert.Equal("3", results[^1].Text);
    }

    [Fact]
    public async Task EvaluatesLabelsAndAdditivePercentagesAndSkipsComments()
    {
        using var calculator = new QalcDocumentEvaluator();

        var results = await calculator.EvaluateAsync("# heading\nGroceries: 100 + 20%\n// note\n");

        Assert.Equal(LineKind.Comment, results[0].Kind);
        Assert.Equal("120", results[1].Text);
        Assert.Equal(LineKind.Comment, results[2].Kind);
        Assert.Equal(LineKind.Blank, results[3].Kind);
    }

    [Fact]
    public async Task EvaluatesUnitsAndReportsMalformedSyntax()
    {
        using var calculator = new QalcDocumentEvaluator();

        var results = await calculator.EvaluateAsync("10 km to miles\n2 + * 3");

        Assert.Equal(LineKind.Result, results[0].Kind);
        Assert.Contains("mi", results[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LineKind.Error, results[1].Kind);
    }

    [Fact]
    public async Task SentinelKeepsFollowingExpressionsSynchronizedAfterErrors()
    {
        using var engine = new QalcProcessEngine();

        var malformed = await engine.EvaluateAsync("sin(");
        var following = await engine.EvaluateAsync("2 + 2");

        Assert.DoesNotContain("__CLAY_END_", malformed);
        Assert.Equal("4", following.Trim());
    }

    [Fact]
    public async Task HonorsAngleAndDecimalSettings()
    {
        using var calculator = new QalcDocumentEvaluator { AngleMode = AngleMode.Degrees, Decimals = 2 };

        var results = await calculator.EvaluateAsync("sin(30)\n1/3");

        Assert.Equal("0.5", results[0].Text);
        Assert.Equal("0.33", results[1].Text);
    }

    [Fact]
    public async Task AppliesDigitGroupingOnlyToDisplayedResults()
    {
        using var calculator = new QalcDocumentEvaluator { GroupDigits = true };

        var results = await calculator.EvaluateAsync("1234567\nprev + 1\nTotal");

        Assert.Equal("1,234,567", results[0].Text);
        Assert.Equal("1,234,568", results[1].Text);
        Assert.Equal("2,469,135", results[2].Text);
    }
}
