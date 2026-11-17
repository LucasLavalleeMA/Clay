using System.Text.RegularExpressions;

namespace Clay.Engine;

public sealed class QalcDocumentEvaluator : IDisposable
{
    private static readonly Regex Newline = new("\\r\\n|\\r|\\n", RegexOptions.Compiled);
    private static readonly Regex Label = new(@"^\p{L}[^:=]*:\s*(?<expression>\S.*)$", RegexOptions.Compiled);
    private static readonly Regex AdditivePercent = new(@"^(?<base>.+?)\s*\+\s*(?<percent>(?:\d+(?:\.\d*)?|\.\d+)\s*%)$", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<![\p{L}\d.,])(?<sign>[+-]?)(?<integer>\d+)(?<fraction>\.\d+)?(?<exponent>[eE][+-]?\d+)?(?![\p{L}\d])", RegexOptions.Compiled);
    private readonly QalcProcessEngine _engine;

    public int Decimals { get; set; } = 6;
    public bool GroupDigits { get; set; } = true;
    public AngleMode AngleMode { get; set; } = AngleMode.Radians;
    public bool PhysicsUnits { get; set; }

    public QalcDocumentEvaluator(QalcProcessEngine? engine = null) => _engine = engine ?? new QalcProcessEngine();

    public async Task<List<LineResult>> EvaluateAsync(string text, CancellationToken cancellationToken = default)
    {
        var lines = Newline.Split(text);
        try
        {
            await _engine.ResetVariablesAsync(cancellationToken);
            await _engine.EvaluateAsync($"set angle unit {(AngleMode == AngleMode.Degrees ? "degrees" : "radians")}", cancellationToken);
            await _engine.EvaluateAsync($"set max decimals {Math.Clamp(Decimals, 0, 15)}", cancellationToken);
            await _engine.EvaluateAsync("set digit grouping 0", cancellationToken);
            await _engine.EvaluateAsync("set decimal comma 0", cancellationToken);
        }
        catch (TimeoutException ex)
        {
            return lines.Select(line => line.Trim() is { Length: 0 } ? LineResult.Blank :
                line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    ? LineResult.Comment
                    : new LineResult(LineKind.Error, ex.Message, null)).ToList();
        }

        var results = new List<LineResult>();
        var block = new List<string>();
        string? previous = null;
        bool timedOut = false;

        foreach (var originalLine in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = originalLine.Trim();
            if (line.StartsWith('#'))
            {
                block.Clear();
                results.Add(LineResult.Comment);
                continue;
            }
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                results.Add(LineResult.Comment);
                continue;
            }

            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment].TrimEnd();
            if (line.Length == 0)
            {
                block.Clear();
                results.Add(LineResult.Blank);
                continue;
            }
            if (timedOut)
            {
                results.Add(new LineResult(LineKind.Error, "qalc restarted after a timeout; recalculate the document.", null));
                continue;
            }

            var label = Label.Match(line);
            if (label.Success) line = label.Groups["expression"].Value;

            if (IsAggregate(line))
            {
                if (block.Count == 0)
                {
                    results.Add(LineResult.Blank);
                    continue;
                }

                try
                {
                    if (previous != null) await _engine.SetVariableAsync("prev", previous, cancellationToken);
                    var expression = AggregateExpression(line, block);
                    var aggregateResult = await _engine.EvaluateAsync(expression, cancellationToken);
                    var parsed = MakeResult(aggregateResult, expression);
                    results.Add(ForDisplay(parsed));
                    if (parsed.Kind == LineKind.Result) previous = parsed.Text;
                }
                catch (TimeoutException ex)
                {
                    results.Add(new LineResult(LineKind.Error, ex.Message, null));
                    timedOut = true;
                }
                continue;
            }

            string expressionToEvaluate = ExpandAdditivePercent(line);
            try
            {
                if (previous != null)
                    await _engine.SetVariableAsync("prev", previous, cancellationToken);

                if (expressionToEvaluate[0] is '*' or '/' or '^')
                    expressionToEvaluate = $"prev {expressionToEvaluate}";

                var output = await _engine.EvaluateAsync(expressionToEvaluate, cancellationToken);
                var result = MakeResult(output, expressionToEvaluate);
                results.Add(ForDisplay(result));
                if (result.Kind != LineKind.Result) continue;

                previous = result.Text;
                block.Add(result.Text);
            }
            catch (TimeoutException ex)
            {
                results.Add(new LineResult(LineKind.Error, ex.Message, null));
                timedOut = true;
            }
        }

        return results;
    }

    private static LineResult MakeResult(string output, string expression)
    {
        var text = output.Trim();
        if (text.Length == 0)
            return new LineResult(LineKind.Error, $"qalc returned no result for '{expression}'.", null);
        if (Regex.IsMatch(text, @"^(?:error|warning):", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text, @"(?:syntax error|parse error|not a valid|unknown variable)", RegexOptions.IgnoreCase))
            return new LineResult(LineKind.Error, text, null);
        if (IsMalformedExpression(expression))
            return new LineResult(LineKind.Error, $"Could not evaluate '{expression}'.", null);
        return new LineResult(LineKind.Result, text, null);
    }

    private static bool IsMalformedExpression(string expression)
    {
        var text = expression.Trim();
        if (text.Length == 0 || text[^1] is '+' or '*' or '/' or '^' or '(' or ',') return true;
        if (text[0] is '*' or '/' or '^') return false;
        if (Regex.IsMatch(text, @"(?:\+\s*[*/]|[*/]\s*[+*/])")) return true;
        int depth = 0;
        foreach (char c in text)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return true;
        }
        return depth != 0;
    }

    private LineResult ForDisplay(LineResult result)
    {
        if (!GroupDigits || result.Kind != LineKind.Result) return result;
        return result with { Text = Number.Replace(result.Text, GroupNumber) };
    }

    private static string GroupNumber(Match match)
    {
        var digits = match.Groups["integer"].Value;
        if (digits.Length < 4) return match.Value;
        var grouped = string.Concat(digits.Select((digit, index) =>
            index > 0 && (digits.Length - index) % 3 == 0 ? $",{digit}" : digit.ToString()));
        return match.Groups["sign"].Value + grouped + match.Groups["fraction"].Value + match.Groups["exponent"].Value;
    }

    private static string ExpandAdditivePercent(string expression)
    {
        var match = AdditivePercent.Match(expression.Trim());
        return match.Success ? $"({match.Groups["base"].Value}) * (1 + {match.Groups["percent"].Value})" : expression;
    }

    private static bool IsAggregate(string text) =>
        text.Equals("total", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("sum", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("average", StringComparison.OrdinalIgnoreCase);

    private static string AggregateExpression(string aggregate, IReadOnlyList<string> values) =>
        aggregate.Equals("average", StringComparison.OrdinalIgnoreCase)
            ? $"average({string.Join(", ", values)})"
            : string.Join(" + ", values);

    public void Dispose() => _engine.Dispose();
}
