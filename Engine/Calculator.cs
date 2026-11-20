using System.Text.RegularExpressions;

namespace Clay.Engine;

public enum LineKind { Blank, Comment, Result, Error }
public enum AngleMode { Radians, Degrees }

public sealed record LineResult(LineKind Kind, string Text, Value? Value)
{
    public static readonly LineResult Blank = new(LineKind.Blank, "", null);
    public static readonly LineResult Comment = new(LineKind.Comment, "", null);
}

/// <summary>Evaluates a whole document, one line at a time, top to bottom.</summary>
public sealed class Calculator
{
    public int Decimals { get; set; } = 6;
    public bool GroupDigits { get; set; } = true;
    public AngleMode AngleMode { get; set; } = AngleMode.Radians;

    /// <summary>Enables N, Hz, A, V, Ω and related units, and names results with them.</summary>
    public bool PhysicsUnits { get; set; }

    /// <summary>Allows whitespace inside numeric literals (e.g. 123 456 789).</summary>
    public bool IgnoreWhitespace { get; set; }

    /// <summary>Optional base directory for load(...) CSV data files; null disables file loading.</summary>
    public string? BaseDirectory { get; set; }

    /// <summary>Uncertainty propagation mode: 1 = variance/Gaussian (default), 2 = interval arithmetic.</summary>
    public int IntervalMode { get; set; } = 1;

    /// <summary>When true, symbolic variables are assumed to be positive real numbers.</summary>
    public bool AssumePositive { get; set; }
    private static readonly Regex Newline = new("\r\n|\r|\n", RegexOptions.Compiled);
    private static readonly Regex AssignRx =
        new(@"^(?<name>[\p{L}_][\p{L}\p{N}_]*)\s*:?=(?!=)\s*(?<expr>.+)$", RegexOptions.Compiled);
    private static readonly Regex ShortcutAssignRx = new(
        $@"^(?<name>[\p{{L}}_][\p{{L}}\p{{N}}_]*)\s+(?:{string.Join("|", Lexer.Shortcuts.Where(kv => kv.Value.Trim() == "=").Select(kv => Regex.Escape(kv.Key)))})\s+(?<expr>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Match MatchAssign(string t)
    {
        var m = AssignRx.Match(t);
        if (m.Success) return m;

        var alt = ShortcutAssignRx.Match(t);
        // "what is 3" must stay an expression, not become what = 3
        if (alt.Success && !Lexer.Shortcuts.ContainsKey(alt.Groups["name"].Value))
            return alt;

        return m;
    }

    public List<LineResult> Evaluate(string text)
    {
        using var physics = UnitTable.UsePhysics(PhysicsUnits);
        using var interval = Ops.UseIntervalMode(IntervalMode);
        using var assume = Ops.UseAssumePositive(AssumePositive);
        var scope = new Scope(AngleMode);
        var results = new List<LineResult>();
        foreach (var line in Newline.Split(text))
            results.Add(EvaluateLine(line, scope));
        return results;
    }



    private LineResult EvaluateLine(string line, Scope scope)
    {
        var t = line.Trim();

        if (t.StartsWith("//")) return LineResult.Comment;
        if (t.StartsWith('#'))
        {
            scope.Block.Clear();           // headings start a new block for sum / average
            return LineResult.Comment;
        }

        // A line that starts with a binary operator continues from the previous result: "+5" is prev + 5.
        // A leading "-" stays a negative number.
        if (scope.Prev != null &&
            t.Length > 0 &&
            t[0] is '*' or '/' or '^' or '+')
        {
            t = $"prev {t}";
        }

        t = StripInlineComment(t);

        if (t.Length == 0)
        {
            scope.Block.Clear();           // blank lines too
            return LineResult.Blank;
        }

        if (Preset(t, scope) is { } preset) return new LineResult(LineKind.Result, preset, null);

        if (t.StartsWith("delete ", StringComparison.OrdinalIgnoreCase))
        {
            var varName = t[7..].Trim();
            if (varName.Length > 0) scope.Vars.Remove(varName);
            return LineResult.Blank;
        }

        t = StripLabel(t);

        string? name = null;
        var assign = MatchAssign(t);
        if (assign.Success)
        {
            name = assign.Groups["name"].Value;
            t = assign.Groups["expr"].Value;
        }

        try
        {
            if (name != null && (Parser.Keywords.Contains(name) || Parser.Targets.Contains(name) ||
                Parser.Specials.Contains(name) || Parser.IsConst(name) || Parser.Scale(name) != null ||
                Functions.Table.ContainsKey(name) || UnitTable.Parse(name) != null || UnitTable.IsUnitless(name) ||
                Lexer.Shortcuts.ContainsKey(name)))
                throw new EvalException($"'{name}' is a reserved name");

            var value = Run(t, scope, out bool aggregate);
            if (value == null) return LineResult.Blank;   // plain text, nothing to calculate

            if (name != null) scope.Vars[name] = value;
            scope.Prev = value;
            if (!aggregate) scope.Block.Add(value);

            return new LineResult(LineKind.Result, Formatter.Format(value, Decimals, GroupDigits), value);
        }
        catch (EvalException ex)
        {
            return new LineResult(LineKind.Error, ex.Message, null);
        }
    }

    private static string StripLabel(string t)
    {
        if (t.Length == 0 || !char.IsLetter(t[0])) return t;
        int parenDepth = 0;
        char quote = '\0';
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < t.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '(') { parenDepth++; continue; }
            if (c == ')') { if (parenDepth == 0) return t; parenDepth--; continue; }
            if (parenDepth == 0)
            {
                if (c == '=') return t;
                if (c == ':')
                {
                    if (i + 1 < t.Length && t[i + 1] == '=') return t;
                    var rest = t[(i + 1)..].TrimStart();
                    return rest.Length > 0 ? rest : t;
                }
            }
        }
        return t;
    }

    private static string StripInlineComment(string t)
    {
        char quote = '\0';
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < t.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '#') return t[..i].TrimEnd();
        }
        return t;
    }

    private static readonly byte[] PresetKey = [8, 0, 8, 4, 0];

    /// <summary>
    /// Intentional, by design: a preset word on a line by itself shows fixed text instead of being
    /// evaluated. It has no value, so it doesn't feed prev or sum, and a variable of the same name wins.
    /// </summary>
    private static string? Preset(string t, Scope scope)
    {
        if (t.Length != PresetKey.Length || scope.Vars.ContainsKey(t)) return null;

        var s = t.ToLowerInvariant();
        uint h = 2166136261;
        foreach (char c in s) h = (h ^ c) * 16777619;
        if (h != 0x09354B67) return null;

        return string.Create(s.Length, s, (span, src) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = (char)(src[i] ^ PresetKey[i]);
        });
    }

    /// <summary>Strict parse first; if that fails, retry after removing dangling operators and keywords.</summary>
    private Value? Run(string expr, Scope scope, out bool aggregate)
    {
        aggregate = false;

        try
        {
            var parser = new Parser(Lexer.Tokenize(expr, IgnoreWhitespace), scope, BaseDirectory);
            var v = parser.ParseAll();
            aggregate = parser.UsedAggregate;
            return v;
        }
        catch (ParseException) { /* fall through to lenient mode */ }

        try
        {
            var tokens = Lenient(expr, scope, IgnoreWhitespace);
            if (tokens == null) return null;
            var parser = new Parser(tokens, scope, BaseDirectory);
            var v = parser.ParseAll();
            aggregate = parser.UsedAggregate;
            return v;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    private static List<Token>? Lenient(string expr, Scope scope, bool ignoreWhitespace)
    {
        var all = Lexer.Tokenize(expr, ignoreWhitespace);
        var kept = new List<Token>();
        for (int i = 0; i < all.Count; i++)
        {
            var k = all[i];
            if (k.Kind == Tk.End) continue;

            bool call = i + 1 < all.Count && all[i + 1].Kind == Tk.Op && all[i + 1].Text == "(";

            if (k.Kind == Tk.Ident && !Known(k.Text, scope, call)) throw new EvalException($"Unknown variable '{k.Text}'");

            kept.Add(k);
        }

        while (kept.Count > 0 && Dangling(kept[0], atStart: true)) kept.RemoveAt(0);
        while (kept.Count > 0 && Dangling(kept[^1], atStart: false)) kept.RemoveAt(kept.Count - 1);

        if (!kept.Any(k => k.Kind == Tk.Num)) return null;
        kept.Add(new Token(Tk.End, ""));
        return kept;
    }

    private static bool Known(string w, Scope scope, bool isCall) =>
        Parser.Keywords.Contains(w) || Parser.Targets.Contains(w) || Parser.Specials.Contains(w) ||
        Parser.IsConst(w) || Parser.Scale(w) != null || (isCall && Functions.Table.ContainsKey(w)) ||
        scope.Vars.ContainsKey(w) || UnitTable.Parse(w) != null || UnitTable.IsUnitless(w);

    private static bool Dangling(Token k, bool atStart) => k.Kind switch
    {
        Tk.Ident => Parser.Keywords.Contains(k.Text),
        Tk.Op => atStart ? "*/^),%!=]".Contains(k.Text) : "+-*/^(,=[".Contains(k.Text),
        _ => false
    };
}

internal sealed class Scope
{
    public AngleMode AngleMode { get; }
    public Dictionary<string, Value> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Value> Block { get; } = [];
    public Value? Prev { get; set; }

    public Scope(AngleMode angleMode) => AngleMode = angleMode;
}

internal static class Functions
{
    public static readonly Dictionary<string, Func<List<Value>, Value>> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sqrt"] = a =>
            {
                Need(a, 1);
                if (a[0].IsSymbolic) return Value.FromSym(Symbolic.Call("sqrt", [a[0].ToSym()], Ops.AssumePositive));
                return a[0].Unit != null ? Ops.Root(a[0], 2, Math.Sqrt) : Map(a, x => Math.Sqrt(x));
            },
            ["cbrt"] = a =>
            {
                Need(a, 1);
                if (a[0].IsSymbolic) return Value.FromSym(Symbolic.Call("cbrt", [a[0].ToSym()], Ops.AssumePositive));
                return a[0].Unit != null ? Ops.Root(a[0], 3, Math.Cbrt) : Map(a, x => Math.Cbrt(x));
            },
            ["abs"] = a =>
            {
                Need(a, 1);
                if (a[0].IsSymbolic) return Value.FromSym(Symbolic.Call("abs", [a[0].ToSym()], Ops.AssumePositive));
                return Map(a, x => Math.Abs(x), keepUnit: true);
            },
            ["sgn"] = a =>
            {
                Need(a, 1);
                if (a[0].IsSymbolic) return Value.FromSym(Symbolic.Call("sgn", [a[0].ToSym()], Ops.AssumePositive));
                return new Value(Math.Sign(a[0].Frac));
            },
            ["ceil"] = a => Map(a, x => Math.Ceiling(x), keepUnit: true),
            ["floor"] = a => Map(a, x => Math.Floor(x), keepUnit: true),
            ["round"] = a =>
            {
                Need(a, 1);
                int digits = a.Count > 1 ? (int)a[1].Frac : 0;
                return new Value(Math.Round(a[0].Frac, Math.Clamp(digits, 0, 15), MidpointRounding.AwayFromZero), a[0].Unit);
            },
            ["ln"] = a =>
            {
                Need(a, 1);
                if (a[0].IsSymbolic) return Value.FromSym(Symbolic.Call("ln", [a[0].ToSym()], Ops.AssumePositive));
                if (a[0].IsComplex) return Value.Complex(ComplexInterval.Ln(a[0].ComplexValue!.Value, Ops.IntervalMode));
                return new Value(Math.Log(a[0].Frac));
            },
            ["Ei"] = a =>
            {
                Need(a, 1);
                if (a[0].IsSymbolic) return Value.FromSym(Symbolic.Call("Ei", [a[0].ToSym()], Ops.AssumePositive));
                return Value.Complex(ComplexInterval.Ei(a[0].ToComplexInterval(), Ops.IntervalMode));
            },
            ["Shi"] = a =>
            {
                Need(a, 1);
                if (a[0].IsSymbolic) return Value.FromSym(new SymExpr.Func("Shi", [a[0].ToSym()]));
                return new Value(Calculus.Shi(a[0].Frac));
            },
            ["fresnels"] = a => { Need(a, 1); return new Value(Calculus.FresnelS(a[0].Frac)); },
            ["gammainc"] = a => { Need(a, 2); return new Value(Calculus.GammaInc(a[0].Frac, a[1].Frac)); },
            ["betainc"] = a => { Need(a, 3); return Calculus.BetaInc(a[0].ToComplexInterval(), a[1].Frac, a[2].Frac); },
            ["diff"] = a =>
            {
                Need(a, 1);
                string v = a.Count > 1 && a[1].SymValue is SymExpr.Var sv ? sv.Name : "x";
                int order = a.Count > 2 ? (int)Math.Round(a[2].Frac) : 1;
                return Value.FromSym(Calculus.Diff(a[0].ToSym(), v, order));
            },
            ["integrate"] = a =>
            {
                Need(a, 1);
                if (a.Count == 1) return Value.FromSym(Calculus.IntegrateIndefinite(a[0].ToSym(), "x"));
                if (a.Count == 2 && a[1].SymValue is SymExpr.Var sv)
                    return Value.FromSym(Calculus.IntegrateIndefinite(a[0].ToSym(), sv.Name));
                Need(a, 3);
                string v = a.Count > 3 && a[3].SymValue is SymExpr.Var iv ? iv.Name : "x";
                return Calculus.IntegrateDefinite(a[0].ToSym(), a[1].Frac, a[2].Frac, v);
            },
            ["romberg"] = a =>
            {
                Need(a, 3);
                string v = a.Count > 3 && a[3].SymValue is SymExpr.Var rv ? rv.Name : "x";
                return Calculus.IntegrateDefinite(a[0].ToSym(), a[1].Frac, a[2].Frac, v);
            },
            ["limit"] = a =>
            {
                Need(a, 2);
                double point = a[1].Frac;
                string v = "x";
                int dir = 0;
                if (a.Count > 2)
                {
                    if (a[2].SymValue is SymExpr.Var lv) v = lv.Name;
                    else dir = (int)Math.Round(a[2].Frac);
                }
                if (a.Count > 3) dir = (int)Math.Round(a[3].Frac);
                return Limits.Compute(a[0].ToSym(), point, v, dir);
            },
            ["root"] = a =>
            {
                Need(a, 2);
                if (a.Any(x => x.IsSymbolic))
                    return Value.FromSym(new SymExpr.Func("root", [a[0].ToSym(), a[1].ToSym()]));
                return new Value(Limits.RealRoot(a[0].Frac, a[1].Frac));
            },
            ["cot"] = a =>
            {
                Need(a, 1);
                if (a[0].IsSymbolic) return Value.FromSym(Symbolic.Call("cot", [a[0].ToSym()], Ops.AssumePositive));
                return new Value(1.0 / Math.Tan(a[0].Frac));
            },
            ["acot"] = a =>
            {
                Need(a, 1);
                if (a[0].IsSymbolic) return Value.FromSym(Symbolic.Call("acot", [a[0].ToSym()], Ops.AssumePositive));
                return new Value(Limits.EvalAcot(a[0].Frac));
            },
            ["lambertw"] = a =>
            {
                Need(a, 1);
                if (a.Any(x => x.IsSymbolic))
                    return Value.FromSym(Symbolic.Call("lambertw", a.Select(x => x.ToSym()).ToList(), Ops.AssumePositive));
                int branch = a.Count > 1 ? (int)Math.Round(a[1].Frac) : 0;
                return Value.Complex(ComplexInterval.LambertW(a[0].ToComplexInterval(), branch, Ops.IntervalMode));
            },
            ["powertower"] = a => { Need(a, 2); return ComplexInterval.PowerTower(a[0], (int)Math.Round(a[1].Frac)); },
            ["allroots"] = a => { Need(a, 2); return ComplexInterval.AllRoots(a[0].Frac, (int)Math.Round(a[1].Frac)); },
            ["log"] = a =>
            {
                Need(a, 1);
                if (a.Any(x => x.IsSymbolic))
                {
                    var symArgs = a.Count > 1
                        ? new[] { a[0].ToSym(), a[1].ToSym() }
                        : new[] { a[0].ToSym(), Symbolic.FromLong(10) };
                    return Value.FromSym(Symbolic.Call("log", symArgs, Ops.AssumePositive));
                }
                return new Value(a.Count > 1 ? Math.Log(a[0].Frac) / Math.Log(a[1].Frac) : Math.Log10(a[0].Frac));
            },
            ["factor"] = a => { Need(a, 1); return Value.FromSym(Symbolic.Factor(a[0].ToSym(), Ops.AssumePositive)); },
            ["expand"] = a => { Need(a, 1); return Value.FromSym(Symbolic.Expand(a[0].ToSym(), Ops.AssumePositive)); },
            ["coeff"] = a =>
            {
                Need(a, 2);
                int deg = (int)Math.Round(a[1].Frac);
                string? v = a.Count > 2 ? SymVarName(a[2]) : null;
                return Value.FromSym(Symbolic.PolyCoeff(a[0].ToSym(), deg, v));
            },
            ["pcontent"] = a =>
            {
                Need(a, 1);
                string? v = a.Count > 1 ? SymVarName(a[1]) : null;
                return Value.FromSym(Symbolic.PolyContent(a[0].ToSym(), v));
            },
            ["lcoeff"] = a =>
            {
                Need(a, 1);
                string? v = a.Count > 1 ? SymVarName(a[1]) : null;
                return Value.FromSym(Symbolic.PolyLeadingCoeff(a[0].ToSym(), v));
            },
            ["tcoeff"] = a =>
            {
                Need(a, 1);
                string? v = a.Count > 1 ? SymVarName(a[1]) : null;
                return Value.FromSym(Symbolic.PolyTrailingCoeff(a[0].ToSym(), v));
            },
            ["degree"] = a =>
            {
                Need(a, 1);
                string? v = a.Count > 1 ? SymVarName(a[1]) : null;
                return new Value(ExactNumber.FromLong(Symbolic.PolyDegree(a[0].ToSym(), v)));
            },
            ["ldegree"] = a =>
            {
                Need(a, 1);
                string? v = a.Count > 1 ? SymVarName(a[1]) : null;
                return new Value(ExactNumber.FromLong(Symbolic.PolyLowDegree(a[0].ToSym(), v)));
            },
            ["primpart"] = a =>
            {
                Need(a, 1);
                string? v = a.Count > 1 ? SymVarName(a[1]) : null;
                return Value.FromSym(Symbolic.PolyPrimPart(a[0].ToSym(), v));
            },
            ["punit"] = a =>
            {
                Need(a, 1);
                string? v = a.Count > 1 ? SymVarName(a[1]) : null;
                return new Value(ExactNumber.FromLong(Symbolic.PolyUnit(a[0].ToSym(), v)));
            },
            ["quadraticfit"] = a => Statistics.PolyFit(a, 2),
            ["cubicfit"] = a => Statistics.PolyFit(a, 3),
            ["log2"] = a => Map(a, x => Math.Log2(x)),
            ["exp"] = a => Map(a, x => Math.Exp(x)),
            ["fact"] = a => { Need(a, 1); return Ops.Factorial(a[0]); },
            ["pow"] = a => { Need(a, 2); return Ops.EntryWisePow(a[0], a[1]); },

            ["sin"] = a => Trig(a, Math.Sin),
            ["cos"] = a => Trig(a, Math.Cos),
            ["tan"] = a => Trig(a, Math.Tan),
            ["sinh"] = a => { Need(a, 1); return a[0].IsSymbolic ? Value.FromSym(Symbolic.Call("sinh", [a[0].ToSym()], Ops.AssumePositive)) : Map(a, x => Math.Sinh(x)); },
            ["cosh"] = a => { Need(a, 1); return a[0].IsSymbolic ? Value.FromSym(Symbolic.Call("cosh", [a[0].ToSym()], Ops.AssumePositive)) : Map(a, x => Math.Cosh(x)); },
            ["tanh"] = a => { Need(a, 1); return a[0].IsSymbolic ? Value.FromSym(Symbolic.Call("tanh", [a[0].ToSym()], Ops.AssumePositive)) : Map(a, x => Math.Tanh(x)); },
            ["asin"] = a => Inverse(a, Math.Asin),
            ["acos"] = a => Inverse(a, Math.Acos),
            ["atan"] = a => Inverse(a, Math.Atan),

            // Geometry
            ["circle"] = a => { Need(a, 1); return GeoMul(Math.PI, GeoPow2(a[0])); },
            ["circumference"] = a => { Need(a, 1); return GeoMul(2 * Math.PI, a[0]); },
            ["cone"] = a => { Need(a, 2); SameDim(a[0], a[1]); return GeoMul(Math.PI / 3, Ops.Mul(GeoPow2(a[0]), a[1])); },
            ["cone_sa"] = a => { Need(a, 2); SameDim(a[0], a[1]); return GeoMul(Math.PI, Ops.Mul(a[0], GeoAdd(a[0], Ops.Root(GeoAdd(GeoPow2(a[0]), GeoPow2(a[1])), 2, Math.Sqrt)))); },
            ["cube"] = a => { Need(a, 1); return GeoPow3(a[0]); },
            ["cube_sa"] = a => { Need(a, 1); return GeoMul(6, GeoPow2(a[0])); },
            ["cylinder"] = a => { Need(a, 2); SameDim(a[0], a[1]); return GeoMul(Math.PI, Ops.Mul(GeoPow2(a[0]), a[1])); },
            ["cylinder_sa"] = a => { Need(a, 2); SameDim(a[0], a[1]); return GeoMul(2 * Math.PI, Ops.Mul(a[0], GeoAdd(a[0], a[1]))); },
            ["parallelogram"] = a => { Need(a, 2); SameDim(a[0], a[1]); return Ops.Mul(a[0], a[1]); },
            ["parallelogram_perimeter"] = a => { Need(a, 2); SameDim(a[0], a[1]); return GeoMul(2, GeoAdd(a[0], a[1])); },
            ["rectprism"] = a => { Need(a, 3); SameDim(a[0], a[1], a[2]); return Ops.Mul(Ops.Mul(a[0], a[1]), a[2]); },
            ["rectprism_sa"] = a => { Need(a, 3); SameDim(a[0], a[1], a[2]); return GeoMul(2, GeoAdd(GeoAdd(Ops.Mul(a[0], a[1]), Ops.Mul(a[0], a[2])), Ops.Mul(a[1], a[2]))); },
            ["triangleprism"] = a => { Need(a, 3); SameDim(a[0], a[1], a[2]); return GeoMul(0.5, Ops.Mul(Ops.Mul(a[0], a[1]), a[2])); },
            ["tetrahedron"] = a => { Need(a, 1); return GeoMul(1.0 / (6 * Math.Sqrt(2)), GeoPow3(a[0])); },
            ["tetrahedron_height"] = a => { Need(a, 1); return GeoMul(Math.Sqrt(2.0 / 3.0), a[0]); },
            ["tetrahedron_sa"] = a => { Need(a, 1); return GeoMul(Math.Sqrt(3), GeoPow2(a[0])); },
            ["sqpyramid"] = a => { Need(a, 1); return GeoMul(Math.Sqrt(2) / 6, GeoPow3(a[0])); },
            ["sqpyramid_height"] = a => { Need(a, 1); return GeoMul(1.0 / Math.Sqrt(2), a[0]); },
            ["sqpyramid_sa"] = a => { Need(a, 1); return GeoMul(1 + Math.Sqrt(3), GeoPow2(a[0])); },
            ["pyramid"] = a => { Need(a, 3); SameDim(a[0], a[1], a[2]); return GeoMul(1.0 / 3.0, Ops.Mul(Ops.Mul(a[0], a[1]), a[2])); },
            ["rect"] = a => { Need(a, 2); SameDim(a[0], a[1]); return Ops.Mul(a[0], a[1]); },
            ["rect_perimeter"] = a => { Need(a, 2); SameDim(a[0], a[1]); return GeoMul(2, GeoAdd(a[0], a[1])); },
            ["sphere"] = a => { Need(a, 1); return GeoMul(4.0 / 3.0 * Math.PI, GeoPow3(a[0])); },
            ["sphere_sa"] = a => { Need(a, 1); return GeoMul(4 * Math.PI, GeoPow2(a[0])); },
            ["square"] = a => { Need(a, 1); return GeoPow2(a[0]); },
            ["square_perimeter"] = a => { Need(a, 1); return GeoMul(4, a[0]); },
            ["trapezoid"] = a => { Need(a, 3); SameDim(a[0], a[1], a[2]); return Ops.Mul(GeoMul(0.5, GeoAdd(a[0], a[1])), a[2]); },
            ["triangle"] = a => { Need(a, 2); SameDim(a[0], a[1]); return GeoMul(0.5, Ops.Mul(a[0], a[1])); },
            ["triangle_perimeter"] = a => { Need(a, 3); SameDim(a[0], a[1], a[2]); return GeoAdd(GeoAdd(a[0], a[1]), a[2]); },
            ["hypot"] = a => { Need(a, 2); SameDim(a[0], a[1]); return Ops.Root(GeoAdd(GeoPow2(a[0]), GeoPow2(a[1])), 2, Math.Sqrt); },
            ["floatError"] = a => { Need(a, 1); double x = a[0].Frac; return new Value(Math.Abs(x - (double)(float)x)); },

            // Aggregates accept loose arguments or lists: sum(1, 2, 3) and sum([1, 2, 3]) are the same.
            ["min"] = a => Pick(Flat(a), Math.Min),
            ["max"] = a => Pick(Flat(a), Math.Max),
            ["sum"] = a => Sum(Flat(a)),
            ["total"] = a => Sum(Flat(a)),
            ["product"] = a => { var f = Flat(a); Need(f, 1); return f.Aggregate((x, y) => Ops.Mul(x, y)); },
            ["avg"] = a => Average(Flat(a)),
            ["mean"] = a => Average(Flat(a)),
            ["average"] = a => Average(Flat(a)),
            ["median"] = a => Median(Flat(a)),

            // Statistics & distributions
            ["stdev"] = a => new Value(Statistics.Stdev(Statistics.FromArgs(a))),
            ["stderr"] = a => new Value(Statistics.Stderr(Statistics.FromArgs(a))),
            ["meandev"] = a => new Value(Statistics.MeanDev(Statistics.FromArgs(a))),
            ["geomean"] = a => new Value(Statistics.GeoMean(Statistics.FromArgs(a))),
            ["harmmean"] = a => new Value(Statistics.HarmMean(Statistics.FromArgs(a))),
            ["rms"] = a => new Value(Statistics.Rms(Statistics.FromArgs(a))),
            ["mode"] = a => new Value(Statistics.Mode(Statistics.FromArgs(a))),
            ["number"] = a => new Value(ExactNumber.FromLong(Flat(a).Count)),
            ["trimmean"] = a => { Need(a, 2); return new Value(Statistics.TrimMean(Statistics.ToDoubles(a[0]), a[1].Frac)); },
            ["winsormean"] = a => { Need(a, 2); return new Value(Statistics.WinsorMean(Statistics.ToDoubles(a[0]), a[1].Frac)); },
            ["weighmean"] = a => { Need(a, 2); return new Value(Statistics.WeightedMean(Statistics.ToDoubles(a[0]), Statistics.ToDoubles(a[1]))); },
            ["quartile"] = a =>
            {
                Need(a, 2);
                int method = a.Count > 2 ? (int)Math.Round(a[2].Frac) : 8;
                return new Value(Statistics.Quantile(Statistics.ToDoubles(a[0]), a[1].Frac / 4.0, method));
            },
            ["percentile"] = a =>
            {
                Need(a, 2);
                int method = a.Count > 2 ? (int)Math.Round(a[2].Frac) : 8;
                return new Value(Statistics.Quantile(Statistics.ToDoubles(a[0]), a[1].Frac / 100.0, method));
            },
            ["decile"] = a =>
            {
                Need(a, 2);
                int method = a.Count > 2 ? (int)Math.Round(a[2].Frac) : 8;
                return new Value(Statistics.Quantile(Statistics.ToDoubles(a[0]), a[1].Frac / 10.0, method));
            },
            ["iqr"] = a =>
            {
                Need(a, 1);
                int method = a.Count > 1 ? (int)Math.Round(a[1].Frac) : 7;
                return new Value(Statistics.Iqr(Statistics.ToDoubles(a[0]), method));
            },
            ["covar"] = a => { Need(a, 2); return new Value(Statistics.Covar(Statistics.ToDoubles(a[0]), Statistics.ToDoubles(a[1]))); },
            ["poolvar"] = a => { Need(a, 2); return new Value(Statistics.PoolVar(Statistics.ToDoubles(a[0]), Statistics.ToDoubles(a[1]))); },
            ["pearson"] = a => { Need(a, 2); return new Value(Statistics.Pearson(Statistics.ToDoubles(a[0]), Statistics.ToDoubles(a[1]))); },
            ["spearman"] = a => { Need(a, 2); return new Value(Statistics.Spearman(Statistics.ToDoubles(a[0]), Statistics.ToDoubles(a[1]))); },
            ["ttest"] = a => { Need(a, 2); return new Value(Statistics.TTest(Statistics.ToDoubles(a[0]), Statistics.ToDoubles(a[1]))); },
            ["pttest"] = a => { Need(a, 2); return new Value(Statistics.PairedTTest(Statistics.ToDoubles(a[0]), Statistics.ToDoubles(a[1]))); },
            ["normdist"] = a =>
            {
                Need(a, 1);
                double mean = a.Count > 1 ? a[1].Frac : 0;
                double sd = a.Count > 2 ? a[2].Frac : 1;
                bool cdf = a.Count > 3 && a[3].Frac != 0;
                return new Value(Statistics.NormDist(a[0].Frac, mean, sd, cdf));
            },
            ["normdistinv"] = a =>
            {
                Need(a, 1);
                double mean = a.Count > 1 ? a[1].Frac : 0;
                double sd = a.Count > 2 ? a[2].Frac : 1;
                return new Value(Statistics.NormDistInv(a[0].Frac, mean, sd));
            },
            ["fdist"] = a =>
            {
                Need(a, 3);
                bool cdf = a.Count > 3 && a[3].Frac != 0;
                return new Value(Statistics.FDist(a[0].Frac, a[1].Frac, a[2].Frac, cdf));
            },
            ["chisqdistinv"] = a => { Need(a, 2); return new Value(Statistics.ChiSqDistInv(a[0].Frac, a[1].Frac)); },
            ["genvector"] = a =>
            {
                Need(a, 3);
                double start = a[1].Frac, end = a[2].Frac;
                double step = a.Count > 3 ? a[3].Frac : (end >= start ? 1 : -1);
                if (step == 0 || (end - start) * step < 0) throw new EvalException("Step points the wrong way");
                int count = (int)Math.Floor((end - start) / step + 1e-9) + 1;
                if (count > 10000) throw new EvalException("Vector is too long (10,000 items max)");
                return Value.List(Enumerable.Repeat(a[0], count));
            },

            // List and string shaping
            ["count"] = a => new Value(Elements(a).Count),
            ["len"] = a =>
            {
                if (a.Count == 1 && a[0].IsString)
                    return new Value(ExactNumber.FromLong(a[0].StringValue!.EnumerateRunes().Count()));
                if (a.Count == 1 && !a[0].IsArray)
                    return new Value(ExactNumber.FromLong(Formatter.Format(a[0], 6, false).Length));
                return new Value(Elements(a).Count);
            },
            ["concatenate"] = a =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in a)
                    sb.Append(item.IsString ? item.StringValue : Formatter.Format(item, 6, false));
                return Value.Str(sb.ToString());
            },
            ["char"] = a =>
            {
                Need(a, 1);
                if (a[0].Unit != null || !a[0].ExactFrac.TryGetBigInteger(out var cp) || cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                    throw new EvalException("Invalid Unicode code point");
                return Value.Str(char.ConvertFromUtf32((int)cp));
            },
            ["stamptodate"] = a =>
            {
                Need(a, 1);
                long minSec = DateTimeOffset.MinValue.ToUnixTimeSeconds();
                long maxSec = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
                long sec;
                if (a[0].ExactFrac.TryGetBigInteger(out var big))
                {
                    if (big < minSec || big > maxSec)
                        throw new EvalException("Timestamp is out of range");
                    sec = (long)big;
                }
                else
                {
                    double d = Math.Round(a[0].Frac);
                    if (!double.IsFinite(d) || d < minSec || d > maxSec)
                        throw new EvalException("Timestamp is out of range");
                    sec = (long)d;
                }
                try
                {
                    return Value.Date(new CalcDate(DateTimeOffset.FromUnixTimeSeconds(sec), HasTime: true, IsUtc: true));
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw new EvalException("Timestamp is out of range");
                }
            },
            ["sort"] = a =>
            {
                if (a.Count == 2 && a[0].IsArray && !a[1].IsArray)
                    return Sort(a[0].Items!.ToList(), ascending: a[1].Frac != 0);
                return Sort(Elements(a), ascending: true);
            },
            ["adj"] = a => { Need(a, 1); return LinearAlgebra.FromMatrix(LinearAlgebra.Adjugate(LinearAlgebra.ToMatrix(a[0]))); },
            ["cofactor"] = a => { Need(a, 3); return new Value(LinearAlgebra.Cofactor(LinearAlgebra.ToMatrix(a[0]), (int)a[1].Frac, (int)a[2].Frac)); },
            ["columns"] = a =>
            {
                Need(a, 1);
                if (!a[0].IsArray) return new Value(ExactNumber.One);
                var items = a[0].Items!;
                if (items.Count == 0) return new Value(ExactNumber.Zero);
                return new Value(ExactNumber.FromLong(items[0].IsArray ? items[0].Items!.Count : items.Count));
            },
            ["rows"] = a =>
            {
                Need(a, 1);
                if (!a[0].IsArray) return new Value(ExactNumber.One);
                var items = a[0].Items!;
                if (items.Count == 0) return new Value(ExactNumber.Zero);
                return new Value(ExactNumber.FromLong(items[0].IsArray ? items.Count : 1));
            },
            ["dimension"] = a => { Need(a, 1); return new Value(ExactNumber.FromLong(a[0].IsArray ? a[0].Items!.Count : 1)); },
            ["elements"] = a => { Need(a, 1); return new Value(ExactNumber.FromLong(a[0].IsArray ? Ops.Flatten(a[0]).Count() : 1)); },
            ["matrix"] = a =>
            {
                Need(a, 2);
                int r = (int)a[0].Frac, c = (int)a[1].Frac;
                if (r < 0 || c < 0) throw new EvalException("Matrix dimensions must be non-negative");
                var flat = a.Skip(2).SelectMany(Ops.Flatten).Select(x => x.ExactFrac).ToList();
                var m = new ExactNumber[r, c];
                for (int i = 0; i < r; i++)
                    for (int j = 0; j < c; j++)
                    {
                        int idx = i * c + j;
                        m[i, j] = idx < flat.Count ? flat[idx] : ExactNumber.Zero;
                    }
                return LinearAlgebra.FromMatrix(m, collapseSingle: true);
            },
            ["matrix2vector"] = a =>
            {
                Need(a, 1);
                var flat = Ops.Flatten(a[0]).ToList();
                return flat.Count == 1 ? flat[0] : Value.List(flat);
            },
            ["horzcat"] = LinearAlgebra.HorzCat,
            ["vertcat"] = LinearAlgebra.VertCat,
            ["combine"] = a => Value.List(a.SelectMany(Ops.Flatten)),
            ["cross"] = a => { Need(a, 2); return LinearAlgebra.Cross(a[0], a[1]); },
            ["det"] = a => { Need(a, 1); return new Value(LinearAlgebra.Det(LinearAlgebra.ToMatrix(a[0]))); },
            ["dot"] = a => { Need(a, 2); return LinearAlgebra.Dot(a[0], a[1]); },
            ["element"] = a =>
            {
                Need(a, 2);
                int idx1 = (int)a[1].Frac;
                if (a.Count >= 3)
                {
                    var m = LinearAlgebra.ToMatrix(a[0]);
                    int r = idx1 - 1, c = (int)a[2].Frac - 1;
                    if (r < 0 || r >= m.GetLength(0) || c < 0 || c >= m.GetLength(1)) throw new EvalException("Index out of range");
                    return new Value(m[r, c]);
                }
                if (!a[0].IsArray || idx1 < 1 || idx1 > a[0].Items!.Count) throw new EvalException("Index out of range");
                return a[0].Items![idx1 - 1];
            },
            ["row"] = a =>
            {
                Need(a, 2);
                var m = LinearAlgebra.ToMatrix(a[0]);
                int r = (int)a[1].Frac - 1;
                if (r < 0 || r >= m.GetLength(0)) throw new EvalException("Index out of range");
                int cols = m.GetLength(1);
                if (cols == 1) return new Value(m[r, 0]);
                return Value.List(Enumerable.Range(0, cols).Select(j => new Value(m[r, j])));
            },
            ["column"] = a =>
            {
                Need(a, 2);
                var m = LinearAlgebra.ToMatrix(a[0]);
                int c = (int)a[1].Frac - 1;
                if (c < 0 || c >= m.GetLength(1)) throw new EvalException("Index out of range");
                int rows = m.GetLength(0);
                if (rows == 1) return new Value(m[0, c]);
                return Value.List(Enumerable.Range(0, rows).Select(i => new Value(m[i, c])));
            },
            ["multiply"] = a => { Need(a, 1); return a.Aggregate(Ops.Mul); },
            ["divide"] = a => { Need(a, 2); return Ops.Div(a[0], a[1]); },
            ["hadamard"] = a =>
            {
                Need(a, 1);
                var res = a.Aggregate(Ops.ElementWiseMul);
                return res.IsArray && res.Items!.Count == 1 && !res.Items[0].IsArray ? res.Items[0] : res;
            },
            ["identity"] = a =>
            {
                Need(a, 1);
                int n = a[0].IsArray ? LinearAlgebra.ToMatrix(a[0]).GetLength(0) : (int)a[0].Frac;
                return LinearAlgebra.FromMatrix(LinearAlgebra.Identity(n), collapseSingle: true);
            },
            ["magnitude"] = a => { Need(a, 1); return LinearAlgebra.Magnitude(a[0]); },
            ["norm"] = a => { Need(a, 1); return LinearAlgebra.Magnitude(a[0]); },
            ["part"] = a =>
            {
                Need(a, 5);
                var m = LinearAlgebra.ToMatrix(a[0]);
                int r1 = (int)a[1].Frac - 1, c1 = (int)a[2].Frac - 1, r2 = (int)a[3].Frac - 1, c2 = (int)a[4].Frac - 1;
                if (r1 < 0 || c1 < 0 || r2 >= m.GetLength(0) || c2 >= m.GetLength(1) || r1 > r2 || c1 > c2)
                    throw new EvalException("Index out of range");
                var sub = new ExactNumber[r2 - r1 + 1, c2 - c1 + 1];
                for (int i = r1; i <= r2; i++)
                    for (int j = c1; j <= c2; j++)
                        sub[i - r1, j - c1] = m[i, j];
                return LinearAlgebra.FromMatrix(sub, collapseSingle: true);
            },
            ["inverse"] = a => { Need(a, 1); return LinearAlgebra.FromMatrix(LinearAlgebra.Inverse(LinearAlgebra.ToMatrix(a[0]))); },
            ["rk"] = a => { Need(a, 1); LinearAlgebra.Rref(LinearAlgebra.ToMatrix(a[0]), out int rk); return new Value(ExactNumber.FromLong(rk)); },
            ["permanent"] = a => { Need(a, 1); return new Value(LinearAlgebra.Permanent(LinearAlgebra.ToMatrix(a[0]))); },
            ["rref"] = a => { Need(a, 1); return LinearAlgebra.FromMatrix(LinearAlgebra.Rref(LinearAlgebra.ToMatrix(a[0]), out _)); },
            ["transpose"] = a => { Need(a, 1); return LinearAlgebra.FromMatrix(LinearAlgebra.Transpose(LinearAlgebra.ToMatrix(a[0]))); },
            ["rank"] = a =>
            {
                Need(a, 1);
                var xs = Statistics.ToDoubles(a[0]);
                bool asc = a.Count < 2 || a[1].Frac != 0;
                var ordered = asc
                    ? xs.Select((v, i) => (v, i)).OrderBy(t => t.v).ToArray()
                    : xs.Select((v, i) => (v, i)).OrderByDescending(t => t.v).ToArray();
                var ranks = new Value[xs.Length];
                for (int i = 0; i < ordered.Length; i++)
                    ranks[ordered[i].i] = new Value(ExactNumber.FromLong(i + 1));
                return Value.List(ranks);
            },
            ["slice"] = a =>
            {
                Need(a, 3);
                var xs = Ops.Flatten(a[0]).ToList();
                int start = (int)a[1].Frac - 1, end = (int)a[2].Frac - 1;
                if (start < 0 || end >= xs.Count || start > end) throw new EvalException("Index out of range");
                var sub = xs.GetRange(start, end - start + 1);
                return sub.Count == 1 ? sub[0] : Value.List(sub);
            },
            ["reverse"] = a => { var e = Elements(a); e.Reverse(); return Value.List(e); },
            ["first"] = a => { var e = Elements(a); Need(e, 1); return e[0]; },
            ["last"] = a => { var e = Elements(a); Need(e, 1); return e[^1]; },
            ["range"] = MakeRange,
        };

    /// <summary>Functions that apply to each item when given a list: sqrt([4, 9]) is [2, 3].</summary>
    private static readonly HashSet<string> ElementWise = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqrt", "cbrt", "abs", "ceil", "floor", "round", "ln", "log", "log2", "exp", "fact",
        "sin", "cos", "tan", "sinh", "cosh", "tanh", "asin", "acos", "atan", "char"
    };

    public static Value Call(string name, List<Value> args, AngleMode angleMode = AngleMode.Radians)
    {
        if (ElementWise.Contains(name) && args.Count > 0 && args[0].IsArray)
        {
            var rest = args.Skip(1).ToList();
            return Ops.Map(args[0], x => Call(name, [x, .. rest], angleMode));
        }

        var result = name.ToLowerInvariant() switch
        {
            "sin" => args.Count > 0 && args[0].IsSymbolic ? Value.FromSym(Symbolic.Call("sin", [args[0].ToSym()], Ops.AssumePositive)) : Trig(args, Math.Sin, angleMode),
            "cos" => args.Count > 0 && args[0].IsSymbolic ? Value.FromSym(Symbolic.Call("cos", [args[0].ToSym()], Ops.AssumePositive)) : Trig(args, Math.Cos, angleMode),
            "tan" => args.Count > 0 && args[0].IsSymbolic ? Value.FromSym(Symbolic.Call("tan", [args[0].ToSym()], Ops.AssumePositive)) : Trig(args, Math.Tan, angleMode),
            "asin" => args.Count > 0 && args[0].IsSymbolic ? Value.FromSym(Symbolic.Call("asin", [args[0].ToSym()], Ops.AssumePositive)) : Inverse(args, Math.Asin, angleMode),
            "acos" => args.Count > 0 && args[0].IsSymbolic ? Value.FromSym(Symbolic.Call("acos", [args[0].ToSym()], Ops.AssumePositive)) : Inverse(args, Math.Acos, angleMode),
            "atan" => args.Count > 0 && args[0].IsSymbolic ? Value.FromSym(Symbolic.Call("atan", [args[0].ToSym()], Ops.AssumePositive)) : Inverse(args, Math.Atan, angleMode),
            _ => Table[name](args),
        };
        if (!result.IsArray && !result.IsString && !result.IsDate && !result.IsComplex && !result.IsSymbolic && double.IsNaN(result.Number)) throw new EvalException("Not a number");
        return result;
    }

    private static string SymVarName(Value v) =>
        v.SymValue is SymExpr.Var sv ? sv.Name : throw new EvalException("Expected a variable name");

    /// <summary>Every scalar in the arguments, however deeply nested.</summary>
    private static List<Value> Flat(List<Value> a) => a.SelectMany(Ops.Flatten).ToList();

    /// <summary>The items of a single list argument, or the arguments themselves.</summary>
    private static List<Value> Elements(List<Value> a) =>
        a.Count == 1 && a[0].Items is { } items ? items.ToList() : a.ToList();

    private static Func<Value, double> SortKey(List<Value> items)
    {
        var unit = items.FirstOrDefault(v => v.Unit != null)?.Unit;
        return v => unit == null ? v.Frac : Ops.In(v, unit);
    }

    private static Value Sort(List<Value> items, bool ascending = true)
    {
        if (items.Any(v => v.IsArray)) throw new EvalException("Can't sort nested lists");
        var key = SortKey(items);
        var sorted = ascending ? items.OrderBy(key) : items.OrderByDescending(key);
        return Value.List(sorted.ToList());
    }

    private static Value Median(List<Value> items)
    {
        Need(items, 1);
        var key = SortKey(items);
        var xs = items.Select(key).OrderBy(x => x).ToList();
        int n = xs.Count;
        double m = n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2;
        return new Value(m, items.FirstOrDefault(v => v.Unit != null)?.Unit);
    }

    /// <summary>range(1, 5) is [1, 2, 3, 4, 5]; range(list) is max(list) - min(list).</summary>
    private static Value MakeRange(List<Value> a)
    {
        if (a.Count == 1 && a[0].IsArray)
        {
            var xs = Statistics.ToDoubles(a[0]);
            if (xs.Length == 0) throw new EvalException("Missing argument");
            return new Value(xs.Max() - xs.Min());
        }
        Need(a, 2);
        double start = a[0].Frac, end = a[1].Frac;
        double step = a.Count > 2 ? a[2].Frac : (end >= start ? 1 : -1);
        if (step == 0 || (end - start) * step < 0) throw new EvalException("Step points the wrong way");

        double count = Math.Floor((end - start) / step + 1e-9) + 1;
        if (count > 10000) throw new EvalException("Range is too long (10,000 items max)");

        var unit = a[0].Unit;
        return Value.List(Enumerable.Range(0, (int)count).Select(i => new Value(start + i * step, unit)));
    }

    private static void Need(List<Value> a, int n)
    {
        if (a.Count < n) throw new EvalException("Missing argument");
    }

    private static Value GeoMul(double k, Value v) => Ops.Mul(new Value(k), v);
    private static Value GeoAdd(Value a, Value b) => Ops.Add(a, b, 1);
    private static Value GeoPow2(Value v) => Ops.Mul(v, v);
    private static Value GeoPow3(Value v) => Ops.Mul(Ops.Mul(v, v), v);

    private static void SameDim(params Value[] xs)
    {
        Unit? first = xs.FirstOrDefault(x => x.Unit != null)?.Unit;
        if (first == null) return;
        foreach (var x in xs)
        {
            if (x.Unit == null)
                throw new EvalException($"Can't mix {first.Symbol} and a plain number");
            if (x.Unit.Dimension != first.Dimension)
                throw new EvalException($"Can't mix {first.Symbol} and {x.Unit.Symbol}");
        }
    }

    private static Value Map(List<Value> a, Func<double, double> f, bool keepUnit = false)
    {
        Need(a, 1);
        return new Value(f(a[0].Frac), keepUnit ? a[0].Unit : null);
    }

    private static double ToRadians(Value v, AngleMode angleMode) =>
        v.Unit is { } u && u.Dimension == Dim.Angle
            ? Unit.Convert(v.Number, u, UnitTable.Radian)
            : angleMode == AngleMode.Degrees ? v.Frac * Math.PI / 180 : v.Frac;

    private static Value Trig(List<Value> a, Func<double, double> f, AngleMode angleMode = AngleMode.Radians)
    {
        Need(a, 1);
        return new Value(f(ToRadians(a[0], angleMode)));
    }

    private static Value Inverse(List<Value> a, Func<double, double> f, AngleMode angleMode = AngleMode.Radians)
    {
        Need(a, 1);
        var result = f(a[0].Frac);
        return angleMode == AngleMode.Degrees
            ? new Value(result * 180 / Math.PI, UnitTable.Lookup("deg"))
            : new Value(result, UnitTable.Radian);
    }

    private static Value Pick(List<Value> a, Func<double, double, double> f)
    {
        Need(a, 1);
        var unit = a.FirstOrDefault(v => v.Unit != null)?.Unit;
        double Get(Value v) => unit == null ? v.Frac : Ops.In(v, unit);
        return new Value(a.Skip(1).Aggregate(Get(a[0]), (acc, v) => f(acc, Get(v))), unit);
    }

    private static Value Average(List<Value> a)
    {
        Need(a, 1);
        if (a.Any(v => v.Unit?.Dimension == Dim.Temperature))
        {
            var unit = a.First(v => v.Unit?.Dimension == Dim.Temperature).Unit!;
            return new Value(a.Average(v => Ops.In(v, unit)), unit);
        }
        var sum = Sum(a);
        return sum.IsPercent ? Value.Pct(sum.Number / a.Count) : Ops.Div(sum, new Value(a.Count));
    }

    private static Value Sum(List<Value> items)
    {
        if (items.Count == 0) return new Value(0);
        if (items.All(v => v.IsPercent)) return Value.Pct(items.Sum(v => v.Number));
        var temperatureUnit = items.FirstOrDefault(v => v.Unit?.Dimension == Dim.Temperature)?.Unit;
        if (temperatureUnit != null)
            return items.Select(v => new Value(Ops.In(v, temperatureUnit), temperatureUnit))
                .Aggregate((x, y) => Ops.Add(x, y, 1));
        return items.Select(v => v.IsPercent ? new Value(v.Frac) : v)
            .Aggregate((x, y) => Ops.Add(x, y, 1));
    }
}

internal sealed class Parser
{
    public static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        { "in", "to", "as", "into", "of", "on", "off", "mod", "rem", "div", "plus", "minus", "times", "xor", "percent", "per" };

    public static readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase)
        { "hex", "bin", "bin4", "bin8", "bin16", "bin32", "bin64", "oct", "dec", "decimal", "float", "roman", "sexa", "unicode" };

    public static readonly HashSet<string> Specials = new(StringComparer.OrdinalIgnoreCase)
        { "prev", "ans", "sum", "total", "average", "avg" };

    private static readonly Dictionary<string, double> Consts = new(StringComparer.OrdinalIgnoreCase)
    { ["pi"] = Math.PI, ["π"] = Math.PI, ["e"] = Math.E, ["tau"] = Math.Tau, ["infinity"] = double.PositiveInfinity, ["∞"] = double.PositiveInfinity };

    private static readonly Dictionary<string, double> ScaleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["thousand"] = 1e3,
        ["million"] = 1e6,
        ["billion"] = 1e9,
        ["bn"] = 1e9,
        ["trillion"] = 1e12
    };

    public static bool IsConst(string w) => Consts.ContainsKey(w);

    public static double? Scale(string w)
    {
        if (w == "k") return 1e3;
        if (w == "M") return 1e6;
        return w.Length > 1 && ScaleWords.TryGetValue(w, out var s) ? s : null;
    }

    private readonly List<Token> _t;
    private readonly Scope _sc;
    private readonly string? _baseDir;
    private int _p;

    public bool UsedAggregate { get; private set; }

    public Parser(List<Token> tokens, Scope scope, string? baseDir = null)
    {
        _sc = scope;
        _baseDir = baseDir;
        _t = SplitImplicitVarPairs(tokens, scope);
    }

    private const string ImplicitVarChars = "xyzabcuvw";

    private static List<Token> SplitImplicitVarPairs(List<Token> tokens, Scope scope)
    {
        var result = new List<Token>(tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            var tk = tokens[i];
            if (tk.Kind == Tk.Ident && tk.Text.Length == 2 &&
                ImplicitVarChars.Contains(tk.Text[0]) && ImplicitVarChars.Contains(tk.Text[1]) &&
                !(i + 1 < tokens.Count && tokens[i + 1].Kind == Tk.Op && tokens[i + 1].Text == "(") &&
                !scope.Vars.ContainsKey(tk.Text) && !Keywords.Contains(tk.Text) &&
                !Targets.Contains(tk.Text) && !Specials.Contains(tk.Text) &&
                !Consts.ContainsKey(tk.Text) && UnitTable.Parse(tk.Text) == null)
            {
                result.Add(new Token(Tk.Ident, tk.Text[0..1]));
                result.Add(new Token(Tk.Ident, tk.Text[1..2]));
                continue;
            }
            result.Add(tk);
        }
        return result;
    }

    private static bool IsSymbolicIdent(string name) =>
        name.Length == 1 && char.IsAsciiLetterLower(name[0]) &&
        name != "e" && name != "i" && name != "k" &&
        UnitTable.Parse(name) == null && !UnitTable.IsUnitless(name);

    private Token Cur => _t[_p];
    private Token At(int n) => _t[Math.Min(_p + n, _t.Count - 1)];
    private void Next() { if (_p < _t.Count - 1) _p++; }

    private bool IsOp(string s) => Cur.Kind == Tk.Op && Cur.Text == s;
    private static bool OpAt(Token k, string s) => k.Kind == Tk.Op && k.Text == s;

    private bool IsAnyWord(params string[] words) =>
        Cur.Kind == Tk.Ident && words.Any(w => string.Equals(Cur.Text, w, StringComparison.OrdinalIgnoreCase));

    private static bool IsSpecialCall(string name) =>
        name.Equals("hex", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("float", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("code", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("addDays", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("load", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("vector", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("genvector", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("entrywise", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("newtonsolve", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("secantsolve", StringComparison.OrdinalIgnoreCase);

    private bool IsValueIdent(int n)
    {
        var k = At(n);
        if (k.Kind != Tk.Ident) return false;
        return IsVariable(k.Text) || Consts.ContainsKey(k.Text) || k.Text == "i" || Specials.Contains(k.Text) ||
               IsSymbolicIdent(k.Text) ||
               ((Functions.Table.ContainsKey(k.Text) || IsSpecialCall(k.Text)) && OpAt(At(n + 1), "("));
    }

    // Variables ignore case, but an exact physics symbol (V, A, N) beats a variable like v, a or n
    private bool IsVariable(string name)
    {
        if (!_sc.Vars.ContainsKey(name)) return false;
        return !UnitTable.IsPhysicsSymbol(name) || _sc.Vars.Keys.Contains(name, StringComparer.Ordinal);
    }

    public Value ParseAll()
    {
        if (IsAnyWord("factor") && !OpAt(At(1), "("))
        {
            Next();
            var inner = Conversion();
            if (Cur.Kind != Tk.End) throw new ParseException($"Unexpected '{Cur.Text}'");
            return Value.FromSym(Symbolic.Factor(inner.ToSym(), Ops.AssumePositive));
        }

        var v = Conversion();
        if (IsOp("="))
        {
            Next();
            var rhs = Conversion();
            if (Cur.Kind != Tk.End) throw new ParseException($"Unexpected '{Cur.Text}'");
            return Value.FromSym(Solver.Solve(v.ToSym(), rhs.ToSym(), Ops.AssumePositive));
        }
        if (IsOp(",") || IsOp(";"))
        {
            var items = new List<Value> { v };
            while (IsOp(",") || IsOp(";"))
            {
                Next();
                if (Cur.Kind == Tk.End || IsOp(",") || IsOp(";"))
                    items.Add(new Value(ExactNumber.Zero));
                else
                    items.Add(Conversion());
            }
            v = Value.List(items);
        }
        if (Cur.Kind != Tk.End) throw new ParseException($"Unexpected '{Cur.Text}'");
        return v;
    }

    // conversion := bitwiseOr (("in" | "to" | "as" | "into") target)*
    private Value Conversion()
    {
        var v = BitwiseOr();
        while (IsAnyWord("in", "to", "as", "into"))
        {
            Next();
            v = ApplyConversion(v);
        }
        return v;
    }

    private Value ApplyConversion(Value left)
    {
        // "as a %", "as an percent"
        if (IsAnyWord("a", "an") && (OpAt(At(1), "%") || (At(1).Kind == Tk.Ident &&
            string.Equals(At(1).Text, "percent", StringComparison.OrdinalIgnoreCase))))
            Next();

        if (IsOp("%") || IsAnyWord("percent"))
        {
            Next();
            if (IsAnyWord("of"))
            {
                Next();
                return Ops.Broadcast(left, Additive(), PercentOf);
            }
            return Ops.Map(left, x => Value.Pct(x.Frac * 100));
        }

        if (Cur.Kind == Tk.Ident && Cur.Text.Equals("b", StringComparison.OrdinalIgnoreCase) &&
            OpAt(At(1), "?") && At(2).Kind == Tk.Ident &&
            (At(2).Text.Equals("byte", StringComparison.OrdinalIgnoreCase) ||
             At(2).Text.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
             At(2).Text == "B"))
        {
            Next(); Next(); Next();
            var byteUnit = UnitTable.Lookup("B")!;
            return Ops.Map(left, x =>
            {
                double bytes = Ops.In(x, byteUnit);
                var target = UnitTable.BestBinaryByte(bytes);
                return new Value(bytes / target.Factor, target);
            });
        }

        if (Cur.Kind == Tk.Ident)
        {
            var word = Cur.Text.ToLowerInvariant();
            if (word == "time")
            {
                Next();
                return Ops.Map(left, FormatAsTime);
            }
            if (word == "utc")
            {
                Next();
                double offsetHours = 0;
                bool asUtc = true;
                if (IsOp("+") || IsOp("-"))
                {
                    int sign = Cur.Text == "-" ? -1 : 1;
                    Next();
                    offsetHours = sign * Unary().Frac;
                    asUtc = false;
                }
                if (!double.IsFinite(offsetHours) || offsetHours is < -14 or > 14)
                    throw new EvalException("UTC offset must be between -14 and +14 hours");
                double totalMinutes = offsetHours * 60;
                double roundedMinutes = Math.Round(totalMinutes);
                if (Math.Abs(totalMinutes - roundedMinutes) > 1e-9)
                    throw new EvalException("UTC offset must be a whole number of minutes");
                var offset = TimeSpan.FromMinutes((int)roundedMinutes);
                return Ops.Map(left, x =>
                {
                    if (x.DateValue is not { } d) throw new EvalException("Expected a date");
                    return Value.Date(d.ToOffset(offset, asUtc));
                });
            }
            if (word is "hex" or "bin" or "bin4" or "bin8" or "bin16" or "bin32" or "bin64" or "oct" or "float" or "roman" or "sexa" or "unicode")
            {
                Next();
                return Ops.Map(left, x => ToBase(x, word));
            }
            if (word == "base")
            {
                Next();
                if (Cur.Kind == Tk.End || IsOp(")") || IsOp(",") || IsOp(";") || IsOp("]") || IsAnyWord("in", "to", "as", "into"))
                    return Ops.ToBaseUnits(left);
                var radixVal = Unary();
                if (radixVal.Unit != null || radixVal.Frac != Math.Truncate(radixVal.Frac) || radixVal.Frac is < 2 or > 36)
                    throw new EvalException("Base must be an integer between 2 and 36");
                int radix = (int)radixVal.Frac;
                return Ops.Map(left, x => ToRadix(x, radix));
            }
            if (word is "dec" or "decimal") { Next(); return Ops.Map(left, x => new Value(x.Frac, x.Unit)); }
            if (word == "in") { Next(); return Ops.ConvertTo(left, TargetUnit(UnitTable.Inch)); }
        }

        if (Cur.Kind is Tk.Ident or Tk.Sym)
        {
            var unit = Cur.Kind == Tk.Sym ? UnitTable.Lookup(Cur.Text) : UnitTable.Parse(Cur.Text);
            if (unit != null || UnitlessAt(0))
            {
                Next();
                return Ops.ConvertTo(left, TargetUnit(unit));
            }
        }

        throw new ParseException("Unknown unit");
    }

    /// <summary>The rest of a conversion target: "in ft/s^2", "in miles per hour", "in kWh/$".</summary>
    private Unit TargetUnit(Unit? unit)
    {
        // A zero-powered factor (m^0, m⁰) is one, so the target is only decided once every factor is read
        var result = UnitPower(unit, out _);
        while ((IsOp("/") || IsAnyWord("per")) && (UnitAt(1) != null || UnitlessAt(1) || SymbolUnitAt(1) != null))
        {
            Next();
            var divisor = UnitAt(0) ?? SymbolUnitAt(0);
            Next();

            if (UnitPower(divisor, out _) is not { } power) continue;
            if (result == null) result = UnitTable.Pow(power, -1, out _);
            else if (result.Offset == 0 && power.Offset == 0 && result.SameAs(power)) result = null;   // m per m cancels exactly
            else result = UnitTable.Divide(result, power) ?? throw new EvalException($"Can't combine {result.Symbol} and {power.Symbol}");
        }
        return result ?? throw new ParseException("Unknown unit");
    }

    /// <summary>A currency symbol at offset n with no value after it ($ in 5 kWh/$), which means one of it.</summary>
    private Unit? SymbolUnitAt(int n)
    {
        if (At(n).Kind != Tk.Sym) return null;
        var next = At(n + 1);
        bool value = next.Kind is Tk.Num or Tk.Sym || OpAt(next, "(") || OpAt(next, "[") ||
                     (next.Kind == Tk.Ident && !Keywords.Contains(next.Text));
        return value ? null : UnitTable.Lookup(At(n).Text);
    }

    /// <summary>A unit word at offset n that isn't a variable, keyword, or function call; "in" means inches.</summary>
    private Unit? UnitAt(int n)
    {
        var k = At(n);
        if (k.Kind != Tk.Ident || OpAt(At(n + 1), "(")) return null;
        if (k.Text.Equals("in", StringComparison.OrdinalIgnoreCase)) return UnitTable.Inch;
        if (Keywords.Contains(k.Text) || IsValueIdent(n)) return null;
        return UnitTable.Parse(k.Text);
    }

    /// <summary>Applies following integer powers to a unit just read: 5 m^2 is 5 m², 5 m^2^2 is 5 m⁴. Null when a power is 0.</summary>
    private Unit? UnitPower(Unit? unit, out double scale)
    {
        scale = 1;
        Unit? result = unit;

        while (IsOp("^"))
        {
            bool negative = OpAt(At(1), "-");
            int sign = negative || OpAt(At(1), "+") ? 1 : 0;
            var power = At(1 + sign);
            if (power.Kind != Tk.Num) break;
            if (power.Num != Math.Floor(power.Num))
                throw new EvalException("Unit powers must be whole numbers; use parentheses to raise the whole quantity");
            if (power.Num > UnitTable.MaxPower) throw UnitTable.PowerTooLarge();

            for (int i = 2 + sign; i > 0; i--) Next();
            if (result == null) continue;

            result = UnitTable.Pow(result, (int)(negative ? -power.Num : power.Num), out double step);
            scale *= step;
        }
        return result;
    }

    /// <summary>A unit raised to the power 0 at offset n (m⁰), which is the plain number 1.</summary>
    private bool UnitlessAt(int n)
    {
        var k = At(n);
        return k.Kind == Tk.Ident && !OpAt(At(n + 1), "(") && !Keywords.Contains(k.Text) &&
               !IsValueIdent(n) && UnitTable.IsUnitless(k.Text);
    }

    /// <summary>The right operand of *, / or per; a bare unit means one of it (5 m / s, 60 miles per hour).</summary>
    private Value Operand()
    {
        Value v;
        if (UnitlessAt(0)) { Next(); UnitPower(null, out _); v = new Value(1); }
        else if ((UnitAt(0) ?? SymbolUnitAt(0)) is { } unit)
        {
            Next();
            var powered = UnitPower(unit, out double scale);
            v = new Value(scale, powered);
        }
        else
        {
            v = Unary();
            // Bind implicit multiplication with a symbolic variable tighter than '/' (e.g. 1/3x = 1/(3x))
            if (!v.IsSymbolic && v.Unit == null && Cur.Kind == Tk.Ident && IsSymbolicIdent(Cur.Text))
                v = Ops.Mul(v, Unary());
            return v;
        }

        // One of a unit raised to any other exponent is still that unit's power (5 m / s^(2) is 5 m/s²)
        if (!IsOp("^")) return v;
        Next();
        return Ops.Pow(v, Unary());
    }

    private static Value PercentOf(Value part, Value whole)
    {
        double w = part.Unit != null && whole.Unit != null ? Ops.In(whole, part.Unit) : whole.Frac;
        if (w == 0) throw new EvalException("Division by zero");
        return Value.Pct(part.Frac / w * 100);
    }

    private static Value FormatAsTime(Value v)
    {
        if (v.Unit?.Dimension != Dim.Time)
            throw new EvalException("Expected a time duration");
        double totalSeconds = Math.Round(Ops.In(v, UnitTable.Lookup("s")!));
        string sign = totalSeconds < 0 ? "-" : "";
        long absSec = (long)Math.Abs(totalSeconds);
        long hours = absSec / 3600;
        long minutes = (absSec % 3600) / 60;
        long seconds = absSec % 60;
        string text = seconds == 0
            ? $"{sign}{hours}:{minutes:00}"
            : $"{sign}{hours}:{minutes:00}:{seconds:00}";
        return new Value(v.Number, v.Unit) { Text = text };
    }

    private static string GroupNibbles(string bits) =>
        string.Join(' ', Enumerable.Range(0, (bits.Length + 3) / 4)
            .Select(idx => bits.Substring(idx * 4, Math.Min(4, bits.Length - idx * 4))));

    private static Value ToBase(Value v, string kind)
    {
        if (kind == "float")
        {
            uint rawBits = BitConverter.SingleToUInt32Bits((float)v.Frac);
            string bitStr = System.Convert.ToString(rawBits, 2).PadLeft(32, '0');
            return new Value(v.Frac) { Text = GroupNibbles(bitStr) };
        }

        if (kind == "sexa")
        {
            double val = v.Frac;
            string sign = val < 0 ? "-" : "";
            double abs = Math.Abs(val);
            int deg = (int)Math.Floor(abs);
            double minFrac = (abs - deg) * 60;
            int min = (int)Math.Floor(minFrac + 1e-9);
            double sec = Math.Round((minFrac - min) * 60, 6);
            if (sec >= 60) { sec -= 60; min++; }
            if (min >= 60) { min -= 60; deg++; }
            string secText = Math.Abs(sec - Math.Round(sec)) < 1e-6
                ? ((int)Math.Round(sec)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : sec.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            return new Value(val) { Text = $"{sign}{deg}°{min}′{secText}″" };
        }

        System.Numerics.BigInteger big;
        if (!v.ExactFrac.TryGetBigInteger(out big))
        {
            double d = Math.Round(v.Frac);
            if (!double.IsFinite(d) || Math.Abs(d) > long.MaxValue / 2.0) throw new EvalException("Number too large");
            big = new System.Numerics.BigInteger(d);
        }

        if (kind == "unicode")
        {
            if (big < 0 || big > 0x10FFFF || (big >= 0xD800 && big <= 0xDFFF))
                throw new EvalException("Invalid Unicode code point");
            return new Value(ExactNumber.FromBigInteger(big)) { Text = char.ConvertFromUtf32((int)big) };
        }

        if (kind == "roman")
        {
            if (big < 1 || big > 3999) throw new EvalException("Roman numerals support 1 to 3999");
            int rem = (int)big;
            (int Val, string Sym)[] numerals =
            [
                (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
                (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
                (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
            ];
            var sb = new System.Text.StringBuilder();
            foreach (var (val, sym) in numerals)
            {
                while (rem >= val) { sb.Append(sym); rem -= val; }
            }
            return new Value(ExactNumber.FromBigInteger(big)) { Text = sb.ToString() };
        }

        if (kind.StartsWith("bin", StringComparison.Ordinal) && kind.Length > 3 && int.TryParse(kind[3..], out int bits))
        {
            var mask = (System.Numerics.BigInteger.One << bits) - 1;
            string raw = FormatBigRadix(big & mask, 2).PadLeft(bits, '0');
            if (raw.Length > bits) raw = raw[^bits..];
            return new Value(ExactNumber.FromBigInteger(big)) { Text = GroupNibbles(raw) };
        }

        var absBig = System.Numerics.BigInteger.Abs(big);
        string body = kind switch
        {
            "hex" => "0x" + FormatBigRadix(absBig, 16),
            "bin" => "0b" + FormatBigRadix(absBig, 2),
            _ => "0o" + FormatBigRadix(absBig, 8)
        };
        return new Value(ExactNumber.FromBigInteger(big)) { Text = (big.Sign < 0 ? "-" : "") + body };
    }

    private static string FormatBigRadix(System.Numerics.BigInteger n, int radix)
    {
        if (n.IsZero) return "0";
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var chars = new Stack<char>();
        while (n > 0)
        {
            n = System.Numerics.BigInteger.DivRem(n, radix, out var rem);
            chars.Push(digits[(int)rem]);
        }
        return new string(chars.ToArray());
    }

    private static Value ToRadix(Value v, int radix)
    {
        System.Numerics.BigInteger big;
        if (!v.ExactFrac.TryGetBigInteger(out big))
        {
            double d = Math.Round(v.Frac);
            if (!double.IsFinite(d) || Math.Abs(d) > long.MaxValue / 2.0) throw new EvalException("Number too large");
            big = new System.Numerics.BigInteger(d);
        }

        var absBig = System.Numerics.BigInteger.Abs(big);
        return new Value(ExactNumber.FromBigInteger(big))
        {
            Text = (big.Sign < 0 ? "-" : "") + FormatBigRadix(absBig, radix)
        };
    }

    private Value BitwiseOr()
    {
        var v = BitwiseXor();
        while (IsOp("|"))
        {
            Next();
            v = Ops.BitOr(v, BitwiseXor());
        }
        return v;
    }

    private Value BitwiseXor()
    {
        var v = BitwiseAnd();
        while (IsOp("^^") || IsAnyWord("xor"))
        {
            Next();
            v = Ops.BitXor(v, BitwiseAnd());
        }
        return v;
    }

    private Value BitwiseAnd()
    {
        var v = Shift();
        while (IsOp("&"))
        {
            Next();
            v = Ops.BitAnd(v, Shift());
        }
        return v;
    }

    private Value Shift()
    {
        var v = Additive();
        while (IsOp("<<") || IsOp(">>"))
        {
            bool left = Cur.Text == "<<";
            Next();
            v = left ? Ops.ShiftLeft(v, Additive()) : Ops.ShiftRight(v, Additive());
        }
        return v;
    }

    private Value Additive()
    {
        var v = Term();
        while (IsOp("+") || IsOp("-") || IsAnyWord("plus", "minus"))
        {
            int sign = IsOp("+") || IsAnyWord("plus") ? 1 : -1;
            Next();
            v = Ops.Add(v, Term(), sign);
        }
        return v;
    }

    private Value Term()
    {
        var v = Unary();
        while (true)
        {
            if (IsOp("*") || IsAnyWord("times")) { Next(); v = Ops.Mul(v, Operand()); }
            else if (IsOp(".*")) { Next(); v = Ops.ElementWiseMul(v, Unary()); }
            else if (IsOp("./")) { Next(); v = Ops.ElementWiseDiv(v, Unary()); }
            else if (IsOp(".")) { Next(); v = LinearAlgebra.Dot(v, Unary()); }
            else if (IsOp("/") || IsAnyWord("per")) { Next(); v = Ops.Div(v, Operand()); }
            else if (IsOp("//") || IsOp("\\") || IsAnyWord("div")) { Next(); v = Ops.IntDiv(v, Unary()); }
            else if (IsOp("%") || IsAnyWord("rem")) { Next(); v = Ops.Mod(v, Unary()); }
            else if (IsOp("%%") || IsAnyWord("mod")) { Next(); v = Ops.FloorMod(v, Unary()); }
            else if (IsAnyWord("of")) { Next(); v = Ops.Mul(v, Unary()); }              // 20% of 150
            else if (IsAnyWord("on")) { Next(); v = Ops.Add(Unary(), AsPercent(v), 1); }  // 20% on 100
            else if (IsAnyWord("off")) { Next(); v = Ops.Add(Unary(), AsPercent(v), -1); } // 20% off 100
            else if (v.Unit?.Dimension == Dim.Time && Cur.Kind == Tk.Num && UnitAt(1)?.Dimension == Dim.Time)
            {
                v = Ops.Add(v, Postfix(), 1);
            }
            else if (IsOp("(") || (Cur.Kind == Tk.Ident && IsValueIdent(0))) { v = Ops.Mul(v, Unary()); } // 2(3), 2x, 3 pi
            else break;
        }
        return v;
    }

    private static Value AsPercent(Value v) => v.IsPercent ? v : Value.Pct(v.Number);

    private Value Unary()
    {
        if (IsOp("-") || IsAnyWord("minus"))
        {
            Next();
            var v = Unary();
            return Ops.Map(v, x =>
                x.IsSymbolic ? Value.FromSym(Symbolic.Negate(x.SymValue!, Ops.AssumePositive)) :
                x.IsComplex ? Value.Complex(ComplexInterval.Negate(x.ComplexValue!.Value)) :
                x.IsPercent ? Value.Pct(-x.Exact) :
                new Value(-x.Exact, x.Unit));
        }
        if (IsOp("+") || IsAnyWord("plus")) { Next(); return Unary(); }
        if (IsOp("~")) { Next(); return Ops.BitNot(Unary()); }
        return Power();
    }

    private Value Power()
    {
        var b = Postfix();
        if (IsOp("+/-"))
        {
            Next();
            return Ops.Uncertainty(b, Postfix());
        }
        if (IsOp("^"))
        {
            Next();
            return Ops.Pow(b, Unary());
        }
        if (IsOp(".^"))
        {
            Next();
            return Ops.ElementWisePow(b, Unary());
        }
        return b;
    }

    private Value Postfix()
    {
        var v = Primary();
        while (true)
        {
            if (IsOp(".'"))
            {
                Next();
                v = LinearAlgebra.FromMatrix(LinearAlgebra.Transpose(LinearAlgebra.ToMatrix(v)));
            }
            else if (IsOp("%"))
            {
                if (v.IsPercent || v.Unit != null) break;
                if (At(1).Kind is Tk.Num or Tk.Sym || OpAt(At(1), "[")) break;
                Next();
                v = v.IsSymbolic ? Value.Pct(v.SymValue!) : Value.Pct(v.Exact);
            }
            else if (IsOp("!"))
            {
                Next();
                v = Ops.Factorial(v);
            }
            else if (Cur.Kind == Tk.Ident)
            {
                if (IsValueIdent(0) || v.IsPercent) break;

                if (UnitlessAt(0))
                {
                    Next();
                    UnitPower(null, out _);
                    continue;
                }

                var scale = Scale(Cur.Text);
                if (scale.HasValue)
                {
                    Next();
                    v = new Value(v.Number * scale.Value, v.Unit);
                    continue;
                }

                // "12 in" means inches unless it reads as a conversion ("12 in cm"); "5 in per s" is inches too
                if (IsAnyWord("in") && v.Unit == null && (At(1).Kind == Tk.End || (At(1).Kind == Tk.Op && At(1).Text != "(") ||
                    (At(1).Kind == Tk.Ident && At(1).Text.Equals("per", StringComparison.OrdinalIgnoreCase))))
                {
                    Next();
                    var inch = UnitPower(UnitTable.Inch, out double inchScale);
                    v = new Value(v.Number * inchScale, inch);
                    continue;
                }

                var unit = Keywords.Contains(Cur.Text) || OpAt(At(1), "(") ? null : UnitTable.Parse(Cur.Text);
                if (unit != null && v.Unit == null)
                {
                    Next();
                    var powered = UnitPower(unit, out double unitScale);
                    v = new Value(v.Number * unitScale, powered);
                    continue;
                }
                break;
            }
            else if (Cur.Kind == Tk.Sym && v.Unit == null && !v.IsPercent)
            {
                var unit = UnitTable.Lookup(Cur.Text);
                if (unit == null) break;
                Next();
                var powered = UnitPower(unit, out double symbolScale);
                v = new Value(v.Number * symbolScale, powered);
            }
            else break;
        }
        return v;
    }

    private Value Primary()
    {
        var k = Cur;
        switch (k.Kind)
        {
            case Tk.Num:
                Next();
                if (IsOp(":") && At(1).Kind == Tk.Num)
                {
                    Next();
                    var mins = Cur.Exact;
                    Next();
                    var secs = ExactNumber.Zero;
                    if (IsOp(":") && At(1).Kind == Tk.Num)
                    {
                        Next();
                        secs = Cur.Exact;
                        Next();
                    }
                    var hours = k.Exact + mins / ExactNumber.FromLong(60) + secs / ExactNumber.FromLong(3600);
                    return new Value(hours, UnitTable.Lookup("h"));
                }
                return new Value(k.Exact);

            case Tk.Str:
                Next();
                return CalcDate.TryParse(k.Text, out var parsedDate) ? Value.Date(parsedDate) : Value.Str(k.Text);

            case Tk.Sym:
                {
                    var unit = UnitTable.Lookup(k.Text) ?? throw new ParseException("Unknown currency");
                    Next();
                    var inner = Primary();
                    if (inner.Unit != null || inner.IsPercent) throw new ParseException("Unexpected unit");
                    return new Value(inner.Number, unit);
                }

            case Tk.Op when k.Text == "[":
                {
                    Next();
                    var rows = new List<List<Value>>();
                    var current = new List<Value>();

                    while (!IsOp("]") && Cur.Kind != Tk.End)
                    {
                        if (IsOp(";"))
                        {
                            rows.Add(current);
                            current = [];
                            Next();
                            continue;
                        }

                        if (IsOp(","))
                        {
                            if (current.Count == 0) current.Add(new Value(ExactNumber.Zero));
                            Next();
                            if (IsOp(",") || IsOp(";") || IsOp("]"))
                                current.Add(new Value(ExactNumber.Zero));
                            continue;
                        }

                        int before = _p;
                        bool rowHasComma = RowHasCommaBeforeDelimiter();
                        current.Add(rowHasComma ? Conversion() : Unary());
                        if (_p == before) throw new ParseException($"Unexpected '{Cur.Text}'");
                        if (IsOp(","))
                        {
                            Next();
                            if (IsOp(",") || IsOp(";") || IsOp("]"))
                                current.Add(new Value(ExactNumber.Zero));
                        }
                    }

                    if (!IsOp("]"))
                        throw new ParseException("Expected ']'");
                    Next();

                    if (rows.Count > 0)
                    {
                        if (current.Count > 0) rows.Add(current);
                        return Value.List(rows.Select(Value.List));
                    }
                    return Value.List(current);
                }

            case Tk.Op when k.Text == "(":
                {
                    Next();
                    if (IsOp(")")) { Next(); return Value.List([]); }

                    bool isList = false;
                    var items = new List<Value>();
                    if (IsOp(",") || IsOp(";"))
                    {
                        isList = true;
                        items.Add(new Value(ExactNumber.Zero));
                    }
                    else
                    {
                        items.Add(Conversion());
                    }

                    while (IsOp(",") || IsOp(";"))
                    {
                        isList = true;
                        Next();
                        if (IsOp(",") || IsOp(";") || IsOp(")"))
                            items.Add(new Value(ExactNumber.Zero));
                        else
                            items.Add(Conversion());
                    }

                    if (!IsOp(")")) throw new ParseException("Expected ')'");
                    Next();
                    return isList ? Value.List(items) : items[0];
                }

            case Tk.Ident:
                return Identifier();
        }

        throw new ParseException(k.Kind == Tk.End ? "Unexpected end" : $"Unexpected '{k.Text}'");
    }

    private Value Identifier()
    {
        string name = Cur.Text;

        if (IsVariable(name) && _sc.Vars.TryGetValue(name, out var variable))
        {
            Next();
            return variable;
        }

        if (OpAt(At(1), "(") && name.Equals("vector", StringComparison.OrdinalIgnoreCase))
        {
            Next(); Next();
            if (IsOp(")")) { Next(); return Value.List([]); }
            var items = new List<Value>();
            if (IsOp(",") || IsOp(";")) items.Add(new Value(ExactNumber.Zero));
            else items.Add(Conversion());
            while (IsOp(",") || IsOp(";"))
            {
                Next();
                if (IsOp(",") || IsOp(";") || IsOp(")"))
                    items.Add(new Value(ExactNumber.Zero));
                else
                    items.Add(Conversion());
            }
            if (!IsOp(")")) throw new ParseException("Expected ')'");
            Next();
            return Value.List(items);
        }

        if (OpAt(At(1), "(") && name.Equals("genvector", StringComparison.OrdinalIgnoreCase))
        {
            Next(); Next();
            var exprTokens = ReadRawArgTokens();
            if (!(IsOp(",") || IsOp(";"))) throw new ParseException("Expected ',' or ';'");
            Next();
            var minVal = Conversion().ExactFrac;
            if (!(IsOp(",") || IsOp(";"))) throw new ParseException("Expected ',' or ';'");
            Next();
            var maxVal = Conversion().ExactFrac;

            ExactNumber countOrStep = ExactNumber.One;
            bool has4th = false;
            string varName = "x";
            bool stepMode = false;

            if (IsOp(",") || IsOp(";"))
            {
                Next();
                has4th = true;
                countOrStep = Conversion().ExactFrac;
            }
            if (IsOp(",") || IsOp(";"))
            {
                Next();
                if (Cur.Kind == Tk.Ident) { varName = Cur.Text; Next(); }
                else throw new ParseException("Expected variable name");
            }
            if (IsOp(",") || IsOp(";"))
            {
                Next();
                stepMode = !Conversion().ExactFrac.IsZero;
            }
            if (!IsOp(")")) throw new ParseException("Expected ')'");
            Next();

            var points = new List<ExactNumber>();
            if (!has4th)
            {
                // 3-arg form: genvector(expr, min, max) with step 1
                var step = maxVal.ToDouble() >= minVal.ToDouble() ? ExactNumber.One : -ExactNumber.One;
                int count = (int)Math.Floor((maxVal.ToDouble() - minVal.ToDouble()) / step.ToDouble() + 1e-9) + 1;
                if (count < 0 || count > 10000) throw new EvalException("Vector is too long (10,000 items max)");
                for (int i = 0; i < count; i++) points.Add(minVal + ExactNumber.FromLong(i) * step);
            }
            else if (stepMode)
            {
                if (countOrStep.IsZero) throw new EvalException("Step cannot be zero");
                int count = (int)Math.Floor((maxVal.ToDouble() - minVal.ToDouble()) / countOrStep.ToDouble() + 1e-9) + 1;
                if (count < 0 || count > 10000) throw new EvalException("Vector is too long (10,000 items max)");
                for (int i = 0; i < count; i++) points.Add(minVal + ExactNumber.FromLong(i) * countOrStep);
            }
            else
            {
                int n = (int)Math.Round(countOrStep.ToDouble());
                if (n < 1 || n > 10000) throw new EvalException("Vector length out of range");
                if (n == 1) points.Add(minVal);
                else
                {
                    var step = (maxVal - minVal) / ExactNumber.FromLong(n - 1);
                    for (int i = 0; i < n; i++) points.Add(minVal + ExactNumber.FromLong(i) * step);
                }
            }

            var results = new List<Value>(points.Count);
            foreach (var pt in points)
            {
                var child = new Scope(_sc.AngleMode);
                foreach (var kv in _sc.Vars) child.Vars[kv.Key] = kv.Value;
                child.Vars[varName] = new Value(pt);
                results.Add(new Parser(exprTokens, child, _baseDir).ParseAll());
            }
            return Value.List(results);
        }

        if (OpAt(At(1), "(") && (name.Equals("newtonsolve", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("secantsolve", StringComparison.OrdinalIgnoreCase)))
        {
            bool isSecant = name.Equals("secantsolve", StringComparison.OrdinalIgnoreCase);
            Next(); Next();
            var eqTokens = ReadRawArgTokens();
            if (!(IsOp(",") || IsOp(";"))) throw new ParseException("Expected ',' or ';'");
            Next();
            var x0 = Conversion();
            Value? x1 = null;
            if (isSecant)
            {
                if (!(IsOp(",") || IsOp(";"))) throw new ParseException("Expected ',' or ';'");
                Next();
                x1 = Conversion();
            }
            string varName = "x";
            if (IsOp(",") || IsOp(";"))
            {
                Next();
                if (Cur.Kind == Tk.Ident) { varName = Cur.Text; Next(); }
                else throw new ParseException("Expected variable name");
            }
            if (!IsOp(")")) throw new ParseException("Expected ')'");
            Next();

            // Split eqTokens on top-level '=' into lhs and optional rhs
            var lhsTokens = new List<Token>();
            List<Token>? rhsTokens = null;
            int depth = 0;
            foreach (var tk in eqTokens)
            {
                if (tk.Kind == Tk.End) break;
                if (tk.Kind == Tk.Op && tk.Text is "(" or "[") depth++;
                else if (tk.Kind == Tk.Op && tk.Text is ")" or "]") depth--;
                if (depth == 0 && rhsTokens == null && tk.Kind == Tk.Op && tk.Text == "=")
                {
                    rhsTokens = [];
                    continue;
                }
                (rhsTokens ?? lhsTokens).Add(tk);
            }
            lhsTokens.Add(new Token(Tk.End, ""));
            rhsTokens?.Add(new Token(Tk.End, ""));

            Value EvalDiff(Value pt)
            {
                var child = new Scope(_sc.AngleMode);
                foreach (var kv in _sc.Vars) child.Vars[kv.Key] = kv.Value;
                child.Vars[varName] = pt;
                var lv = new Parser(lhsTokens, child, _baseDir).ParseAll();
                if (rhsTokens == null) return lv;
                var rv = new Parser(rhsTokens, child, _baseDir).ParseAll();
                return Ops.Add(lv, rv, -1);
            }

            return isSecant ? Solver.SecantSolve(EvalDiff, x0, x1!) : Solver.NewtonSolve(EvalDiff, x0);
        }

        if (OpAt(At(1), "(") && name.Equals("entrywise", StringComparison.OrdinalIgnoreCase))
        {
            Next(); Next();
            var exprTokens = ReadRawArgTokens();
            var bindings = new List<(string Var, List<Value> Values)>();
            while (IsOp(",") || IsOp(";"))
            {
                Next();
                var vec = Ops.Flatten(Conversion()).ToList();
                if (!(IsOp(",") || IsOp(";"))) throw new ParseException("Expected variable name");
                Next();
                if (Cur.Kind != Tk.Ident) throw new ParseException("Expected variable name");
                string vName = Cur.Text;
                Next();
                bindings.Add((vName, vec));
            }
            if (!IsOp(")")) throw new ParseException("Expected ')'");
            Next();
            if (bindings.Count == 0) throw new EvalException("Missing vector argument");

            int len = bindings[0].Values.Count;
            if (bindings.Any(b => b.Values.Count != len))
                throw new EvalException("Vectors must have equal length");

            var results = new List<Value>(len);
            for (int i = 0; i < len; i++)
            {
                var child = new Scope(_sc.AngleMode);
                foreach (var kv in _sc.Vars) child.Vars[kv.Key] = kv.Value;
                foreach (var (vName, vals) in bindings) child.Vars[vName] = vals[i];
                results.Add(new Parser(exprTokens, child, _baseDir).ParseAll());
            }
            return Value.List(results);
        }

        if (OpAt(At(1), "(") && name.Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            Next(); Next();
            string pathArg;
            if (Cur.Kind == Tk.Str)
            {
                pathArg = Cur.Text;
                Next();
            }
            else
            {
                var raw = new System.Text.StringBuilder();
                while (Cur.Kind != Tk.End && !IsOp(")"))
                {
                    raw.Append(Cur.Text);
                    Next();
                }
                pathArg = raw.ToString().Trim();
            }
            if (!IsOp(")")) throw new ParseException("Expected ')'");
            Next();
            return LoadCsv(pathArg);
        }

        if (OpAt(At(1), "(") && (name.Equals("timestamp", StringComparison.OrdinalIgnoreCase) || name.Equals("addDays", StringComparison.OrdinalIgnoreCase)))
        {
            Next(); Next();
            var date = ReadDateArg();
            if (name.Equals("timestamp", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsOp(")")) throw new ParseException("Expected ')'");
                Next();
                return new Value(ExactNumber.FromLong(date.Instant.ToUnixTimeSeconds()));
            }
            if (!(IsOp(",") || IsOp(";"))) throw new ParseException("Expected ',' or ';'");
            Next();
            var days = Conversion();
            if (!IsOp(")")) throw new ParseException("Expected ')'");
            Next();
            return Value.Date(date.AddSeconds(days.Frac * 86400));
        }

        if (OpAt(At(1), "(") && name.Equals("code", StringComparison.OrdinalIgnoreCase))
        {
            Next(); Next();
            string text;
            if (Cur.Kind == Tk.Str)
            {
                text = Cur.Text;
                Next();
            }
            else if (Cur.Kind == Tk.Ident && IsVariable(Cur.Text) && _sc.Vars.TryGetValue(Cur.Text, out var strVar) && strVar.IsString)
            {
                text = strVar.StringValue!;
                Next();
            }
            else if (Cur.Kind != Tk.End && !IsOp(")") && !IsOp(","))
            {
                text = Cur.Text;
                Next();
            }
            else
            {
                throw new ParseException("Expected text argument for code()");
            }

            string encoding = "utf-32";
            if (IsOp(","))
            {
                Next();
                var enc = new System.Text.StringBuilder();
                while (Cur.Kind != Tk.End && !IsOp(",") && !IsOp(")"))
                {
                    enc.Append(Cur.Text);
                    Next();
                }
                encoding = enc.ToString().Trim().ToLowerInvariant();
            }

            // Optional 3rd argument (signed/unsigned flag); consume if present
            if (IsOp(","))
            {
                Next();
                _ = Conversion();
            }

            if (!IsOp(")")) throw new ParseException("Expected ')'");
            Next();

            if (encoding is "utf-8" or "utf8")
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                var val = System.Numerics.BigInteger.Zero;
                foreach (byte b in bytes)
                    val = (val << 8) | b;
                return new Value(ExactNumber.FromBigInteger(val));
            }

            var runes = text.EnumerateRunes().Select(r => new Value(ExactNumber.FromLong(r.Value))).ToList();
            if (runes.Count == 0) return new Value(ExactNumber.Zero);
            return runes.Count == 1 ? runes[0] : Value.List(runes);
        }

        if (OpAt(At(1), "(") && (name.Equals("hex", StringComparison.OrdinalIgnoreCase) || name.Equals("float", StringComparison.OrdinalIgnoreCase)))
        {
            Next(); Next();
            var raw = new System.Text.StringBuilder();
            while (Cur.Kind != Tk.End && !IsOp(")"))
            {
                raw.Append(Cur.Text);
                Next();
            }
            if (!IsOp(")")) throw new ParseException("Expected ')'");
            Next();
            var arg = string.Concat(raw.ToString().Where(c => c != '_' && !char.IsWhiteSpace(c)));
            if (name.Equals("hex", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) arg = arg[2..];
                if (arg.Length == 0 || !System.Numerics.BigInteger.TryParse("0" + arg, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hexVal))
                    throw new EvalException($"Invalid hex literal '{raw}'");
                return new Value(ExactNumber.FromBigInteger(hexVal));
            }
            if (arg.Length is 0 or > 32 || arg.Any(ch => ch is not ('0' or '1')))
                throw new EvalException($"Invalid 32-bit float binary literal '{raw}'");
            uint bits = System.Convert.ToUInt32(arg, 2);
            return new Value(BitConverter.UInt32BitsToSingle(bits));
        }

        if (OpAt(At(1), "(") && Functions.Table.ContainsKey(name))
        {
            Next(); Next();
            var args = new List<Value>();
            if (!IsOp(")"))
            {
                do { args.Add(Conversion()); }
                while ((IsOp(",") || IsOp(";")) && Advance());
            }
            if (!IsOp(")")) throw new ParseException("Expected ')'");
            Next();
            return Functions.Call(name, args, _sc.AngleMode);
        }

        if (name.Equals("prev", StringComparison.OrdinalIgnoreCase) || name.Equals("ans", StringComparison.OrdinalIgnoreCase))
        {
            Next();
            return _sc.Prev ?? throw new EvalException("No previous result");
        }

        if (Specials.Contains(name))
        {
            Next();
            UsedAggregate = true;
            return Aggregate(averaged: name.StartsWith("av", StringComparison.OrdinalIgnoreCase));
        }

        if (Consts.TryGetValue(name, out var c))
        {
            Next();
            return new Value(c);
        }

        if (name == "i")
        {
            Next();
            return Value.Complex(new ComplexInterval(0, 1));
        }

        if (IsSymbolicIdent(name) || name == "C")
        {
            Next();
            return Value.Sym(Symbolic.Var(name));
        }

        throw new ParseException($"Unknown '{name}'");
    }

    private bool RowHasCommaBeforeDelimiter()
    {
        int depth = 0;
        for (int i = _p; i < _t.Count; i++)
        {
            var tk = _t[i];
            if (tk.Kind == Tk.End) return false;
            if (tk.Kind == Tk.Op)
            {
                if (tk.Text is "(" or "[") { depth++; continue; }
                if (tk.Text is ")" or "]")
                {
                    if (depth == 0) return false;
                    depth--;
                    continue;
                }
                if (depth == 0)
                {
                    if (tk.Text == ";") return false;
                    if (tk.Text == ",") return true;
                }
            }
        }
        return false;
    }

    private List<Token> ReadRawArgTokens()
    {
        var tokens = new List<Token>();
        int depth = 0;
        while (Cur.Kind != Tk.End)
        {
            if (depth == 0 && (IsOp(",") || IsOp(";") || IsOp(")")))
                break;
            if (Cur.Kind == Tk.Op && Cur.Text is "(" or "[") depth++;
            else if (Cur.Kind == Tk.Op && Cur.Text is ")" or "]") depth--;
            tokens.Add(Cur);
            Next();
        }
        tokens.Add(new Token(Tk.End, ""));
        return tokens;
    }

    private CalcDate ReadDateArg()
    {
        if (Cur.Kind == Tk.Str)
        {
            var text = Cur.Text;
            Next();
            if (CalcDate.TryParse(text, out var parsed)) return parsed;
            throw new EvalException($"Invalid date '{text}'");
        }

        if (Cur.Kind == Tk.Ident && IsVariable(Cur.Text) && _sc.Vars.TryGetValue(Cur.Text, out var v) && v.DateValue is { } vd)
        {
            Next();
            return vd;
        }

        var raw = new System.Text.StringBuilder();
        while (Cur.Kind != Tk.End && !IsOp(",") && !IsOp(";") && !IsOp(")"))
        {
            raw.Append(Cur.Text);
            Next();
        }
        if (CalcDate.TryParse(raw.ToString(), out var rawDate)) return rawDate;
        throw new EvalException($"Invalid date '{raw}'");
    }

    private Value LoadCsv(string relativePath)
    {
        if (string.IsNullOrEmpty(_baseDir))
            throw new EvalException("File loading is disabled");

        string baseFull = Path.GetFullPath(_baseDir);
        string basePrefix = baseFull.EndsWith(Path.DirectorySeparatorChar) ? baseFull : baseFull + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(baseFull, relativePath));
        if (!candidate.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            candidate = Path.GetFullPath(Path.Combine(baseFull, Path.GetFileName(relativePath)));

        if (!candidate.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            throw new EvalException($"File not found: '{relativePath}'");

        var items = new List<Value>();
        foreach (var line in File.ReadLines(candidate))
        {
            foreach (var part in line.Split([',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ExactNumber.TryParseDecimal(part, out var exact))
                    items.Add(new Value(exact));
                else if (double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                    items.Add(new Value(d));
            }
        }
        return Value.List(items);
    }

    private bool Advance() { Next(); return true; }

    /// <summary>sum / total / average over the lines above, back to the last blank line or heading.</summary>
    private Value Aggregate(bool averaged)
    {
        var values = _sc.Block.SelectMany(Ops.Flatten).Where(v => !v.IsPercent && v.Text == null).ToList();
        var unit = values.FirstOrDefault(v => v.Unit != null)?.Unit;
        double sum = 0;
        int count = 0;

        foreach (var v in values)
        {
            sum += unit == null ? v.Number : Ops.In(v, unit);
            count++;
        }

        return new Value(averaged ? (count == 0 ? 0 : sum / count) : sum, unit);
    }
}
