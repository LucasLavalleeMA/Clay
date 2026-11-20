using System.Numerics;

namespace Clay.Engine;

/// <summary>Exact-rational and numeric matrix/vector linear algebra operations.</summary>
internal static class LinearAlgebra
{
    public static bool IsMatrix(Value v) =>
        v.Items is { Count: > 0 } rows &&
        rows.All(r => r.IsArray) &&
        rows.Select(r => r.Items!.Count).Distinct().Count() == 1;

    private static ExactNumber RequirePlainCell(Value v)
    {
        if (v.Unit != null) throw new EvalException("Matrix operations do not support units");
        return v.ExactFrac;
    }

    public static ExactNumber[,] ToMatrix(Value v)
    {
        if (!v.IsArray)
        {
            var m = new ExactNumber[1, 1];
            m[0, 0] = RequirePlainCell(v);
            return m;
        }

        var items = v.Items!;
        if (items.Count == 0) return new ExactNumber[0, 0];

        if (items[0].IsArray)
        {
            int r = items.Count;
            int c = items[0].Items!.Count;
            var m = new ExactNumber[r, c];
            for (int i = 0; i < r; i++)
            {
                if (!items[i].IsArray || items[i].Items!.Count != c)
                    throw new EvalException("Matrix rows must have equal length");
                for (int j = 0; j < c; j++)
                    m[i, j] = RequirePlainCell(items[i].Items![j]);
            }
            return m;
        }
        else
        {
            var m = new ExactNumber[1, items.Count];
            for (int j = 0; j < items.Count; j++)
                m[0, j] = RequirePlainCell(items[j]);
            return m;
        }
    }

    public static Value FromMatrix(ExactNumber[,] m, bool collapseSingle = false)
    {
        int r = m.GetLength(0);
        int c = m.GetLength(1);
        if (r == 0 || c == 0) return Value.List([]);
        if (collapseSingle && r == 1 && c == 1) return new Value(m[0, 0]);
        if (r == 1)
        {
            var row = new Value[c];
            for (int j = 0; j < c; j++) row[j] = new Value(m[0, j]);
            return Value.List(row);
        }
        var rows = new Value[r];
        for (int i = 0; i < r; i++)
        {
            var row = new Value[c];
            for (int j = 0; j < c; j++) row[j] = new Value(m[i, j]);
            rows[i] = Value.List(row);
        }
        return Value.List(rows);
    }

    public static Value MatMul(Value a, Value b)
    {
        var ma = ToMatrix(a);
        var mb = ToMatrix(b);
        int r1 = ma.GetLength(0), c1 = ma.GetLength(1);
        int r2 = mb.GetLength(0), c2 = mb.GetLength(1);
        if (c1 != r2) throw new EvalException($"Matrix dimensions don't match ({r1}x{c1} vs {r2}x{c2})");

        var res = new ExactNumber[r1, c2];
        for (int i = 0; i < r1; i++)
        {
            for (int j = 0; j < c2; j++)
            {
                var sum = ExactNumber.Zero;
                for (int k = 0; k < c1; k++)
                    sum += ma[i, k] * mb[k, j];
                res[i, j] = sum;
            }
        }
        return FromMatrix(res, collapseSingle: true);
    }

    public static Value MatrixPow(Value a, long exp)
    {
        var m = ToMatrix(a);
        int r = m.GetLength(0), c = m.GetLength(1);
        if (r != c) throw new EvalException("Matrix power requires a square matrix");
        if (exp == 0) return FromMatrix(Identity(r));

        ExactNumber[,] baseMat = exp < 0 ? Inverse(m) : m;
        long power = Math.Abs(exp);
        var result = Identity(r);
        var cur = baseMat;
        while (power > 0)
        {
            if ((power & 1) != 0) result = MulRaw(result, cur);
            power >>= 1;
            if (power > 0) cur = MulRaw(cur, cur);
        }
        return FromMatrix(result);
    }

    private static ExactNumber[,] MulRaw(ExactNumber[,] a, ExactNumber[,] b)
    {
        int n = a.GetLength(0);
        var res = new ExactNumber[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                var sum = ExactNumber.Zero;
                for (int k = 0; k < n; k++) sum += a[i, k] * b[k, j];
                res[i, j] = sum;
            }
        }
        return res;
    }

    public static Value ElementWise(Value a, Value b, Func<ExactNumber, ExactNumber, ExactNumber> op)
    {
        if (!a.IsArray && !b.IsArray) return new Value(op(a.ExactFrac, b.ExactFrac));
        var ma = ToMatrix(a);
        var mb = ToMatrix(b);
        int r1 = ma.GetLength(0), c1 = ma.GetLength(1);
        int r2 = mb.GetLength(0), c2 = mb.GetLength(1);
        int r = Math.Max(r1, r2);
        int c = Math.Max(c1, c2);
        if ((r1 != r && r1 != 1) || (r2 != r && r2 != 1) || (c1 != c && c1 != 1) || (c2 != c && c2 != 1))
            throw new EvalException($"Incompatible dimensions ({r1}x{c1} vs {r2}x{c2})");

        var res = new ExactNumber[r, c];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                res[i, j] = op(ma[r1 == 1 ? 0 : i, c1 == 1 ? 0 : j], mb[r2 == 1 ? 0 : i, c2 == 1 ? 0 : j]);

        return FromMatrix(res, collapseSingle: !a.IsArray && !b.IsArray);
    }

    public static ExactNumber Det(ExactNumber[,] m)
    {
        int n = m.GetLength(0);
        if (n != m.GetLength(1)) throw new EvalException("Determinant requires a square matrix");
        if (n == 0) return ExactNumber.One;
        if (n == 1) return m[0, 0];
        if (n == 2) return m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0];

        var a = (ExactNumber[,])m.Clone();
        var det = ExactNumber.One;
        for (int col = 0; col < n; col++)
        {
            int pivot = FindPivotRow(a, col, n, col);
            if (pivot < 0) return ExactNumber.Zero;
            if (pivot != col)
            {
                for (int j = col; j < n; j++) (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
                det = -det;
            }
            var pv = a[col, col];
            det *= pv;
            for (int i = col + 1; i < n; i++)
            {
                if (a[i, col].IsZero) continue;
                var factor = a[i, col] / pv;
                for (int j = col + 1; j < n; j++)
                    a[i, j] -= factor * a[col, j];
            }
        }
        return det;
    }

    private static int FindPivotRow(ExactNumber[,] a, int startRow, int rowCount, int col)
    {
        int best = -1;
        double bestMag = double.NegativeInfinity;
        for (int i = startRow; i < rowCount; i++)
        {
            var v = a[i, col];
            if (v.IsZero) continue;
            double mag = LogMagnitude(v);
            if (best < 0 || mag > bestMag)
            {
                best = i;
                bestMag = mag;
            }
        }
        return best;
    }

    private static double LogMagnitude(ExactNumber x)
    {
        if (x.IsZero) return double.NegativeInfinity;
        if (x.IsExact)
            return BigInteger.Log10(BigInteger.Abs(x.Num)) - BigInteger.Log10(x.Den);
        double ax = Math.Abs(x.ToDouble());
        return ax == 0 ? double.NegativeInfinity : Math.Log10(ax);
    }

    public static ExactNumber[,] Minor(ExactNumber[,] m, int row, int col)
    {
        int n = m.GetLength(0);
        var res = new ExactNumber[n - 1, n - 1];
        int ri = 0;
        for (int i = 0; i < n; i++)
        {
            if (i == row) continue;
            int cj = 0;
            for (int j = 0; j < n; j++)
            {
                if (j == col) continue;
                res[ri, cj++] = m[i, j];
            }
            ri++;
        }
        return res;
    }

    public static ExactNumber Cofactor(ExactNumber[,] m, int row1, int col1)
    {
        int n = m.GetLength(0);
        if (n != m.GetLength(1)) throw new EvalException("Cofactor requires a square matrix");
        if (row1 < 1 || row1 > n || col1 < 1 || col1 > n) throw new EvalException("Index out of range");
        var d = Det(Minor(m, row1 - 1, col1 - 1));
        return ((row1 + col1) % 2 == 0) ? d : -d;
    }

    public static ExactNumber[,] Adjugate(ExactNumber[,] m)
    {
        int n = m.GetLength(0);
        if (n != m.GetLength(1)) throw new EvalException("Adjugate requires a square matrix");
        if (n == 1) return new[,] { { ExactNumber.One } };
        var res = new ExactNumber[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                res[j, i] = Cofactor(m, i + 1, j + 1);
        return res;
    }

    public static ExactNumber[,] Inverse(ExactNumber[,] m)
    {
        int n = m.GetLength(0);
        if (n != m.GetLength(1)) throw new EvalException("Inverse requires a square matrix");
        var aug = new ExactNumber[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) aug[i, j] = m[i, j];
            aug[i, n + i] = ExactNumber.One;
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = FindPivotRow(aug, col, n, col);
            if (pivot < 0) throw new EvalException("Matrix is singular");
            if (pivot != col)
                for (int j = 0; j < 2 * n; j++) (aug[col, j], aug[pivot, j]) = (aug[pivot, j], aug[col, j]);

            var pv = aug[col, col];
            for (int j = 0; j < 2 * n; j++) aug[col, j] /= pv;
            for (int i = 0; i < n; i++)
            {
                if (i == col || aug[i, col].IsZero) continue;
                var f = aug[i, col];
                for (int j = 0; j < 2 * n; j++) aug[i, j] -= f * aug[col, j];
            }
        }

        var inv = new ExactNumber[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                inv[i, j] = aug[i, n + j];
        return inv;
    }

    public static ExactNumber[,] Rref(ExactNumber[,] m, out int rank)
    {
        int r = m.GetLength(0), c = m.GetLength(1);
        var a = (ExactNumber[,])m.Clone();
        int lead = 0;
        for (int row = 0; row < r && lead < c; row++)
        {
            int i = FindPivotRow(a, row, r, lead);
            while (i < 0)
            {
                lead++;
                if (lead == c) { rank = row; return a; }
                i = FindPivotRow(a, row, r, lead);
            }
            if (i != row)
                for (int j = 0; j < c; j++) (a[row, j], a[i, j]) = (a[i, j], a[row, j]);

            var pv = a[row, lead];
            for (int j = 0; j < c; j++) a[row, j] /= pv;
            for (int k = 0; k < r; k++)
            {
                if (k == row || a[k, lead].IsZero) continue;
                var f = a[k, lead];
                for (int j = 0; j < c; j++) a[k, j] -= f * a[row, j];
            }
            lead++;
        }

        rank = 0;
        for (int i = 0; i < r; i++)
        {
            for (int j = 0; j < c; j++)
                if (!a[i, j].IsZero) { rank++; break; }
        }
        return a;
    }

    public static ExactNumber Permanent(ExactNumber[,] m)
    {
        int n = m.GetLength(0);
        if (n != m.GetLength(1)) throw new EvalException("Permanent requires a square matrix");
        if (n == 0) return ExactNumber.One;
        if (n == 1) return m[0, 0];
        if (n > 10) throw new EvalException("Matrix too large for permanent");
        var used = new bool[n];
        return PermRecurse(m, 0, n, used);
    }

    private static ExactNumber PermRecurse(ExactNumber[,] m, int row, int n, bool[] used)
    {
        if (row == n) return ExactNumber.One;
        var sum = ExactNumber.Zero;
        for (int col = 0; col < n; col++)
        {
            if (used[col] || m[row, col].IsZero) continue;
            used[col] = true;
            sum += m[row, col] * PermRecurse(m, row + 1, n, used);
            used[col] = false;
        }
        return sum;
    }

    public static ExactNumber[,] Transpose(ExactNumber[,] m)
    {
        int r = m.GetLength(0), c = m.GetLength(1);
        var t = new ExactNumber[c, r];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                t[j, i] = m[i, j];
        return t;
    }

    public static ExactNumber[,] Identity(int n)
    {
        if (n < 1 || n > 200) throw new EvalException("Identity size out of range");
        var id = new ExactNumber[n, n];
        for (int i = 0; i < n; i++) id[i, i] = ExactNumber.One;
        return id;
    }

    public static Value Dot(Value a, Value b)
    {
        var va = Ops.Flatten(a).ToList();
        var vb = Ops.Flatten(b).ToList();
        if (va.Count != vb.Count) throw new EvalException($"Vectors differ in length ({va.Count} vs {vb.Count})");
        if (va.Count == 0) return new Value(ExactNumber.Zero);
        var sum = Ops.Mul(va[0], vb[0]);
        for (int i = 1; i < va.Count; i++)
            sum = Ops.Add(sum, Ops.Mul(va[i], vb[i]), 1);
        return sum;
    }

    public static Value Cross(Value a, Value b)
    {
        var va = Ops.Flatten(a).ToList();
        var vb = Ops.Flatten(b).ToList();
        if (va.Count != 3 || vb.Count != 3) throw new EvalException("Cross product requires 3D vectors");
        return Value.List([
            Ops.Add(Ops.Mul(va[1], vb[2]), Ops.Mul(va[2], vb[1]), -1),
            Ops.Add(Ops.Mul(va[2], vb[0]), Ops.Mul(va[0], vb[2]), -1),
            Ops.Add(Ops.Mul(va[0], vb[1]), Ops.Mul(va[1], vb[0]), -1),
        ]);
    }

    public static Value Magnitude(Value v)
    {
        var xs = Ops.Flatten(v).ToList();
        if (xs.Count == 0) return new Value(ExactNumber.Zero);
        Unit? first = xs.FirstOrDefault(x => x.Unit != null)?.Unit;
        if (first != null)
        {
            foreach (var x in xs)
            {
                if (x.Unit == null)
                    throw new EvalException($"Can't mix {first.Symbol} and a plain number");
                if (x.Unit.Dimension != first.Dimension)
                    throw new EvalException($"Can't mix {first.Symbol} and {x.Unit.Symbol}");
            }
            var sumSq = Ops.Mul(xs[0], xs[0]);
            for (int i = 1; i < xs.Count; i++)
                sumSq = Ops.Add(sumSq, Ops.Mul(xs[i], xs[i]), 1);
            return Ops.Root(sumSq, 2, Math.Sqrt);
        }
        double plainSumSq = xs.Sum(x => x.Frac * x.Frac);
        return new Value(Math.Sqrt(plainSumSq));
    }

    public static Value HorzCat(List<Value> args)
    {
        if (args.Count == 0) return Value.List([]);
        var mats = args.Select(ToMatrix).ToList();
        int rows = mats[0].GetLength(0);
        if (mats.Any(m => m.GetLength(0) != rows)) throw new EvalException("horzcat requires matrices with the same row count");
        int cols = mats.Sum(m => m.GetLength(1));
        var res = new ExactNumber[rows, cols];
        int colOffset = 0;
        foreach (var m in mats)
        {
            int mc = m.GetLength(1);
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < mc; j++)
                    res[i, colOffset + j] = m[i, j];
            colOffset += mc;
        }
        return FromMatrix(res);
    }

    public static Value VertCat(List<Value> args)
    {
        if (args.Count == 0) return Value.List([]);
        var mats = args.Select(ToMatrix).ToList();
        int cols = mats[0].GetLength(1);
        if (mats.Any(m => m.GetLength(1) != cols)) throw new EvalException("vertcat requires matrices with the same column count");
        int rows = mats.Sum(m => m.GetLength(0));
        var res = new ExactNumber[rows, cols];
        int rowOffset = 0;
        foreach (var m in mats)
        {
            int mr = m.GetLength(0);
            for (int i = 0; i < mr; i++)
                for (int j = 0; j < cols; j++)
                    res[rowOffset + i, j] = m[i, j];
            rowOffset += mr;
        }
        return FromMatrix(res);
    }
}
