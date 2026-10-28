using Newtonsoft.Json.Linq;

namespace KalashiWinForms;

/// <summary>
/// Middle line = floor_strike = 60s CF Benchmarks average at window OPEN.
/// Settlement pin = 60s CF Benchmarks average at window CLOSE.
/// YES if settle_avg &gt;= floor_strike.
/// Last tick − strike is what the old Δ Spot column printed. That number
/// lies once the last minute starts — a one-second wick does not flip the contract.
/// </summary>
public sealed class LineSnap
{
    public double Last;
    public double Strike;
    public double LastDist;
    public double LastBps;
    public string LastSide = "";
    public double ExpectedSettle;
    public double LineDist;
    public double LineBps;
    public string LineSide = "";
    public bool InWindow;
    public bool Disagree;
    public string Src = "";
    public double Tte;
}

public static class LineMath
{
    public static readonly Dictionary<string, int> Decimals = new()
    {
        ["BTC"] = 2, ["ETH"] = 2, ["SOL"] = 4, ["BNB"] = 2,
        ["XRP"] = 4, ["NEAR"] = 4, ["ZEC"] = 2, ["TON"] = 4,
        ["BCH"] = 2, ["ADA"] = 5, ["DOGE"] = 6, ["HYPE"] = 4,
    };

    public static DateTime ParseUtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        if (DateTime.TryParse(
                s,
                null,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return DateTime.MinValue;
    }

    public static long WindowKey(DateTime closeUtc)
    {
        if (closeUtc == DateTime.MinValue) return 0;
        var unix = new DateTimeOffset(DateTime.SpecifyKind(closeUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return unix / 900 * 900;
    }

    public static LineSnap Compute(
        double last,
        double strike,
        DateTime utcNow,
        DateTime closeUtc,
        IReadOnlyList<(DateTime ts, double px)> history)
    {
        var snap = new LineSnap { Last = last, Strike = strike };
        if (last <= 0 || strike <= 0)
        {
            snap.Src = "no feed";
            return snap;
        }

        // Unknown close = no window yet. Treating it as tte 0 used to hit the "closed"
        // branch and crash on DateTime.MinValue.AddSeconds(-60).
        var closeKnown = closeUtc != DateTime.MinValue;
        var tte = closeKnown ? (closeUtc - utcNow).TotalSeconds : double.PositiveInfinity;
        snap.Tte = tte;
        snap.LastDist = last - strike;
        snap.LastBps = snap.LastDist / strike * 10_000.0;
        snap.LastSide = snap.LastDist >= 0 ? "YES" : "NO";

        double expected;
        if (!closeKnown)
        {
            expected = last;
            snap.InWindow = false;
            snap.Src = "last print — close time unknown";
        }
        else if (tte > 60)
        {
            expected = last;
            snap.InWindow = false;
            snap.Src = "last print — 60s avg not open yet";
        }
        else if (tte <= 0.25)
        {
            var closed = AverageIn(history, closeUtc.AddSeconds(-60), closeUtc);
            expected = closed ?? last;
            snap.InWindow = true;
            snap.Src = "closed — last 60s average";
        }
        else
        {
            var start = closeUtc.AddSeconds(-60);
            var have = AverageIn(history, start, utcNow);
            var elapsed = Math.Max(0.5, 60.0 - tte);
            expected = have.HasValue
                ? (have.Value * elapsed + last * tte) / 60.0
                : last;
            snap.InWindow = true;
            snap.Src = "60s running average — this is the pin";
        }

        snap.ExpectedSettle = expected;
        snap.LineDist = expected - strike;
        snap.LineBps = snap.LineDist / strike * 10_000.0;
        snap.LineSide = snap.LineDist >= 0 ? "YES" : "NO";
        snap.Disagree = snap.LastSide != snap.LineSide;
        return snap;
    }

    static double? AverageIn(IReadOnlyList<(DateTime ts, double px)> hist, DateTime from, DateTime to)
    {
        if (hist == null || hist.Count == 0) return null;
        double sum = 0;
        var n = 0;
        // History is appended in time order: walk back from the newest point and stop
        // once well before the window (30s slack covers small clock disorder between
        // Kalshi timestamps and local UtcNow) instead of scanning all ~2400 points.
        var stop = from.AddSeconds(-30);
        for (var i = hist.Count - 1; i >= 0; i--)
        {
            var (ts, px) = hist[i];
            if (ts < stop) break;
            if (px <= 0) continue;
            if (ts >= from && ts <= to)
            {
                sum += px;
                n++;
            }
        }
        return n > 0 ? sum / n : null;
    }

    public static string Money(double v, string coin)
    {
        var d = Decimals.TryGetValue(coin, out var n) ? n : 2;
        var sign = v >= 0 ? "+" : "-";
        return $"{sign}${Math.Abs(v).ToString("F" + d)}";
    }

    public static string Bps(double bps) => $"{(bps >= 0 ? "+" : "")}{bps:F1}";

    public static (double spot, List<(long ms, double v)> points) ParseLive(JObject data)
    {
        var points = new List<(long ms, double v)>();
        var details = data["live_data"]?["details"];
        if (details?["timeseries"] is JArray series)
        {
            foreach (var p in series)
            {
                var t = AsLong(p?["t"]);
                var v = AsDouble(p?["v"]);
                if (t > 0 && v > 0) points.Add((t, v));
            }
        }
        var spot = points.Count > 0 ? points[^1].v : 0;
        if (spot <= 0)
            spot = AsDouble(details?["current_price"] ?? details?["value"] ?? details?["spot"]);
        return (spot, points);
    }

    static long AsLong(JToken? t)
    {
        if (t == null || t.Type is JTokenType.Null or JTokenType.Undefined) return 0;
        try { return t.ToObject<long>(); }
        catch { return 0; }
    }

    static double AsDouble(JToken? t)
    {
        if (t == null || t.Type is JTokenType.Null or JTokenType.Undefined) return 0;
        try { return t.ToObject<double>(); }
        catch
        {
            return double.TryParse(
                t.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d) ? d : 0;
        }
    }

    public static DateTime FromUnixMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
