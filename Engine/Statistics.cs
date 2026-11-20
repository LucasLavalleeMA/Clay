namespace Clay.Engine;

/// <summary>Statistical functions and probability distributions.</summary>
internal static class Statistics
{
    private static double RequirePlainDouble(Value v)
    {
        if (v.Unit != null) throw new EvalException("Statistical functions do not support units");
        return v.Frac;
    }

    public static double[] ToDoubles(Value v) =>
        Ops.Flatten(v).Select(RequirePlainDouble).ToArray();

    public static double[] FromArgs(List<Value> args) =>
        args.SelectMany(Ops.Flatten).Select(RequirePlainDouble).ToArray();

    public static double Mean(double[] xs)
    {
        if (xs.Length == 0) throw new EvalException("Missing argument");
        return xs.Average();
    }

    public static double Variance(double[] xs, bool sample = true)
    {
        int denom = sample ? xs.Length - 1 : xs.Length;
        if (denom <= 0) throw new EvalException("Need at least two values");
        double m = Mean(xs);
        double sum = 0;
        foreach (double x in xs) { double d = x - m; sum += d * d; }
        return sum / denom;
    }

    public static double Stdev(double[] xs) => Math.Sqrt(Variance(xs, sample: true));

    public static double Stderr(double[] xs) => Stdev(xs) / Math.Sqrt(xs.Length);

    public static double MeanDev(double[] xs)
    {
        double m = Mean(xs);
        return xs.Average(x => Math.Abs(x - m));
    }

    public static double GeoMean(double[] xs)
    {
        if (xs.Length == 0) throw new EvalException("Missing argument");
        double sumLog = 0;
        foreach (double x in xs)
        {
            if (x <= 0) throw new EvalException("Geometric mean requires positive values");
            sumLog += Math.Log(x);
        }
        return Math.Exp(sumLog / xs.Length);
    }

    public static double HarmMean(double[] xs)
    {
        if (xs.Length == 0) throw new EvalException("Missing argument");
        double sumInv = 0;
        foreach (double x in xs)
        {
            if (x == 0) throw new EvalException("Division by zero");
            sumInv += 1.0 / x;
        }
        return xs.Length / sumInv;
    }

    public static double Rms(double[] xs)
    {
        if (xs.Length == 0) throw new EvalException("Missing argument");
        return Math.Sqrt(xs.Average(x => x * x));
    }

    public static double TrimMean(double[] xs, double percent)
    {
        if (xs.Length == 0) throw new EvalException("Missing argument");
        var sorted = xs.OrderBy(x => x).ToArray();
        double frac = percent / 100.0;
        if (frac < 0 || frac >= 0.5) throw new EvalException("Trim percentage out of range");
        int k = (int)Math.Floor(sorted.Length * frac);
        int count = sorted.Length - 2 * k;
        if (count <= 0) throw new EvalException("Trim percentage too large");
        double sum = 0;
        for (int i = k; i < sorted.Length - k; i++) sum += sorted[i];
        return sum / count;
    }

    public static double WinsorMean(double[] xs, double percent)
    {
        if (xs.Length == 0) throw new EvalException("Missing argument");
        var sorted = xs.OrderBy(x => x).ToArray();
        double frac = percent / 100.0;
        if (frac < 0 || frac >= 0.5) throw new EvalException("Winsor percentage out of range");
        int k = (int)Math.Floor(sorted.Length * frac);
        if (2 * k >= sorted.Length) throw new EvalException("Winsor percentage too large");
        double lo = sorted[k];
        double hi = sorted[sorted.Length - 1 - k];
        double sum = k * lo + k * hi;
        for (int i = k; i < sorted.Length - k; i++) sum += sorted[i];
        return sum / sorted.Length;
    }

    public static double WeightedMean(double[] xs, double[] ws)
    {
        if (xs.Length == 0 || xs.Length != ws.Length)
            throw new EvalException("Weighted mean requires two lists of equal length");
        double sumW = 0, sumXW = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            sumXW += xs[i] * ws[i];
            sumW += ws[i];
        }
        if (sumW == 0) throw new EvalException("Division by zero");
        return sumXW / sumW;
    }

    public static double Mode(double[] xs)
    {
        if (xs.Length == 0) throw new EvalException("Missing argument");
        return xs.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
    }

    /// <summary>R / Hyndman-Fan quantile (methods 1-9; defaults to method 8 as in Qalculate).</summary>
    public static double Quantile(double[] xs, double p, int method = 8)
    {
        if (xs.Length == 0) throw new EvalException("Missing argument");
        if (p < 0 || p > 1) throw new EvalException("Quantile probability must be between 0 and 1");
        var sorted = xs.OrderBy(x => x).ToArray();
        int n = sorted.Length;
        if (n == 1) return sorted[0];

        if (method < 1 || method > 9)
            throw new EvalException("Quantile method must be an integer between 1 and 9");

        if (method == 1)
        {
            if (p == 0) return sorted[0];
            int idx = (int)Math.Ceiling(n * p);
            return sorted[Math.Clamp(idx - 1, 0, n - 1)];
        }
        if (method == 2)
        {
            if (p == 0) return sorted[0];
            if (p == 1) return sorted[n - 1];
            double np = n * p;
            int fl = (int)Math.Floor(np);
            if (Math.Abs(np - fl) < 1e-12)
                return 0.5 * (sorted[Math.Clamp(fl - 1, 0, n - 1)] + sorted[Math.Clamp(fl, 0, n - 1)]);
            return sorted[Math.Clamp(fl, 0, n - 1)];
        }
        if (method == 3)
        {
            if (p <= 0.5 / n) return sorted[0];
            int idx = (int)Math.Round(n * p, MidpointRounding.ToEven);
            return sorted[Math.Clamp(idx - 1, 0, n - 1)];
        }

        double h = method switch
        {
            4 => n * p,
            5 => n * p + 0.5,
            6 => (n + 1) * p,
            7 => (n - 1) * p + 1,
            8 => (n + 1.0 / 3.0) * p + 1.0 / 3.0,
            9 => (n + 0.25) * p + 0.375,
            _ => throw new EvalException("Quantile method must be an integer between 1 and 9"),
        };

        if (h <= 1) return sorted[0];
        if (h >= n) return sorted[n - 1];
        int lo = (int)Math.Floor(h);
        double frac = h - lo;
        return sorted[lo - 1] + frac * (sorted[lo] - sorted[lo - 1]);
    }

    public static double Iqr(double[] xs, int method = 7) =>
        Quantile(xs, 0.75, method) - Quantile(xs, 0.25, method);

    public static double Covar(double[] xs, double[] ys, bool sample = false)
    {
        int denom = sample ? xs.Length - 1 : xs.Length;
        if (xs.Length < 2 || xs.Length != ys.Length || denom <= 0)
            throw new EvalException("Covariance requires two lists of equal length >= 2");
        double mx = Mean(xs), my = Mean(ys), sum = 0;
        for (int i = 0; i < xs.Length; i++)
            sum += (xs[i] - mx) * (ys[i] - my);
        return sum / denom;
    }

    public static double PoolVar(double[] xs, double[] ys)
    {
        if (xs.Length < 2 || ys.Length < 2)
            throw new EvalException("Pooled variance requires lists of length >= 2");
        double vx = Variance(xs), vy = Variance(ys);
        return ((xs.Length - 1) * vx + (ys.Length - 1) * vy) / (xs.Length + ys.Length - 2);
    }

    public static double Pearson(double[] xs, double[] ys)
    {
        double denom = Stdev(xs) * Stdev(ys);
        if (denom == 0) throw new EvalException("Division by zero");
        return Covar(xs, ys, sample: true) / denom;
    }

    public static double Spearman(double[] xs, double[] ys)
    {
        if (xs.Length < 2 || xs.Length != ys.Length)
            throw new EvalException("Spearman correlation requires two lists of equal length >= 2");
        return Pearson(Ranks(xs), Ranks(ys));
    }

    private static double[] Ranks(double[] xs)
    {
        int n = xs.Length;
        var indexed = xs.Select((v, i) => (v, i)).OrderBy(t => t.v).ToArray();
        var ranks = new double[n];
        int pos = 0;
        while (pos < n)
        {
            int end = pos;
            while (end + 1 < n && indexed[end + 1].v == indexed[pos].v) end++;
            double avgRank = (pos + end) / 2.0 + 1.0;
            for (int k = pos; k <= end; k++)
                ranks[indexed[k].i] = avgRank;
            pos = end + 1;
        }
        return ranks;
    }

    /// <summary>Two-sample pooled-variance Student t-statistic.</summary>
    public static double TTest(double[] xs, double[] ys)
    {
        double sp2 = PoolVar(xs, ys);
        double se = Math.Sqrt(sp2 * (1.0 / xs.Length + 1.0 / ys.Length));
        if (se == 0) throw new EvalException("Division by zero");
        return Math.Abs(Mean(xs) - Mean(ys)) / se;
    }

    /// <summary>Paired Student t-statistic.</summary>
    public static double PairedTTest(double[] xs, double[] ys)
    {
        if (xs.Length < 2 || xs.Length != ys.Length)
            throw new EvalException("Paired t-test requires two lists of equal length >= 2");
        var diffs = new double[xs.Length];
        for (int i = 0; i < xs.Length; i++) diffs[i] = xs[i] - ys[i];
        double se = Stderr(diffs);
        if (se == 0) throw new EvalException("Division by zero");
        return Math.Abs(Mean(diffs)) / se;
    }

    // ------------------------------------------------------------- distributions

    public static double NormDist(double x, double mean = 0, double sd = 1, bool cdf = false)
    {
        if (sd <= 0) throw new EvalException("Standard deviation must be positive");
        double z = (x - mean) / sd;
        if (!cdf)
            return Math.Exp(-0.5 * z * z) / (sd * Math.Sqrt(2 * Math.PI));
        return 0.5 * Erfc(-z / Math.Sqrt(2.0));
    }

    /// <summary>Inverse normal CDF (Peter Acklam rational approximation).</summary>
    public static double NormDistInv(double p, double mean = 0, double sd = 1)
    {
        if (sd <= 0) throw new EvalException("Standard deviation must be positive");
        if (p <= 0 || p >= 1) throw new EvalException("Probability must be between 0 and 1");

        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                      1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                      6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                      -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
                      3.754408661907416e+00];

        const double pLow = 0.02425;
        const double pHigh = 1 - pLow;
        double z;
        if (p < pLow)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            z = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        else if (p <= pHigh)
        {
            double q = p - 0.5;
            double r = q * q;
            z = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
                (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        else
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            z = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                 ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        return mean + sd * z;
    }

    public static double FDist(double x, double d1, double d2, bool cdf = false)
    {
        if (d1 <= 0 || d2 <= 0) throw new EvalException("Degrees of freedom must be positive");
        if (x < 0) return 0;
        if (!cdf)
        {
            if (x == 0) return d1 < 2 ? double.PositiveInfinity : d1 == 2 ? 1.0 : 0.0;
            double logPdf = 0.5 * (d1 * Math.Log(d1) + d2 * Math.Log(d2))
                          + (0.5 * d1 - 1) * Math.Log(x)
                          - 0.5 * (d1 + d2) * Math.Log(d1 * x + d2)
                          - LogBeta(0.5 * d1, 0.5 * d2);
            return Math.Exp(logPdf);
        }
        double z = d1 * x / (d1 * x + d2);
        return RegularizedIncompleteBeta(z, 0.5 * d1, 0.5 * d2);
    }

    public static double ChiSqDistInv(double p, double k)
    {
        if (k <= 0) throw new EvalException("Degrees of freedom must be positive");
        if (p <= 0 || p >= 1) throw new EvalException("Probability must be between 0 and 1");

        double a = 0.5 * k;
        double lo = 0, hi = Math.Max(k + 10 * Math.Sqrt(2 * k), 20.0);
        while (RegularizedLowerGamma(a, 0.5 * hi) < p) hi *= 2;
        while (hi > 1e-300 && RegularizedLowerGamma(a, 0.25 * hi) >= p) hi *= 0.5;

        double pTol = Math.Min(p, 1.0 - p) * 1e-12;
        for (int iter = 0; iter < 200; iter++)
        {
            double mid = 0.5 * (lo + hi);
            double f = RegularizedLowerGamma(a, 0.5 * mid);
            if ((Math.Abs(f - p) <= pTol && hi - lo <= mid * 1e-12) ||
                hi - lo <= Math.Max(double.Epsilon, mid * 1e-14))
                return mid;
            if (f < p) lo = mid;
            else hi = mid;
        }
        return 0.5 * (lo + hi);
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

    private static double LogBeta(double a, double b) =>
        LogGamma(a) + LogGamma(b) - LogGamma(a + b);

    private static double RegularizedIncompleteBeta(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double bt = Math.Exp(a * Math.Log(x) + b * Math.Log(1 - x) - LogBeta(a, b));
        if (x < (a + 1) / (a + b + 2))
            return bt * BetaContinuedFraction(x, a, b) / a;
        return 1.0 - bt * BetaContinuedFraction(1 - x, b, a) / b;
    }

    private static double BetaContinuedFraction(double x, double a, double b)
    {
        const double fpMin = 1e-30;
        double qab = a + b, qap = a + 1, qam = a - 1;
        double c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < fpMin) d = fpMin;
        d = 1 / d;
        double h = d;
        for (int m = 1; m <= 200; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d; if (Math.Abs(d) < fpMin) d = fpMin;
            c = 1 + aa / c; if (Math.Abs(c) < fpMin) c = fpMin;
            d = 1 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d; if (Math.Abs(d) < fpMin) d = fpMin;
            c = 1 + aa / c; if (Math.Abs(c) < fpMin) c = fpMin;
            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < 1e-14) break;
        }
        return h;
    }

    private static double Erfc(double x)
    {
        if (x == 0) return 1.0;
        double q = RegularizedUpperGamma(0.5, x * x);
        return x > 0 ? q : 2.0 - q;
    }

    private static double RegularizedLowerGamma(double a, double x)
    {
        if (x <= 0) return 0;
        if (x < a + 1)
        {
            double ap = a, sum = 1 / a, del = sum;
            for (int n = 1; n <= 200; n++)
            {
                ap += 1;
                del *= x / ap;
                sum += del;
                if (Math.Abs(del) < Math.Abs(sum) * 1e-14) break;
            }
            return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
        }

        return 1.0 - RegularizedUpperGamma(a, x);
    }

    private static double RegularizedUpperGamma(double a, double x)
    {
        if (x <= 0) return 1.0;
        if (x < a + 1) return 1.0 - RegularizedLowerGamma(a, x);

        const double fpMin = 1e-30;
        double b = x + 1 - a, c = 1 / fpMin, d = 1 / b, h = d;
        for (int i = 1; i <= 200; i++)
        {
            double an = -i * (i - a);
            b += 2;
            d = an * d + b; if (Math.Abs(d) < fpMin) d = fpMin;
            c = b + an / c; if (Math.Abs(c) < fpMin) c = fpMin;
            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < 1e-14) break;
        }
        return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
    }

    public static Value PolyFit(List<Value> args, int degree)
    {
        if (args.Count < 1) throw new EvalException("Missing argument");
        double[] xs, ys;
        if (args.Count == 1)
        {
            ys = ToDoubles(args[0]);
            xs = Enumerable.Range(1, ys.Length).Select(i => (double)i).ToArray();
        }
        else
        {
            xs = ToDoubles(args[0]);
            ys = ToDoubles(args[1]);
        }
        if (xs.Length != ys.Length || xs.Length < degree + 1)
            throw new EvalException("Not enough data points for polynomial fit");

        int m = degree + 1;
        // Normal equations: (X^T X) c = X^T y
        var a = new double[m, m + 1];
        for (int row = 0; row < m; row++)
        {
            for (int col = 0; col < m; col++)
            {
                double sum = 0;
                for (int i = 0; i < xs.Length; i++)
                    sum += Math.Pow(xs[i], row + col);
                a[row, col] = sum;
            }
            double rhs = 0;
            for (int i = 0; i < xs.Length; i++)
                rhs += ys[i] * Math.Pow(xs[i], row);
            a[row, m] = rhs;
        }

        // Gaussian elimination with partial pivoting
        for (int col = 0; col < m; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < m; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-15)
                throw new EvalException("Singular regression matrix");
            if (pivot != col)
                for (int j = col; j <= m; j++)
                    (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);

            double div = a[col, col];
            for (int j = col; j <= m; j++) a[col, j] /= div;
            for (int r = 0; r < m; r++)
            {
                if (r == col) continue;
                double factor = a[r, col];
                for (int j = col; j <= m; j++) a[r, j] -= factor * a[col, j];
            }
        }

        // Build descending polynomial expression c_deg*x^deg + ... + c_1*x + c_0
        double maxAbsX = xs.Max(Math.Abs);
        double xScale = maxAbsX > 0 ? maxAbsX : 1.0;
        double maxTermScale = 0;
        for (int k = 0; k <= degree; k++)
            maxTermScale = Math.Max(maxTermScale, Math.Abs(a[k, m]) * Math.Pow(xScale, k));
        double noiseFloor = maxTermScale * 1e-12;

        var terms = new List<SymExpr>();
        var xVar = Symbolic.Var("x");
        for (int k = degree; k >= 0; k--)
        {
            double ck = a[k, m];
            if (ck == 0 || Math.Abs(ck) * Math.Pow(xScale, k) < noiseFloor) continue;
            var cExpr = new SymExpr.Num(ExactNumber.FromDouble(ck));
            if (k == 0) terms.Add(cExpr);
            else if (k == 1) terms.Add(new SymExpr.Mul([cExpr, xVar]));
            else terms.Add(new SymExpr.Mul([cExpr, new SymExpr.Pow(xVar, Symbolic.FromLong(k))]));
        }
        if (terms.Count == 0) return new Value(0);
        return Value.Sym(terms.Count == 1 ? terms[0] : new SymExpr.Add(terms));
    }
}
