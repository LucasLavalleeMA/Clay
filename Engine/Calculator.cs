using System.Text.RegularExpressions;
using System.Xml.Linq;

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

    private static readonly Regex Newline = new("\r\n|\r|\n", RegexOptions.Compiled);
    private static readonly Regex LabelRx = new(@"^\p{L}[^:=]*:\s*(?<rest>\S.*)$", RegexOptions.Compiled);
    private static readonly Regex AssignRx =
        new(@"^(?<name>[\p{L}_][\p{L}\p{N}_]*)\s*=(?!=)\s*(?<expr>.+)$", RegexOptions.Compiled);
	private static Match MatchAssign(string t)
	{
		var m = AssignRx.Match(t);
		if (m.Success) return m;

		// Shortcuts that expand to "=" (e.g. "is") also count as assignment
		var words = Lexer.Shortcuts
			.Where(kv => kv.Value.Trim() == "=")
			.Select(kv => Regex.Escape(kv.Key))
			.ToList();
		if (words.Count == 0) return m;

		var rx = new Regex(
			$@"^(?<name>[\p{{L}}_][\p{{L}}\p{{N}}_]*)\s+(?:{string.Join("|", words)})\s+(?<expr>.+)$",
			RegexOptions.IgnoreCase);

		var alt = rx.Match(t);
		// "what is 3" must stay an expression, not become what = 3
		if (alt.Success && !Lexer.Shortcuts.ContainsKey(alt.Groups["name"].Value))
			return alt;

		return m;
	}

	public List<LineResult> Evaluate(string text)
    {
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

		// Pull from last expression if need be
		if (scope.Prev != null &&
	        t.Length > 0 &&
	        t[0] is '*' or '/' or '^')
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

    /// <summary>Strict parse first; if that fails, retry while ignoring unknown words ("what is 20% of 150").</summary>
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

			// If an unknown value is here, like an undefined x, skip it
			//if (k.Kind == Tk.Ident && !Known(k.Text, scope, call)) continue;

			// If an unknown value is here, like an undefined x, make it NaN
			//if (k.Kind == Tk.Ident && !Known(k.Text, scope, call)) k = new Token(Tk.Num, "NaN", double.NaN);

            // If an unknown variable, just throw
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
        scope.Vars.ContainsKey(w) || UnitTable.Lookup(w) != null;

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
            ["sqrt"] = a => Map(a, x => Math.Sqrt(x)),
            ["cbrt"] = a => Map(a, x => Math.Cbrt(x)),
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
            ["sum"] = a => { var f = Flat(a); return f.Count == 0 ? new Value(0) : f.Aggregate((x, y) => Ops.Add(x, y, 1)); },
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

        var result = name switch
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
        a.Count == 1 && a[0].Items != null ? a[0].Items.ToList() : a.ToList();

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
        v.Unit is { Dimension: Dim.Angle } u
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
        var sum = a.Aggregate((x, y) => Ops.Add(x, y, 1));
        return Ops.Div(sum, new Value(a.Count));
    }
}

internal sealed class Parser
{
    public static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        { "in", "to", "as", "into", "of", "on", "off", "mod", "percent" };

    public static readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase)
        { "hex", "bin", "oct", "dec", "decimal" };

    public static readonly HashSet<string> Specials = new(StringComparer.OrdinalIgnoreCase)
        { "prev", "ans", "sum", "total", "average", "avg" };

    private static readonly Dictionary<string, double> Consts = new(StringComparer.OrdinalIgnoreCase)
        { ["pi"] = Math.PI, ["π"] = Math.PI, ["e"] = Math.E, ["tau"] = Math.Tau };

    private static readonly Dictionary<string, double> ScaleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["thousand"] = 1e3, ["million"] = 1e6, ["billion"] = 1e9, ["bn"] = 1e9, ["trillion"] = 1e12
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
        return _sc.Vars.ContainsKey(k.Text) || Consts.ContainsKey(k.Text) || Specials.Contains(k.Text) ||
               (Functions.Table.ContainsKey(k.Text) && OpAt(At(n + 1), "("));
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
            if (word == "in") { Next(); return Ops.ConvertTo(left, UnitTable.Inch); }
        }

        if (Cur.Kind is Tk.Ident or Tk.Sym)
        {
            var unit = UnitTable.Lookup(Cur.Text);
            if (unit != null)
            {
                Next();
                return Ops.ConvertTo(left, unit);
            }
        }

        throw new ParseException("Unknown unit");
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
            if (IsOp("*")) { Next(); v = Ops.Mul(v, Unary()); }
            else if (IsOp("/")) { Next(); v = Ops.Div(v, Unary()); }
            else if (IsAnyWord("mod")) { Next(); v = Ops.Mod(v, Unary()); }
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

                var scale = Scale(Cur.Text);
                if (scale.HasValue)
                {
                    Next();
                    v = new Value(v.Number * scale.Value, v.Unit);
                    continue;
                }

                // "12 in" means inches unless it reads as a conversion ("12 in cm")
                if (IsAnyWord("in") && v.Unit == null && (At(1).Kind == Tk.End || (At(1).Kind == Tk.Op && At(1).Text != "(")))
                {
                    Next();
                    v = new Value(v.Number, UnitTable.Inch);
                    continue;
                }

                var unit = Keywords.Contains(Cur.Text) || OpAt(At(1), "(") ? null : UnitTable.Lookup(Cur.Text);
                if (unit != null && v.Unit == null)
                {
                    Next();
                    v = new Value(v.Number, unit);
                    continue;
                }
                break;
            }
            else if (Cur.Kind == Tk.Sym && v.Unit == null && !v.IsPercent)
            {
                var unit = UnitTable.Lookup(Cur.Text);
                if (unit == null) break;
                Next();
                v = new Value(v.Number, unit);
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

        if (_sc.Vars.TryGetValue(name, out var variable))
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
        var unit = _sc.Block.FirstOrDefault(v => v.Unit != null)?.Unit;
        double sum = 0;
        int count = 0;

        foreach (var v in _sc.Block)
        {
            if (v.IsPercent || v.Text != null) continue;
            if (v.Unit == null || unit == null) { sum += v.Number; count++; }
            else if (v.Unit.Dimension == unit.Dimension) { sum += Unit.Convert(v.Number, v.Unit, unit); count++; }
        }

        return new Value(averaged ? (count == 0 ? 0 : sum / count) : sum, unit);
    }
}
