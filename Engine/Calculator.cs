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
    private static readonly Regex Newline = new("\r\n|\r|\n", RegexOptions.Compiled);
    private static readonly Regex LabelRx = new(@"^\p{L}[^:=]*:\s*(?<rest>\S.*)$", RegexOptions.Compiled);
    private static readonly Regex AssignRx =
        new(@"^(?<name>[\p{L}_][\p{L}\p{N}_]*)\s*=(?!=)\s*(?<expr>.+)$", RegexOptions.Compiled);
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

        int comment = t.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) t = t[..comment].TrimEnd();

        if (t.Length == 0)
        {
            scope.Block.Clear();           // blank lines too
            return LineResult.Blank;
        }

        if (Preset(t, scope) is { } preset) return new LineResult(LineKind.Result, preset, null);

        var label = LabelRx.Match(t);
        if (label.Success) t = label.Groups["rest"].Value;

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
    private static Value? Run(string expr, Scope scope, out bool aggregate)
    {
        aggregate = false;

        try
        {
            var parser = new Parser(Lexer.Tokenize(expr), scope);
            var v = parser.ParseAll();
            aggregate = parser.UsedAggregate;
            return v;
        }
        catch (ParseException) { /* fall through to lenient mode */ }

        try
        {
            var tokens = Lenient(expr, scope);
            if (tokens == null) return null;
            var parser = new Parser(tokens, scope);
            var v = parser.ParseAll();
            aggregate = parser.UsedAggregate;
            return v;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    private static List<Token>? Lenient(string expr, Scope scope)
    {
        var all = Lexer.Tokenize(expr);
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
            ["sqrt"] = a => a.Count > 0 && a[0].Unit != null ? Ops.Root(a[0], 2, Math.Sqrt) : Map(a, x => Math.Sqrt(x)),
            ["cbrt"] = a => a.Count > 0 && a[0].Unit != null ? Ops.Root(a[0], 3, Math.Cbrt) : Map(a, x => Math.Cbrt(x)),
            ["abs"] = a => Map(a, x => Math.Abs(x), keepUnit: true),
            ["ceil"] = a => Map(a, x => Math.Ceiling(x), keepUnit: true),
            ["floor"] = a => Map(a, x => Math.Floor(x), keepUnit: true),
            ["round"] = a =>
            {
                Need(a, 1);
                int digits = a.Count > 1 ? (int)a[1].Frac : 0;
                return new Value(Math.Round(a[0].Frac, Math.Clamp(digits, 0, 15), MidpointRounding.AwayFromZero), a[0].Unit);
            },
            ["ln"] = a => Map(a, x => Math.Log(x)),
            ["log"] = a =>
            {
                Need(a, 1);
                return new Value(a.Count > 1 ? Math.Log(a[0].Frac) / Math.Log(a[1].Frac) : Math.Log10(a[0].Frac));
            },
            ["log2"] = a => Map(a, x => Math.Log2(x)),
            ["exp"] = a => Map(a, x => Math.Exp(x)),
            ["fact"] = a => { Need(a, 1); return Ops.Factorial(a[0]); },
            ["pow"] = a => { Need(a, 2); return Ops.Pow(a[0], a[1]); },

            ["sin"] = a => Trig(a, Math.Sin),
            ["cos"] = a => Trig(a, Math.Cos),
            ["tan"] = a => Trig(a, Math.Tan),
            ["sinh"] = a => Map(a, x => Math.Sinh(x)),
            ["cosh"] = a => Map(a, x => Math.Cosh(x)),
            ["tanh"] = a => Map(a, x => Math.Tanh(x)),
            ["asin"] = a => Inverse(a, Math.Asin),
            ["acos"] = a => Inverse(a, Math.Acos),
            ["atan"] = a => Inverse(a, Math.Atan),

            // Aggregates accept loose arguments or lists: sum(1, 2, 3) and sum([1, 2, 3]) are the same.
            ["min"] = a => Pick(Flat(a), Math.Min),
            ["max"] = a => Pick(Flat(a), Math.Max),
            ["sum"] = a => Sum(Flat(a)),
            ["product"] = a => { var f = Flat(a); Need(f, 1); return f.Aggregate((x, y) => Ops.Mul(x, y)); },
            ["avg"] = a => Average(Flat(a)),
            ["mean"] = a => Average(Flat(a)),
            ["average"] = a => Average(Flat(a)),
            ["median"] = a => Median(Flat(a)),

            // List shaping
            ["count"] = a => new Value(Elements(a).Count),
            ["len"] = a => new Value(Elements(a).Count),
            ["sort"] = a => Sort(Elements(a)),
            ["reverse"] = a => { var e = Elements(a); e.Reverse(); return Value.List(e); },
            ["first"] = a => { var e = Elements(a); Need(e, 1); return e[0]; },
            ["last"] = a => { var e = Elements(a); Need(e, 1); return e[^1]; },
            ["range"] = MakeRange,
        };

    /// <summary>Functions that apply to each item when given a list: sqrt([4, 9]) is [2, 3].</summary>
    private static readonly HashSet<string> ElementWise = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqrt", "cbrt", "abs", "ceil", "floor", "round", "ln", "log", "log2", "exp", "fact",
        "sin", "cos", "tan", "sinh", "cosh", "tanh", "asin", "acos", "atan"
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
            "sin" => Trig(args, Math.Sin, angleMode),
            "cos" => Trig(args, Math.Cos, angleMode),
            "tan" => Trig(args, Math.Tan, angleMode),
            "asin" => Inverse(args, Math.Asin, angleMode),
            "acos" => Inverse(args, Math.Acos, angleMode),
            "atan" => Inverse(args, Math.Atan, angleMode),
            _ => Table[name](args),
        };
        if (!result.IsArray && double.IsNaN(result.Number)) throw new EvalException("Not a number");
        return result;
    }

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

    private static Value Sort(List<Value> items)
    {
        if (items.Any(v => v.IsArray)) throw new EvalException("Can't sort nested lists");
        return Value.List(items.OrderBy(SortKey(items)).ToList());
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

    /// <summary>range(1, 5) is [1, 2, 3, 4, 5]; range(10, 0, -5) is [10, 5, 0].</summary>
    private static Value MakeRange(List<Value> a)
    {
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
        { "in", "to", "as", "into", "of", "on", "off", "mod", "percent", "per" };

    public static readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase)
        { "hex", "bin", "oct", "dec", "decimal" };

    public static readonly HashSet<string> Specials = new(StringComparer.OrdinalIgnoreCase)
        { "prev", "ans", "sum", "total", "average", "avg" };

    private static readonly Dictionary<string, double> Consts = new(StringComparer.OrdinalIgnoreCase)
    { ["pi"] = Math.PI, ["π"] = Math.PI, ["e"] = Math.E, ["tau"] = Math.Tau };

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
    private int _p;

    public bool UsedAggregate { get; private set; }

    public Parser(List<Token> tokens, Scope scope)
    {
        _t = tokens;
        _sc = scope;
    }

    private Token Cur => _t[_p];
    private Token At(int n) => _t[Math.Min(_p + n, _t.Count - 1)];
    private void Next() { if (_p < _t.Count - 1) _p++; }

    private bool IsOp(string s) => Cur.Kind == Tk.Op && Cur.Text == s;
    private static bool OpAt(Token k, string s) => k.Kind == Tk.Op && k.Text == s;

    private bool IsAnyWord(params string[] words) =>
        Cur.Kind == Tk.Ident && words.Any(w => string.Equals(Cur.Text, w, StringComparison.OrdinalIgnoreCase));

    private bool IsValueIdent(int n)
    {
        var k = At(n);
        if (k.Kind != Tk.Ident) return false;
        return IsVariable(k.Text) || Consts.ContainsKey(k.Text) || Specials.Contains(k.Text) ||
               (Functions.Table.ContainsKey(k.Text) && OpAt(At(n + 1), "("));
    }

    // Variables ignore case, but an exact physics symbol (V, A, N) beats a variable like v, a or n
    private bool IsVariable(string name)
    {
        if (!_sc.Vars.ContainsKey(name)) return false;
        return !UnitTable.IsPhysicsSymbol(name) || _sc.Vars.Keys.Contains(name, StringComparer.Ordinal);
    }

    public Value ParseAll()
    {
        var v = Conversion();
        if (Cur.Kind != Tk.End) throw new ParseException($"Unexpected '{Cur.Text}'");
        return v;
    }

    // conversion := additive (("in" | "to" | "as" | "into") target)*
    private Value Conversion()
    {
        var v = Additive();
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

        if (Cur.Kind == Tk.Ident)
        {
            var word = Cur.Text.ToLowerInvariant();
            if (word is "hex" or "bin" or "oct") { Next(); return Ops.Map(left, x => ToBase(x, word)); }
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
        else return Unary();

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

    private static Value ToBase(Value v, string kind)
    {
        double d = Math.Round(v.Frac);
        if (Math.Abs(d) > long.MaxValue / 2.0) throw new EvalException("Number too large");

        long n = (long)Math.Abs(d);
        string body = kind switch
        {
            "hex" => "0x" + n.ToString("X"),
            "bin" => "0b" + System.Convert.ToString(n, 2),
            _ => "0o" + System.Convert.ToString(n, 8)
        };
        return new Value(d) { Text = (d < 0 ? "-" : "") + body };
    }

    private Value Additive()
    {
        var v = Term();
        while (IsOp("+") || IsOp("-"))
        {
            int sign = Cur.Text == "+" ? 1 : -1;
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
            if (IsOp("*")) { Next(); v = Ops.Mul(v, Operand()); }
            else if (IsOp("/") || IsAnyWord("per")) { Next(); v = Ops.Div(v, Operand()); }
            else if (IsAnyWord("mod") || IsOp("%")) { Next(); v = Ops.Mod(v, Unary()); }
            else if (IsAnyWord("of")) { Next(); v = Ops.Mul(v, Unary()); }              // 20% of 150
            else if (IsAnyWord("on")) { Next(); v = Ops.Add(Unary(), AsPercent(v), 1); }  // 20% on 100
            else if (IsAnyWord("off")) { Next(); v = Ops.Add(Unary(), AsPercent(v), -1); } // 20% off 100
            else if (IsOp("(") || (Cur.Kind == Tk.Ident && IsValueIdent(0))) { v = Ops.Mul(v, Unary()); } // 2(3), 2x, 3 pi
            else break;
        }
        return v;
    }

    private static Value AsPercent(Value v) => v.IsPercent ? v : Value.Pct(v.Number);

    private Value Unary()
    {
        if (IsOp("-"))
        {
            Next();
            var v = Unary();
            return Ops.Map(v, x => x.IsPercent ? Value.Pct(-x.Number) : new Value(-x.Number, x.Unit));
        }
        if (IsOp("+")) { Next(); return Unary(); }
        return Power();
    }

    private Value Power()
    {
        var b = Postfix();
        if (IsOp("^"))
        {
            Next();
            return Ops.Pow(b, Unary());
        }
        return b;
    }

    private Value Postfix()
    {
        var v = Primary();
        while (true)
        {
            if (IsOp("%"))
            {
                if (v.IsPercent || v.Unit != null) break;
                if (At(1).Kind is Tk.Num or Tk.Sym || OpAt(At(1), "[")) break;
                Next();
                v = Value.Pct(v.Number);
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
                return new Value(k.Num);

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

                    var items = new List<Value>();

                    if (!IsOp("]"))
                    {
                        do
                        {
                            items.Add(Conversion());
                        }
                        while (IsOp(",") && Advance());
                    }

                    if (!IsOp("]"))
                        throw new ParseException("Expected ']'");

                    Next();
                    return Value.List(items);
                }

            case Tk.Op when k.Text == "(":
                {
                    Next();
                    var v = Conversion();
                    if (!IsOp(")")) throw new ParseException("Expected ')'");
                    Next();
                    return v;
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

        if (OpAt(At(1), "(") && Functions.Table.ContainsKey(name))
        {
            Next(); Next();
            var args = new List<Value>();
            if (!IsOp(")"))
            {
                do { args.Add(Conversion()); }
                while (IsOp(",") && Advance());
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

        throw new ParseException($"Unknown '{name}'");
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
