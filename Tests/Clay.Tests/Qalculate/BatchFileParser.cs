namespace Clay.Tests.Qalculate;

public sealed record BatchTestCase(
    string FileName,
    int LineNumber,
    string Expression,
    string Expected,
    IReadOnlyList<string> PriorExpressions,
    int IntervalMode = 1,
    bool AssumePositive = false)
{
    public override string ToString() => $"{FileName}:{LineNumber} | {Expression} => {Expected}";
}

/// <summary>
/// Parses Qalculate .batch test files (from Qalculate/libqalculate, GPL-2.0+, commit f58bc53).
/// Format: unindented expression lines followed by a tab-indented expected output line,
/// '#' comments, blank lines, and optional '/set ...' or 'set ...' runner directives.
/// </summary>
public static class BatchFileParser
{
    public static IReadOnlyList<BatchTestCase> ParseFile(string path) =>
        Parse(Path.GetFileName(path), File.ReadAllLines(path));

    public static IReadOnlyList<BatchTestCase> Parse(string fileName, IEnumerable<string> lines)
    {
        var cases = new List<BatchTestCase>();
        var prior = new List<string>();
        string? pendingExpression = null;
        int pendingLine = 0;
        int intervalMode = 1;
        bool assumePositive = false;

        int lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                pendingExpression = null;
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            if (line.StartsWith('\t'))
            {
                if (pendingExpression is null)
                    throw new FormatException($"{fileName}:{lineNumber}: expected output without preceding expression");

                var expected = line[1..].Trim();
                cases.Add(new BatchTestCase(fileName, pendingLine, pendingExpression, expected, prior.ToArray(), intervalMode, assumePositive));
                prior.Add(pendingExpression);
                pendingExpression = null;
                continue;
            }

            var trimmed = line.Trim();
            if (IsDirective(trimmed))
            {
                if (trimmed.StartsWith("/set ic ", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(trimmed[8..].Trim(), out int mode))
                    intervalMode = mode;
                else if (trimmed.StartsWith("/assume ", StringComparison.OrdinalIgnoreCase))
                    assumePositive = trimmed[8..].Trim().Equals("positive", StringComparison.OrdinalIgnoreCase);
                pendingExpression = null;
                continue;
            }

            // A previous expression with no expected output line (e.g., a setup assignment) still affects state.
            if (pendingExpression is not null)
                prior.Add(pendingExpression);

            pendingExpression = trimmed;
            pendingLine = lineNumber;
        }

        return cases;
    }

    private static bool IsDirective(string line) =>
        line.StartsWith("/set ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("set ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("/assume ", StringComparison.OrdinalIgnoreCase);
}
