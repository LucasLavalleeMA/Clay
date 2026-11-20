using System.Globalization;
using System.Numerics;
using System.Text;

namespace Clay.Engine;

/// <summary>
/// Symbolic expression AST, algebraic simplifier, multivariate polynomial engine,
/// and exact polynomial equation solver built on ExactNumber.
/// </summary>
public abstract record SymExpr
{
    public sealed record Num(ExactNumber Value) : SymExpr;
    public sealed record Var(string Name) : SymExpr;
    public sealed record Add(IReadOnlyList<SymExpr> Terms) : SymExpr;
    public sealed record Mul(IReadOnlyList<SymExpr> Factors) : SymExpr;
    public sealed record Pow(SymExpr Base, SymExpr Exp) : SymExpr;
    public sealed record Func(string Name, IReadOnlyList<SymExpr> Args) : SymExpr;
    public sealed record Solutions(string Variable, IReadOnlyList<SymExpr> Roots) : SymExpr;
}

public static class Symbolic
{
    public static readonly SymExpr Zero = new SymExpr.Num(ExactNumber.Zero);
    public static readonly SymExpr One = new SymExpr.Num(ExactNumber.One);
    public static readonly SymExpr MinusOne = new SymExpr.Num(ExactNumber.FromLong(-1));

    // --------------------------------------------------------- construction

    public static SymExpr FromExact(ExactNumber n) => new SymExpr.Num(n);
    public static SymExpr FromLong(long n) => new SymExpr.Num(ExactNumber.FromLong(n));
    public static SymExpr Var(string name) => new SymExpr.Var(name);

    public static SymExpr Negate(SymExpr e, bool assumePositive = false) =>
        Simplify(new SymExpr.Mul([MinusOne, e]), assumePositive);

    public static SymExpr Add(SymExpr a, SymExpr b, int sign = 1, bool assumePositive = false) =>
        Simplify(new SymExpr.Add([a, sign < 0 ? new SymExpr.Mul([MinusOne, b]) : b]), assumePositive);

    public static SymExpr Mul(SymExpr a, SymExpr b, bool assumePositive = false) =>
        Simplify(new SymExpr.Mul([a, b]), assumePositive);

    public static SymExpr Div(SymExpr a, SymExpr b, bool assumePositive = false)
    {
        if (b is SymExpr.Num bn && bn.Value.IsZero)
            throw new EvalException("Division by zero");
        return Simplify(new SymExpr.Mul([a, new SymExpr.Pow(b, MinusOne)]), assumePositive);
    }

    public static SymExpr Pow(SymExpr a, SymExpr b, bool assumePositive = false) =>
        Simplify(new SymExpr.Pow(a, b), assumePositive);

    public static SymExpr Call(string name, IReadOnlyList<SymExpr> args, bool assumePositive = false) =>
        Simplify(new SymExpr.Func(name.ToLowerInvariant(), args), assumePositive);

    // --------------------------------------------------------- simplification

    public static SymExpr Simplify(SymExpr expr, bool assumePositive = false)
    {
        return expr switch
        {
            SymExpr.Num => expr,
            SymExpr.Var => expr,
            SymExpr.Solutions => expr,
            SymExpr.Add add => SimplifyAdd(add.Terms, assumePositive),
            SymExpr.Mul mul => SimplifyMul(mul.Factors, assumePositive),
            SymExpr.Pow pow => SimplifyPow(pow.Base, pow.Exp, assumePositive),
            SymExpr.Func fn => SimplifyFunc(fn.Name, fn.Args, assumePositive),
            _ => expr
        };
    }

    private static SymExpr SimplifyAdd(IReadOnlyList<SymExpr> rawTerms, bool assumePositive)
    {
        var flat = new List<SymExpr>();
        foreach (var t in rawTerms)
        {
            var s = Simplify(t, assumePositive);
            if (s is SymExpr.Add inner) flat.AddRange(inner.Terms);
            else flat.Add(s);
        }

        var combined = CombineLikeTerms(flat);
        if (combined.Count == 0) return Zero;
        if (combined.Count == 1) return combined[0];

        // If unexpanded products exist inside a sum (e.g. x*(2x^2+5-5x) + 21), try expanding if it simplifies.
        var expanded = ExpandTerms(combined, assumePositive);
        var expCombined = CombineLikeTerms(expanded);
        if (expCombined.Count == 0) return Zero;
        if (expCombined.Count == 1) return expCombined[0];
        if (expCombined.Count < combined.Count)
            return new SymExpr.Add(expCombined);

        return new SymExpr.Add(combined);
    }

    private static List<SymExpr> ExpandTerms(List<SymExpr> terms, bool assumePositive)
    {
        var result = new List<SymExpr>();
        foreach (var t in terms)
        {
            var e = Expand(t, assumePositive);
            if (e is SymExpr.Add add) result.AddRange(add.Terms);
            else result.Add(e);
        }
        return result;
    }

    private static List<SymExpr> CombineLikeTerms(List<SymExpr> terms)
    {
        var groups = new List<(string Key, SymExpr? Core, ExactNumber Coeff)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var t in terms)
        {
            var (coeff, core) = SplitCoeff(t);
            if (coeff.IsZero) continue;
            string key = core == null ? "" : CanonicalKey(core);
            if (index.TryGetValue(key, out int idx))
            {
                var g = groups[idx];
                groups[idx] = (g.Key, g.Core, g.Coeff + coeff);
            }
            else
            {
                index[key] = groups.Count;
                groups.Add((key, core, coeff));
            }
        }

        var result = new List<SymExpr>();
        foreach (var (_, core, coeff) in groups)
        {
            if (coeff.IsZero) continue;
            if (core == null)
            {
                result.Add(new SymExpr.Num(coeff));
            }
            else if (coeff == ExactNumber.One)
            {
                result.Add(core);
            }
            else if (core is SymExpr.Mul m)
            {
                var factors = new List<SymExpr>(m.Factors.Count + 1) { new SymExpr.Num(coeff) };
                factors.AddRange(m.Factors);
                result.Add(new SymExpr.Mul(factors));
            }
            else
            {
                result.Add(new SymExpr.Mul([new SymExpr.Num(coeff), core]));
            }
        }
        return result;
    }

    private static (ExactNumber Coeff, SymExpr? Core) SplitCoeff(SymExpr expr)
    {
        if (expr is SymExpr.Num n) return (n.Value, null);
        if (expr is SymExpr.Mul m && m.Factors.Count > 0 && m.Factors[0] is SymExpr.Num lead)
        {
            if (m.Factors.Count == 1) return (lead.Value, null);
            if (m.Factors.Count == 2) return (lead.Value, m.Factors[1]);
            return (lead.Value, new SymExpr.Mul(m.Factors.Skip(1).ToList()));
        }
        return (ExactNumber.One, expr);
    }

    private static SymExpr SimplifyMul(IReadOnlyList<SymExpr> rawFactors, bool assumePositive)
    {
        var flat = new List<SymExpr>();
        foreach (var f in rawFactors)
        {
            var s = Simplify(f, assumePositive);
            if (s is SymExpr.Mul inner) flat.AddRange(inner.Factors);
            else flat.Add(s);
        }

        var numCoeff = ExactNumber.One;
        var baseExps = new List<(SymExpr Base, SymExpr Exp)>();

        foreach (var f in flat)
        {
            if (f is SymExpr.Num n)
            {
                if (n.Value.IsZero) return Zero;
                numCoeff *= n.Value;
                continue;
            }

            SymExpr b;
            SymExpr e;
            if (f is SymExpr.Pow p)
            {
                b = p.Base;
                e = p.Exp;
            }
            else
            {
                b = f;
                e = One;
            }

            // Combine matching bases
            string bKey = CanonicalKey(b);
            int existing = baseExps.FindIndex(pair => CanonicalKey(pair.Base) == bKey);
            if (existing >= 0)
            {
                var newExp = Simplify(new SymExpr.Add([baseExps[existing].Exp, e]), assumePositive);
                baseExps[existing] = (baseExps[existing].Base, newExp);
            }
            else
            {
                baseExps.Add((b, e));
            }
        }

        // Simplify u / sqrt(u) -> sqrt(u)
        for (int i = 0; i < baseExps.Count; i++)
        {
            if (baseExps[i].Base is SymExpr.Func { Name: "sqrt", Args.Count: 1 } sqFn &&
                baseExps[i].Exp is SymExpr.Num sqE && sqE.Value == ExactNumber.FromLong(-1))
            {
                string innerKey = CanonicalKey(sqFn.Args[0]);
                int matchIdx = baseExps.FindIndex(pair => CanonicalKey(pair.Base) == innerKey);
                if (matchIdx >= 0 && baseExps[matchIdx].Exp is SymExpr.Num uE && uE.Value == ExactNumber.One)
                {
                    baseExps[i] = (sqFn, One);
                    baseExps.RemoveAt(matchIdx);
                    break;
                }
            }
        }

        // Simplify u / abs(u) -> sgn(u) and abs(u) / u -> sgn(u)
        for (int i = 0; i < baseExps.Count; i++)
        {
            if (baseExps[i].Base is SymExpr.Func { Name: "abs", Args.Count: 1 } absFn)
            {
                string innerKey = CanonicalKey(absFn.Args[0]);
                int matchIdx = baseExps.FindIndex(pair => CanonicalKey(pair.Base) == innerKey);
                if (matchIdx >= 0 &&
                    ((baseExps[i].Exp is SymExpr.Num e1 && e1.Value == ExactNumber.FromLong(-1) &&
                      baseExps[matchIdx].Exp is SymExpr.Num e2 && e2.Value == ExactNumber.One) ||
                     (baseExps[i].Exp is SymExpr.Num e3 && e3.Value == ExactNumber.One &&
                      baseExps[matchIdx].Exp is SymExpr.Num e4 && e4.Value == ExactNumber.FromLong(-1))))
                {
                    baseExps[matchIdx] = (new SymExpr.Func("sgn", [absFn.Args[0]]), One);
                    baseExps.RemoveAt(i);
                    break;
                }
            }
        }

        // Combine sqrt(a) * sqrt(b) -> sqrt(a*b) when assumePositive (and numeric sqrt(a)*sqrt(a) -> a)
        var rebuilt = new List<SymExpr>();
        var sqrtArgs = new List<SymExpr>();
        foreach (var (b, e) in baseExps)
        {
            if (e is SymExpr.Num en && en.Value.IsZero) continue;
            if (b is SymExpr.Func { Name: "sqrt", Args.Count: 1 } sq && e is SymExpr.Num sqExp)
            {
                if (sqExp.Value == ExactNumber.FromLong(2))
                {
                    rebuilt.Add(sq.Args[0]);
                    continue;
                }
                if (sqExp.Value == ExactNumber.One)
                {
                    sqrtArgs.Add(sq.Args[0]);
                    continue;
                }
            }
            rebuilt.Add(e is SymExpr.Num e1 && e1.Value == ExactNumber.One ? b : SimplifyPow(b, e, assumePositive));
        }

        if (sqrtArgs.Count == 1)
        {
            rebuilt.Add(new SymExpr.Func("sqrt", [sqrtArgs[0]]));
        }
        else if (sqrtArgs.Count > 1)
        {
            if (assumePositive || sqrtArgs.All(IsNonNegativeConstant))
            {
                var prod = SimplifyMul(sqrtArgs, assumePositive);
                rebuilt.Add(SimplifyFunc("sqrt", [prod], assumePositive));
            }
            else
            {
                foreach (var sa in sqrtArgs)
                    rebuilt.Add(new SymExpr.Func("sqrt", [sa]));
            }
        }

        // Sort non-numeric factors canonically so x*y and y*x match
        rebuilt.Sort((a, b) => string.CompareOrdinal(CanonicalKey(a), CanonicalKey(b)));

        if (rebuilt.Count == 0) return new SymExpr.Num(numCoeff);

        // Distribute simple numeric negation/scaling over Add? Keep -1 * (a + b) distributed for cleaner canonicalization
        if (rebuilt.Count == 1 && rebuilt[0] is SymExpr.Add addOnly && numCoeff == ExactNumber.FromLong(-1))
        {
            return SimplifyAdd(addOnly.Terms.Select(t => (SymExpr)new SymExpr.Mul([MinusOne, t])).ToList(), assumePositive);
        }

        if (numCoeff == ExactNumber.One)
            return rebuilt.Count == 1 ? rebuilt[0] : new SymExpr.Mul(rebuilt);

        var finalFactors = new List<SymExpr>(rebuilt.Count + 1) { new SymExpr.Num(numCoeff) };
        finalFactors.AddRange(rebuilt);
        return new SymExpr.Mul(finalFactors);
    }

    private static SymExpr SimplifyPow(SymExpr rawBase, SymExpr rawExp, bool assumePositive)
    {
        var b = Simplify(rawBase, assumePositive);
        var e = Simplify(rawExp, assumePositive);

        if (b is SymExpr.Num b1 && b1.Value == ExactNumber.One) return One;

        if (e is SymExpr.Num en)
        {
            if (en.Value.IsZero) return One;
            if (en.Value == ExactNumber.One) return b;
            if (b is SymExpr.Num bn)
            {
                if (en.Value.TryGetBigInteger(out var bi) && bi >= -1000 && bi <= 1000)
                {
                    if (bn.Value.IsZero && bi < 0) throw new EvalException("Division by zero");
                    return new SymExpr.Num(ExactNumber.Pow(bn.Value, ExactNumber.FromBigInteger(bi)));
                }
                if (en.Value == ExactNumber.FromLong(1) / ExactNumber.FromLong(2))
                    return SimplifyFunc("sqrt", [b], assumePositive);
            }
            if (b is SymExpr.Mul bm && en.Value.TryGetBigInteger(out var mExp) && mExp >= -100 && mExp <= 100)
            {
                return SimplifyMul(bm.Factors.Select(f => SimplifyPow(f, e, assumePositive)).ToList(), assumePositive);
            }
            if (b is SymExpr.Pow bp && en.Value.TryGetBigInteger(out _))
            {
                var combinedExp = Simplify(new SymExpr.Mul([bp.Exp, e]), assumePositive);
                return SimplifyPow(bp.Base, combinedExp, assumePositive);
            }
            if (b is SymExpr.Func { Name: "sqrt", Args.Count: 1 } sq &&
                en.Value.TryGetBigInteger(out var sqPow) && sqPow % 2 == 0)
            {
                return SimplifyPow(sq.Args[0], FromLong((long)(sqPow / 2)), assumePositive);
            }
        }

        // b^(log(y, b)) -> y
        if (e is SymExpr.Func { Name: "log", Args.Count: 2 } logFn &&
            CanonicalKey(logFn.Args[1]) == CanonicalKey(b))
        {
            return logFn.Args[0];
        }

        return new SymExpr.Pow(b, e);
    }

    private static SymExpr SimplifyFunc(string name, IReadOnlyList<SymExpr> rawArgs, bool assumePositive)
    {
        var args = rawArgs.Select(a => Simplify(a, assumePositive)).ToList();

        switch (name)
        {
            case "abs" when args.Count == 1:
                return SimplifyAbs(args[0], assumePositive);

            case "sqrt" when args.Count == 1:
                return SimplifySqrt(args[0], assumePositive);

            case "ln" when args.Count == 1:
                return SimplifyLn(args[0], assumePositive);

            case "log" when args.Count == 2:
                if (CanonicalKey(args[0]) == CanonicalKey(args[1])) return One;
                if (args[0] is SymExpr.Num n0 && n0.Value == ExactNumber.One) return Zero;
                return new SymExpr.Func("log", args);

            case "sgn" when args.Count == 1:
                if (args[0] is SymExpr.Num sn)
                    return FromLong(sn.Value.Sign);
                if (IsProvablyPositive(args[0], assumePositive)) return One;
                return new SymExpr.Func("sgn", args);

            default:
                return new SymExpr.Func(name, args);
        }
    }

    private static SymExpr SimplifyAbs(SymExpr arg, bool assumePositive)
    {
        if (arg is SymExpr.Num n)
            return new SymExpr.Num(n.Value.Sign < 0 ? -n.Value : n.Value);

        if (IsProvablyNonNegative(arg, assumePositive))
            return arg;

        // abs(u - abs(u)) = abs(u) - u because u <= abs(u) for all real u
        if (arg is SymExpr.Add add && add.Terms.Count == 2)
        {
            var t0 = add.Terms[0];
            var t1 = add.Terms[1];
            var (c0, core0) = SplitCoeff(t0);
            var (c1, core1) = SplitCoeff(t1);
            if (c0 == ExactNumber.One && c1 == ExactNumber.FromLong(-1) &&
                core1 is SymExpr.Func { Name: "abs", Args.Count: 1 } a1 &&
                core0 != null && CanonicalKey(a1.Args[0]) == CanonicalKey(core0))
            {
                return Simplify(new SymExpr.Add([a1, new SymExpr.Mul([MinusOne, core0])]), assumePositive);
            }
            if (c1 == ExactNumber.One && c0 == ExactNumber.FromLong(-1) &&
                core0 is SymExpr.Func { Name: "abs", Args.Count: 1 } a0 &&
                core1 != null && CanonicalKey(a0.Args[0]) == CanonicalKey(core1))
            {
                return Simplify(new SymExpr.Add([a0, new SymExpr.Mul([MinusOne, core1])]), assumePositive);
            }
        }

        // Canonicalize sign inside abs(u): abs(-u) = abs(u)
        if (arg is SymExpr.Mul m && m.Factors.Count > 0 && m.Factors[0] is SymExpr.Num lead && lead.Value.Sign < 0)
            return SimplifyAbs(Negate(arg, assumePositive), assumePositive);

        if (arg is SymExpr.Add sum && sum.Terms.Count > 0)
        {
            // Order terms deterministically by core key; if the first term has a negative coefficient, negate the sum.
            var ordered = sum.Terms
                .Select(t => (Term: t, Parts: SplitCoeff(t)))
                .OrderBy(x => x.Parts.Core == null ? "\uFFFF" : CanonicalKey(x.Parts.Core), StringComparer.Ordinal)
                .ToList();
            if (ordered[0].Parts.Coeff.Sign < 0)
            {
                var negated = ordered.Select(x => Negate(x.Term, assumePositive)).ToList();
                return new SymExpr.Func("abs", [SimplifyAdd(negated, assumePositive)]);
            }
            return new SymExpr.Func("abs", [new SymExpr.Add(ordered.Select(x => x.Term).ToList())]);
        }

        return new SymExpr.Func("abs", [arg]);
    }

    private static SymExpr SimplifySqrt(SymExpr arg, bool assumePositive)
    {
        if (arg is SymExpr.Num n)
        {
            if (n.Value.IsZero) return Zero;
            if (n.Value == ExactNumber.One) return One;
            if (n.Value.Sign > 0 && TryExactRationalSqrt(n.Value, out var r))
                return new SymExpr.Num(r);
            return new SymExpr.Func("sqrt", [arg]);
        }

        // Nested radical denesting: sqrt(a ± b*sqrt(c))
        if (TryDenestSqrt(arg, out var denested))
            return denested;

        // Perfect square factoring under sqrt: sqrt((u + v)^2)
        var factored = Factor(arg, assumePositive);
        if (factored is SymExpr.Pow { Exp: SymExpr.Num exp } p && exp.Value == ExactNumber.FromLong(2))
        {
            return IsProvablyNonNegative(p.Base, assumePositive)
                ? p.Base
                : SimplifyAbs(p.Base, assumePositive);
        }

        if (arg is SymExpr.Pow { Exp: SymExpr.Num pExp } ap && pExp.Value == ExactNumber.FromLong(2))
        {
            return IsProvablyNonNegative(ap.Base, assumePositive)
                ? ap.Base
                : SimplifyAbs(ap.Base, assumePositive);
        }

        return new SymExpr.Func("sqrt", [arg]);
    }

    private static bool TryDenestSqrt(SymExpr arg, out SymExpr result)
    {
        result = Zero;
        if (arg is not SymExpr.Add { Terms.Count: 2 } add) return false;

        ExactNumber a;
        SymExpr radTerm;
        if (add.Terms[0] is SymExpr.Num n0) { a = n0.Value; radTerm = add.Terms[1]; }
        else if (add.Terms[1] is SymExpr.Num n1) { a = n1.Value; radTerm = add.Terms[0]; }
        else return false;

        if (a.Sign <= 0) return false;

        var (b, core) = SplitCoeff(radTerm);
        if (core is not SymExpr.Func { Name: "sqrt", Args.Count: 1 } sq || sq.Args[0] is not SymExpr.Num cn)
            return false;

        var c = cn.Value;
        if (c.Sign <= 0) return false;

        // Check if a^2 - b^2 * c = d^2 is a rational perfect square
        var disc = a * a - b * b * c;
        if (disc.Sign < 0 || !TryExactRationalSqrt(disc, out var d))
            return false;

        var two = ExactNumber.FromLong(2);
        var u = (a + d) / two;
        var v = (a - d) / two;
        if (u.Sign < 0 || v.Sign < 0) return false;

        var su = SimplifySqrt(new SymExpr.Num(u), true);
        var sv = SimplifySqrt(new SymExpr.Num(v), true);
        result = b.Sign >= 0
            ? SimplifyAdd([su, sv], true)
            : SimplifyAdd([su, new SymExpr.Mul([MinusOne, sv])], true);
        return true;
    }

    private static SymExpr SimplifyLn(SymExpr arg, bool assumePositive)
    {
        if (arg is SymExpr.Num n && n.Value == ExactNumber.One) return Zero;

        if (arg is SymExpr.Pow { Exp: SymExpr.Num pExp } p &&
            IsProvablyPositive(p.Base, assumePositive))
        {
            return SimplifyMul([new SymExpr.Num(pExp.Value), SimplifyLn(p.Base, assumePositive)], assumePositive);
        }

        // Factor argument to catch ln(x^2 + 2x + 1) -> ln((x+1)^2) -> 2*ln(x+1)
        var factored = Factor(arg, assumePositive);
        if (factored is SymExpr.Pow { Exp: SymExpr.Num fExp } fp &&
            IsProvablyPositive(fp.Base, assumePositive))
        {
            return SimplifyMul([new SymExpr.Num(fExp.Value), SimplifyLn(fp.Base, assumePositive)], assumePositive);
        }

        return new SymExpr.Func("ln", [arg]);
    }

    // --------------------------------------------------------- expand & factor

    public static SymExpr Expand(SymExpr expr, bool assumePositive = false)
    {
        switch (expr)
        {
            case SymExpr.Add add:
                return SimplifyAdd(add.Terms.Select(t => Expand(t, assumePositive)).ToList(), assumePositive);

            case SymExpr.Mul mul:
            {
                var expandedFactors = mul.Factors.Select(f => Expand(f, assumePositive)).ToList();
                var terms = new List<SymExpr> { One };
                foreach (var factor in expandedFactors)
                {
                    var factorTerms = factor is SymExpr.Add fa ? fa.Terms : [factor];
                    var next = new List<SymExpr>(terms.Count * factorTerms.Count);
                    foreach (var left in terms)
                        foreach (var right in factorTerms)
                            next.Add(SimplifyMul([left, right], assumePositive));
                    terms = next;
                }
                return SimplifyAdd(terms, assumePositive);
            }

            case SymExpr.Pow { Exp: SymExpr.Num en } pow when en.Value.TryGetBigInteger(out var exp) && exp >= 2 && exp <= 12:
            {
                var baseExp = Expand(pow.Base, assumePositive);
                if (baseExp is not SymExpr.Add) return pow;
                var acc = baseExp;
                for (int i = 1; i < (int)exp; i++)
                    acc = Expand(new SymExpr.Mul([acc, baseExp]), assumePositive);
                return acc;
            }

            default:
                return expr;
        }
    }

    /// <summary>
    /// Factors algebraic expressions: recognizes perfect square trinomials (including radical terms
    /// like x + 2*sqrt(x) + 1 and x + 2*sqrt(x*y) + y) and univariate rational polynomials.
    /// </summary>
    public static SymExpr Factor(SymExpr expr, bool assumePositive = false)
    {
        var simplified = Simplify(expr, assumePositive);

        // 1. Perfect square trinomial recognition: u^2 ± 2*u*v + v^2 = (u ± v)^2
        if (simplified is SymExpr.Add { Terms.Count: 3 } add)
        {
            var terms = add.Terms;
            for (int i = 0; i < 3; i++)
            {
                for (int j = i + 1; j < 3; j++)
                {
                    int mid = 3 - i - j;
                    if (!TrySymbolicSquareRoot(terms[i], assumePositive, out var u)) continue;
                    if (!TrySymbolicSquareRoot(terms[j], assumePositive, out var v)) continue;

                    var twoUV = Simplify(new SymExpr.Mul([FromLong(2), u, v]), assumePositive);
                    var diffPlus = Simplify(new SymExpr.Add([terms[mid], Negate(twoUV, assumePositive)]), assumePositive);
                    if (diffPlus is SymExpr.Num dp && dp.Value.IsZero)
                        return new SymExpr.Pow(OrderSquareTerms(u, v), FromLong(2));

                    var diffMinus = Simplify(new SymExpr.Add([terms[mid], twoUV]), assumePositive);
                    if (diffMinus is SymExpr.Num dm && dm.Value.IsZero)
                        return new SymExpr.Pow(new SymExpr.Add([u, Negate(v, assumePositive)]), FromLong(2));
                }
            }
        }

        return simplified;
    }

    private static SymExpr OrderSquareTerms(SymExpr u, SymExpr v)
    {
        // Put non-constant before constant, and lexicographically smaller variable first: (sqrt(x) + 1), (sqrt(x) + sqrt(y))
        bool uConst = u is SymExpr.Num;
        bool vConst = v is SymExpr.Num;
        if (uConst && !vConst) return new SymExpr.Add([v, u]);
        if (!uConst && vConst) return new SymExpr.Add([u, v]);
        return string.CompareOrdinal(Format(u), Format(v)) <= 0
            ? new SymExpr.Add([u, v])
            : new SymExpr.Add([v, u]);
    }

    private static bool TrySymbolicSquareRoot(SymExpr term, bool assumePositive, out SymExpr root)
    {
        root = Zero;
        if (term is SymExpr.Num n)
        {
            if (n.Value.Sign > 0 && TryExactRationalSqrt(n.Value, out var r))
            {
                root = new SymExpr.Num(r);
                return true;
            }
            return false;
        }
        if (term is SymExpr.Pow { Exp: SymExpr.Num exp } p && exp.Value.TryGetBigInteger(out var bi) && bi > 0 && bi % 2 == 0)
        {
            root = SimplifyPow(p.Base, FromLong((long)(bi / 2)), assumePositive);
            return true;
        }
        if (term is SymExpr.Var)
        {
            root = new SymExpr.Func("sqrt", [term]);
            return true;
        }
        var (coeff, core) = SplitCoeff(term);
        if (core != null && coeff != ExactNumber.One && coeff.Sign > 0 && TryExactRationalSqrt(coeff, out var rc) &&
            TrySymbolicSquareRoot(core, assumePositive, out var coreRoot))
        {
            root = SimplifyMul([new SymExpr.Num(rc), coreRoot], assumePositive);
            return true;
        }
        return false;
    }

    // --------------------------------------------------------- sign helpers

    private static bool IsNonNegativeConstant(SymExpr e) =>
        e is SymExpr.Num n && n.Value.Sign >= 0;

    private static bool IsProvablyPositive(SymExpr e, bool assumePositive) => e switch
    {
        SymExpr.Num n => n.Value.Sign > 0,
        SymExpr.Var => assumePositive,
        SymExpr.Func { Name: "sqrt", Args.Count: 1 } sq => IsProvablyPositive(sq.Args[0], assumePositive),
        SymExpr.Add add => add.Terms.All(t => IsProvablyNonNegative(t, assumePositive)) &&
                           add.Terms.Any(t => IsProvablyPositive(t, assumePositive)),
        SymExpr.Mul mul => mul.Factors.All(f => IsProvablyPositive(f, assumePositive)),
        SymExpr.Pow pow => IsProvablyPositive(pow.Base, assumePositive),
        _ => false
    };

    private static bool IsProvablyNonNegative(SymExpr e, bool assumePositive) => e switch
    {
        SymExpr.Num n => n.Value.Sign >= 0,
        SymExpr.Var => assumePositive,
        SymExpr.Func { Name: "abs" } => true,
        SymExpr.Func { Name: "sqrt" } => true,
        SymExpr.Pow { Exp: SymExpr.Num en } => en.Value.TryGetBigInteger(out var bi) && bi % 2 == 0,
        SymExpr.Add add => add.Terms.All(t => IsProvablyNonNegative(t, assumePositive)),
        SymExpr.Mul mul => mul.Factors.All(f => IsProvablyNonNegative(f, assumePositive)),
        _ => false
    };

    private static bool TryExactRationalSqrt(ExactNumber n, out ExactNumber root)
    {
        root = ExactNumber.Zero;
        if (!n.IsExact || n.Sign < 0) return false;
        if (!TryExactBigIntSqrt(n.Num, out var sn)) return false;
        if (!TryExactBigIntSqrt(n.Den, out var sd)) return false;
        root = ExactNumber.FromRational(sn, sd);
        return true;
    }

    private static bool TryExactBigIntSqrt(BigInteger n, out BigInteger root)
    {
        root = BigInteger.Zero;
        if (n < 0) return false;
        if (n <= 1) { root = n; return true; }
        BigInteger x = BigInteger.One << (int)((n.GetBitLength() + 1) / 2);
        while (true)
        {
            BigInteger y = (x + n / x) >> 1;
            if (y >= x) break;
            x = y;
        }
        if (x * x != n) return false;
        root = x;
        return true;
    }

    // --------------------------------------------------------- multivariate polynomial

    public sealed class Poly
    {
        // Key: canonical monomial string -> (Monomial exponents map, ExactNumber coeff)
        public SortedDictionary<string, (SortedDictionary<string, int> Exps, ExactNumber Coeff)> Terms { get; } = new(StringComparer.Ordinal);

        public bool IsZero => Terms.Count == 0;

        public void AddTerm(SortedDictionary<string, int> exps, ExactNumber coeff)
        {
            if (coeff.IsZero) return;
            var clean = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var (v, e) in exps)
                if (e != 0) clean[v] = e;
            string key = MonomialKey(clean);
            if (Terms.TryGetValue(key, out var existing))
            {
                var sum = existing.Coeff + coeff;
                if (sum.IsZero) Terms.Remove(key);
                else Terms[key] = (existing.Exps, sum);
            }
            else
            {
                Terms[key] = (clean, coeff);
            }
        }

        public static string MonomialKey(SortedDictionary<string, int> exps)
        {
            if (exps.Count == 0) return "";
            return string.Join("*", exps.Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Key}^{kv.Value}"));
        }

        public static Poly FromNum(ExactNumber n)
        {
            var p = new Poly();
            p.AddTerm([], n);
            return p;
        }

        public static Poly FromVar(string name)
        {
            var p = new Poly();
            p.AddTerm(new SortedDictionary<string, int>(StringComparer.Ordinal) { [name] = 1 }, ExactNumber.One);
            return p;
        }

        public static Poly Add(Poly a, Poly b, int sign = 1)
        {
            var res = new Poly();
            foreach (var (_, (e, c)) in a.Terms) res.AddTerm(e, c);
            foreach (var (_, (e, c)) in b.Terms) res.AddTerm(e, sign < 0 ? -c : c);
            return res;
        }

        public static Poly Mul(Poly a, Poly b)
        {
            var res = new Poly();
            foreach (var (_, (ea, ca)) in a.Terms)
            {
                foreach (var (_, (eb, cb)) in b.Terms)
                {
                    var exps = new SortedDictionary<string, int>(ea, StringComparer.Ordinal);
                    foreach (var (v, e) in eb)
                        exps[v] = exps.TryGetValue(v, out int prev) ? prev + e : e;
                    res.AddTerm(exps, ca * cb);
                }
            }
            return res;
        }

        public static Poly Pow(Poly a, int exp)
        {
            if (exp < 0) throw new EvalException("Polynomial exponent must be non-negative");
            var res = FromNum(ExactNumber.One);
            var cur = a;
            int e = exp;
            while (e > 0)
            {
                if ((e & 1) != 0) res = Mul(res, cur);
                e >>= 1;
                if (e > 0) cur = Mul(cur, cur);
            }
            return res;
        }

        public List<string> Variables()
        {
            var vars = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (_, (exps, _)) in Terms)
                foreach (var v in exps.Keys)
                    vars.Add(v);
            return vars.ToList();
        }

        public string DefaultVariable()
        {
            var vars = Variables();
            if (vars.Contains("x")) return "x";
            return vars.Count > 0 ? vars[0] : "x";
        }

        public int Degree(string varName)
        {
            int max = 0;
            foreach (var (_, (exps, _)) in Terms)
                if (exps.TryGetValue(varName, out int e) && e > max)
                    max = e;
            return max;
        }

        public int LowDegree(string varName)
        {
            if (IsZero) return 0;
            int min = int.MaxValue;
            foreach (var (_, (exps, _)) in Terms)
            {
                int e = exps.TryGetValue(varName, out int d) ? d : 0;
                if (e < min) min = e;
            }
            return min == int.MaxValue ? 0 : min;
        }

        public Poly Coeff(string varName, int degree)
        {
            var res = new Poly();
            foreach (var (_, (exps, c)) in Terms)
            {
                int e = exps.TryGetValue(varName, out int d) ? d : 0;
                if (e != degree) continue;
                var rest = new SortedDictionary<string, int>(exps, StringComparer.Ordinal);
                rest.Remove(varName);
                res.AddTerm(rest, c);
            }
            return res;
        }

        public Poly Scale(ExactNumber factor)
        {
            var res = new Poly();
            foreach (var (_, (exps, c)) in Terms)
                res.AddTerm(exps, c * factor);
            return res;
        }

        public bool TryGetConstant(out ExactNumber value)
        {
            if (IsZero) { value = ExactNumber.Zero; return true; }
            if (Terms.Count == 1 && Terms.TryGetValue("", out var single))
            {
                value = single.Coeff;
                return true;
            }
            value = ExactNumber.Zero;
            return false;
        }

        public SymExpr ToSymExpr(string? primaryVar = null)
        {
            if (IsZero) return Zero;
            var ordered = Terms.Values.ToList();
            // Sort by degree of primaryVar descending, then total degree descending, then monomial key
            ordered.Sort((a, b) =>
            {
                if (primaryVar != null)
                {
                    int da = a.Exps.TryGetValue(primaryVar, out int ea) ? ea : 0;
                    int db = b.Exps.TryGetValue(primaryVar, out int eb) ? eb : 0;
                    if (da != db) return db.CompareTo(da);
                }
                int ta = a.Exps.Values.Sum();
                int tb = b.Exps.Values.Sum();
                if (ta != tb) return tb.CompareTo(ta);
                return string.CompareOrdinal(MonomialKey(a.Exps), MonomialKey(b.Exps));
            });

            var symTerms = new List<SymExpr>(ordered.Count);
            foreach (var (exps, coeff) in ordered)
            {
                if (exps.Count == 0)
                {
                    symTerms.Add(new SymExpr.Num(coeff));
                    continue;
                }
                var factors = new List<SymExpr>();
                if (coeff != ExactNumber.One)
                    factors.Add(new SymExpr.Num(coeff));
                foreach (var (v, e) in exps)
                    factors.Add(e == 1 ? new SymExpr.Var(v) : new SymExpr.Pow(new SymExpr.Var(v), FromLong(e)));
                symTerms.Add(factors.Count == 1 ? factors[0] : new SymExpr.Mul(factors));
            }
            return symTerms.Count == 1 ? symTerms[0] : new SymExpr.Add(symTerms);
        }
    }

    public static bool TryToPoly(SymExpr expr, out Poly poly)
    {
        poly = new Poly();
        switch (expr)
        {
            case SymExpr.Num n:
                poly = Poly.FromNum(n.Value);
                return true;
            case SymExpr.Var v:
                poly = Poly.FromVar(v.Name);
                return true;
            case SymExpr.Add add:
            {
                var acc = new Poly();
                foreach (var t in add.Terms)
                {
                    if (!TryToPoly(t, out var tp)) return false;
                    acc = Poly.Add(acc, tp);
                }
                poly = acc;
                return true;
            }
            case SymExpr.Mul mul:
            {
                var acc = Poly.FromNum(ExactNumber.One);
                foreach (var f in mul.Factors)
                {
                    if (!TryToPoly(f, out var fp)) return false;
                    acc = Poly.Mul(acc, fp);
                }
                poly = acc;
                return true;
            }
            case SymExpr.Pow { Exp: SymExpr.Num en } pow:
            {
                if (!en.Value.TryGetBigInteger(out var bi)) return false;
                if (bi < 0 && pow.Base is SymExpr.Num bn && !bn.Value.IsZero && bi >= -1000)
                {
                    poly = Poly.FromNum(ExactNumber.Pow(bn.Value, ExactNumber.FromBigInteger(bi)));
                    return true;
                }
                if (bi < 0 || bi > 100) return false;
                if (!TryToPoly(pow.Base, out var bp)) return false;
                poly = Poly.Pow(bp, (int)bi);
                return true;
            }
            default:
                return false;
        }
    }

    private static Poly RequirePoly(SymExpr expr) =>
        TryToPoly(expr, out var p) ? p : throw new EvalException("Expected a polynomial expression");

    // --------------------------------------------------------- polynomial functions

    public static SymExpr PolyCoeff(SymExpr expr, int n, string? varName = null)
    {
        var p = RequirePoly(expr);
        var v = varName ?? p.DefaultVariable();
        return p.Coeff(v, n).ToSymExpr();
    }

    public static int PolyDegree(SymExpr expr, string? varName = null)
    {
        var p = RequirePoly(expr);
        var v = varName ?? p.DefaultVariable();
        return p.Degree(v);
    }

    public static int PolyLowDegree(SymExpr expr, string? varName = null)
    {
        var p = RequirePoly(expr);
        var v = varName ?? p.DefaultVariable();
        return p.LowDegree(v);
    }

    public static SymExpr PolyLeadingCoeff(SymExpr expr, string? varName = null)
    {
        var p = RequirePoly(expr);
        var v = varName ?? p.DefaultVariable();
        return p.Coeff(v, p.Degree(v)).ToSymExpr();
    }

    public static SymExpr PolyTrailingCoeff(SymExpr expr, string? varName = null)
    {
        var p = RequirePoly(expr);
        var v = varName ?? p.DefaultVariable();
        return p.Coeff(v, p.LowDegree(v)).ToSymExpr();
    }

    public static int PolyUnit(SymExpr expr, string? varName = null)
    {
        var p = RequirePoly(expr);
        if (p.IsZero) return 1;
        var v = varName ?? p.DefaultVariable();
        var lc = p.Coeff(v, p.Degree(v));
        if (lc.TryGetConstant(out var c)) return c.Sign < 0 ? -1 : 1;
        // Multivariate leading coefficient: check its own leading coefficient recursively
        return PolyUnit(lc.ToSymExpr(), lc.DefaultVariable());
    }

    public static SymExpr PolyContent(SymExpr expr, string? varName = null)
    {
        var p = RequirePoly(expr);
        if (p.IsZero) return Zero;
        var v = varName ?? p.DefaultVariable();
        int deg = p.Degree(v);
        int ldeg = p.LowDegree(v);
        if (deg == ldeg)
        {
            var single = p.Coeff(v, deg);
            int unit = PolyUnit(single.ToSymExpr(), single.DefaultVariable());
            return (unit < 0 ? single.Scale(ExactNumber.FromLong(-1)) : single).ToSymExpr();
        }

        var ic = RationalContent(p);
        var commonMonomial = CommonMonomialExcept(p, v);
        if (commonMonomial.Count > 0)
        {
            var res = new Poly();
            res.AddTerm(commonMonomial, ic);
            return res.ToSymExpr();
        }

        // Reject unsupported non-trivial multivariate polynomial GCD when multiple non-constant
        // coefficients in other variables could share a non-monomial polynomial factor.
        var otherVars = p.Variables().Where(name => name != v).ToList();
        if (otherVars.Count == 1)
        {
            string u = otherVars[0];
            Poly? gcdPoly = null;
            for (int k = ldeg; k <= deg; k++)
            {
                var ck = p.Coeff(v, k);
                if (ck.IsZero) continue;
                gcdPoly = gcdPoly == null ? ck : UnivariatePolyGcd(gcdPoly, ck, u);
                if (gcdPoly.Degree(u) == 0) break;
            }
            if (gcdPoly != null && gcdPoly.Degree(u) > 0)
            {
                var gcdPrim = MakePrimitive(gcdPoly, u);
                return gcdPrim.Scale(ic).ToSymExpr(u);
            }
        }

        return new SymExpr.Num(ic);
    }

    public static SymExpr PolyPrimPart(SymExpr expr, string? varName = null)
    {
        var p = RequirePoly(expr);
        if (p.IsZero) return Zero;
        var v = varName ?? p.DefaultVariable();
        int unit = PolyUnit(expr, v);
        var content = PolyContent(expr, v);
        if (content is SymExpr.Num cn)
        {
            var divisor = unit < 0 ? -cn.Value : cn.Value;
            if (divisor.IsZero) return Zero;
            return p.Scale(ExactNumber.One / divisor).ToSymExpr(v);
        }
        if (TryToPoly(content, out var cPoly) && cPoly.Terms.Count == 1)
        {
            var (cExps, cCoeff) = cPoly.Terms.Values.First();
            var divisor = unit < 0 ? -cCoeff : cCoeff;
            if (divisor.IsZero) return Zero;
            var res = new Poly();
            foreach (var (_, (exps, coeff)) in p.Terms)
            {
                var nextExps = new SortedDictionary<string, int>(exps, StringComparer.Ordinal);
                foreach (var (cv, ce) in cExps)
                {
                    int rem = (nextExps.TryGetValue(cv, out int prev) ? prev : 0) - ce;
                    if (rem == 0) nextExps.Remove(cv);
                    else nextExps[cv] = rem;
                }
                res.AddTerm(nextExps, coeff / divisor);
            }
            return res.ToSymExpr(v);
        }
        var quot = Simplify(Div(expr, unit < 0 ? Negate(content) : content));
        return quot;
    }

    private static SortedDictionary<string, int> CommonMonomialExcept(Poly p, string excludeVar)
    {
        SortedDictionary<string, int>? common = null;
        foreach (var (_, (exps, _)) in p.Terms)
        {
            if (common == null)
            {
                common = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var (name, e) in exps)
                    if (name != excludeVar && e > 0) common[name] = e;
            }
            else
            {
                foreach (var key in common.Keys.ToList())
                {
                    if (!exps.TryGetValue(key, out int e) || e <= 0) common.Remove(key);
                    else if (e < common[key]) common[key] = e;
                }
            }
            if (common.Count == 0) break;
        }
        return common ?? new SortedDictionary<string, int>(StringComparer.Ordinal);
    }

    private static Poly UnivariatePolyGcd(Poly a, Poly b, string v)
    {
        var x = a;
        var y = b;
        while (!y.IsZero)
        {
            var r = UnivariatePolyRem(x, y, v);
            x = y;
            y = r;
        }
        return x;
    }

    private static Poly UnivariatePolyRem(Poly a, Poly b, string v)
    {
        var rem = a;
        int degB = b.Degree(v);
        if (!b.Coeff(v, degB).TryGetConstant(out var lcB) || lcB.IsZero) return new Poly();
        while (!rem.IsZero && rem.Degree(v) >= degB)
        {
            int degR = rem.Degree(v);
            if (!rem.Coeff(v, degR).TryGetConstant(out var lcR)) break;
            var factor = lcR / lcB;
            int shift = degR - degB;
            var sub = new Poly();
            foreach (var (_, (exps, c)) in b.Terms)
            {
                var nextExps = new SortedDictionary<string, int>(exps, StringComparer.Ordinal);
                int curE = nextExps.TryGetValue(v, out int prev) ? prev : 0;
                if (curE + shift > 0) nextExps[v] = curE + shift;
                sub.AddTerm(nextExps, c * factor);
            }
            rem = Poly.Add(rem, sub, -1);
        }
        return rem;
    }

    private static Poly MakePrimitive(Poly p, string v)
    {
        var ic = RationalContent(p);
        if (ic.IsZero) return p;
        int unit = PolyUnit(p.ToSymExpr(), v);
        var div = unit < 0 ? -ic : ic;
        return p.Scale(ExactNumber.One / div);
    }

    private static ExactNumber RationalContent(Poly p)
    {
        BigInteger numGcd = BigInteger.Zero;
        BigInteger denLcm = BigInteger.One;
        foreach (var (_, (_, c)) in p.Terms)
        {
            if (!c.IsExact) return ExactNumber.One;
            numGcd = numGcd.IsZero ? BigInteger.Abs(c.Num) : BigInteger.GreatestCommonDivisor(numGcd, BigInteger.Abs(c.Num));
            denLcm = denLcm / BigInteger.GreatestCommonDivisor(denLcm, c.Den) * c.Den;
        }
        return numGcd.IsZero ? ExactNumber.Zero : ExactNumber.FromRational(numGcd, denLcm);
    }

    // --------------------------------------------------------- polynomial equation solver

    public static SymExpr SolveEquation(SymExpr lhs, SymExpr rhs, bool assumePositive = false) =>
        Solver.Solve(lhs, rhs, assumePositive);

    private static List<SymExpr> SolveIntegerPolynomial(BigInteger[] c)
    {
        var roots = new List<SymExpr>();
        var cur = c.ToList();

        // Trim leading zeros
        while (cur.Count > 1 && cur[^1].IsZero) cur.RemoveAt(cur.Count - 1);

        // Factor out x = 0 roots
        while (cur.Count > 1 && cur[0].IsZero)
        {
            roots.Add(Zero);
            cur.RemoveAt(0);
        }

        // Deflate rational roots via Rational Root Theorem when degree >= 3
        while (cur.Count - 1 >= 3)
        {
            if (!TryFindRationalRoot(cur, out var p, out var q))
                break;

            roots.Add(new SymExpr.Num(ExactNumber.FromRational(p, q)));
            cur = SyntheticDivide(cur, p, q);
        }

        int deg = cur.Count - 1;
        if (deg == 1)
        {
            roots.Add(new SymExpr.Num(ExactNumber.FromRational(-cur[0], cur[1])));
        }
        else if (deg == 2)
        {
            BigInteger a = cur[2], b = cur[1], d0 = cur[0];
            BigInteger disc = b * b - 4 * a * d0;
            if (disc.IsZero)
            {
                roots.Add(new SymExpr.Num(ExactNumber.FromRational(-b, 2 * a)));
            }
            else if (disc > 0)
            {
                if (TryExactBigIntSqrt(disc, out var sd))
                {
                    roots.Add(new SymExpr.Num(ExactNumber.FromRational(-b + sd, 2 * a)));
                    roots.Add(new SymExpr.Num(ExactNumber.FromRational(-b - sd, 2 * a)));
                }
                else
                {
                    var (k, rad) = ExtractSquareFactor(disc);
                    var twoA = ExactNumber.FromBigInteger(2 * a);
                    var halfB = ExactNumber.FromRational(-b, 2 * a);
                    var radCoeff = ExactNumber.FromRational(k, 2 * a);
                    var sqrtRad = new SymExpr.Func("sqrt", [new SymExpr.Num(ExactNumber.FromBigInteger(rad))]);
                    var radPart = radCoeff == ExactNumber.One ? (SymExpr)sqrtRad : new SymExpr.Mul([new SymExpr.Num(radCoeff), sqrtRad]);
                    roots.Add(SimplifyAdd([new SymExpr.Num(halfB), radPart], false));
                    roots.Add(SimplifyAdd([new SymExpr.Num(halfB), new SymExpr.Mul([MinusOne, radPart])], false));
                }
            }
        }

        return roots;
    }

    private static bool TryFindRationalRoot(List<BigInteger> c, out BigInteger rootP, out BigInteger rootQ)
    {
        rootP = 0; rootQ = 1;
        BigInteger a0 = BigInteger.Abs(c[0]);
        BigInteger an = BigInteger.Abs(c[^1]);
        if (a0 > 1_000_000 || an > 1_000_000) return false;

        var ps = Divisors((long)a0);
        var qs = Divisors((long)an);

        foreach (long q in qs)
        {
            foreach (long p in ps)
            {
                if (BigInteger.GreatestCommonDivisor(p, q) != 1) continue;
                if (EvalPolyAtRational(c, p, q).IsZero) { rootP = p; rootQ = q; return true; }
                if (EvalPolyAtRational(c, -p, q).IsZero) { rootP = -p; rootQ = q; return true; }
            }
        }
        return false;
    }

    private static List<long> Divisors(long n)
    {
        var list = new List<long>();
        for (long i = 1; i * i <= n; i++)
        {
            if (n % i == 0)
            {
                list.Add(i);
                if (i * i != n) list.Add(n / i);
            }
        }
        return list;
    }

    private static BigInteger EvalPolyAtRational(List<BigInteger> c, BigInteger p, BigInteger q)
    {
        // Evaluate q^deg * P(p/q) = sum_{k=0}^{deg} c[k] * p^k * q^(deg-k)
        int deg = c.Count - 1;
        BigInteger sum = 0;
        BigInteger pPow = 1;
        var qPows = new BigInteger[deg + 1];
        qPows[0] = 1;
        for (int i = 1; i <= deg; i++) qPows[i] = qPows[i - 1] * q;
        for (int k = 0; k <= deg; k++)
        {
            sum += c[k] * pPow * qPows[deg - k];
            pPow *= p;
        }
        return sum;
    }

    private static List<BigInteger> SyntheticDivide(List<BigInteger> c, BigInteger p, BigInteger q)
    {
        // Divide P(x) by (q*x - p)
        int deg = c.Count - 1;
        var res = new BigInteger[deg];
        BigInteger carry = 0;
        for (int k = deg; k >= 1; k--)
        {
            BigInteger coeff = (c[k] + carry) / q;
            res[k - 1] = coeff;
            carry = coeff * p;
        }
        return res.ToList();
    }

    private static (BigInteger Factor, BigInteger Radicand) ExtractSquareFactor(BigInteger n)
    {
        BigInteger factor = 1;
        BigInteger rem = n;
        for (long d = 2; d * d <= rem && d <= 10000; d++)
        {
            long d2 = d * d;
            while (rem % d2 == 0)
            {
                rem /= d2;
                factor *= d;
            }
        }
        return (factor, rem);
    }

    public static double EvalNumericApprox(SymExpr expr) => expr switch
    {
        SymExpr.Num n => n.Value.ToDouble(),
        SymExpr.Var v => v.Name switch { "pi" => Math.PI, "e" => Math.E, _ => double.NaN },
        SymExpr.Add a => a.Terms.Sum(EvalNumericApprox),
        SymExpr.Mul m => m.Factors.Aggregate(1.0, (acc, f) => acc * EvalNumericApprox(f)),
        SymExpr.Pow p => Math.Pow(EvalNumericApprox(p.Base), EvalNumericApprox(p.Exp)),
        SymExpr.Func { Name: "sqrt", Args.Count: 1 } sq => Math.Sqrt(EvalNumericApprox(sq.Args[0])),
        SymExpr.Func { Name: "cbrt", Args.Count: 1 } cb => Math.Cbrt(EvalNumericApprox(cb.Args[0])),
        SymExpr.Func { Name: "ln", Args.Count: 1 } ln => Math.Log(EvalNumericApprox(ln.Args[0])),
        SymExpr.Func { Name: "exp", Args.Count: 1 } ex => Math.Exp(EvalNumericApprox(ex.Args[0])),
        SymExpr.Func { Name: "sin", Args.Count: 1 } sn => Math.Sin(EvalNumericApprox(sn.Args[0])),
        SymExpr.Func { Name: "cos", Args.Count: 1 } cs => Math.Cos(EvalNumericApprox(cs.Args[0])),
        SymExpr.Func { Name: "tan", Args.Count: 1 } tn => Math.Tan(EvalNumericApprox(tn.Args[0])),
        SymExpr.Func { Name: "sinh", Args.Count: 1 } sh => Math.Sinh(EvalNumericApprox(sh.Args[0])),
        SymExpr.Func { Name: "cosh", Args.Count: 1 } ch => Math.Cosh(EvalNumericApprox(ch.Args[0])),
        SymExpr.Func { Name: "tanh", Args.Count: 1 } th => Math.Tanh(EvalNumericApprox(th.Args[0])),
        SymExpr.Func { Name: "shi" or "Shi", Args.Count: 1 } shi => Calculus.Shi(EvalNumericApprox(shi.Args[0])),
        SymExpr.Func { Name: "ei" or "Ei", Args.Count: 1 } ei => ComplexInterval.Ei(ComplexInterval.FromReal(EvalNumericApprox(ei.Args[0])), 1).Re,
        SymExpr.Func { Name: "abs", Args.Count: 1 } ab => Math.Abs(EvalNumericApprox(ab.Args[0])),
        SymExpr.Func { Name: "lambertw", Args.Count: >= 1 } lw =>
            ComplexInterval.LambertW(
                ComplexInterval.FromReal(EvalNumericApprox(lw.Args[0])),
                lw.Args.Count > 1 ? (int)Math.Round(EvalNumericApprox(lw.Args[1])) : 0,
                1).Re,
        _ => double.NaN
    };

    // --------------------------------------------------------- canonical key & formatting

    public static string CanonicalKey(SymExpr expr) => expr switch
    {
        SymExpr.Num n => n.Value.IsExact
            ? $"N({n.Value.Num}/{n.Value.Den})"
            : $"D({BitConverter.DoubleToInt64Bits(n.Value.ToDouble() == 0 ? 0.0 : n.Value.ToDouble())})",
        SymExpr.Var v => $"V({v.Name})",
        SymExpr.Add a => "A(" + string.Join(",", a.Terms.Select(CanonicalKey).OrderBy(s => s, StringComparer.Ordinal)) + ")",
        SymExpr.Mul m => "M(" + string.Join(",", m.Factors.Select(CanonicalKey).OrderBy(s => s, StringComparer.Ordinal)) + ")",
        SymExpr.Pow p => $"P({CanonicalKey(p.Base)},{CanonicalKey(p.Exp)})",
        SymExpr.Func f => $"F({f.Name},{string.Join(",", f.Args.Select(CanonicalKey))})",
        SymExpr.Solutions s => $"S({s.Variable},{string.Join(",", s.Roots.Select(CanonicalKey))})",
        _ => expr.ToString() ?? ""
    };

    public static string Format(SymExpr expr, int decimals = 6) => expr switch
    {
        SymExpr.Solutions sol => string.Join(" or ", sol.Roots.Select(r => $"{sol.Variable} = {Format(r, decimals)}")),
        SymExpr.Num n => FormatNum(n.Value, decimals),
        SymExpr.Var v => v.Name,
        SymExpr.Add add => FormatAdd(add.Terms, decimals),
        SymExpr.Mul mul => FormatMul(mul.Factors, decimals),
        SymExpr.Pow pow => FormatPow(pow.Base, pow.Exp, decimals),
        SymExpr.Func fn => $"{fn.Name}({string.Join(", ", fn.Args.Select(a => Format(a, decimals)))})",
        _ => ""
    };

    private static string FormatNum(ExactNumber n, int decimals)
    {
        if (n.IsExact && n.Den == BigInteger.One)
            return n.Num.ToString(CultureInfo.InvariantCulture);
        if (n.IsExact && n.Den <= 10000 && BigInteger.Abs(n.Num) <= 1_000_000)
            return $"{n.Num}/{n.Den}";
        return Formatter.Number(n.ToDouble(), decimals, false);
    }

    private static string FormatAdd(IReadOnlyList<SymExpr> terms, int decimals)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < terms.Count; i++)
        {
            var t = terms[i];
            bool neg = IsNegativeTerm(t, out var posTerm);
            if (i == 0)
            {
                if (neg) sb.Append('-').Append(Format(posTerm, decimals));
                else sb.Append(Format(t, decimals));
            }
            else
            {
                if (neg) sb.Append(" - ").Append(Format(posTerm, decimals));
                else sb.Append(" + ").Append(Format(t, decimals));
            }
        }
        return sb.ToString();
    }

    private static bool IsNegativeTerm(SymExpr t, out SymExpr positive)
    {
        if (t is SymExpr.Num n && n.Value.Sign < 0)
        {
            positive = new SymExpr.Num(-n.Value);
            return true;
        }
        if (t is SymExpr.Mul m && m.Factors.Count > 0 && m.Factors[0] is SymExpr.Num lead && lead.Value.Sign < 0)
        {
            var absLead = -lead.Value;
            if (absLead == ExactNumber.One)
                positive = m.Factors.Count == 2 ? m.Factors[1] : new SymExpr.Mul(m.Factors.Skip(1).ToList());
            else
                positive = new SymExpr.Mul([new SymExpr.Num(absLead), .. m.Factors.Skip(1)]);
            return true;
        }
        positive = t;
        return false;
    }

    private static string FormatMul(IReadOnlyList<SymExpr> factors, int decimals)
    {
        // Split into numerator and denominator (for rational coeff 1/2 or negative exponents)
        ExactNumber coeff = ExactNumber.One;
        var numFactors = new List<SymExpr>();
        var denFactors = new List<SymExpr>();

        foreach (var f in factors)
        {
            if (f is SymExpr.Num n) { coeff *= n.Value; continue; }
            if (f is SymExpr.Pow { Exp: SymExpr.Num en } p && en.Value.Sign < 0)
            {
                denFactors.Add(en.Value == ExactNumber.FromLong(-1) ? p.Base : new SymExpr.Pow(p.Base, new SymExpr.Num(-en.Value)));
                continue;
            }
            numFactors.Add(f);
        }

        string sign = coeff.Sign < 0 ? "-" : "";
        var absCoeff = coeff.Sign < 0 ? -coeff : coeff;

        // Pure fraction denominator from exact rational coeff (e.g. (1/2) * sqrt(5) -> sqrt(5) / 2)
        BigInteger coeffDen = BigInteger.One;
        ExactNumber coeffNum = absCoeff;
        if (absCoeff.IsExact && absCoeff.Den != BigInteger.One && absCoeff.Den <= 10000 && numFactors.Count > 0)
        {
            coeffDen = absCoeff.Den;
            coeffNum = ExactNumber.FromBigInteger(absCoeff.Num);
        }

        var numSb = new StringBuilder();
        bool hasCoeff = coeffNum != ExactNumber.One || numFactors.Count == 0;
        if (hasCoeff)
            numSb.Append(FormatNum(coeffNum, decimals));

        for (int i = 0; i < numFactors.Count; i++)
        {
            var f = numFactors[i];
            string fs = f is SymExpr.Add ? $"({Format(f, decimals)})" : Format(f, decimals);
            if (numSb.Length > 0)
            {
                bool adjacent = hasCoeff && i == 0 && (f is SymExpr.Var || (f is SymExpr.Pow p && p.Base is SymExpr.Var));
                bool adjacentVars = i > 0 && numFactors[i - 1] is SymExpr.Var && (f is SymExpr.Var || (f is SymExpr.Pow pv && pv.Base is SymExpr.Var));
                if (!adjacent && !adjacentVars)
                    numSb.Append(" * ");
            }
            numSb.Append(fs);
        }

        if (coeffDen == BigInteger.One && denFactors.Count == 0)
            return sign + numSb;

        var denParts = new List<string>();
        if (coeffDen != BigInteger.One) denParts.Add(coeffDen.ToString(CultureInfo.InvariantCulture));
        foreach (var d in denFactors)
            denParts.Add(d is SymExpr.Add or SymExpr.Mul ? $"({Format(d, decimals)})" : Format(d, decimals));

        string denStr = denParts.Count == 1 ? denParts[0] : $"({string.Join(" * ", denParts)})";
        string numStr = numFactors.Count > 1 && coeffDen != BigInteger.One ? $"({numSb})" : numSb.ToString();
        return $"{sign}{numStr} / {denStr}";
    }

    private static string FormatPow(SymExpr b, SymExpr e, int decimals)
    {
        bool parenBase = b is SymExpr.Add or SymExpr.Mul or SymExpr.Pow ||
                         (b is SymExpr.Num bn && (bn.Value.Sign < 0 || (bn.Value.IsExact && bn.Value.Den != BigInteger.One)));
        bool parenExp = e is SymExpr.Add or SymExpr.Mul or SymExpr.Pow ||
                        (e is SymExpr.Num en && en.Value.IsExact && en.Value.Den != BigInteger.One);
        string bs = parenBase ? $"({Format(b, decimals)})" : Format(b, decimals);
        string es = parenExp ? $"({Format(e, decimals)})" : Format(e, decimals);
        return $"{bs}^{es}";
    }
}
