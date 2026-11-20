namespace Clay.Engine;

/// <summary>
/// Limit evaluator combining exact direct substitution, symbolic L'Hôpital's rule,
/// log-transform for indeterminate powers (1^∞, 0^0, ∞^0), overflow-safe log-space
/// evaluation for x -> ±∞, and Richardson extrapolation.
/// </summary>
public static class Limits
{
    public static Value Compute(SymExpr expr, double point, string varName = "x", int direction = 0)
    {
        var simplified = Symbolic.Simplify(expr, Ops.AssumePositive);

        // 1. Expressions with extra symbolic parameters (e.g. limit((1 - x^(1/z))/(1 - x^(1/y)), 1) -> y/z)
        var extraVars = CollectExtraVars(simplified, varName);
        if (extraVars.Count > 0)
            return ComputeSymbolic(simplified, point, varName);

        // 2. Finite limit: try direct evaluation first
        if (double.IsFinite(point))
        {
            double direct = EvalSafe(simplified, varName, point);
            if (double.IsFinite(direct))
            {
                // Verify continuity near point to avoid removable/jump traps
                double probe = EvalSafe(simplified, varName, point + 1e-7);
                if (double.IsFinite(probe) && Math.Abs(probe - direct) <= Math.Max(1e-4, Math.Abs(direct) * 1e-3))
                    return new Value(CleanZero(direct));
            }

            // Try symbolic L'Hôpital for 0/0 or ∞/∞ quotients (and log-transform for f^g)
            if (TrySymbolicLHopitalFinite(simplified, point, varName, out double lhopVal))
                return new Value(CleanZero(lhopVal));

            // Numerical approach with Richardson extrapolation
            double numVal = ApproachFinite(simplified, point, varName, direction);
            return new Value(CleanZero(numVal));
        }

        // 3. Infinite limit: x -> +∞ or x -> -∞
        bool posInf = point > 0;
        double infVal = ApproachInfinity(simplified, posInf, varName);
        return new Value(CleanZero(infVal));
    }

    // --------------------------------------------------------- symbolic parameter limits

    private static Value ComputeSymbolic(SymExpr expr, double point, string v)
    {
        if (!double.IsFinite(point))
            throw new EvalException("Symbolic limits at infinity are not supported");

        var pointExpr = point == Math.Truncate(point) && Math.Abs(point) < 1e15
            ? new SymExpr.Num(ExactNumber.FromLong((long)point))
            : new SymExpr.Num(ExactNumber.FromDouble(point));
        if (TrySplitQuotient(expr, out var num, out var den))
        {
            for (int step = 0; step < 5; step++)
            {
                var nSub = Substitute(num, v, pointExpr);
                var dSub = Substitute(den, v, pointExpr);
                bool nZero = IsSymZero(nSub);
                bool dZero = IsSymZero(dSub);
                if (!nZero && !dZero)
                    return Value.FromSym(Symbolic.Div(nSub, dSub, Ops.AssumePositive));
                if (nZero && !dZero)
                    return new Value(0);
                if (!nZero && dZero)
                    throw new EvalException("Limit is undefined");

                num = Calculus.Diff(num, v);
                den = Calculus.Diff(den, v);
            }
        }

        var direct = Substitute(expr, v, pointExpr);
        return Value.FromSym(direct);
    }

    // --------------------------------------------------------- symbolic L'Hôpital for finite numeric limits

    private static bool TrySymbolicLHopitalFinite(SymExpr expr, double point, string v, out double result)
    {
        result = double.NaN;

        // Indeterminate power f(x)^g(x) -> exp(limit(g(x) * ln(f(x))))
        if (expr is SymExpr.Pow pow)
        {
            var prod = Symbolic.Mul(pow.Exp, Symbolic.Call("ln", [pow.Base]));
            if (TrySymbolicLHopitalFinite(prod, point, v, out double expLim))
            {
                result = Math.Exp(expLim);
                return double.IsFinite(result) || double.IsInfinity(result);
            }
        }

        if (!TrySplitQuotient(expr, out var num, out var den))
            return false;

        try
        {
            var curNum = num;
            var curDen = den;
            for (int step = 0; step < 4; step++)
            {
                // Probe slightly off point if direct evaluation is NaN or infinite (e.g. ln(x) at 0+)
                double nv0 = EvalSafe(curNum, v, point);
                double dv0 = EvalSafe(curDen, v, point);
                bool directInfInf = double.IsInfinity(nv0) && double.IsInfinity(dv0);
                double nv = nv0, dv = dv0;
                if (!double.IsFinite(nv) || !double.IsFinite(dv))
                {
                    nv = EvalSafe(curNum, v, point + 1e-6);
                    dv = EvalSafe(curDen, v, point + 1e-6);
                }

                bool isZeroZero = Math.Abs(nv) < 1e-4 && Math.Abs(dv) < 1e-4;
                bool isInfInf = directInfInf || (Math.Abs(nv) > 1e5 && Math.Abs(dv) > 1e5) ||
                    (Math.Abs(nv) > 10 && Math.Abs(dv) > 10 &&
                     Math.Abs(EvalSafe(curNum, v, point + 1e-12)) > Math.Abs(nv) * 1.3 &&
                     Math.Abs(EvalSafe(curDen, v, point + 1e-12)) > Math.Abs(dv) * 1.3);
                if (step > 0 && !isZeroZero && !isInfInf && double.IsFinite(nv) && double.IsFinite(dv) && Math.Abs(dv) > 1e-12)
                {
                    result = nv / dv;
                    return true;
                }

                if (step > 0)
                {
                    // Even if still 0/0 right at point, probe the differentiated ratio via Richardson
                    var ratioExpr = Symbolic.Div(curNum, curDen);
                    double probe = ProbeOneSideFinite(ratioExpr, point, v, +1, out bool diverged, out int divSign);
                    if (!diverged && double.IsFinite(probe))
                    {
                        double probeNeg = ProbeOneSideFinite(ratioExpr, point, v, -1, out bool divNeg, out _);
                        if (double.IsNaN(probeNeg) || (!divNeg && Math.Abs(probe - probeNeg) <= Math.Max(1e-5, Math.Abs(probe) * 1e-4)))
                        {
                            result = probe;
                            return true;
                        }
                    }
                }

                if (!isZeroZero && !isInfInf) break;
                curNum = Calculus.Diff(curNum, v);
                curDen = Calculus.Diff(curDen, v);
            }
        }
        catch (EvalException)
        {
            return false;
        }

        return false;
    }

    // --------------------------------------------------------- numerical finite limit

    private static double ApproachFinite(SymExpr expr, double point, string v, int direction)
    {
        if (direction > 0)
            return ResolveOneSide(expr, point, v, +1);
        if (direction < 0)
            return ResolveOneSide(expr, point, v, -1);

        double right = ProbeOneSideFinite(expr, point, v, +1, out bool rightDiv, out int rightSign);
        double left = ProbeOneSideFinite(expr, point, v, -1, out bool leftDiv, out int leftSign);

        // Domain-boundary one-sided limits (e.g. sqrt(x) or ln(x) at 0)
        if (double.IsNaN(left) && !leftDiv)
            return rightDiv ? (rightSign > 0 ? double.PositiveInfinity : double.NegativeInfinity) : right;
        if (double.IsNaN(right) && !rightDiv)
            return leftDiv ? (leftSign > 0 ? double.PositiveInfinity : double.NegativeInfinity) : left;

        if (rightDiv && leftDiv)
        {
            if (rightSign == leftSign && rightSign != 0)
                return rightSign > 0 ? double.PositiveInfinity : double.NegativeInfinity;
            throw new EvalException("Limit is undefined");
        }
        if (rightDiv || leftDiv)
            throw new EvalException("Limit is undefined");

        if (double.IsFinite(right) && double.IsFinite(left))
        {
            double tol = Math.Max(1e-4, Math.Max(Math.Abs(right), Math.Abs(left)) * 1e-3);
            if (Math.Abs(right - left) <= tol)
                return 0.5 * (right + left);
            throw new EvalException("Limit is undefined");
        }

        throw new EvalException("Limit is undefined");
    }

    private static double ResolveOneSide(SymExpr expr, double point, string v, int side)
    {
        double val = ProbeOneSideFinite(expr, point, v, side, out bool div, out int divSign);
        if (div) return divSign > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        if (double.IsFinite(val)) return val;
        throw new EvalException("Limit is undefined");
    }

    private static double ProbeOneSideFinite(SymExpr expr, double point, string v, int side, out bool diverged, out int divSign)
    {
        diverged = false;
        divSign = 0;

        // Sample h across a geometric range; extend to 1e-14 for O(sqrt(h)) terms like (x - sqrt(x))/sqrt(x)
        var samples = new List<(double H, double Y)>();
        double[] hs = [1e-2, 5e-3, 2.5e-3, 1.25e-3, 6.25e-4, 3.125e-4, 1e-4, 1e-5, 1e-6, 1e-8, 1e-10, 1e-12, 1e-14];
        foreach (double h in hs)
        {
            double y = EvalSafe(expr, v, point + side * h);
            if (double.IsFinite(y)) samples.Add((h, y));
        }

        if (samples.Count < 3) return double.NaN;

        // Check divergence: magnitudes growing rapidly with consistent sign
        int n = samples.Count;
        var tail = samples.Skip(Math.Max(0, n - 4)).ToList();
        if (tail.All(s => Math.Abs(s.Y) > 1e4) &&
            Math.Abs(tail[^1].Y) > Math.Abs(tail[0].Y) * 5 &&
            tail.All(s => Math.Sign(s.Y) == Math.Sign(tail[^1].Y)))
        {
            diverged = true;
            divSign = Math.Sign(tail[^1].Y);
            return divSign > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        }

        // Apply Richardson extrapolation on the dyadic subset (h, h/2, h/4, h/8, h/16, h/32)
        double[] dyadicH = [1e-2, 5e-3, 2.5e-3, 1.25e-3, 6.25e-4, 3.125e-4];
        var dyadicY = new List<double>();
        foreach (double h in dyadicH)
        {
            double y = EvalSafe(expr, v, point + side * h);
            if (double.IsFinite(y)) dyadicY.Add(y);
            else break;
        }

        if (dyadicY.Count >= 4 && TryRichardsonExtrapolate(dyadicY, out double rich))
        {
            double refSample = dyadicY[^1];
            if (Math.Abs(rich - refSample) <= Math.Max(1e-2, Math.Abs(rich) * 1e-2))
                return rich;
        }

        // Fallback: pick the pair of consecutive fine samples with smallest delta
        double bestVal = samples[^1].Y;
        double bestDelta = double.PositiveInfinity;
        for (int i = 1; i < samples.Count; i++)
        {
            double d = Math.Abs(samples[i].Y - samples[i - 1].Y);
            if (d < bestDelta)
            {
                bestDelta = d;
                bestVal = samples[i].Y;
            }
        }
        return bestVal;
    }

    // --------------------------------------------------------- numerical infinite limit (x -> ±∞)

    private static double ApproachInfinity(SymExpr expr, bool posInf, string v)
    {
        // Fast exact leading-term asymptotic extraction for rational/radical expressions (no cancellation)
        if (TryAsymptoticInfinity(expr, v, posInf, out int asymSign, out double asymLogAbs, out double asymDeg))
        {
            if (Math.Abs(asymDeg) < 1e-9)
                return asymSign == 0 ? 0.0 : asymSign * Math.Exp(asymLogAbs);
            if (asymDeg < 0)
                return 0.0;
            if (asymDeg > 0 && asymSign != 0)
                return asymSign > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        }

        double sign = posInf ? 1.0 : -1.0;

        // Sample using log-space overflow-safe evaluator on a wide geometric ladder
        double[] xs = [1e2, 1e3, 1e4, 1e5, 1e6, 1e8, 1e10, 1e12, 1e14, 1e16];
        var samples = new List<(double X, double Y)>();
        foreach (double x in xs)
        {
            double y = EvalLogSafe(expr, v, sign * x);
            if (double.IsFinite(y)) samples.Add((x, y));
        }

        // Check log-magnitude across consecutive decades for genuine divergence (avoids false positives on large finite limits like e^16)
        var logSamples = new List<(int Sign, double LogAbs)>();
        foreach (double x in new[] { 1e3, 1e4, 1e5, 1e6 })
        {
            if (TryEvalLogMag(expr, v, sign * x, out int sgn, out double logAbs) && double.IsFinite(logAbs))
                logSamples.Add((sgn, logAbs));
        }

        if (logSamples.Count == 4 &&
            logSamples[^1].LogAbs > 15 &&
            logSamples[^1].LogAbs - logSamples[^2].LogAbs > 0.5 &&
            logSamples[^2].LogAbs - logSamples[^3].LogAbs > 0.5 &&
            logSamples.All(s => s.Sign == logSamples[^1].Sign && s.Sign != 0))
        {
            return logSamples[^1].Sign > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        }

        if (samples.Count >= 4)
        {
            var tail = samples.Skip(samples.Count - 4).ToList();
            if (tail.All(s => Math.Abs(s.Y) > 1e3) &&
                Math.Abs(tail[^1].Y) > Math.Abs(tail[^2].Y) * 2 &&
                Math.Abs(tail[^2].Y) > Math.Abs(tail[^3].Y) * 2 &&
                tail.All(s => Math.Sign(s.Y) == Math.Sign(tail[^1].Y)))
            {
                return tail[^1].Y > 0 ? double.PositiveInfinity : double.NegativeInfinity;
            }
        }

        // Check for rapid convergence to 0 in log-space (e.g. ((x^3-8)/(x^4+16))^10 -> 0)
        if (logSamples.Count == 4 &&
            logSamples[^1].LogAbs < -20 &&
            logSamples[^2].LogAbs - logSamples[^1].LogAbs > 0.5)
            return 0.0;

        if (samples.Count == 0)
            throw new EvalException("Limit is undefined");

        // Apply Richardson extrapolation in 1/x on a moderate dyadic ladder (avoids cancellation at huge x)
        double[] dyadicX = [1e3, 2e3, 4e3, 8e3, 1.6e4, 3.2e4];
        var dyadicY = new List<double>();
        foreach (double x in dyadicX)
        {
            double y = EvalLogSafe(expr, v, sign * x);
            if (double.IsFinite(y)) dyadicY.Add(y);
            else break;
        }

        if (dyadicY.Count >= 4 && TryRichardsonExtrapolate(dyadicY, out double rich))
        {
            double refSample = dyadicY[^1];
            if (Math.Abs(rich - refSample) <= Math.Max(1e-1, Math.Abs(rich) * 1e-1))
                return rich;
        }

        // Fallback: choose the finest sample with smallest consecutive delta (handles O(x^-1/2), O(x^-1/4))
        double finest = samples[^1].Y;
        double bestDelta = double.PositiveInfinity;
        for (int i = 1; i < samples.Count; i++)
        {
            double d = Math.Abs(samples[i].Y - samples[i - 1].Y);
            if (d < bestDelta)
            {
                bestDelta = d;
                finest = samples[i].Y;
            }
        }

        return finest;
    }

    /// <summary>
    /// Extracts the exact leading asymptotic term sign * exp(logAbs) * |x|^deg as x -> ±∞
    /// for algebraic and radical expressions. Returns false if leading terms cancel or non-algebraic functions appear.
    /// </summary>
    private static bool TryAsymptoticInfinity(SymExpr expr, string v, bool posInf, out int sign, out double logAbs, out double deg)
    {
        sign = 0; logAbs = double.NegativeInfinity; deg = 0;
        switch (expr)
        {
            case SymExpr.Num n:
            {
                double d = n.Value.ToDouble();
                if (!double.IsFinite(d)) return false;
                if (d == 0) { sign = 0; logAbs = double.NegativeInfinity; deg = 0; return true; }
                sign = d > 0 ? 1 : -1;
                logAbs = Math.Log(Math.Abs(d));
                deg = 0;
                return true;
            }
            case SymExpr.Var vr when vr.Name == v:
                sign = posInf ? 1 : -1;
                logAbs = 0;
                deg = 1;
                return true;
            case SymExpr.Var vr when vr.Name is "pi" or "e":
                sign = 1;
                logAbs = Math.Log(vr.Name == "pi" ? Math.PI : Math.E);
                deg = 0;
                return true;
            case SymExpr.Mul mul:
            {
                int s = 1; double la = 0, d = 0;
                foreach (var f in mul.Factors)
                {
                    if (!TryAsymptoticInfinity(f, v, posInf, out int fs, out double fla, out double fd)) return false;
                    if (fs == 0) { sign = 0; logAbs = double.NegativeInfinity; deg = 0; return true; }
                    s *= fs; la += fla; d += fd;
                }
                sign = s; logAbs = la; deg = d;
                return true;
            }
            case SymExpr.Pow { Exp: SymExpr.Num en } pow:
            {
                double e = en.Value.ToDouble();
                if (!double.IsFinite(e)) return false;
                if (!TryAsymptoticInfinity(pow.Base, v, posInf, out int bs, out double bla, out double bd)) return false;
                if (bs == 0)
                {
                    if (e > 0) { sign = 0; logAbs = double.NegativeInfinity; deg = 0; return true; }
                    return false;
                }
                if (bs < 0)
                {
                    if (Math.Abs(e - Math.Round(e)) > 1e-9) return false;
                    sign = ((long)Math.Round(e) & 1) == 0 ? 1 : -1;
                }
                else sign = 1;
                logAbs = e * bla;
                deg = e * bd;
                return true;
            }
            case SymExpr.Func { Name: "sqrt", Args.Count: 1 } sq:
            {
                if (!TryAsymptoticInfinity(sq.Args[0], v, posInf, out int bs, out double bla, out double bd)) return false;
                if (bs < 0) return false;
                if (bs == 0) { sign = 0; logAbs = double.NegativeInfinity; deg = 0; return true; }
                sign = 1; logAbs = 0.5 * bla; deg = 0.5 * bd;
                return true;
            }
            case SymExpr.Func { Name: "cbrt", Args.Count: 1 } cb:
            {
                if (!TryAsymptoticInfinity(cb.Args[0], v, posInf, out int bs, out double bla, out double bd)) return false;
                if (bs == 0) { sign = 0; logAbs = double.NegativeInfinity; deg = 0; return true; }
                sign = bs; logAbs = bla / 3.0; deg = bd / 3.0;
                return true;
            }
            case SymExpr.Func { Name: "root", Args.Count: 2 } rt when rt.Args[1] is SymExpr.Num rn:
            {
                double n = rn.Value.ToDouble();
                if (!double.IsFinite(n) || n == 0) return false;
                if (!TryAsymptoticInfinity(rt.Args[0], v, posInf, out int bs, out double bla, out double bd)) return false;
                if (bs == 0) { sign = 0; logAbs = double.NegativeInfinity; deg = 0; return true; }
                if (bs < 0 && (Math.Abs(n - Math.Round(n)) > 1e-9 || ((long)Math.Round(n) & 1) == 0)) return false;
                sign = bs; logAbs = bla / n; deg = bd / n;
                return true;
            }
            case SymExpr.Add add:
            {
                var terms = new List<(int Sign, double LogAbs, double Deg)>();
                double maxDeg = double.NegativeInfinity;
                foreach (var t in add.Terms)
                {
                    if (!TryAsymptoticInfinity(t, v, posInf, out int ts, out double tla, out double td)) return false;
                    if (ts == 0) continue;
                    terms.Add((ts, tla, td));
                    if (td > maxDeg) maxDeg = td;
                }
                if (terms.Count == 0) { sign = 0; logAbs = double.NegativeInfinity; deg = 0; return true; }
                var leading = terms.Where(t => Math.Abs(t.Deg - maxDeg) < 1e-9).ToList();
                if (leading.Count == 1)
                {
                    sign = leading[0].Sign; logAbs = leading[0].LogAbs; deg = leading[0].Deg;
                    return true;
                }
                // Sum leading coefficients carefully in log-scaled coordinates
                double maxLog = leading.Max(t => t.LogAbs);
                double scaledSum = leading.Sum(t => t.Sign * Math.Exp(t.LogAbs - maxLog));
                if (Math.Abs(scaledSum) < 1e-9) return false; // leading terms cancel -> let numerical solver resolve
                sign = scaledSum > 0 ? 1 : -1;
                logAbs = maxLog + Math.Log(Math.Abs(scaledSum));
                deg = maxDeg;
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryRichardsonExtrapolate(List<double> ys, out double value)
    {
        value = double.NaN;
        int n = ys.Count;
        var table = new double[n, n];
        for (int i = 0; i < n; i++) table[i, 0] = ys[i];
        double pow2 = 2.0;
        for (int j = 1; j < n; j++)
        {
            for (int i = j; i < n; i++)
                table[i, j] = (pow2 * table[i, j - 1] - table[i - 1, j - 1]) / (pow2 - 1.0);
            pow2 *= 2.0;
        }
        double best = table[n - 1, n - 1];
        double prev = table[n - 2, n - 2];
        if (!double.IsFinite(best) || !double.IsFinite(prev)) return false;
        if (Math.Abs(best - prev) > Math.Max(1e-7, Math.Abs(best) * 1e-7)) return false;
        value = best;
        return true;
    }

    // --------------------------------------------------------- overflow-safe evaluators

    private static double EvalLogSafe(SymExpr expr, string v, double val)
    {
        double direct = EvalSafe(expr, v, val);
        if (double.IsFinite(direct)) return direct;
        if (TryEvalLogMag(expr, v, val, out int sign, out double logAbs))
        {
            if (sign == 0) return 0;
            if (logAbs > 709) return sign > 0 ? double.PositiveInfinity : double.NegativeInfinity;
            if (logAbs < -745) return 0;
            return sign * Math.Exp(logAbs);
        }
        return double.NaN;
    }

    private static bool TryEvalLogMag(SymExpr expr, string v, double val, out int sign, out double logAbs)
    {
        sign = 0; logAbs = double.NegativeInfinity;
        switch (expr)
        {
            case SymExpr.Mul mul:
            {
                int s = 1;
                double sumLog = 0;
                foreach (var f in mul.Factors)
                {
                    if (!TryEvalLogMag(f, v, val, out int fs, out double fl)) return false;
                    if (fs == 0) { sign = 0; logAbs = double.NegativeInfinity; return true; }
                    s *= fs;
                    sumLog += fl;
                }
                sign = s; logAbs = sumLog;
                return true;
            }
            case SymExpr.Pow pow:
            {
                double e = EvalSafe(pow.Exp, v, val);
                if (!double.IsFinite(e)) return false;
                if (!TryEvalLogMag(pow.Base, v, val, out int bs, out double bl)) return false;
                if (bs == 0)
                {
                    if (e > 0) { sign = 0; logAbs = double.NegativeInfinity; return true; }
                    return false;
                }
                if (bs < 0)
                {
                    if (Math.Abs(e - Math.Round(e)) > 1e-9) return false;
                    long ie = (long)Math.Round(e);
                    sign = (ie & 1) == 0 ? 1 : -1;
                }
                else sign = 1;
                logAbs = e * bl;
                return true;
            }
            case SymExpr.Func { Name: "exp", Args.Count: 1 } ex:
            {
                double a = EvalSafe(ex.Args[0], v, val);
                if (!double.IsFinite(a)) return false;
                sign = 1; logAbs = a;
                return true;
            }
            default:
            {
                double d = EvalSafe(expr, v, val);
                if (!double.IsFinite(d)) return false;
                if (d == 0) { sign = 0; logAbs = double.NegativeInfinity; return true; }
                sign = d > 0 ? 1 : -1;
                logAbs = Math.Log(Math.Abs(d));
                return true;
            }
        }
    }

    private static double EvalSafe(SymExpr expr, string v, double val)
    {
        try
        {
            return expr switch
            {
                SymExpr.Num n => n.Value.ToDouble(),
                SymExpr.Var vr => vr.Name == v ? val : vr.Name == "pi" ? Math.PI : vr.Name == "e" ? Math.E : double.NaN,
                SymExpr.Add a => a.Terms.Sum(t => EvalSafe(t, v, val)),
                SymExpr.Mul m => m.Factors.Aggregate(1.0, (acc, f) => acc * EvalSafe(f, v, val)),
                SymExpr.Pow p => EvalPowSafe(EvalSafe(p.Base, v, val), EvalSafe(p.Exp, v, val)),
                SymExpr.Func { Name: "sqrt", Args.Count: 1 } f => Math.Sqrt(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "cbrt", Args.Count: 1 } f => Math.Cbrt(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "root", Args.Count: 2 } f => RealRoot(EvalSafe(f.Args[0], v, val), EvalSafe(f.Args[1], v, val)),
                SymExpr.Func { Name: "sin", Args.Count: 1 } f => Math.Sin(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "cos", Args.Count: 1 } f => Math.Cos(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "tan", Args.Count: 1 } f => Math.Tan(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "cot", Args.Count: 1 } f => 1.0 / Math.Tan(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "asin", Args.Count: 1 } f => Math.Asin(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "acos", Args.Count: 1 } f => Math.Acos(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "atan", Args.Count: 1 } f => Math.Atan(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "acot", Args.Count: 1 } f => EvalAcot(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "sinh", Args.Count: 1 } f => Math.Sinh(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "cosh", Args.Count: 1 } f => Math.Cosh(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "tanh", Args.Count: 1 } f => Math.Tanh(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "ln", Args.Count: 1 } f => Math.Log(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "exp", Args.Count: 1 } f => Math.Exp(EvalSafe(f.Args[0], v, val)),
                SymExpr.Func { Name: "abs", Args.Count: 1 } f => Math.Abs(EvalSafe(f.Args[0], v, val)),
                _ => double.NaN
            };
        }
        catch
        {
            return double.NaN;
        }
    }

    private static double EvalPowSafe(double b, double e)
    {
        if (b < 0 && Math.Abs(e - Math.Round(e)) > 1e-9)
        {
            // Check odd denominator rational exponent (e.g. 2/3, 3/5)
            for (int q = 3; q <= 9; q += 2)
            {
                double p = e * q;
                if (Math.Abs(p - Math.Round(p)) < 1e-9)
                {
                    long ip = (long)Math.Round(p);
                    double r = Math.Pow(-b, e);
                    return (ip & 1) == 0 ? r : -r;
                }
            }
        }
        return Math.Pow(b, e);
    }

    public static double RealRoot(double x, double n)
    {
        if (n == 0) throw new EvalException("Division by zero");
        if (x < 0 && Math.Abs(n - Math.Round(n)) < 1e-9 && ((long)Math.Round(n) & 1) == 1)
            return -Math.Pow(-x, 1.0 / n);
        return Math.Pow(x, 1.0 / n);
    }

    public static double EvalAcot(double x) =>
        x == 0 ? 0.5 * Math.PI : Math.Atan(1.0 / x);

    // --------------------------------------------------------- helpers

    private static double CleanZero(double x) =>
        double.IsFinite(x) && Math.Abs(x) < 1e-11 ? 0.0 : x;

    private static bool TrySplitQuotient(SymExpr expr, out SymExpr num, out SymExpr den)
    {
        num = Symbolic.One;
        den = Symbolic.One;
        if (expr is not SymExpr.Mul mul) return false;

        var numFactors = new List<SymExpr>();
        var denFactors = new List<SymExpr>();
        foreach (var f in mul.Factors)
        {
            if (f is SymExpr.Pow { Exp: SymExpr.Num en } p && en.Value.Sign < 0)
                denFactors.Add(en.Value == ExactNumber.FromLong(-1) ? p.Base : new SymExpr.Pow(p.Base, new SymExpr.Num(-en.Value)));
            else
                numFactors.Add(f);
        }
        if (denFactors.Count == 0) return false;
        num = numFactors.Count == 0 ? Symbolic.One : numFactors.Count == 1 ? numFactors[0] : new SymExpr.Mul(numFactors);
        den = denFactors.Count == 1 ? denFactors[0] : new SymExpr.Mul(denFactors);
        return true;
    }

    private static bool IsSymZero(SymExpr e) =>
        e is SymExpr.Num n && (n.Value.IsZero || Math.Abs(n.Value.ToDouble()) < 1e-12);

    private static SymExpr Substitute(SymExpr expr, string v, SymExpr replacement) => expr switch
    {
        SymExpr.Var vr when vr.Name == v => replacement,
        SymExpr.Add a => Symbolic.Simplify(new SymExpr.Add(a.Terms.Select(t => Substitute(t, v, replacement)).ToList()), Ops.AssumePositive),
        SymExpr.Mul m => Symbolic.Simplify(new SymExpr.Mul(m.Factors.Select(f => Substitute(f, v, replacement)).ToList()), Ops.AssumePositive),
        SymExpr.Pow p => Symbolic.Pow(Substitute(p.Base, v, replacement), Substitute(p.Exp, v, replacement), Ops.AssumePositive),
        SymExpr.Func fn => Symbolic.Call(fn.Name, fn.Args.Select(a => Substitute(a, v, replacement)).ToList(), Ops.AssumePositive),
        _ => expr
    };

    private static HashSet<string> CollectExtraVars(SymExpr e, string exclude)
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
}
