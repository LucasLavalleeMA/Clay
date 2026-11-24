using System.Text.RegularExpressions;
using Clay.Engine;
using Clay.Tests.Qalculate;
using FlaUI.Core.Tools;

namespace Clay.UITests;

[Trait("Category", "UI")]
public sealed class SmokeTests
{
    [UIFact]
    public void Launch_FirstRun_ShowsWelcomeSample()
    {
        using var app = new ClayApp();

        app.Window.Title.ShouldStartWith("Clay");

        string text = app.Editor.Text;
        text.ShouldContain("# Welcome to Clay");

        string[] lines = text.Replace("\r\n", "\n").Split(['\r', '\n']);
        int sampleLine = Array.FindIndex(lines, line => line.Trim() == "20% of 150");
        (sampleLine >= 0).ShouldBeTrue($"Expected '20% of 150' line in editor text:\n{text}");

        app.WaitForResult(sampleLine, "30").ShouldBe("30");
    }

    [UIFact]
    public void SimpleExpression_Evaluates()
    {
        using var app = new ClayApp();

        app.SetDocument("2 + 3");

        app.WaitForResult(0, "5").ShouldBe("5");
    }

    [UIFact]
    public void SingleLetterVariables_EvaluateAcrossLines()
    {
        using var app = new ClayApp();

        app.SetDocument("x = 5\ny = 3\nx + y");

        app.WaitForResult(2, "8").ShouldBe("8");
    }

    [UIFact]
    public void ReservedName_ShowsErrorFeedback()
    {
        using var app = new ClayApp();

        app.SetDocument("m = 5");

        app.WaitForResult(0, "'m' is a reserved name").ShouldBe("'m' is a reserved name");
    }

    [UIFact]
    public void UnitsBatch_UiResultsMatchEngine()
    {
        // Currency lines are dropped because the app may load live rates that the engine's built-in rates won't match.
        var cases = BatchFileParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "Qalculate", "units.batch"));
        var currency = new Regex(@"[$€£¥]|\b(" + string.Join("|", UnitTable.CurrencyCodes) + @")\b");
        string[] lines = [.. cases[^1].PriorExpressions.Append(cases[^1].Expression).Where(line => !currency.IsMatch(line))];
        string document = string.Join("\n", lines);

        var engine = new Calculator { PhysicsUnits = true }.Evaluate(document);
        using var app = new ClayApp("""{"Decimals":6,"AngleMode":"Radians","GroupDigits":true,"Theme":"Default","PhysicsUnits":true}""");
        app.SetDocument(document);
        app.WaitForResult(lines.Length - 1, engine[^1].Text);

        var mismatches = lines.Select((line, i) => (line, expected: engine[i].Text, actual: app.ResultText(i) ?? ""))
            .Where(r => r.actual != r.expected)
            .Select(r => $"`{r.line}`: UI `{r.actual}`, engine `{r.expected}`")
            .ToList();
        (engine.Count(r => r.Kind == LineKind.Result) > lines.Length / 2).ShouldBeTrue("Most lines should produce results");
        mismatches.ShouldBeEmpty(string.Join("\n", mismatches));
    }

    [UIFact]
    public void Autosave_WritesToCustomDataDirectory()
    {
        string realDoc = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clay",
            "document.txt");
        bool realExistedBefore = File.Exists(realDoc);
        DateTime? realLastWriteBefore = realExistedBefore ? File.GetLastWriteTimeUtc(realDoc) : null;

        using (var app = new ClayApp())
        {
            app.SetDocument("1 + 1");
            app.WaitForResult(0, "2").ShouldBe("2");

            string customDoc = Path.Combine(app.DataDirectory, "document.txt");
            string? savedContent = null;
            Retry.WhileFalse(() =>
            {
                if (!File.Exists(customDoc))
                    return false;
                savedContent = File.ReadAllText(customDoc);
                return savedContent.Contains("1 + 1");
            }, TimeSpan.FromSeconds(5), ignoreException: true);

            File.Exists(customDoc).ShouldBeTrue($"Expected autosave file at {customDoc}");
            (savedContent ?? "").ShouldContain("1 + 1");
        }

        if (realExistedBefore)
            File.GetLastWriteTimeUtc(realDoc).ShouldBe(realLastWriteBefore!.Value);
        else
            File.Exists(realDoc).ShouldBeFalse();
    }
}
