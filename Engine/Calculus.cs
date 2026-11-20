using System.Numerics;

namespace Clay.Engine;

/// <summary>
/// Symbolic differentiation and integration, Romberg numerical quadrature,
/// and special calculus functions (Shi, fresnels, gammainc, betainc).
/// </summary>
public static class Calculus
{
    private static readonly SymExpr ConstC = Symbolic.Var("C");

    // --------------------------------------------------------- symbolic differentiation

    public static SymExpr Diff(SymExpr expr, string varName, int order = 1)
    {
        var cur = Symbolic.Simplify(expr, Ops.AssumePositive);
        for (int i = 0; i < order; i++)
            cur = Symbolic.Simplify(DiffOnce(cur, varName), Ops.AssumePositive);
        return cur;
    }

    private static SymExpr DiffOnce(SymExpr expr, string v)
    {
        if (!ContainsVar(expr, v)) return Symbolic.Zero;

        switch (expr)
        {
            case SymExpr.Var vr:
                return vr.Name == v ? Symbolic.One : Symbolic.Zero;

            case SymExpr.Add add:
                return Symbolic.Simplify(new SymExpr.Add(add.Terms.Select(t => DiffOnce(t, v)).ToList()));

            case SymExpr.Mul mul:
            {
                var terms = new List<SymExpr>(mul.Factors.Count);
                for (int i = 0; i < mul.Factors.Count; i++)
                {
                    var df = DiffOnce(mul.Factors[i], v);
                    if (df is SymExpr.Num dn && dn.Value.IsZero) continue;
                    var prod = new List<SymExpr>(mul.Factors.Count);
                    for (int j = 0; j < mul.Factors.Count; j++)
                        prod.Add(j == i ? df : mul.Factors[j]);
                    terms.Add(new SymExpr.Mul(prod));
                }
                return terms.Count == 0 ? Symbolic.Zero : Symbolic.Simplify(new SymExpr.Add(terms));
            }

            case SymExpr.Pow pow:
            {
                var f = pow.Base;
                var g = pow.Exp;
                bool fDep = ContainsVar(f, v);
                bool gDep = ContainsVar(g, v);
                if (fDep && !gDep)
                {
                    // (f^c)' = c * f^(c-1) * f'
                    var df = DiffOnce(f, v);
                    var newExp = Symbolic.Add(g, Symbolic.One, -1);
                    return Symbolic.Mul(Symbolic.Mul(g, Symbolic.Pow(f, newExp)), df);
                }
                if (!fDep && gDep)
                {
                    // (c^g)' = c^g * ln(c) * g'
                    var dg = DiffOnce(g, v);
                    return Symbolic.Mul(Symbolic.Mul(pow, Symbolic.Call("ln", [f])), dg);
                }
                // General: (f^g)' = f^g * (g' * ln(f) + g * f'/f)
                var dfg = DiffOnce(f, v);
                var dgg = DiffOnce(g, v);
                var inner = Symbolic.Add(
                    Symbolic.Mul(dgg, Symbolic.Call("ln", [f])),
                    Symbolic.Div(Symbolic.Mul(g, dfg), f));
                return Symbolic.Mul(pow, inner);
            }

            case SymExpr.Func { Args.Count: 1 } fn:
            {
                var u = fn.Args[0];
                var du = DiffOnce(u, v);
                if (du is SymExpr.Num dn && dn.Value.IsZero) return Symbolic.Zero;
                SymExpr outer = fn.Name switch
                {
                    "sqrt" => Symbolic.Div(Symbolic.One, Symbolic.Mul(Symbolic.FromLong(2), fn)),
                    "cbrt" => Symbolic.Div(Symbolic.One, Symbolic.Mul(Symbolic.FromLong(3), Symbolic.Pow(fn, Symbolic.FromLong(2)))),
                    "sin" => Symbolic.Call("cos", [u]),
                    "cos" => Symbolic.Negate(Symbolic.Call("sin", [u])),
                    "tan" => Symbolic.Div(Symbolic.One, Symbolic.Pow(Symbolic.Call("cos", [u]), Symbolic.FromLong(2))),
                    "cot" => Symbolic.Negate(Symbolic.Div(Symbolic.One, Symbolic.Pow(Symbolic.Call("sin", [u]), Symbolic.FromLong(2)))),
                    "asin" => Symbolic.Div(Symbolic.One, Symbolic.Call("sqrt", [Symbolic.Add(Symbolic.One, Symbolic.Pow(u, Symbolic.FromLong(2)), -1)])),
                    "acos" => Symbolic.Negate(Symbolic.Div(Symbolic.One, Symbolic.Call("sqrt", [Symbolic.Add(Symbolic.One, Symbolic.Pow(u, Symbolic.FromLong(2)), -1)]))),
                    "atan" => Symbolic.Div(Symbolic.One, Symbolic.Add(Symbolic.One, Symbolic.Pow(u, Symbolic.FromLong(2)))),
                    "acot" => Symbolic.Negate(Symbolic.Div(Symbolic.One, Symbolic.Add(Symbolic.One, Symbolic.Pow(u, Symbolic.FromLong(2))))),
                    "sinh" => Symbolic.Call("cosh", [u]),
                    "cosh" => Symbolic.Call("sinh", [u]),
                    "tanh" => Symbolic.Div(Symbolic.One, Symbolic.Pow(Symbolic.Call("cosh", [u]), Symbolic.FromLong(2))),
                    "ln" => Symbolic.Div(Symbolic.One, u),
                    "exp" => fn,
                    _ => throw new EvalException($"Differentiation of '{fn.Name}' is not supported")
                };
                return Symbolic.Mul(outer, du);
            }

            case SymExpr.Func { Name: "root", Args.Count: 2 } rfn:
            {
                var exp = Symbolic.Div(Symbolic.One, rfn.Args[1]);
                return DiffOnce(Symbolic.Pow(rfn.Args[0], exp), v);
            }

            default:
                throw new EvalException("Differentiation of this expression is not supported");
        }
    }

    // --------------------------------------------------------- symbolic & definite integration

    public static SymExpr IntegrateIndefinite(SymExpr expr, string varName)
    {
        var anti = IntegrateExpr(Symbolic.Simplify(expr), varName);
        return Symbolic.Add(anti, ConstC);
    }

    public static Value IntegrateDefinite(SymExpr expr, double a, double b, string varName)
    {
        var simplified = Symbolic.Simplify(expr);
        var otherVars = CollectVarsExcept(simplified, varName);

        // Purely numeric integrand in varName: direct Romberg quadrature
        if (otherVars.Count == 0)
        {
            double val = Romberg(x => EvalDouble(simplified, varName, x), a, b);
            return new Value(val);
        }

        // Integrand with extra symbolic parameters (e.g. y): integrate term-by-term,
        // factoring out varName-independent multipliers and using Romberg on the varName-dependent part.
        var terms = simplified is SymExpr.Add add ? add.Terms : [simplified];
        SymExpr total = Symbolic.Zero;
        foreach (var t in terms)
        {
            SplitFactorsByVar(t, varName, out var indep, out var dep);
            double numIntegral = Romberg(x => EvalDouble(dep, varName, x), a, b);
            var termVal = Symbolic.Mul(new SymExpr.Num(ExactNumber.FromDouble(numIntegral)), indep);
            total = Symbolic.Add(total, termVal);
        }
        return Value.FromSym(total);
    }

    private static SymExpr IntegrateExpr(SymExpr expr, string v)
    {
        if (!ContainsVar(expr, v))
            return Symbolic.Mul(expr, Symbolic.Var(v));

        if (expr is SymExpr.Add add)
            return Symbolic.Simplify(new SymExpr.Add(add.Terms.Select(t => IntegrateExpr(t, v)).ToList()));

        // Split into v-independent multiplier and v-dependent core
        SplitFactorsByVar(expr, v, out var indep, out var dep);

        var coreAnti = IntegrateDep(dep, v);
        return Symbolic.Mul(indep, coreAnti);
    }

    private static SymExpr IntegrateDep(SymExpr dep, string v)
    {
        // v -> v^2 / 2
        if (dep is SymExpr.Var vr && vr.Name == v)
            return Symbolic.Div(Symbolic.Pow(vr, Symbolic.FromLong(2)), Symbolic.FromLong(2));

        // sqrt(v) -> (2/3) * v * sqrt(v)
        if (dep is SymExpr.Func { Name: "sqrt", Args.Count: 1 } sq && sq.Args[0] is SymExpr.Var sv && sv.Name == v)
            return Symbolic.Mul(
                new SymExpr.Num(ExactNumber.FromRational(2, 3)),
                Symbolic.Mul(sv, sq));

        // v^p -> v^(p+1)/(p+1) or ln(abs(v))
        if (dep is SymExpr.Pow { Base: SymExpr.Var pv, Exp: SymExpr.Num pe } && pv.Name == v)
        {
            if (pe.Value == ExactNumber.FromLong(-1))
                return Symbolic.Call("ln", [Symbolic.Call("abs", [pv])]);
            var nextExp = pe.Value + ExactNumber.One;
            return Symbolic.Div(Symbolic.Pow(pv, new SymExpr.Num(nextExp)), new SymExpr.Num(nextExp));
        }

        // sinh(a * v^n) / v  ->  (1/n) * Shi(a * v^n)
        if (dep is SymExpr.Mul mul && mul.Factors.Count == 2)
        {
            SymExpr.Func? sinhFn = null;
            bool hasInvV = false;
            foreach (var f in mul.Factors)
            {
                if (f is SymExpr.Func { Name: "sinh", Args.Count: 1 } sh) sinhFn = sh;
                else if (f is SymExpr.Pow { Base: SymExpr.Var iv, Exp: SymExpr.Num ie } &&
                         iv.Name == v && ie.Value == ExactNumber.FromLong(-1))
                    hasInvV = true;
            }
            if (sinhFn != null && hasInvV && TryMatchMonomial(sinhFn.Args[0], v, out var nExp) && nExp != 0)
            {
                return Symbolic.Mul(
                    new SymExpr.Num(ExactNumber.FromRational(1, nExp)),
                    new SymExpr.Func("Shi", [sinhFn.Args[0]]));
            }
        }

        // Basic trig/hyperbolic/exp of v
        if (dep is SymExpr.Func { Args.Count: 1 } fn && fn.Args[0] is SymExpr.Var fv && fv.Name == v)
        {
            return fn.Name switch
            {
                "sin" => Symbolic.Negate(Symbolic.Call("cos", [fv])),
                "cos" => Symbolic.Call("sin", [fv]),
                "sinh" => Symbolic.Call("cosh", [fv]),
                "cosh" => Symbolic.Call("sinh", [fv]),
                "exp" => fn,
                _ => throw new EvalException($"Symbolic integration of '{fn.Name}' is not supported")
            };
        }

        throw new EvalException("Symbolic integration of this expression is not supported");
    }

    private static bool TryMatchMonomial(SymExpr expr, string v, out long exp)
    {
        exp = 0;
        if (expr is SymExpr.Var vr && vr.Name == v) { exp = 1; return true; }
        if (expr is SymExpr.Pow { Base: SymExpr.Var pv, Exp: SymExpr.Num pe } &&
            pv.Name == v && pe.Value.TryGetBigInteger(out var bi) && bi > 0 && bi <= 100)
        {
            exp = (long)bi;
            return true;
        }
        if (expr is SymExpr.Mul mul)
        {
            foreach (var f in mul.Factors)
            {
                if (!ContainsVar(f, v)) continue;
                if (exp != 0 || !TryMatchMonomial(f, v, out exp)) return false;
            }
            return exp != 0;
        }
        return false;
    }

    private static void SplitFactorsByVar(SymExpr expr, string v, out SymExpr indep, out SymExpr dep)
    {
        if (expr is not SymExpr.Mul mul)
        {
            if (ContainsVar(expr, v)) { indep = Symbolic.One; dep = expr; }
            else { indep = expr; dep = Symbolic.One; }
            return;
        }
        var indepList = new List<SymExpr>();
        var depList = new List<SymExpr>();
        foreach (var f in mul.Factors)
        {
            if (ContainsVar(f, v)) depList.Add(f);
            else indepList.Add(f);
        }
        indep = indepList.Count == 0 ? Symbolic.One : indepList.Count == 1 ? indepList[0] : Symbolic.Simplify(new SymExpr.Mul(indepList));
        dep = depList.Count == 0 ? Symbolic.One : depList.Count == 1 ? depList[0] : Symbolic.Simplify(new SymExpr.Mul(depList));
    }

    // --------------------------------------------------------- Romberg numerical quadrature

    public static double Romberg(Func<double, double> f, double a, double b, int maxOrder = 14)
    {
        if (a == b) return 0;
        double sign = 1;
        if (a > b) { (a, b) = (b, a); sign = -1; }

        var r = new double[maxOrder + 1, maxOrder + 1];
        double h = b - a;
        r[0, 0] = 0.5 * h * (f(a) + f(b));

        for (int k = 1; k <= maxOrder; k++)
        {
            h *= 0.5;
            long count = 1L << (k - 1);
            double sum = 0;
            for (long i = 1; i <= count; i++)
                sum += f(a + (2 * i - 1) * h);
            r[k, 0] = 0.5 * r[k - 1, 0] + h * sum;

            double factor = 4.0;
            for (int j = 1; j <= k; j++)
            {
                r[k, j] = r[k, j - 1] + (r[k, j - 1] - r[k - 1, j - 1]) / (factor - 1.0);
                factor *= 4.0;
            }

            if (k >= 4 && double.IsFinite(r[k, k]) &&
                Math.Abs(r[k, k] - r[k - 1, k - 1]) <= 1e-12 * Math.Max(1.0, Math.Abs(r[k, k])))
                return sign * r[k, k];
        }

        return sign * r[maxOrder, maxOrder];
    }

    // --------------------------------------------------------- special functions

    /// <summary>Hyperbolic sine integral Shi(x) = ∫_0^x sinh(t)/t dt.</summary>
    public static double Shi(double x)
    {
        if (x == 0) return 0;
        double x2 = x * x;
        double term = x;
        double sum = term;
        for (int k = 1; k <= 100; k++)
        {
            term *= x2 / ((2 * k) * (2 * k + 1));
            double delta = term / (2 * k + 1);
            sum += delta;
            if (Math.Abs(delta) < 1e-16 * Math.Max(1.0, Math.Abs(sum))) break;
        }
        return sum;
    }

    /// <summary>Normalized Fresnel sine integral S(x) = ∫_0^x sin(π/2 * t^2) dt.</summary>
    public static double FresnelS(double x)
    {
        if (x == 0) return 0;
        double sign = x < 0 ? -1 : 1;
        double ax = Math.Abs(x);
        // Subdivide [0, ax] so each panel has at most ~0.5 oscillation, then apply Romberg per panel.
        int panels = Math.Max(4, (int)Math.Ceiling(ax * ax));
        double total = 0;
        for (int i = 0; i < panels; i++)
        {
            double lo = ax * i / panels;
            double hi = ax * (i + 1) / panels;
            total += Romberg(t => Math.Sin(0.5 * Math.PI * t * t), lo, hi, maxOrder: 8);
        }
        return sign * total;
    }

    /// <summary>Lower incomplete gamma function γ(s, x) = ∫_0^x t^(s-1) e^(-t) dt.</summary>
    public static double GammaInc(double s, double x)
    {
        if (s <= 0) throw new EvalException("gammainc requires s > 0");
        if (x < 0) throw new EvalException("gammainc requires x >= 0");
        if (x == 0) return 0;

        if (x < s + 1.0)
        {
            double term = 1.0 / s;
            double sum = term;
            bool converged = false;
            for (int k = 1; k <= 500; k++)
            {
                term *= x / (s + k);
                sum += term;
                if (Math.Abs(term) <= 1e-16 * Math.Abs(sum)) { converged = true; break; }
            }
            if (!converged || !double.IsFinite(sum))
                throw new EvalException("gammainc series did not converge");
            return Math.Exp(s * Math.Log(x) - x + Math.Log(sum));
        }

        // Upper-tail Lentz continued fraction for Q(s, x) = Γ(s, x) / Γ(s), then γ(s, x) = Γ(s) * (1 - Q(s, x))
        const double fpMin = 1e-30;
        double b = x + 1.0 - s;
        double c = 1.0 / fpMin;
        double d = 1.0 / b;
        double h = d;
        bool cfConverged = false;
        for (int i = 1; i <= 500; i++)
        {
            double an = -i * (i - s);
            b += 2.0;
            d = an * d + b; if (Math.Abs(d) < fpMin) d = fpMin;
            c = b + an / c; if (Math.Abs(c) < fpMin) c = fpMin;
            d = 1.0 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1.0) < 1e-15) { cfConverged = true; break; }
        }
        if (!cfConverged || !double.IsFinite(h))
            throw new EvalException("gammainc continued fraction did not converge");
        double logGammaS = LogGamma(s);
        double q = Math.Exp(-x + s * Math.Log(x) - logGammaS) * h;
        return Math.Exp(logGammaS) * (1.0 - q);
    }

    /// <summary>Regularized incomplete beta function I_z(a, b) = B(z; a, b) / B(a, b) for real or complex z.</summary>
    public static Value BetaInc(ComplexInterval zIn, double a, double b)
    {
        if (a <= 0 || b <= 0) throw new EvalException("betainc requires a > 0 and b > 0");
        var z = new Complex(zIn.Re, zIn.Im);
        double logBeta = LogGamma(a) + LogGamma(b) - LogGamma(a + b);

        Complex result;
        if (z == Complex.Zero) result = Complex.Zero;
        else if (z == Complex.One) result = Complex.One;
        else
        {
            var integral = ComplexGaussLegendre(u =>
            {
                double ua = Math.Pow(u, a - 1.0);
                var oneMinusUz = Complex.Pow(Complex.One - u * z, b - 1.0);
                return ua * oneMinusUz;
            }, 0.0, 1.0, panels: 32);
            result = Complex.Exp(a * Complex.Log(z) - logBeta) * integral;
        }

        if (zIn.HasError)
        {
            if ((z == Complex.Zero && a < 1) || (z == Complex.One && b < 1))
                throw new EvalException("betainc derivative is singular at this point");
            var deriv = Complex.Pow(z, a - 1.0) * Complex.Pow(Complex.One - z, b - 1.0) * Math.Exp(-logBeta);
            int mode = Ops.IntervalMode;
            double reErr = mode == 2
                ? Math.Abs(deriv.Real) * zIn.ReErr + Math.Abs(deriv.Imaginary) * zIn.ImErr
                : Math.Sqrt(Math.Pow(deriv.Real * zIn.ReErr, 2) + Math.Pow(deriv.Imaginary * zIn.ImErr, 2));
            double imErr = mode == 2
                ? Math.Abs(deriv.Imaginary) * zIn.ReErr + Math.Abs(deriv.Real) * zIn.ImErr
                : Math.Sqrt(Math.Pow(deriv.Imaginary * zIn.ReErr, 2) + Math.Pow(deriv.Real * zIn.ImErr, 2));
            return Value.Complex(ComplexInterval.FromComplex(result, reErr, imErr));
        }

        if (!zIn.IsComplex && Math.Abs(result.Imaginary) < 1e-12 * Math.Max(1.0, Math.Abs(result.Real)))
            return new Value(result.Real);
        return Value.Complex(new ComplexInterval(result.Real, result.Imaginary));
    }

    private static readonly double[] GlNodes =
    [
        -0.96028985649753623168, -0.79666647741362673959, -0.52553240991632898582, -0.18343464249564980494,
         0.18343464249564980494,  0.52553240991632898582,  0.79666647741362673959,  0.96028985649753623168
    ];

    private static readonly double[] GlWeights =
    [
        0.10122853629037625915, 0.22238103445337447054, 0.31370664587788728734, 0.36268378337836198297,
        0.36268378337836198297, 0.31370664587788728734, 0.22238103445337447054, 0.10122853629037625915
    ];

    private static Complex ComplexGaussLegendre(Func<double, Complex> f, double a, double b, int panels)
    {
        Complex sum = Complex.Zero;
        double step = (b - a) / panels;
        for (int p = 0; p < panels; p++)
        {
            double lo = a + p * step;
            double mid = lo + 0.5 * step;
            double half = 0.5 * step;
            for (int i = 0; i < GlNodes.Length; i++)
                sum += GlWeights[i] * f(mid + half * GlNodes[i]);
            
        }
        return sum * (0.5 * step);
    }

    private static double LogGamma(double z)
    {
        double[] p =
        [
            0.99999999999980993, 676.5203681218851, -1259.1392167224028,
            771.32342877765313, -176.61502916214059, 12.507343278686905,
            -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7
        ];
        if (z < 0.5)
            return Math.Log(Math.PI / Math.Sin(Math.PI * z)) - LogGamma(1 - z);
        z -= 1;
        double x = p[0];
        for (int i = 1; i < p.Length; i++) x += p[i] / (z + i);
        double t = z + 7.5;
        return 0.5 * Math.Log(2 * Math.PI) + (z + 0.5) * Math.Log(t) - t + Math.Log(x);
    }

    // --------------------------------------------------------- helpers

    private static bool ContainsVar(SymExpr e, string v) => e switch
    {
        SymExpr.Var vr => vr.Name == v,
        SymExpr.Add a => a.Terms.Any(t => ContainsVar(t, v)),
        SymExpr.Mul m => m.Factors.Any(f => ContainsVar(f, v)),
        SymExpr.Pow p => ContainsVar(p.Base, v) || ContainsVar(p.Exp, v),
        SymExpr.Func fn => fn.Args.Any(a => ContainsVar(a, v)),
        _ => false
    };

    private static HashSet<string> CollectVarsExcept(SymExpr e, string exclude)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        Walk(e);
        return set;
        void Walk(SymExpr node)
        {
            switch (node)
            {
                case SymExpr.Var vr when vr.Name != exclude && vr.Name is not ("pi" or "e"): set.Add(vr.Name); break;
                case SymExpr.Add a: foreach (var t in a.Terms) Walk(t); break;
                case SymExpr.Mul m: foreach (var f in m.Factors) Walk(f); break;
                case SymExpr.Pow p: Walk(p.Base); Walk(p.Exp); break;
                case SymExpr.Func fn: foreach (var a in fn.Args) Walk(a); break;
            }
        }
    }

    private static double EvalDouble(SymExpr expr, string v, double val) => expr switch
    {
        SymExpr.Num n => n.Value.ToDouble(),
        SymExpr.Var vr => vr.Name == v ? val : vr.Name == "pi" ? Math.PI : vr.Name == "e" ? Math.E : double.NaN,
        SymExpr.Add a => a.Terms.Sum(t => EvalDouble(t, v, val)),
        SymExpr.Mul m => m.Factors.Aggregate(1.0, (acc, f) => acc * EvalDouble(f, v, val)),
        SymExpr.Pow p => Math.Pow(EvalDouble(p.Base, v, val), EvalDouble(p.Exp, v, val)),
        SymExpr.Func { Name: "sqrt", Args.Count: 1 } f => Math.Sqrt(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "cbrt", Args.Count: 1 } f => Math.Cbrt(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "sin", Args.Count: 1 } f => Math.Sin(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "cos", Args.Count: 1 } f => Math.Cos(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "tan", Args.Count: 1 } f => Math.Tan(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "sinh", Args.Count: 1 } f => Math.Sinh(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "cosh", Args.Count: 1 } f => Math.Cosh(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "tanh", Args.Count: 1 } f => Math.Tanh(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "ln", Args.Count: 1 } f => Math.Log(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "exp", Args.Count: 1 } f => Math.Exp(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "abs", Args.Count: 1 } f => Math.Abs(EvalDouble(f.Args[0], v, val)),
        SymExpr.Func { Name: "ei", Args.Count: 1 } f => ComplexInterval.Ei(ComplexInterval.FromReal(EvalDouble(f.Args[0], v, val)), 1).Re,
        SymExpr.Func { Name: "shi", Args.Count: 1 } f => Shi(EvalDouble(f.Args[0], v, val)),
        _ => double.NaN
    };
}
