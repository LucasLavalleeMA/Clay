using System.Globalization;
using System.Text.RegularExpressions;

namespace Clay.Engine;

/// <summary>A calendar date or timestamp with optional time-of-day and UTC/offset formatting.</summary>
public readonly record struct CalcDate(DateTimeOffset Instant, bool HasTime, bool IsUtc)
{
    private static readonly Dictionary<string, TimeSpan> TimeZones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = TimeSpan.Zero,
        ["GMT"] = TimeSpan.Zero,
        ["CET"] = TimeSpan.FromHours(1),
        ["CEST"] = TimeSpan.FromHours(2),
        ["EET"] = TimeSpan.FromHours(2),
        ["EEST"] = TimeSpan.FromHours(3),
        ["EST"] = TimeSpan.FromHours(-5),
        ["EDT"] = TimeSpan.FromHours(-4),
        ["CST"] = TimeSpan.FromHours(-6),
        ["CDT"] = TimeSpan.FromHours(-5),
        ["MST"] = TimeSpan.FromHours(-7),
        ["MDT"] = TimeSpan.FromHours(-6),
        ["PST"] = TimeSpan.FromHours(-8),
        ["PDT"] = TimeSpan.FromHours(-7),
        ["JST"] = TimeSpan.FromHours(9),
        ["IST"] = TimeSpan.FromHours(5.5),
    };

    private static readonly Regex IsoDateOnlyRx = new(
        @"^(?<y>[0-9]{4})-(?<m>[0-9]{2})-(?<d>[0-9]{2})$",
        RegexOptions.Compiled);

    private static readonly Regex IsoDateTimeRx = new(
        @"^(?<y>[0-9]{4})-(?<m>[0-9]{2})-(?<d>[0-9]{2})[T ](?<H>[0-9]{2}):(?<M>[0-9]{2})(?::(?<S>[0-9]{2})(?:\.(?<frac>[0-9]{1,7}))?)?(?<tz>Z|[+-][0-9]{2}:?[0-9]{2}|[A-Za-z]{2,5})?$",
        RegexOptions.Compiled);

    private static readonly Regex SlashDateRx = new(
        @"^(?<m>[0-9]{1,2})/(?<d>[0-9]{1,2})/(?<y>[0-9]{2}|[0-9]{4})$",
        RegexOptions.Compiled);

    public static bool TryParse(string text, out CalcDate result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        var m = IsoDateOnlyRx.Match(s);
        if (m.Success && TryBuildDate(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture), out var dateOnly))
        {
            result = new CalcDate(new DateTimeOffset(dateOnly, TimeSpan.Zero), HasTime: false, IsUtc: true);
            return true;
        }

        m = IsoDateTimeRx.Match(s);
        if (m.Success)
        {
            int y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            int mo = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            int d = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            int H = int.Parse(m.Groups["H"].Value, CultureInfo.InvariantCulture);
            int M = int.Parse(m.Groups["M"].Value, CultureInfo.InvariantCulture);
            int S = m.Groups["S"].Success ? int.Parse(m.Groups["S"].Value, CultureInfo.InvariantCulture) : 0;
            string tz = m.Groups["tz"].Value;

            TimeSpan offset = TimeSpan.Zero;
            bool isUtc = true;
            if (tz.Length > 0)
            {
                if (tz.Equals("Z", StringComparison.OrdinalIgnoreCase))
                {
                    offset = TimeSpan.Zero;
                    isUtc = true;
                }
                else if (tz[0] is '+' or '-')
                {
                    string clean = tz.Replace(":", "");
                    int sign = clean[0] == '-' ? -1 : 1;
                    int oh = int.Parse(clean[1..3], CultureInfo.InvariantCulture);
                    int om = int.Parse(clean[3..5], CultureInfo.InvariantCulture);
                    if (om > 59 || oh > 14 || (oh == 14 && om > 0)) return false;
                    offset = new TimeSpan(sign * oh, sign * om, 0);
                    isUtc = false;
                }
                else if (TimeZones.TryGetValue(tz, out var namedOffset))
                {
                    offset = namedOffset;
                    isUtc = tz.Equals("UTC", StringComparison.OrdinalIgnoreCase) || tz.Equals("GMT", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    return false;
                }
            }

            try
            {
                var dt = new DateTime(y, mo, d, H, M, S, DateTimeKind.Unspecified);
                if (m.Groups["frac"].Success)
                {
                    long ticks = long.Parse(m.Groups["frac"].Value.PadRight(7, '0'), CultureInfo.InvariantCulture);
                    dt = dt.AddTicks(ticks);
                }
                result = new CalcDate(new DateTimeOffset(dt, offset), HasTime: true, IsUtc: isUtc);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        m = SlashDateRx.Match(s);
        if (m.Success)
        {
            int mo = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            int d = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            string yStr = m.Groups["y"].Value;
            int y = int.Parse(yStr, CultureInfo.InvariantCulture);
            if (yStr.Length == 2)
                y += y < 70 ? 2000 : 1900;

            if (TryBuildDate(y, mo, d, out var slashDate))
            {
                result = new CalcDate(new DateTimeOffset(slashDate, TimeSpan.Zero), HasTime: false, IsUtc: true);
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildDate(int y, int m, int d, out DateTime dt)
    {
        try
        {
            dt = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            dt = default;
            return false;
        }
    }

    public CalcDate AddSeconds(double seconds)
    {
        if (!double.IsFinite(seconds))
            throw new EvalException("Date is out of range");
        try
        {
            var next = Instant.AddSeconds(seconds);
            bool keepDateOnly = !HasTime && Math.Abs(seconds % 86400) < 1e-6;
            return new CalcDate(next, HasTime: !keepDateOnly, IsUtc: IsUtc);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new EvalException("Date is out of range");
        }
    }

    public CalcDate ToOffset(TimeSpan offset, bool asUtc)
    {
        try
        {
            return new(Instant.ToOffset(offset), HasTime: true, IsUtc: asUtc);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new EvalException("Date is out of range for target UTC offset");
        }
    }

    public string Format()
    {
        if (!HasTime)
            return $"\"{Instant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\"";
        string timeFmt = Instant.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss"
            : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF";
        if (IsUtc && Instant.Offset == TimeSpan.Zero)
            return $"\"{Instant.ToString(timeFmt, CultureInfo.InvariantCulture)}Z\"";
        return $"\"{Instant.ToString(timeFmt + "zzz", CultureInfo.InvariantCulture)}\"";
    }
}
