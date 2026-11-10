using System.Globalization;
using System.Collections.ObjectModel;

namespace Clay.Engine;

public sealed class ParseException(string message) : Exception(message);
public sealed class EvalException(string message) : Exception(message);

public enum Tk { Num, Ident, Sym, Op, End }

public readonly record struct Token(Tk Kind, string Text, double Num = 0);

public static class Lexer
{
    private const string CurrencySymbols = "$€£¥₹₩";
    private const string Operators = "+-*/^(),%=![]";

    public static IReadOnlyDictionary<string, string> Shortcuts { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "what", "" },
            { "is", "=" }
        });

    public static List<Token> Tokenize(string source)
    {
        var s = source.Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace('–', '-');
        var list = new List<Token>();
        int i = 0;

        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (IsDigit(c) || (c == '.' && i + 1 < s.Length && IsDigit(s[i + 1])))
            {
                list.Add(ReadNumber(s, ref i));
            }
            else if (IsIdentStart(c))
            {
                // After a divide, m/m/s reads as (m/m)/s so the unit isn't joined to the divisor
                bool afterDivide = list.Count > 0 && (list[^1] is { Kind: Tk.Op, Text: "/" } ||
                    (list[^1].Kind == Tk.Ident && list[^1].Text.Equals("per", StringComparison.OrdinalIgnoreCase)));
                list.Add(ReadIdent(s, ref i, afterDivide));
            }
            else if (CurrencySymbols.IndexOf(c) >= 0)
            {
                list.Add(new Token(Tk.Sym, c.ToString()));
                i++;
            }
            else if (Operators.IndexOf(c) >= 0)
            {
                list.Add(new Token(Tk.Op, c.ToString()));
                i++;
            }
            else if (c == '·')
            {
                list.Add(new Token(Tk.Op, "*"));
                i++;
            }
            else
            {
                throw new ParseException($"Unexpected '{c}'");
            }
        }

        // Second pass: expand shortcuts
        var expanded = new List<Token>();

        foreach (var token in list)
        {
            if (token.Kind == Tk.Ident &&
                Shortcuts.TryGetValue(token.Text, out var replacement))
            {
                if (string.IsNullOrWhiteSpace(replacement))
                    continue; // Empty shortcut removes the token

                var replacementTokens = Tokenize(replacement);

                foreach (var t in replacementTokens)
                {
                    if (t.Kind != Tk.End)
                        expanded.Add(t);
                }
            }
            else
            {
                expanded.Add(token);
            }
        }

        expanded.Add(new Token(Tk.End, ""));
        return expanded;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '°';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '°' || Superscripts.IndexOf(c) >= 0;

    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹⁻";



    private static Token ReadNumber(string s, ref int i)
    {
        int start = i;

        // 0x1F, 0b1010, and 0o17 literals
        if (s[i] == '0' && i + 2 < s.Length)
        {
            char p = char.ToLowerInvariant(s[i + 1]);
            if (p == 'x' && Uri.IsHexDigit(s[i + 2]))
                return ReadBase(s, ref i, start, 16, Uri.IsHexDigit);
            if (p == 'b' && (s[i + 2] == '0' || s[i + 2] == '1'))
                return ReadBase(s, ref i, start, 2, ch => ch == '0' || ch == '1');
            if (p == 'o' && s[i + 2] is >= '0' and <= '7')
                return ReadBase(s, ref i, start, 8, ch => ch is >= '0' and <= '7');
        }

        while (i < s.Length && (IsDigit(s[i]) || s[i] == '_')) i++;
        if (i + 1 < s.Length && s[i] == '.' && IsDigit(s[i + 1]))
        {
            i++;
            while (i < s.Length && IsDigit(s[i])) i++;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
            if (j < s.Length && IsDigit(s[j]))
            {
                i = j;
                while (i < s.Length && IsDigit(s[i])) i++;
            }
        }

        var text = s[start..i];
        double.TryParse(text.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
        return new Token(Tk.Num, text, value);
    }

    private static Token ReadBase(string s, ref int i, int start, int radix, Func<char, bool> isDigit)
    {
        i += 2;
        double value = 0;
        while (i < s.Length && isDigit(s[i]))
        {
            value = value * radix + Uri.FromHex(s[i]);
            i++;
        }
        return new Token(Tk.Num, s[start..i], value);
    }

    private static Token ReadIdent(string s, ref int i, bool afterDivide)
    {
        int start = i++;
        while (i < s.Length && IsIdentPart(s[i])) i++;
        var word = s[start..i];
        if (afterDivide) return new Token(Tk.Ident, word);

        // Unit expressions such as km/h, m/s², kg·m/s², and m/s^2 (a power binds to the unit before it)
        int end = i;
        var expr = WithPowers(word, s, ref end);
        if (expr == null) return new Token(Tk.Ident, word);

        // A scale word such as k or M (5k/s) only joins a registered name
        bool scaleWord = Parser.Scale(word) != null;

        while (end + 1 < s.Length && (s[end] == '/' || s[end] == '·') && IsIdentStart(s[end + 1]))
        {
            int j = end + 1;
            while (j < s.Length && IsIdentPart(s[j])) j++;

            // A function call such as min(2, 4) isn't a unit
            if (Functions.Table.ContainsKey(s[(end + 1)..j]) && NextNonSpace(s, j) == '(') break;

            var factor = WithPowers(s[(end + 1)..j], s, ref j);
            if (factor == null || !UnitTable.IsFactor(factor) || NumericPowerFollows(s, j)) break;

            expr += s[end] + factor;
            end = j;

            // Keep scanning past a prefix that cancels (m/m/s) and settle on the last valid unit
            if (scaleWord ? UnitTable.Lookup(expr) != null : UnitTable.Parse(expr) != null)
            {
                word = expr;
                i = end;
            }
        }

        return new Token(Tk.Ident, word);
    }

    private static char NextNonSpace(string s, int i)
    {
        i = SkipSpaces(s, i);
        return i < s.Length ? s[i] : '\0';
    }

    /// <summary>
    /// Applies every caret power at i to the factor, so s^2^2 and s²^2 are both s⁴. Null when a power
    /// can't apply, such as to m⁻ or past the largest unit power.
    /// </summary>
    private static string? WithPowers(string factor, string s, ref int i)
    {
        string? result = factor;
        while (result != null && ReadPower(s, ref i) is { } power) result = WithPower(result, power);
        return result;
    }

    /// <summary>Applies a caret power, multiplying any exponent the factor already has (s²^2 is s⁴).</summary>
    private static string? WithPower(string factor, int power)
    {
        int split = factor.Length;
        while (split > 0 && Superscripts.IndexOf(factor[split - 1]) >= 0) split--;

        long exponent = power;
        if (split < factor.Length)
        {
            if (!int.TryParse(FromSuperscripts(factor[split..]), out int existing)) return null;
            exponent *= existing;
        }

        return Math.Abs(exponent) > UnitTable.MaxPower ? null : factor[..split] + ToSuperscripts((int)exponent);
    }

    private static string FromSuperscripts(string s) =>
        string.Concat(s.Select(c => c == '⁻' ? '-' : (char)('0' + Superscripts.IndexOf(c))));

    private static string ToSuperscripts(int n) =>
        string.Concat(n.ToString(CultureInfo.InvariantCulture).Select(c => c == '-' ? '⁻' : Superscripts[c - '0']));

    /// <summary>
    /// Reads "^2", "^-1" or "^ 2" at i; null, leaving i alone, for anything but a whole number up to the
    /// largest unit power. Spaces don't matter, so m/s ^ 2 is m/s² like m/s^2.
    /// </summary>
    private static int? ReadPower(string s, ref int i)
    {
        int j = SkipSpaces(s, i);
        if (j >= s.Length || s[j] != '^') return null;
        j = SkipSpaces(s, j + 1);
        bool negative = j < s.Length && s[j] == '-';
        if (negative || (j < s.Length && s[j] == '+')) j = SkipSpaces(s, j + 1);
        if (j >= s.Length || !IsDigit(s[j])) return null;

        var number = ReadNumber(s, ref j);
        if (number.Num != Math.Floor(number.Num) || number.Num > UnitTable.MaxPower) return null;

        i = j;
        return (int)(negative ? -number.Num : number.Num);
    }

    /// <summary>Whether a numeric caret power (such as the fractional ^2.5) is next at i.</summary>
    private static bool NumericPowerFollows(string s, int i)
    {
        int j = SkipSpaces(s, i);
        if (j >= s.Length || s[j] != '^') return false;
        j = SkipSpaces(s, j + 1);
        if (j < s.Length && (s[j] == '-' || s[j] == '+')) j = SkipSpaces(s, j + 1);
        return j < s.Length && (IsDigit(s[j]) || s[j] == '.');
    }

    private static int SkipSpaces(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }
}
