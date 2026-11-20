using System.Numerics;

namespace Clay.Engine;

/// <summary>
/// General symbolic and numeric equation solver extending Symbolic.SolveEquation with:
/// Cardano cubics, high-degree real root finding, fractional-power substitution,
/// exponential & Lambert W equations, trigonometric equations, and Newton/Secant solvers.
/// </summary>
public static class Solver
{
    private static readonly SymExpr PiSym = Symbolic.Var("pi");
    private static readonly SymExpr ESym = Symbolic.Var("e");
    private static readonly SymExpr NSym = Symbolic.Var("n");

    public static SymExpr Solve(SymExpr lhs, SymExpr rhs, bool assumePositive = false)
    {
        var diff = Symbolic.Simplify(new SymExpr.Add([lhs, Symbolic.Negate(rhs, assumePositive)]), assumePositive);
        var vars = CollectVars(diff);
        if (vars.Count != 1)
            throw new EvalException("Equations must have a single unknown variable");
        string v = vars[0];

        // 1. Polynomial equations (rational roots, quadratic, Cardano cubic, numeric fallback)
        if (Symbolic.TryToPoly(diff, out var poly))
            return SolvePolynomial(poly, v, assumePositive);

        // 2. Fractional-power substitution: e.g. x^(1/3) + x^(2/3) = 3
        if (TrySolveFractionalPower(diff, v, assumePositive, out var fracSol))
            return fracSol;

        // 3. Exponential and Lambert W equations
        if (TrySolveExpAndLambertW(lhs, rhs, diff, v, assumePositive, out var expSol))
            return expSol;

        // 4. Trigonometric equations
        if (TrySolveTrig(diff, v, assumePositive, out var trigSol))
            return trigSol;

        // 5. Generic numeric real root fallback
        var numRoots = FindRealRootsNumeric(x => EvalAt(diff, v, x));
        if (numRoots.Count > 0)
            return new SymExpr.Solutions(v, numRoots.Select(r => (SymExpr)new SymExpr.Num(ExactNumber.FromDouble(r))).ToList());

        throw new EvalException("Can't solve this equation");
    }

    // --------------------------------------------------------- 1. Polynomial (exact + Cardano + numeric)

    private static SymExpr SolvePolynomial(Symbolic.Poly poly, string v, bool assumePositive)
    {
        int deg = poly.Degree(v);
        if (deg <= 0) throw new EvalException("No variable to solve for");

        // Try exact rational-root / linear / quadratic solver first
        var coeffs = new ExactNumber[deg + 1];
        BigInteger lcm = BigInteger.One;
        bool allExact = true;
        for (int k = 0; k <= deg; k++)
        {
            var ck = poly.Coeff(v, k);
            if (!ck.TryGetConstant(out var val) || !val.IsExact) { allExact = false; break; }
            coeffs[k] = val;
            lcm = lcm / BigInteger.GreatestCommonDivisor(lcm, val.Den) * val.Den;
        }

        if (allExact)
        {
            var intCoeffs = new BigInteger[deg + 1];
            for (int k = 0; k <= deg; k++)
                intCoeffs[k] = coeffs[k].Num * (lcm / coeffs[k].Den);
            if (intCoeffs[deg] < 0)
                for (int k = 0; k <= deg; k++) intCoeffs[k] = -intCoeffs[k];

            // First try Symbolic.SolveEquation if deg <= 2 or all roots deflate to <= 2
            var (rationalRoots, rem) = DeflateRationalRoots(intCoeffs);
            int remDeg = rem.Count - 1;

            if (remDeg == 0)
                return BuildSolutions(v, rationalRoots, assumePositive);

            if (remDeg <= 2)
            {
                var all = new List<SymExpr>(rationalRoots);
                all.AddRange(SolveDegree1Or2(rem));
                return BuildSolutions(v, all, assumePositive);
            }

            // Cardano formula for irreducible cubic: a*x^3 + b*x^2 + c*x + d = 0
            if (remDeg == 3 && TrySolveCubicCardano(rem, out var cubicRoots))
            {
                var all = new List<SymExpr>(rationalRoots);
                all.AddRange(cubicRoots);
                return BuildSolutions(v, all, assumePositive);
            }

            // Numeric fallback for remaining higher-degree polynomial
            var remDouble = rem.Select(x => (double)x).ToArray();
            var numRoots = FindRealRootsNumeric(x => EvalPoly(remDouble, x));
            var combined = new List<SymExpr>(rationalRoots);
            foreach (double r in numRoots)
                combined.Add(new SymExpr.Num(ExactNumber.FromDouble(r)));
            if (combined.Count > 0)
                return BuildSolutions(v, combined, assumePositive);
        }

        // General numeric polynomial fallback
        var dblCoeffs = new double[deg + 1];
        for (int k = 0; k <= deg; k++)
            dblCoeffs[k] = poly.Coeff(v, k).TryGetConstant(out var c) ? c.ToDouble() : 0;
        var roots = FindRealRootsNumeric(x => EvalPoly(dblCoeffs, x));
        if (roots.Count == 0) throw new EvalException("No real solutions");
        return BuildSolutions(v, roots.Select(r => (SymExpr)new SymExpr.Num(ExactNumber.FromDouble(r))).ToList(), assumePositive);
    }

    private static (List<SymExpr> Roots, List<BigInteger> Remainder) DeflateRationalRoots(BigInteger[] c)
    {
        var roots = new List<SymExpr>();
        var cur = c.ToList();
        while (cur.Count > 1 && cur[^1].IsZero) cur.RemoveAt(cur.Count - 1);
        while (cur.Count > 1 && cur[0].IsZero)
        {
            roots.Add(Symbolic.Zero);
            cur.RemoveAt(0);
        }
        while (cur.Count - 1 >= 3)
        {
            if (!TryFindRationalRoot(cur, out var p, out var q)) break;
            roots.Add(new SymExpr.Num(ExactNumber.FromRational(p, q)));
            cur = SyntheticDivide(cur, p, q);
        }
        return (roots, cur);
    }

    private static List<SymExpr> SolveDegree1Or2(List<BigInteger> cur)
    {
        var roots = new List<SymExpr>();
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
                roots.Add(new SymExpr.Num(ExactNumber.FromRational(-b, 2 * a)));
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
                    var halfB = ExactNumber.FromRational(-b, 2 * a);
                    var radCoeff = ExactNumber.FromRational(k, 2 * a);
                    var sqrtRad = new SymExpr.Func("sqrt", [new SymExpr.Num(ExactNumber.FromBigInteger(rad))]);
                    var radPart = radCoeff == ExactNumber.One
                        ? (SymExpr)sqrtRad
                        : new SymExpr.Mul([new SymExpr.Num(radCoeff), sqrtRad]);
                    roots.Add(Symbolic.Add(new SymExpr.Num(halfB), radPart));
                    roots.Add(Symbolic.Add(new SymExpr.Num(halfB), radPart, -1));
                }
            }
        }
        return roots;
    }

    private static bool TrySolveCubicCardano(List<BigInteger> c, out List<SymExpr> roots)
    {
        roots = [];
        // a*x^3 + b*x^2 + cc*x + d = 0
        var a = ExactNumber.FromBigInteger(c[3]);
        var b = ExactNumber.FromBigInteger(c[2]);
        var cc = ExactNumber.FromBigInteger(c[1]);
        var d = ExactNumber.FromBigInteger(c[0]);

        var three = ExactNumber.FromLong(3);
        var two = ExactNumber.FromLong(2);
        var nine = ExactNumber.FromLong(9);
        var twentySeven = ExactNumber.FromLong(27);

        // Depressed cubic t^3 + p*t + q = 0 with x = t - b/(3a)
        var p = (three * a * cc - b * b) / (three * a * a);
        var q = (two * b * b * b - nine * a * b * cc + twentySeven * a * a * d) / (twentySeven * a * a * a);
        var halfQ = q / two;
        var thirdP = p / three;
        var disc = halfQ * halfQ + thirdP * thirdP * thirdP;

        if (disc.Sign > 0)
        {
            // Single real root: t = cbrt(-q/2 + sqrt(disc)) + cbrt(-q/2 - sqrt(disc))
            var sqrtDisc = Symbolic.Call("sqrt", [new SymExpr.Num(disc)]);
            var u = new SymExpr.Func("cbrt", [Symbolic.Add(new SymExpr.Num(-halfQ), sqrtDisc)]);
            var w = new SymExpr.Func("cbrt", [Symbolic.Add(new SymExpr.Num(-halfQ), sqrtDisc, -1)]);
            var shift = new SymExpr.Num(-b / (three * a));
            var root = Symbolic.Add(Symbolic.Add(u, w), shift);
            roots.Add(root);
            return true;
        }
        return false;
    }

    // --------------------------------------------------------- 2. Fractional-power substitution

    private static bool TrySolveFractionalPower(SymExpr diff, string v, bool assumePositive, out SymExpr solution)
    {
        solution = Symbolic.Zero;
        // Look for powers v^(p/q) with a common denominator q in [2..6]
        var dens = new HashSet<long>();
        CollectFractionalExponents(diff, v, dens);
        if (dens.Count == 0) return false;

        long lcm = 1;
        foreach (long d in dens)
            lcm = lcm / (long)BigInteger.GreatestCommonDivisor(lcm, d) * d;
        if (lcm < 2 || lcm > 6) return false;

        // Substitute v = u^lcm
        var uVar = Symbolic.Var("__u");
        var substituted = Substitute(diff, v, new SymExpr.Pow(uVar, Symbolic.FromLong(lcm)), assumePositive);
        if (!Symbolic.TryToPoly(substituted, out var uPoly)) return false;

        var uSol = SolvePolynomial(uPoly, "__u", assumePositive);
        if (uSol is not SymExpr.Solutions { Roots.Count: > 0 } uSols) return false;

        var xRoots = new List<SymExpr>();
        foreach (var ur in uSols.Roots)
        {
            double uv = Symbolic.EvalNumericApprox(ur);
            if (!double.IsFinite(uv) || uv < 0) continue; // principal real branch for fractional powers
            var xr = Symbolic.Expand(new SymExpr.Pow(ur, Symbolic.FromLong(lcm)), assumePositive);
            xRoots.Add(Symbolic.Simplify(xr, assumePositive));
        }
        if (xRoots.Count == 0) return false;
        solution = BuildSolutions(v, xRoots, assumePositive);
        return true;
    }

    private static void CollectFractionalExponents(SymExpr e, string v, HashSet<long> dens)
    {
        switch (e)
        {
            case SymExpr.Pow { Base: SymExpr.Var bv, Exp: SymExpr.Num en } when bv.Name == v && en.Value.IsExact && en.Value.Den > 1 && en.Value.Den <= 6:
                dens.Add((long)en.Value.Den);
                break;
            case SymExpr.Add a:
                foreach (var t in a.Terms) CollectFractionalExponents(t, v, dens);
                break;
            case SymExpr.Mul m:
                foreach (var f in m.Factors) CollectFractionalExponents(f, v, dens);
                break;
            case SymExpr.Pow p:
                CollectFractionalExponents(p.Base, v, dens);
                CollectFractionalExponents(p.Exp, v, dens);
                break;
            case SymExpr.Func fn:
                foreach (var a in fn.Args) CollectFractionalExponents(a, v, dens);
                break;
        }
    }

    // --------------------------------------------------------- 3. Exponential & Lambert W

    private static bool TrySolveExpAndLambertW(SymExpr lhs, SymExpr rhs, SymExpr diff, string v, bool assumePositive, out SymExpr solution)
    {
        solution = Symbolic.Zero;

        // Case 3a: b^(p*x + q) = c  (e.g. 5^x = 3)
        if (TryMatchPureExp(lhs, rhs, v, out var bExpr, out var pNum, out var qNum, out var cExpr) ||
            TryMatchPureExp(rhs, lhs, v, out bExpr, out pNum, out qNum, out cExpr))
        {
            // p*x + q = ln(c) / ln(b)
            var lnC = Symbolic.Call("ln", [cExpr], assumePositive);
            var lnB = Symbolic.Call("ln", [bExpr], assumePositive);
            var ratio = Symbolic.Div(lnC, lnB, assumePositive);
            var root = Symbolic.Div(Symbolic.Add(ratio, new SymExpr.Num(qNum), -1, assumePositive), new SymExpr.Num(pNum), assumePositive);
            solution = new SymExpr.Solutions(v, [root]);
            return true;
        }

        // Case 3b: x^(a*x) = c  (e.g. x^(-3x) = 2, x^(5x) = 5)
        if (TryMatchXToAX(lhs, rhs, v, out var aCoeff, out var rhsConst) ||
            TryMatchXToAX(rhs, lhs, v, out aCoeff, out rhsConst))
        {
            // a * x * ln(x) = ln(c) => ln(x) * e^(ln(x)) = ln(c) / a
            var z = Symbolic.Div(Symbolic.Call("ln", [rhsConst], assumePositive), new SymExpr.Num(aCoeff), assumePositive);
            double zVal = Symbolic.EvalNumericApprox(z);
            var roots = new List<SymExpr>();
            if (zVal >= -1.0 / Math.E - 1e-12)
            {
                roots.Add(new SymExpr.Pow(ESym, new SymExpr.Func("lambertw", [z])));
                if (zVal < 0)
                    roots.Add(new SymExpr.Pow(ESym, new SymExpr.Func("lambertw", [z, Symbolic.FromLong(-1)])));
                solution = new SymExpr.Solutions(v, roots);
                return true;
            }
        }

        // Case 3c: a*ln(x) + b*x + c = 0  (e.g. ln(x) + x = 3)
        if (TryMatchLnPlusLinear(diff, v, out var lnCoeff, out var linCoeff, out var constTerm))
        {
            // x = (a/b) * lambertw((b/a) * e^(-c/a))
            var ab = lnCoeff / linCoeff;
            var ba = linCoeff / lnCoeff;
            var expArg = -constTerm / lnCoeff;
            var ePow = expArg == ExactNumber.One ? ESym : new SymExpr.Pow(ESym, new SymExpr.Num(expArg));
            var wArg = ba == ExactNumber.One ? ePow : Symbolic.Mul(new SymExpr.Num(ba), ePow, assumePositive);
            var w = new SymExpr.Func("lambertw", [wArg]);
            var root = ab == ExactNumber.One ? (SymExpr)w : Symbolic.Mul(new SymExpr.Num(ab), w, assumePositive);
            solution = new SymExpr.Solutions(v, [root]);
            return true;
        }

        // Case 3d: A * B^(p*x + q) + C*x + D = 0  (e.g. 2^(3x) + 4x = 5)
        if (TryMatchExpPlusLinear(diff, v, out var A, out var B, out var p, out var q, out var C, out var D))
        {
            // x = -D/C - lambertw((p * ln(B) * A / C) * B^(q - p*D/C)) / (p * ln(B))
            var negDC = -D / C;
            var lnB = Symbolic.Call("ln", [new SymExpr.Num(B)], assumePositive);
            var pLnB = Symbolic.Mul(new SymExpr.Num(p), lnB, assumePositive);
            var expPow = q - p * D / C;
            var bPow = Symbolic.Pow(new SymExpr.Num(B), new SymExpr.Num(expPow), assumePositive);
            var wArg = Symbolic.Mul(Symbolic.Mul(new SymExpr.Num(p * A / C), bPow, assumePositive), lnB, assumePositive);
            var w = new SymExpr.Func("lambertw", [wArg]);
            var root = Symbolic.Add(new SymExpr.Num(negDC), Symbolic.Div(w, pLnB, assumePositive), -1, assumePositive);
            solution = new SymExpr.Solutions(v, [root]);
            return true;
        }

        return false;
    }

    private static bool TryMatchPureExp(SymExpr sideA, SymExpr sideB, string v,
        out SymExpr baseExpr, out ExactNumber p, out ExactNumber q, out SymExpr constSide)
    {
        baseExpr = Symbolic.Zero; p = ExactNumber.Zero; q = ExactNumber.Zero; constSide = Symbolic.Zero;
        if (CollectVars(sideB).Count != 0) return false;
        if (sideA is SymExpr.Pow pow && CollectVars(pow.Base).Count == 0 &&
            TryExtractLinear(pow.Exp, v, out p, out q) && !p.IsZero)
        {
            baseExpr = pow.Base;
            constSide = sideB;
            return true;
        }
        return false;
    }

    private static bool TryMatchXToAX(SymExpr sideA, SymExpr sideB, string v, out ExactNumber a, out SymExpr c)
    {
        a = ExactNumber.Zero; c = Symbolic.Zero;
        if (CollectVars(sideB).Count != 0) return false;
        if (sideA is SymExpr.Pow { Base: SymExpr.Var bv } pow && bv.Name == v &&
            TryExtractLinear(pow.Exp, v, out a, out var q) && !a.IsZero && q.IsZero)
        {
            c = sideB;
            return true;
        }
        return false;
    }

    private static bool TryMatchLnPlusLinear(SymExpr diff, string v,
        out ExactNumber lnCoeff, out ExactNumber linCoeff, out ExactNumber constTerm)
    {
        lnCoeff = ExactNumber.Zero; linCoeff = ExactNumber.Zero; constTerm = ExactNumber.Zero;
        var terms = diff is SymExpr.Add add ? add.Terms : [diff];
        foreach (var t in terms)
        {
            var (c, core) = SplitNumericCoeff(t);
            if (core == null) { constTerm += c; continue; }
            if (core is SymExpr.Var vr && vr.Name == v) { linCoeff += c; continue; }
            if (core is SymExpr.Func { Name: "ln", Args.Count: 1 } ln && ln.Args[0] is SymExpr.Var lv && lv.Name == v)
            {
                lnCoeff += c;
                continue;
            }
            return false;
        }
        return !lnCoeff.IsZero && !linCoeff.IsZero;
    }

    private static bool TryMatchExpPlusLinear(SymExpr diff, string v,
        out ExactNumber A, out ExactNumber B, out ExactNumber p, out ExactNumber q,
        out ExactNumber C, out ExactNumber D)
    {
        A = ExactNumber.Zero; B = ExactNumber.Zero; p = ExactNumber.Zero; q = ExactNumber.Zero;
        C = ExactNumber.Zero; D = ExactNumber.Zero;
        bool hasExp = false;
        var terms = diff is SymExpr.Add add ? add.Terms : [diff];
        foreach (var t in terms)
        {
            var (c, core) = SplitNumericCoeff(t);
            if (core == null) { D += c; continue; }
            if (core is SymExpr.Var vr && vr.Name == v) { C += c; continue; }
            if (core is SymExpr.Pow { Base: SymExpr.Num bn } pow &&
                TryExtractLinear(pow.Exp, v, out var ep, out var eq) && !ep.IsZero)
            {
                if (hasExp) return false;
                hasExp = true;
                A = c; B = bn.Value; p = ep; q = eq;
                continue;
            }
            return false;
        }
        return hasExp && !A.IsZero && !C.IsZero;
    }

    // --------------------------------------------------------- 4. Trigonometric equations

    private readonly record struct PeriodicSol(ExactNumber PeriodPi, ExactNumber OffsetPi);

    private static bool TrySolveTrig(SymExpr diff, string v, bool assumePositive, out SymExpr solution)
    {
        solution = Symbolic.Zero;

        // 4a. Single trig or linear combination a*sin(u) + b*cos(u) + c = 0 or a*tan(u) + c = 0
        if (TrySolveLinearTrig(diff, v, out var sols))
        {
            solution = BuildPeriodicSolutions(v, sols);
            return true;
        }

        // 4b. Polynomial in sin(u) or cos(u): e.g. sin(x)^2 - sin(x)^3 = 0
        if (TrySolveTrigPoly(diff, v, assumePositive, out var polySols))
        {
            solution = BuildPeriodicSolutions(v, polySols);
            return true;
        }

        // 4c. Double-angle sin(2u) + k*sin(u) = 0 or sin(2u) + k*cos(u) = 0
        if (TrySolveDoubleAngleTrig(diff, v, out var dblSols))
        {
            solution = BuildPeriodicSolutions(v, dblSols);
            return true;
        }

        return false;
    }

    private static bool TrySolveLinearTrig(SymExpr diff, string v, out List<PeriodicSol> sols)
    {
        sols = [];
        var terms = diff is SymExpr.Add add ? add.Terms : [diff];

        double sinA = 0, cosB = 0, tanA = 0, constC = 0;
        ExactNumber p = ExactNumber.Zero;
        ExactNumber qPi = ExactNumber.Zero;
        bool hasArg = false;

        foreach (var t in terms)
        {
            var (c, core) = SplitNumericCoeff(t);
            double cv = c.ToDouble();
            if (core == null) { constC += cv; continue; }
            if (core is SymExpr.Func { Name: "sin" or "cos" or "tan", Args.Count: 1 } fn &&
                TryExtractLinearPi(fn.Args[0], v, out var ap, out var aqPi) && !ap.IsZero)
            {
                if (!hasArg) { p = ap; qPi = aqPi; hasArg = true; }
                else if (ap != p || aqPi != qPi) return false;

                if (fn.Name == "sin") sinA += cv;
                else if (fn.Name == "cos") cosB += cv;
                else tanA += cv;
                continue;
            }
            return false;
        }

        if (!hasArg) return false;

        if (Math.Abs(tanA) > 1e-12)
        {
            if (Math.Abs(sinA) > 1e-12 || Math.Abs(cosB) > 1e-12) return false;
            double target = -constC / tanA;
            if (!TryRecognizeAtanOverPi(target, out var alphaPi)) return false;
            sols.Add(LinearUnwrap(p, qPi, ExactNumber.One, alphaPi));
            return true;
        }

        if (Math.Abs(sinA) > 1e-12 && Math.Abs(cosB) > 1e-12)
        {
            // a*sin(u) + b*cos(u) = -c => R*sin(u + phi) = -c
            double r = Math.Sqrt(sinA * sinA + cosB * cosB);
            double target = -constC / r;
            if (Math.Abs(target) > 1.0 + 1e-12) return false;
            if (!TryRecognizeAngleOverPi(Math.Atan2(cosB, sinA), out var phiPi)) return false;
            var shiftedQ = qPi + phiPi;
            sols.AddRange(SolveSinEquals(p, shiftedQ, target));
            return sols.Count > 0;
        }

        if (Math.Abs(sinA) > 1e-12)
        {
            sols.AddRange(SolveSinEquals(p, qPi, -constC / sinA));
            return sols.Count > 0;
        }

        if (Math.Abs(cosB) > 1e-12)
        {
            sols.AddRange(SolveCosEquals(p, qPi, -constC / cosB));
            return sols.Count > 0;
        }

        return false;
    }

    private static bool TrySolveTrigPoly(SymExpr diff, string v, bool assumePositive, out List<PeriodicSol> sols)
    {
        sols = [];
        // Find a single trig call sin(u) or cos(u)
        SymExpr.Func? trigCall = FindFirstTrig(diff);
        if (trigCall == null || !TryExtractLinearPi(trigCall.Args[0], v, out var p, out var qPi) || p.IsZero)
            return false;

        var uVar = Symbolic.Var("__t");
        var replaced = ReplaceExactSubexpr(diff, trigCall, uVar);
        if (!Symbolic.TryToPoly(replaced, out var poly)) return false;

        var uSol = SolvePolynomial(poly, "__t", assumePositive);
        if (uSol is not SymExpr.Solutions uSols) return false;

        foreach (var r in uSols.Roots)
        {
            double rv = Symbolic.EvalNumericApprox(r);
            if (!double.IsFinite(rv) || Math.Abs(rv) > 1.0 + 1e-12) continue;
            if (trigCall.Name == "sin") sols.AddRange(SolveSinEquals(p, qPi, rv));
            else if (trigCall.Name == "cos") sols.AddRange(SolveCosEquals(p, qPi, rv));
        }
        return sols.Count > 0;
    }

    private static bool TrySolveDoubleAngleTrig(SymExpr diff, string v, out List<PeriodicSol> sols)
    {
        sols = [];
        var terms = diff is SymExpr.Add { Terms.Count: 2 } add ? add.Terms : null;
        if (terms == null) return false;

        // Match c1*sin(2u) + c2*sin(u) = 0  or  c1*sin(2u) + c2*cos(u) = 0
        var (c0, f0) = SplitNumericCoeff(terms[0]);
        var (c1, f1) = SplitNumericCoeff(terms[1]);
        if (f0 is not SymExpr.Func fn0 || f1 is not SymExpr.Func fn1) return false;
        if (fn0.Args.Count != 1 || fn1.Args.Count != 1) return false;
        if (!TryExtractLinearPi(fn0.Args[0], v, out var p0, out var q0)) return false;
        if (!TryExtractLinearPi(fn1.Args[0], v, out var p1, out var q1)) return false;

        // Ensure fn0 is the double-angle sin(2u)
        if (fn1.Name == "sin" && p1 == ExactNumber.FromLong(2) * p0 && q1 == ExactNumber.FromLong(2) * q0)
        {
            (c0, c1) = (c1, c0);
            (fn0, fn1) = (fn1, fn0);
            (p0, p1) = (p1, p0);
            (q0, q1) = (q1, q0);
        }

        if (fn0.Name != "sin" || p0 != ExactNumber.FromLong(2) * p1 || q0 != ExactNumber.FromLong(2) * q1)
            return false;

        double ratio = -c1.ToDouble() / (2.0 * c0.ToDouble());
        if (fn1.Name == "sin")
        {
            // sin(u) * (2*c0*cos(u) + c1) = 0
            sols.AddRange(SolveSinEquals(p1, q1, 0));
            sols.AddRange(SolveCosEquals(p1, q1, ratio));
            return sols.Count > 0;
        }
        if (fn1.Name == "cos")
        {
            // cos(u) * (2*c0*sin(u) + c1) = 0
            sols.AddRange(SolveCosEquals(p1, q1, 0));
            sols.AddRange(SolveSinEquals(p1, q1, ratio));
            return sols.Count > 0;
        }
        return false;
    }

    private static List<PeriodicSol> SolveSinEquals(ExactNumber p, ExactNumber qPi, double val)
    {
        var list = new List<PeriodicSol>();
        if (Math.Abs(val) > 1.0 + 1e-12) return list;
        val = Math.Clamp(val, -1.0, 1.0);

        if (Math.Abs(val) < 1e-12)
        {
            list.Add(LinearUnwrap(p, qPi, ExactNumber.One, ExactNumber.Zero));
            return list;
        }
        if (Math.Abs(val - 1.0) < 1e-12)
        {
            list.Add(LinearUnwrap(p, qPi, ExactNumber.FromLong(2), ExactNumber.FromRational(1, 2)));
            return list;
        }
        if (Math.Abs(val + 1.0) < 1e-12)
        {
            list.Add(LinearUnwrap(p, qPi, ExactNumber.FromLong(2), ExactNumber.FromRational(-1, 2)));
            return list;
        }
        if (!TryRecognizeAngleOverPi(Math.Asin(val), out var alphaPi)) return list;
        list.Add(LinearUnwrap(p, qPi, ExactNumber.FromLong(2), ExactNumber.One - alphaPi));
        list.Add(LinearUnwrap(p, qPi, ExactNumber.FromLong(2), alphaPi));
        return list;
    }

    private static List<PeriodicSol> SolveCosEquals(ExactNumber p, ExactNumber qPi, double val)
    {
        var list = new List<PeriodicSol>();
        if (Math.Abs(val) > 1.0 + 1e-12) return list;
        val = Math.Clamp(val, -1.0, 1.0);

        if (Math.Abs(val) < 1e-12)
        {
            list.Add(LinearUnwrap(p, qPi, ExactNumber.One, ExactNumber.FromRational(1, 2)));
            return list;
        }
        if (Math.Abs(val - 1.0) < 1e-12)
        {
            list.Add(LinearUnwrap(p, qPi, ExactNumber.FromLong(2), ExactNumber.Zero));
            return list;
        }
        if (Math.Abs(val + 1.0) < 1e-12)
        {
            list.Add(LinearUnwrap(p, qPi, ExactNumber.FromLong(2), ExactNumber.One));
            return list;
        }
        if (!TryRecognizeAngleOverPi(Math.Acos(val), out var alphaPi)) return list;
        list.Add(LinearUnwrap(p, qPi, ExactNumber.FromLong(2), alphaPi));
        list.Add(LinearUnwrap(p, qPi, ExactNumber.FromLong(2), -alphaPi));
        return list;
    }

    private static PeriodicSol LinearUnwrap(ExactNumber p, ExactNumber qPi, ExactNumber periodPi, ExactNumber offsetPi)
    {
        var absP = p.Sign < 0 ? -p : p;
        var newPeriod = periodPi / absP;
        var newOffset = (offsetPi - qPi) / p;
        // Normalize offset into (-period/2, period/2] or [0, period)
        if (newPeriod.IsExact && newOffset.IsExact)
        {
            double ratio = (newOffset / newPeriod).ToDouble();
            long shift = (long)Math.Floor(ratio + 0.5);
            newOffset -= ExactNumber.FromLong(shift) * newPeriod;
        }
        return new PeriodicSol(newPeriod, newOffset);
    }

    private static SymExpr BuildPeriodicSolutions(string v, List<PeriodicSol> sols)
    {
        var exprs = new List<SymExpr>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sols)
        {
            var periodTerm = Symbolic.Mul(Symbolic.Mul(new SymExpr.Num(s.PeriodPi), PiSym), NSym);
            SymExpr root = s.OffsetPi.IsZero
                ? periodTerm
                : Symbolic.Add(periodTerm, Symbolic.Mul(new SymExpr.Num(s.OffsetPi), PiSym));
            if (seen.Add(Symbolic.CanonicalKey(root)))
                exprs.Add(root);
        }
        return new SymExpr.Solutions(v, exprs);
    }

    private static bool TryRecognizeAtanOverPi(double val, out ExactNumber fracPi) =>
        TryRecognizeAngleOverPi(Math.Atan(val), out fracPi);

    private static bool TryRecognizeAngleOverPi(double radians, out ExactNumber fracPi)
    {
        fracPi = ExactNumber.Zero;
        double ratio = radians / Math.PI;
        int[] dens = [1, 2, 3, 4, 6, 8, 12, 18, 24, 36];
        foreach (int d in dens)
        {
            double scaled = ratio * d;
            long num = (long)Math.Round(scaled);
            if (Math.Abs(scaled - num) < 1e-9)
            {
                fracPi = ExactNumber.FromRational(num, d);
                return true;
            }
        }
        return false;
    }

    private static bool TryExtractLinearPi(SymExpr expr, string v, out ExactNumber p, out ExactNumber qPi)
    {
        p = ExactNumber.Zero;
        qPi = ExactNumber.Zero;
        var terms = expr is SymExpr.Add add ? add.Terms : [expr];
        foreach (var t in terms)
        {
            var (c, core) = SplitNumericCoeff(t);
            if (core == null)
            {
                if (c.IsZero) continue;
                if (!TryRecognizeAngleOverPi(c.ToDouble(), out var qp)) return false;
                qPi += qp;
            }
            else if (core is SymExpr.Var vr && vr.Name == v)
            {
                p += c;
            }
            else return false;
        }
        return true;
    }

    // --------------------------------------------------------- 5. Numeric root finders (Newton & Secant)

    public static Value NewtonSolve(Func<Value, Value> f, Value x0)
    {
        var x = x0.ToComplexInterval();
        var z = new Complex(x.Re, x.Im);
        var f0 = f(ToValue(z)).ToComplexInterval();
        bool isComplex = x.IsComplex || f0.IsComplex;

        // If the target equation is complex (e.g. Ei(x) = 3i) and initial guess is real,
        // nudge slightly off the real axis so complex derivatives flow cleanly into C.
        if (isComplex && z.Imaginary == 0)
            z = new Complex(z.Real, 0.5);

        for (int iter = 0; iter < 100; iter++)
        {
            var fz = ToComplex(f(ToValue(z)));
            if (fz.Magnitude < 1e-13)
                return ToValue(z, forceReal: !isComplex);

            double h = Math.Max(1e-7, z.Magnitude * 1e-7);
            var fp = ToComplex(f(ToValue(z + h)));
            var fm = ToComplex(f(ToValue(z - h)));
            var df = (fp - fm) / (2 * h);
            if (df.Magnitude < 1e-18) break;

            var step = fz / df;
            z -= step;
            if (step.Magnitude < 1e-13 * Math.Max(1.0, z.Magnitude))
                return ToValue(z, forceReal: !isComplex);
        }

        throw new EvalException("newtonsolve did not converge");
    }

    public static Value SecantSolve(Func<Value, Value> f, Value x0, Value x1)
    {
        var c0 = x0.ToComplexInterval();
        var c1 = x1.ToComplexInterval();
        var z0 = new Complex(c0.Re, c0.Im);
        var z1 = new Complex(c1.Re, c1.Im);
        var f0 = ToComplex(f(ToValue(z0)));
        var f1 = ToComplex(f(ToValue(z1)));
        bool isComplex = c0.IsComplex || c1.IsComplex || f0.Imaginary != 0 || f1.Imaginary != 0;

        for (int iter = 0; iter < 100; iter++)
        {
            if (f1.Magnitude < 1e-13)
                return ToValue(z1, forceReal: !isComplex);

            var denom = f1 - f0;
            if (denom.Magnitude < 1e-18) break;

            var z2 = z1 - f1 * (z1 - z0) / denom;
            z0 = z1; f0 = f1;
            z1 = z2; f1 = ToComplex(f(ToValue(z1)));
            if ((z1 - z0).Magnitude < 1e-13 * Math.Max(1.0, z1.Magnitude))
                return ToValue(z1, forceReal: !isComplex);
        }

        throw new EvalException("secantsolve did not converge");
    }

    private static Complex ToComplex(Value v)
    {
        var ci = v.ToComplexInterval();
        return new Complex(ci.Re, ci.Im);
    }

    private static Value ToValue(Complex c, bool forceReal = false) =>
        forceReal || Math.Abs(c.Imaginary) < 1e-12
            ? new Value(c.Real)
            : Value.Complex(new ComplexInterval(c.Real, c.Imaginary));

    // --------------------------------------------------------- helpers

    private static List<double> FindRealRootsNumeric(Func<double, double> f)
    {
        var roots = new List<double>();
        // Scan brackets across [-50, 50] with finer resolution near 0
        var grid = new List<double>();
        for (double x = -50; x < -5; x += 0.5) grid.Add(x);
        for (double x = -5; x <= 5; x += 0.05) grid.Add(x);
        for (double x = 5.5; x <= 50; x += 0.5) grid.Add(x);

        double prevX = grid[0], prevF = f(prevX);
        for (int i = 1; i < grid.Count; i++)
        {
            double curX = grid[i], curF = f(curX);
            if (!double.IsFinite(prevF) || !double.IsFinite(curF))
            {
                prevX = curX; prevF = curF;
                continue;
            }
            if (Math.Abs(curF) < 1e-12)
            {
                AddUniqueRoot(roots, curX);
            }
            else if (prevF * curF < 0)
            {
                double lo = prevX, hi = curX, fLo = prevF;
                for (int iter = 0; iter < 80; iter++)
                {
                    double mid = 0.5 * (lo + hi);
                    double fMid = f(mid);
                    if (Math.Abs(fMid) < 1e-14 || hi - lo < 1e-14) { lo = hi = mid; break; }
                    if (fLo * fMid <= 0) hi = mid;
                    else { lo = mid; fLo = fMid; }
                }
                AddUniqueRoot(roots, 0.5 * (lo + hi));
            }
            prevX = curX; prevF = curF;
        }
        roots.Sort((a, b) => b.CompareTo(a));
        return roots;
    }

    private static void AddUniqueRoot(List<double> roots, double r)
    {
        if (Math.Abs(r) < 1e-12) r = 0;
        if (roots.All(x => Math.Abs(x - r) > 1e-7))
            roots.Add(r);
    }

    private static double EvalPoly(double[] c, double x)
    {
        double acc = 0;
        for (int k = c.Length - 1; k >= 0; k--) acc = acc * x + c[k];
        return acc;
    }

    private static double EvalAt(SymExpr expr, string v, double val) => expr switch
    {
        SymExpr.Num n => n.Value.ToDouble(),
        SymExpr.Var vr => vr.Name == v ? val : vr.Name == "pi" ? Math.PI : vr.Name == "e" ? Math.E : double.NaN,
        SymExpr.Add a => a.Terms.Sum(t => EvalAt(t, v, val)),
        SymExpr.Mul m => m.Factors.Aggregate(1.0, (acc, f) => acc * EvalAt(f, v, val)),
        SymExpr.Pow p => Math.Pow(EvalAt(p.Base, v, val), EvalAt(p.Exp, v, val)),
        SymExpr.Func { Name: "sin", Args.Count: 1 } f => Math.Sin(EvalAt(f.Args[0], v, val)),
        SymExpr.Func { Name: "cos", Args.Count: 1 } f => Math.Cos(EvalAt(f.Args[0], v, val)),
        SymExpr.Func { Name: "tan", Args.Count: 1 } f => Math.Tan(EvalAt(f.Args[0], v, val)),
        SymExpr.Func { Name: "ln", Args.Count: 1 } f => Math.Log(EvalAt(f.Args[0], v, val)),
        SymExpr.Func { Name: "sqrt", Args.Count: 1 } f => Math.Sqrt(EvalAt(f.Args[0], v, val)),
        SymExpr.Func { Name: "cbrt", Args.Count: 1 } f => Math.Cbrt(EvalAt(f.Args[0], v, val)),
        _ => double.NaN
    };

    private static SymExpr BuildSolutions(string v, List<SymExpr> roots, bool assumePositive)
    {
        var unique = new List<SymExpr>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in roots)
        {
            var s = Symbolic.Simplify(r, assumePositive);
            if (seen.Add(Symbolic.CanonicalKey(s))) unique.Add(s);
        }
        unique.Sort((a, b) => Symbolic.EvalNumericApprox(b).CompareTo(Symbolic.EvalNumericApprox(a)));
        return new SymExpr.Solutions(v, unique);
    }

    private static List<string> CollectVars(SymExpr expr)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        Walk(expr);
        return set.ToList();

        void Walk(SymExpr e)
        {
            switch (e)
            {
                case SymExpr.Var vr when vr.Name is not ("pi" or "e"): set.Add(vr.Name); break;
                case SymExpr.Add a: foreach (var t in a.Terms) Walk(t); break;
                case SymExpr.Mul m: foreach (var f in m.Factors) Walk(f); break;
                case SymExpr.Pow p: Walk(p.Base); Walk(p.Exp); break;
                case SymExpr.Func fn: foreach (var a in fn.Args) Walk(a); break;
            }
        }
    }

    private static bool TryExtractLinear(SymExpr expr, string v, out ExactNumber p, out ExactNumber q)
    {
        p = ExactNumber.Zero; q = ExactNumber.Zero;
        if (!Symbolic.TryToPoly(expr, out var poly)) return false;
        if (poly.Variables().Any(name => name != v)) return false;
        if (poly.Degree(v) > 1) return false;
        if (!poly.Coeff(v, 1).TryGetConstant(out p)) return false;
        if (!poly.Coeff(v, 0).TryGetConstant(out q)) return false;
        return true;
    }

    private static (ExactNumber Coeff, SymExpr? Core) SplitNumericCoeff(SymExpr expr)
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

    private static SymExpr Substitute(SymExpr expr, string v, SymExpr replacement, bool assumePositive) => expr switch
    {
        SymExpr.Var vr when vr.Name == v => replacement,
        SymExpr.Add a => Symbolic.Simplify(new SymExpr.Add(a.Terms.Select(t => Substitute(t, v, replacement, assumePositive)).ToList()), assumePositive),
        SymExpr.Mul m => Symbolic.Simplify(new SymExpr.Mul(m.Factors.Select(f => Substitute(f, v, replacement, assumePositive)).ToList()), assumePositive),
        SymExpr.Pow p => Symbolic.Pow(Substitute(p.Base, v, replacement, assumePositive), Substitute(p.Exp, v, replacement, assumePositive), assumePositive),
        SymExpr.Func fn => Symbolic.Call(fn.Name, fn.Args.Select(a => Substitute(a, v, replacement, assumePositive)).ToList(), assumePositive),
        _ => expr
    };

    private static SymExpr.Func? FindFirstTrig(SymExpr expr) => expr switch
    {
        SymExpr.Func { Name: "sin" or "cos", Args.Count: 1 } fn => fn,
        SymExpr.Add a => a.Terms.Select(FindFirstTrig).FirstOrDefault(x => x != null),
        SymExpr.Mul m => m.Factors.Select(FindFirstTrig).FirstOrDefault(x => x != null),
        SymExpr.Pow p => FindFirstTrig(p.Base) ?? FindFirstTrig(p.Exp),
        _ => null
    };

    private static SymExpr ReplaceExactSubexpr(SymExpr expr, SymExpr target, SymExpr replacement)
    {
        if (Symbolic.CanonicalKey(expr) == Symbolic.CanonicalKey(target)) return replacement;
        return expr switch
        {
            SymExpr.Add a => new SymExpr.Add(a.Terms.Select(t => ReplaceExactSubexpr(t, target, replacement)).ToList()),
            SymExpr.Mul m => new SymExpr.Mul(m.Factors.Select(f => ReplaceExactSubexpr(f, target, replacement)).ToList()),
            SymExpr.Pow p => new SymExpr.Pow(ReplaceExactSubexpr(p.Base, target, replacement), ReplaceExactSubexpr(p.Exp, target, replacement)),
            SymExpr.Func fn => new SymExpr.Func(fn.Name, fn.Args.Select(a => ReplaceExactSubexpr(a, target, replacement)).ToList()),
            _ => expr
        };
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
        int deg = c.Count - 1;
        BigInteger sum = 0, pPow = 1;
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

    private static (BigInteger Factor, BigInteger Radicand) ExtractSquareFactor(BigInteger n)
    {
        BigInteger factor = 1, rem = n;
        for (long d = 2; d * d <= rem && d <= 10000; d++)
        {
            long d2 = d * d;
            while (rem % d2 == 0) { rem /= d2; factor *= d; }
        }
        return (factor, rem);
    }
}
