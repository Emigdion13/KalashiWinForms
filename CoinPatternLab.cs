using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KalashiWinForms;

public sealed class CoinMark
{
    public string Side { get; set; } = "";
    public double Bps { get; set; }
    public double Mid { get; set; }
}

public sealed class CoinFinish
{
    public string Ticker { get; set; } = "";
    public string Coin { get; set; } = "";
    public string Series { get; set; } = "";
    public DateTime CloseUtc { get; set; }
    public long Win { get; set; }
    public double Strike { get; set; }
    public double Expiration { get; set; }
    public string Result { get; set; } = ""; // yes / no
    public bool Official { get; set; }
    public double LineBps { get; set; }
    public double LastBps { get; set; }
    public string LineSide { get; set; } = "";
    public double YesMid { get; set; }
    public int Flips { get; set; }
    public Dictionary<string, CoinMark> Marks { get; set; } = new();
}

public sealed class PairRow
{
    public string A = "";
    public string B = "";
    public int N;
    public int Same;
    public int Split;
    public double SamePct;
}

public sealed class RuleRow
{
    public string Id = "";
    public string Label = "";
    public int Hit;
    public int N;
    public double? Pct;
    public bool Ready;
    public string Note = "";
}

/// <summary>
/// Same-side + "what finishes YES/NO" lab. Never places an order.
/// Backfills settled Kalshi windows so you can test on day one.
/// </summary>
public sealed class CoinPatternLab
{
    public const int MinTrust = 12;
    const int MaxFinishes = 800;

    public static readonly (string key, int tte, string label)[] MarkDefs =
    {
        ("t870", 870, "open+30s"),
        ("t600", 600, "T-10m"),
        ("t300", 300, "T-5m"),
        ("t120", 120, "T-2m"),
        ("t60", 60, "T-60s"),
        ("t15", 15, "T-15s"),
    };

    readonly object _gate = new();
    readonly string _jsonl;
    readonly string _json;
    readonly Dictionary<string, CoinFinish> _byTicker = new();

    public CoinPatternLab(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _jsonl = Path.Combine(dataDir, "coin_finishes.jsonl");
        _json = Path.Combine(dataDir, "coin_patterns.json");
        Load();
    }

    public int Count
    {
        get { lock (_gate) return _byTicker.Count; }
    }

    public void Remember(CoinFinish rec)
    {
        if (string.IsNullOrEmpty(rec.Ticker) || rec.Result is not ("yes" or "no"))
            return;
        rec.Win = rec.Win != 0 ? rec.Win : LineMath.WindowKey(rec.CloseUtc);
        lock (_gate)
        {
            if (_byTicker.TryGetValue(rec.Ticker, out var old))
            {
                // official wins; keep marks if the new row is a bare result
                if (old.Official && !rec.Official) return;
                if (rec.Marks.Count == 0 && old.Marks.Count > 0)
                    rec.Marks = old.Marks;
                if (rec.Flips == 0 && old.Flips > 0) rec.Flips = old.Flips;
            }
            _byTicker[rec.Ticker] = rec;
            // Long-running sessions add ~1100 finishes/day; drop the oldest in batches
            if (_byTicker.Count > MaxFinishes + 100)
                TrimOldest();
        }
        try
        {
            File.AppendAllText(_jsonl, JsonConvert.SerializeObject(rec) + Environment.NewLine);
        }
        catch { /* disk full / locked — keep memory */ }
    }

    public bool Has(string ticker)
    {
        lock (_gate) return _byTicker.ContainsKey(ticker);
    }

    public List<CoinFinish> All()
    {
        lock (_gate) return _byTicker.Values.ToList();
    }

    public void StampMark(string ticker, string key, CoinMark mark)
    {
        lock (_gate)
        {
            if (!_byTicker.TryGetValue(ticker, out var rec)) return;
            rec.Marks[key] = mark;
        }
    }

    public (List<PairRow> pairs, List<RuleRow> rules, List<(long win, List<CoinFinish> coins)> recent, int windows) Snapshot()
    {
        List<CoinFinish> rows;
        lock (_gate) rows = _byTicker.Values.Where(r => r.Result is "yes" or "no").ToList();

        var byWin = new Dictionary<long, Dictionary<string, CoinFinish>>();
        foreach (var r in rows)
        {
            if (r.Win == 0) continue;
            if (!byWin.TryGetValue(r.Win, out var bag))
                byWin[r.Win] = bag = new Dictionary<string, CoinFinish>();
            bag[r.Coin] = r;
        }

        var coins = rows.Select(r => r.Coin).Distinct().OrderBy(c => c).ToList();
        var pairs = new List<PairRow>();
        for (var i = 0; i < coins.Count; i++)
        {
            for (var j = i + 1; j < coins.Count; j++)
            {
                var a = coins[i];
                var b = coins[j];
                var same = 0;
                var split = 0;
                foreach (var bag in byWin.Values)
                {
                    if (!bag.TryGetValue(a, out var ra) || !bag.TryGetValue(b, out var rb))
                        continue;
                    if (ra.Result == rb.Result) same++;
                    else split++;
                }
                var n = same + split;
                if (n == 0) continue;
                pairs.Add(new PairRow
                {
                    A = a, B = b, N = n, Same = same, Split = split,
                    SamePct = (double)same / n,
                });
            }
        }
        pairs.Sort((x, y) =>
        {
            var cx = x.N >= MinTrust ? 0 : 1;
            var cy = y.N >= MinTrust ? 0 : 1;
            if (cx != cy) return cx.CompareTo(cy);
            var p = y.SamePct.CompareTo(x.SamePct);
            return p != 0 ? p : y.N.CompareTo(x.N);
        });

        var rules = BuildRules(rows, byWin);
        var recent = byWin.OrderByDescending(kv => kv.Key)
            .Take(10)
            .Select(kv => (kv.Key, kv.Value.Values.OrderBy(c => CoinOrder(c.Coin)).ToList()))
            .ToList();
        return (pairs, rules, recent, byWin.Count);
    }

    static int CoinOrder(string c) => c switch
    {
        "BTC" => 0, "ETH" => 1, "SOL" => 2, "BNB" => 3, "XRP" => 4, "NEAR" => 5,
        "ZEC" => 6, "TON" => 7, "BCH" => 8, "ADA" => 9, "DOGE" => 10, "HYPE" => 11,
        _ => 99,
    };

    static List<RuleRow> BuildRules(List<CoinFinish> rows, Dictionary<long, Dictionary<string, CoinFinish>> byWin)
    {
        var outRules = new List<RuleRow>();

        void Add(string id, string label, int hit, int n, string note)
        {
            outRules.Add(new RuleRow
            {
                Id = id, Label = label, Hit = hit, N = n,
                Pct = n > 0 ? (double)hit / n : null,
                Ready = n >= MinTrust,
                Note = note,
            });
        }

        static bool SameSide(string markSide, string result) =>
            !string.IsNullOrEmpty(markSide) && !string.IsNullOrEmpty(result)
            && markSide.Equals(result, StringComparison.OrdinalIgnoreCase);

        void Persist(string markKey, double minBps, string id, string label)
        {
            var hit = 0;
            var n = 0;
            foreach (var r in rows)
            {
                if (!r.Marks.TryGetValue(markKey, out var mk)) continue;
                if (Math.Abs(mk.Bps) < minBps) continue;
                n++;
                if (SameSide(mk.Side, r.Result)) hit++;
            }
            Add(id, label, hit, n, "already on that side of the line — did it stay?");
        }

        Persist("t300", 8, "persist_5m_8bps", "On a side at T-5m with |Δ|≥8 bps");
        Persist("t120", 8, "persist_2m_8bps", "On a side at T-2m with |Δ|≥8 bps");
        Persist("t120", 3, "persist_2m_3bps", "On a side at T-2m with |Δ|≥3 bps");
        Persist("t60", 3, "persist_60s_3bps", "On a side at T-60s with |Δ|≥3 bps");
        Persist("t870", 3, "persist_open_3bps", "On a side 30s after open with |Δ|≥3 bps");

        {
            var hit = 0;
            var n = 0;
            foreach (var r in rows)
            {
                if (!r.Marks.TryGetValue("t120", out var mk) || mk.Mid <= 0) continue;
                var agree = (mk.Mid >= 0.55 && mk.Side == "YES") || (mk.Mid <= 0.45 && mk.Side == "NO");
                if (!agree) continue;
                n++;
                if (SameSide(mk.Side, r.Result)) hit++;
            }
            Add("tape_agree_2m",
                "Tape ≥55¢ and above the line at T-2m (or ≤45¢ below) → that side",
                hit, n, "book and line pointing the same way");
        }

        {
            var hit = 0;
            var n = 0;
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.LineSide)) continue;
                // last tick vs settle-avg: stored as LastBps sign vs LineSide
                var lastSide = r.LastBps >= 0 ? "YES" : "NO";
                if (lastSide == r.LineSide) continue;
                n++;
                if (SameSide(r.LineSide, r.Result)) hit++;
            }
            Add("line_beats_last",
                "When last tick and 60s-avg disagree, the average is the finish",
                hit, n, "this is why the old Δ Spot lied in the last minute");
        }

        {
            var hit = 0;
            var n = 0;
            foreach (var bag in byWin.Values)
            {
                if (!bag.TryGetValue("BTC", out var btc)) continue;
                foreach (var rec in bag.Values)
                {
                    if (rec.Coin == "BTC") continue;
                    n++;
                    if (rec.Result == btc.Result) hit++;
                }
            }
            Add("vs_btc", "Same side as BTC this window", hit, n, "not a law — just the base rate on your tape");
        }

        {
            var hit = 0;
            var n = 0;
            foreach (var r in rows)
            {
                if (!r.Marks.TryGetValue("t60", out var a) || !r.Marks.TryGetValue("t15", out var b))
                    continue;
                if (a.Side == b.Side) continue;
                n++;
                if (SameSide(b.Side, r.Result)) hit++;
            }
            Add("late_cross",
                "Crossed the line after T-60s — did the NEW side hold?",
                hit, n, "last-second flips are often noise against the average");
        }

        foreach (var g in rows.GroupBy(r => r.Coin).OrderBy(g => CoinOrder(g.Key)))
        {
            var yes = g.Count(r => r.Result == "yes");
            Add("base_" + g.Key, $"{g.Key} finished YES", yes, g.Count(), "raw base rate");
        }

        return outRules;
    }

    public static void ApplyMarksFromTimeseries(CoinFinish rec, List<(long ms, double v)> points)
    {
        if (rec.CloseUtc == DateTime.MinValue || rec.Strike <= 0 || points.Count == 0)
            return;
        var closeMs = new DateTimeOffset(DateTime.SpecifyKind(rec.CloseUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        foreach (var (key, tte, _) in MarkDefs)
        {
            var target = closeMs - tte * 1000L;
            (long ms, double v) pick = (0, 0);
            foreach (var p in points)
            {
                if (p.ms <= target && p.v > 0) pick = p;
            }
            if (pick.v <= 0) continue;
            var bps = (pick.v - rec.Strike) / rec.Strike * 10_000.0;
            rec.Marks[key] = new CoinMark
            {
                Side = pick.v >= rec.Strike ? "YES" : "NO",
                Bps = Math.Round(bps, 3),
            };
        }
    }

    public static CoinFinish? FromSettledMarket(JObject m, string coin, string series)
    {
        var ticker = m["ticker"]?.Value<string>() ?? "";
        if (string.IsNullOrEmpty(ticker)) return null;
        var result = (m["result"]?.Value<string>() ?? "").Trim().ToLowerInvariant();
        var strike = m["floor_strike"]?.Value<double>() ?? 0;
        var expStr = m["expiration_value"]?.ToString();
        double exp = 0;
        if (!string.IsNullOrWhiteSpace(expStr))
            double.TryParse(expStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out exp);
        if (result is not ("yes" or "no") && exp > 0 && strike > 0)
            result = exp >= strike ? "yes" : "no";
        if (result is not ("yes" or "no") || strike <= 0) return null;
        var close = LineMath.ParseUtc(m["close_time"]?.Value<string>() ?? "");
        var lineBps = exp > 0 ? (exp - strike) / strike * 10_000.0 : 0;
        return new CoinFinish
        {
            Ticker = ticker,
            Coin = coin,
            Series = series,
            CloseUtc = close,
            Win = LineMath.WindowKey(close),
            Strike = strike,
            Expiration = exp,
            Result = result,
            Official = true,
            LineBps = Math.Round(lineBps, 3),
            LastBps = Math.Round(lineBps, 3),
            LineSide = result == "yes" ? "YES" : "NO",
        };
    }

    // Caller holds _gate
    void TrimOldest()
    {
        if (_byTicker.Count <= MaxFinishes) return;
        // One sort, not one sort per removed row
        var drop = _byTicker.Values.OrderByDescending(x => x.CloseUtc).Skip(MaxFinishes).ToList();
        foreach (var old in drop)
            _byTicker.Remove(old.Ticker);
    }

    void Load()
    {
        var rows = new List<CoinFinish>();
        var lineCount = 0;
        if (File.Exists(_jsonl))
        {
            try
            {
                foreach (var line in File.ReadLines(_jsonl))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    lineCount++;
                    try
                    {
                        var rec = JsonConvert.DeserializeObject<CoinFinish>(line);
                        if (rec != null) rows.Add(rec);
                    }
                    catch { /* skip bad line */ }
                }
            }
            catch { }
        }
        else if (File.Exists(_json))
        {
            try
            {
                var blob = JObject.Parse(File.ReadAllText(_json));
                var arr = blob["finishes"] as JArray;
                if (arr != null)
                    rows.AddRange(arr.ToObject<List<CoinFinish>>() ?? new());
            }
            catch { }
        }
        lock (_gate)
        {
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.Ticker)) continue;
                _byTicker[r.Ticker] = r;
            }
            TrimOldest();
        }

        // The .jsonl is append-only (every update re-appends the row), so it grows forever
        // and startup gets slower. Rewrite it with just the live rows once it's mostly stale.
        if (lineCount > Count * 2 + 50)
            CompactJsonl();
    }

    void CompactJsonl()
    {
        try
        {
            List<CoinFinish> rows;
            lock (_gate) rows = _byTicker.Values.OrderBy(r => r.CloseUtc).ToList();
            var tmp = _jsonl + ".tmp";
            File.WriteAllLines(tmp, rows.Select(r => JsonConvert.SerializeObject(r)));
            File.Move(tmp, _jsonl, overwrite: true);
        }
        catch { /* keep the old file */ }
    }

    public void SaveSummary()
    {
        try
        {
            List<CoinFinish> rows;
            lock (_gate) rows = _byTicker.Values.OrderByDescending(r => r.CloseUtc).Take(400).ToList();
            var blob = new JObject
            {
                ["saved_ts"] = DateTime.UtcNow.ToString("o"),
                ["n"] = rows.Count,
                ["finishes"] = JArray.FromObject(rows),
            };
            var tmp = _json + ".tmp";
            File.WriteAllText(tmp, blob.ToString(Formatting.None));
            File.Move(tmp, _json, overwrite: true);
        }
        catch { }
    }
}
