using System.Globalization;
using System.Numerics;

namespace Clay.Engine;

/// <summary>
/// Complex number with optional uncertainty/interval half-widths (Re ± ReErr) + (Im ± ImErr)i.
/// Supports Qalculate interval mode 1 (variance / Gaussian error propagation) and mode 2 (interval arithmetic).
/// </summary>
public readonly record struct ComplexInterval(double Re, double Im, double ReErr = 0, double ImErr = 0)
{
    public bool IsComplex => Im != 0 || ImErr != 0;
    public bool HasError => ReErr != 0 || ImErr != 0;
    public Complex Center => new(Re, Im);

    public static ComplexInterval FromReal(double x, double err = 0) => new(x, 0, Math.Abs(err), 0);
    public static ComplexInterval FromComplex(Complex z, double reErr = 0, double imErr = 0) =>
        new(z.Real, z.Imaginary, Math.Abs(reErr), Math.Abs(imErr));

    public static ComplexInterval Negate(ComplexInterval a) => new(-a.Re, -a.Im, a.ReErr, a.ImErr);

    public static ComplexInterval Add(ComplexInterval a, ComplexInterval b, int sign, int icMode)
    {
        double re = a.Re + sign * b.Re;
        double im = a.Im + sign * b.Im;
        double reErr = CombineErr(a.ReErr, b.ReErr, icMode);
        double imErr = CombineErr(a.ImErr, b.ImErr, icMode);
        return new(re, im, reErr, imErr);
    }

    public static ComplexInterval Mul(ComplexInterval a, ComplexInterval b, int icMode)
    {
        if (icMode == 2 && !a.IsComplex && !b.IsComplex && (a.HasError || b.HasError))
        {
            double aLo = a.Re - a.ReErr, aHi = a.Re + a.ReErr;
            double bLo = b.Re - b.ReErr, bHi = b.Re + b.ReErr;
            double p1 = aLo * bLo, p2 = aLo * bHi, p3 = aHi * bLo, p4 = aHi * bHi;
            double lo = Math.Min(Math.Min(p1, p2), Math.Min(p3, p4));
            double hi = Math.Max(Math.Max(p1, p2), Math.Max(p3, p4));
            return FromReal(0.5 * (lo + hi), 0.5 * (hi - lo));
        }

        double re = a.Re * b.Re - a.Im * b.Im;
        double im = a.Re * b.Im + a.Im * b.Re;
        double reErr = CombineErr4(
            Math.Abs(b.Re) * a.ReErr, Math.Abs(a.Re) * b.ReErr,
            Math.Abs(b.Im) * a.ImErr, Math.Abs(a.Im) * b.ImErr, icMode);
        double imErr = CombineErr4(
            Math.Abs(b.Im) * a.ReErr, Math.Abs(a.Re) * b.ImErr,
            Math.Abs(b.Re) * a.ImErr, Math.Abs(a.Im) * b.ReErr, icMode);
        if (icMode == 2)
        {
            reErr += a.ReErr * b.ReErr + a.ImErr * b.ImErr;
            imErr += a.ReErr * b.ImErr + a.ImErr * b.ReErr;
        }
        return new(re, im, reErr, imErr);
    }

    public static ComplexInterval Div(ComplexInterval a, ComplexInterval b, int icMode)
    {
        if (b.Re == 0 && b.Im == 0) throw new EvalException("Division by zero");
        if (!a.HasError && !b.HasError) return FromComplex(a.Center / b.Center);

        if (icMode == 2)
        {
            if (!a.IsComplex && !b.IsComplex)
            {
                double bLo = b.Re - b.ReErr, bHi = b.Re + b.ReErr;
                if (bLo <= 0 && bHi >= 0) throw new EvalException("Division by zero");
                double aLo = a.Re - a.ReErr, aHi = a.Re + a.ReErr;
                double q1 = aLo / bLo, q2 = aLo / bHi, q3 = aHi / bLo, q4 = aHi / bHi;
                double lo = Math.Min(Math.Min(q1, q2), Math.Min(q3, q4));
                double hi = Math.Max(Math.Max(q1, q2), Math.Max(q3, q4));
                return FromReal(0.5 * (lo + hi), 0.5 * (hi - lo));
            }

            var inv = ReciprocalMode2(b);
            return Mul(a, inv, 2);
        }

        var q = a.Center / b.Center;
        var invB = Complex.One / b.Center;
        var dAd = invB;
        var dBd = -q * invB;
        double reErr = CombineErr4(
            Math.Abs(dAd.Real) * a.ReErr, Math.Abs(dAd.Imaginary) * a.ImErr,
            Math.Abs(dBd.Real) * b.ReErr, Math.Abs(dBd.Imaginary) * b.ImErr, icMode);
        double imErr = CombineErr4(
            Math.Abs(dAd.Imaginary) * a.ReErr, Math.Abs(dAd.Real) * a.ImErr,
            Math.Abs(dBd.Imaginary) * b.ReErr, Math.Abs(dBd.Real) * b.ImErr, icMode);
        return FromComplex(q, reErr, imErr);
    }

    private static ComplexInterval ReciprocalMode2(ComplexInterval b)
    {
        if (!b.HasError) return FromComplex(Complex.One / b.Center);

        double x0 = b.Re - b.ReErr, x1 = b.Re + b.ReErr;
        double y0 = b.Im - b.ImErr, y1 = b.Im + b.ImErr;
        if (x0 <= 0 && x1 >= 0 && y0 <= 0 && y1 >= 0)
            throw new EvalException("Division by zero");

        double minRe = double.PositiveInfinity, maxRe = double.NegativeInfinity;
        double minIm = double.PositiveInfinity, maxIm = double.NegativeInfinity;

        void Sample(double x, double y)
        {
            double u, v;
            if (Math.Abs(x) >= Math.Abs(y))
            {
                double r = y / x;
                double denom = x + y * r;
                u = 1.0 / denom;
                v = -r / denom;
            }
            else
            {
                double r = x / y;
                double denom = y + x * r;
                u = r / denom;
                v = -1.0 / denom;
            }
            if (u < minRe) minRe = u;
            if (u > maxRe) maxRe = u;
            if (v < minIm) minIm = v;
            if (v > maxIm) maxIm = v;
        }

        // Harmonic extrema of Re(1/z) and Im(1/z) on a rectangle lie on its boundary
        // at corners and edge stationary points (x = 0, ±y on horizontal edges; y = 0, ±x on vertical edges).
        Sample(x0, y0); Sample(x0, y1); Sample(x1, y0); Sample(x1, y1);
        foreach (double x in new[] { x0, x1 })
        {
            foreach (double y in new[] { 0.0, x, -x })
                if (y >= y0 && y <= y1) Sample(x, y);
        }
        foreach (double y in new[] { y0, y1 })
        {
            foreach (double x in new[] { 0.0, y, -y })
                if (x >= x0 && x <= x1) Sample(x, y);
        }

        return new(
            0.5 * (minRe + maxRe), 0.5 * (minIm + maxIm),
            0.5 * (maxRe - minRe), 0.5 * (maxIm - minIm));
    }

    public static ComplexInterval Pow(ComplexInterval a, ComplexInterval b, int icMode)
    {
        if (!a.HasError && !b.HasError)
        {
            var p = Complex.Pow(a.Center, b.Center);
            if (double.IsNaN(p.Real) || double.IsNaN(p.Imaginary)) throw new EvalException("Not a number");
            return FromComplex(p);
        }

        // Real interval raised to an exact real exponent
        if (!a.IsComplex && !b.IsComplex && !b.HasError)
        {
            double exp = b.Re;
            if (exp == 0) return FromReal(1);
            if (icMode == 2)
            {
                double lo = a.Re - a.ReErr;
                double hi = a.Re + a.ReErr;
                bool containsZero = lo <= 0 && hi >= 0;
                if (containsZero && exp < 0)
                    throw new EvalException("Division by zero");
                var samples = new List<double> { lo, hi };
                if (lo < 0 && hi > 0 && exp > 0) samples.Add(0);
                double minRe = double.PositiveInfinity, maxRe = double.NegativeInfinity;
                double minIm = double.PositiveInfinity, maxIm = double.NegativeInfinity;
                foreach (double x in samples)
                {
                    var v = x == 0 ? Complex.Zero : Complex.Pow(new Complex(x, 0), exp);
                    if (v.Real < minRe) minRe = v.Real;
                    if (v.Real > maxRe) maxRe = v.Real;
                    if (v.Imaginary < minIm) minIm = v.Imaginary;
                    if (v.Imaginary > maxIm) maxIm = v.Imaginary;
                }
                return new(
                    0.5 * (minRe + maxRe), 0.5 * (minIm + maxIm),
                    0.5 * (maxRe - minRe), 0.5 * (maxIm - minIm));
            }
            else
            {
                var c = Complex.Pow(a.Center, exp);
                var deriv = exp * Complex.Pow(a.Center, exp - 1);
                return FromComplex(c, Math.Abs(deriv.Real) * a.ReErr, Math.Abs(deriv.Imaginary) * a.ReErr);
            }
        }

        if (icMode == 2)
        {
            if (!a.IsComplex && !b.IsComplex)
            {
                double aLo = a.Re - a.ReErr, aHi = a.Re + a.ReErr;
                double bLo = b.Re - b.ReErr, bHi = b.Re + b.ReErr;
                if (aLo > 0 || (aLo == 0 && bLo > 0))
                {
                    double[] vals =
                    [
                        aLo == 0 ? 0 : Math.Pow(aLo, bLo),
                        aLo == 0 ? 0 : Math.Pow(aLo, bHi),
                        aHi == 0 ? 0 : Math.Pow(aHi, bLo),
                        aHi == 0 ? 0 : Math.Pow(aHi, bHi)
                    ];
                    if (vals.Any(v => !double.IsFinite(v)))
                        throw new EvalException("Result overflowed");
                    double lo = vals.Min(), hi = vals.Max();
                    return FromReal(0.5 * (lo + hi), 0.5 * (hi - lo));
                }
            }
            throw new EvalException("Interval mode 2 does not support uncertain power on this domain");
        }

        var center = Complex.Pow(a.Center, b.Center);
        var da = b.Center * Complex.Pow(a.Center, b.Center - Complex.One);
        var db = center * Complex.Log(a.Center);
        double rErr = CombineErr4(
            Math.Abs(da.Real) * a.ReErr, Math.Abs(da.Imaginary) * a.ImErr,
            Math.Abs(db.Real) * b.ReErr, Math.Abs(db.Imaginary) * b.ImErr, icMode);
        double iErr = CombineErr4(
            Math.Abs(da.Imaginary) * a.ReErr, Math.Abs(da.Real) * a.ImErr,
            Math.Abs(db.Imaginary) * b.ReErr, Math.Abs(db.Real) * b.ImErr, icMode);
        return FromComplex(center, rErr, iErr);
    }

    public static ComplexInterval Ln(ComplexInterval z, int icMode)
    {
        if (!z.HasError)
        {
            var c = Complex.Log(z.Center);
            return FromComplex(c);
        }

        if (icMode == 2)
        {
            double x0 = z.Re - z.ReErr, x1 = z.Re + z.ReErr;
            double y0 = z.Im - z.ImErr, y1 = z.Im + z.ImErr;
            if (x0 <= 0 && x1 >= 0 && y0 <= 0 && y1 >= 0)
                throw new EvalException("Logarithm of zero");

            double minAbsX = (x0 <= 0 && x1 >= 0) ? 0 : Math.Min(Math.Abs(x0), Math.Abs(x1));
            double maxAbsX = Math.Max(Math.Abs(x0), Math.Abs(x1));
            double minAbsY = (y0 <= 0 && y1 >= 0) ? 0 : Math.Min(Math.Abs(y0), Math.Abs(y1));
            double maxAbsY = Math.Max(Math.Abs(y0), Math.Abs(y1));

            double minRe = LogHypot(minAbsX, minAbsY);
            double maxRe = LogHypot(maxAbsX, maxAbsY);
            if (!double.IsFinite(minRe) || !double.IsFinite(maxRe))
                throw new EvalException("Logarithm of zero");

            double minIm, maxIm;
            if (x1 < 0 && y0 < 0 && y1 >= 0)
            {
                minIm = -Math.PI;
                maxIm = Math.PI;
            }
            else
            {
                double ny0 = y0 == 0 ? 0.0 : y0;
                double ny1 = y1 == 0 ? 0.0 : y1;
                double[] angles =
                [
                    Math.Atan2(ny0, x0), Math.Atan2(ny0, x1),
                    Math.Atan2(ny1, x0), Math.Atan2(ny1, x1)
                ];
                minIm = angles.Min();
                maxIm = angles.Max();
            }
            return new(
                0.5 * (minRe + maxRe), 0.5 * (minIm + maxIm),
                0.5 * (maxRe - minRe), 0.5 * (maxIm - minIm));
        }

        var logC = Complex.Log(z.Center);
        double r2 = z.Re * z.Re + z.Im * z.Im;
        double reErr = Math.Sqrt(Math.Pow(z.Re / r2 * z.ReErr, 2) + Math.Pow(z.Im / r2 * z.ImErr, 2));
        double imErr = Math.Sqrt(Math.Pow(z.Im / r2 * z.ReErr, 2) + Math.Pow(z.Re / r2 * z.ImErr, 2));
        return FromComplex(logC, reErr, imErr);
    }

    public static ComplexInterval Ei(ComplexInterval z, int icMode)
    {
        if (z.IsComplex)
        {
            if (icMode == 2 && z.HasError)
                throw new EvalException("Interval mode 2 does not support uncertain complex Ei");
            var c = new Complex(z.Re, z.Im);
            if (c == Complex.Zero) throw new EvalException("Division by zero");
            Complex sum;
            bool converged = false;
            if (c.Real <= 0 && c.Magnitude >= 10.0)
            {
                // Ei(c) = -E_1(-c) ± iπ via Lentz continued fraction for E_1(w) in Re(w) >= 0
                Complex w = -c;
                const double fpMin = 1e-30;
                Complex b = w + Complex.One;
                Complex cfC = new(1.0 / fpMin, 0);
                Complex cfD = Complex.One / b;
                Complex h = cfD;
                for (int i = 1; i <= 200; i++)
                {
                    double an = -i * (double)i;
                    b += 2.0;
                    cfD = an * cfD + b;
                    if (cfD.Magnitude < fpMin) cfD = new Complex(fpMin, 0);
                    cfC = b + an / cfC;
                    if (cfC.Magnitude < fpMin) cfC = new Complex(fpMin, 0);
                    cfD = Complex.One / cfD;
                    Complex del = cfD * cfC;
                    h *= del;
                    if ((del - Complex.One).Magnitude < 1e-15) { converged = true; break; }
                }
                Complex e1 = Complex.Exp(-w) * h;
                sum = -e1 + new Complex(0, c.Imaginary >= 0 ? Math.PI : -Math.PI);
            }
            else
            {
                const double eulerGamma = 0.57721566490153286060651209;
                sum = eulerGamma + Complex.Log(c);
                var term = Complex.One;
                for (int k = 1; k <= 300; k++)
                {
                    term *= c / k;
                    var delta = term / k;
                    sum += delta;
                    if (delta.Magnitude < 1e-16 * Math.Max(1.0, sum.Magnitude)) { converged = true; break; }
                }
            }
            if (!converged || !double.IsFinite(sum.Real) || !double.IsFinite(sum.Imaginary))
                throw new EvalException("Ei did not converge");
            if (!z.HasError) return new ComplexInterval(sum.Real, sum.Imaginary);
            var cDeriv = Complex.Exp(c) / c;
            double cReErr = CombineErr(Math.Abs(cDeriv.Real) * z.ReErr, Math.Abs(cDeriv.Imaginary) * z.ImErr, icMode);
            double cImErr = CombineErr(Math.Abs(cDeriv.Imaginary) * z.ReErr, Math.Abs(cDeriv.Real) * z.ImErr, icMode);
            return FromComplex(sum, cReErr, cImErr);
        }
        if (!z.HasError) return FromReal(EiReal(z.Re));
        if (icMode == 2)
        {
            double loX = z.Re - z.ReErr, hiX = z.Re + z.ReErr;
            if (loX <= 0 && hiX >= 0) throw new EvalException("Ei is undefined at 0");
            double lo = EiReal(loX);
            double hi = EiReal(hiX);
            return FromReal(0.5 * (lo + hi), 0.5 * Math.Abs(hi - lo));
        }
        double val = EiReal(z.Re);
        double deriv = Math.Exp(z.Re) / z.Re;
        return FromReal(val, Math.Abs(deriv) * z.ReErr);
    }

    private static double EiReal(double x)
    {
        if (x == 0) throw new EvalException("Division by zero");
        if (x < -10.0)
        {
            // Negative tail: Ei(-t) = -E_1(t) via Lentz continued fraction
            double t = -x;
            const double fpMin = 1e-30;
            double b = t + 1.0;
            double c = 1.0 / fpMin;
            double d = 1.0 / b;
            double h = d;
            bool cfConverged = false;
            for (int i = 1; i <= 300; i++)
            {
                double a = -i * (double)i;
                b += 2.0;
                d = a * d + b; if (Math.Abs(d) < fpMin) d = fpMin;
                c = b + a / c; if (Math.Abs(c) < fpMin) c = fpMin;
                d = 1.0 / d;
                double del = d * c;
                h *= del;
                if (Math.Abs(del - 1.0) < 1e-15) { cfConverged = true; break; }
            }
            if (!cfConverged || !double.IsFinite(h))
                throw new EvalException("Ei did not converge");
            return -Math.Exp(-t) * h;
        }

        const double eulerGamma = 0.57721566490153286060651209;
        double sum = eulerGamma + Math.Log(Math.Abs(x));
        double term = 1.0;
        bool converged = false;
        for (int k = 1; k <= 300; k++)
        {
            term *= x / k;
            double delta = term / k;
            sum += delta;
            if (Math.Abs(delta) < Math.Abs(sum) * 1e-15) { converged = true; break; }
        }
        if (!converged || !double.IsFinite(sum))
            throw new EvalException("Ei did not converge");
        return sum;
    }

    /// <summary>Complex Lambert W function W_k(z) via Halley's method.</summary>
    public static ComplexInterval LambertW(ComplexInterval z, int k, int icMode)
    {
        if (!z.HasError) return FromComplex(LambertWComplex(z.Center, k));

        if (icMode == 2)
        {
            if (!z.IsComplex)
            {
                double lo = z.Re - z.ReErr;
                double hi = z.Re + z.ReErr;
                double branchPt = -1.0 / Math.E;
                if (k == 0 && lo >= branchPt)
                {
                    double wLo = LambertWComplex(new Complex(lo, 0), 0).Real;
                    double wHi = LambertWComplex(new Complex(hi, 0), 0).Real;
                    return FromReal(0.5 * (wLo + wHi), 0.5 * Math.Abs(wHi - wLo));
                }
                if (k == -1 && lo >= branchPt && hi < 0)
                {
                    double wLo = LambertWComplex(new Complex(lo, 0), -1).Real;
                    double wHi = LambertWComplex(new Complex(hi, 0), -1).Real;
                    return FromReal(0.5 * (wLo + wHi), 0.5 * Math.Abs(wHi - wLo));
                }
            }
            throw new EvalException("Interval mode 2 does not support uncertain Lambert W on this domain");
        }

        var w = LambertWComplex(z.Center, k);
        var deriv = (z.Center == Complex.Zero && k == 0)
            ? Complex.One
            : w / (z.Center * (Complex.One + w));
        double reErr = CombineErr(Math.Abs(deriv.Real) * z.ReErr, Math.Abs(deriv.Imaginary) * z.ImErr, icMode);
        double imErr = CombineErr(Math.Abs(deriv.Imaginary) * z.ReErr, Math.Abs(deriv.Real) * z.ImErr, icMode);
        return FromComplex(w, reErr, imErr);
    }

    private static Complex LambertWComplex(Complex z, int k)
    {
        if (z == Complex.Zero && k == 0) return Complex.Zero;
        if (z == Complex.Zero) throw new EvalException("lambertw is undefined at 0 for k != 0");
        double branchPt = -1.0 / Math.E;
        if ((k == 0 || k == -1) && z.Imaginary == 0 && (z.Real == branchPt || z.Real == -Math.Exp(-1)))
            return new Complex(-1.0, 0);

        Complex w;
        if (k == 0 && z.Imaginary == 0 && z.Real >= -1.0 / Math.E)
        {
            double x = z.Real;
            if (x < 0)
            {
                double p = Math.Sqrt(Math.Max(0, 2.0 * (Math.E * x + 1.0)));
                w = new Complex(-1.0 + p * (1.0 - p / 3.0), 0);
            }
            else if (x <= 1.0)
            {
                w = new Complex(x / (1.0 + x), 0);
            }
            else
            {
                double lx = Math.Log(x);
                w = new Complex(lx - Math.Log(Math.Max(1.0, lx)), 0);
            }
        }
        else if (k == -1 && z.Imaginary == 0 && z.Real > -1.0 / Math.E && z.Real < 0)
        {
            double l = Math.Log(-z.Real);
            w = new Complex(l - Math.Log(-l), 0);
        }
        else
        {
            Complex lnZ = Complex.Log(z) + new Complex(0, 2 * Math.PI * k);
            if (k == 0 && z.Magnitude <= 1.5 && (Complex.One + z).Magnitude > 0.5)
                w = Complex.Log(Complex.One + z);
            else
                w = lnZ.Magnitude < 1e-6 ? lnZ : lnZ - Complex.Log(lnZ);
        }

        bool converged = false;
        for (int iter = 0; iter < 100; iter++)
        {
            Complex ew = Complex.Exp(w);
            Complex wew = w * ew;
            Complex f = wew - z;
            Complex denom = ew * (w + Complex.One) - (w + 2) * f / (2 * w + 2);
            if (denom == Complex.Zero || !double.IsFinite(denom.Real) || !double.IsFinite(denom.Imaginary)) break;
            Complex delta = f / denom;
            w -= delta;
            if (delta.Magnitude <= 1e-14 * (1 + w.Magnitude)) { converged = true; break; }
        }
        if (!converged || !double.IsFinite(w.Real) || !double.IsFinite(w.Imaginary))
            throw new EvalException("lambertw did not converge");
        return w;
    }

    public static Value PowerTower(Value baseVal, int height)
    {
        if (height < 0) throw new EvalException("Power tower height must be non-negative");
        if (height > 1000) throw new EvalException("Power tower height must be at most 1000");
        if (height == 0) return new Value(ExactNumber.One);
        var acc = baseVal;
        for (int i = 1; i < height; i++)
            acc = Ops.Pow(baseVal, acc);
        return acc;
    }

    public static Value AllRoots(double x, int n)
    {
        if (n < 1) throw new EvalException("Root degree must be a positive integer");
        if (n > 10000) throw new EvalException("Root degree must be at most 10,000");
        double r = Math.Pow(Math.Abs(x), 1.0 / n);
        double theta0 = x >= 0 ? 0 : Math.PI;
        var results = new List<Value>(n);

        int posMax = n / 2;
        for (int k = 0; k <= posMax; k++)
            results.Add(RootAt(r, (theta0 + 2 * Math.PI * k) / n));
        for (int k = -(n - 1) / 2; k < 0; k++)
            results.Add(RootAt(r, (theta0 + 2 * Math.PI * k) / n));

        return Value.List(results);
    }

    private static Value RootAt(double r, double angle)
    {
        double re = r * Math.Cos(angle);
        double im = r * Math.Sin(angle);
        double tol = 1e-13 * r;
        if (Math.Abs(im) <= tol) return new Value(re);
        if (Math.Abs(re) <= tol) re = 0;
        return Value.Complex(new ComplexInterval(re, im));
    }

    private static double LogHypot(double a, double b)
    {
        double hi = Math.Max(a, b);
        double lo = Math.Min(a, b);
        if (hi == 0) return double.NegativeInfinity;
        double r = lo / hi;
        return Math.Log(hi) + 0.5 * Math.Log(1.0 + r * r);
    }

    private static double CombineErr(double e1, double e2, int icMode) =>
        icMode == 2 ? e1 + e2 : Math.Sqrt(e1 * e1 + e2 * e2);

    private static double CombineErr4(double e1, double e2, double e3, double e4, int icMode) =>
        icMode == 2
            ? e1 + e2 + e3 + e4
            : Math.Sqrt(e1 * e1 + e2 * e2 + e3 * e3 + e4 * e4);

    public string Format(int decimals, bool group)
    {
        string rePart = FormatPart(Re, ReErr, decimals, group);
        if (!IsComplex) return rePart;

        bool imNeg = Im < 0;
        string imPart = FormatPart(Math.Abs(Im), ImErr, decimals, group) + "i";
        if (Re == 0 && ReErr == 0)
            return (imNeg ? "-" : "") + imPart;

        return rePart + (imNeg ? " - " : " + ") + imPart;
    }

    private static string FormatPart(double val, double err, int decimals, bool group)
    {
        string v = Formatter.Number(val, decimals, group);
        if (err == 0) return v;
        string e = Formatter.Number(err, decimals, group);
        return $"{v}±{e}";
    }
}
