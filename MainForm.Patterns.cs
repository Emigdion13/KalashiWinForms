using Newtonsoft.Json.Linq;

namespace KalashiWinForms;

public partial class MainForm
{
    // Created at field-init so BuildPatternsUi can never see a null lab,
    // even if someone calls InitializeComponent before InitPatterns.
    CoinPatternLab _lab = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KalashiWinForms"));
    readonly Dictionary<string, List<(DateTime ts, double px)>> _spotHist = new();
    readonly Dictionary<string, string> _coinTickers = new();
    readonly Dictionary<string, double> _coinYesMid = new();
    readonly Dictionary<string, int> _coinFlips = new();
    readonly Dictionary<string, int> _coinLastSign = new();
    readonly Dictionary<string, HashSet<string>> _stampedMarks = new();
    readonly Dictionary<string, Dictionary<string, CoinMark>> _liveMarks = new();
    readonly HashSet<string> _frozenTickers = new();
    readonly HashSet<string> _pendingOfficial = new();
    DataGridView _pairGrid = null!;
    DataGridView _ruleGrid = null!;
    DataGridView _recentGrid = null!;
    Label _lblLineLive = null!;
    bool _backfillStarted;
    bool _patternsBusy;
    bool _spotFetchBusy;
    bool _seededCf;

    // ComputeLine is asked ~10x per coin per second (grid cells, colors, vs-BTC, lab).
    // Cache it; cleared every timer tick and per coin whenever its spot/strike changes.
    readonly Dictionary<string, LineSnap?> _lineCache = new();
    static readonly Font LineBoldFont = new("Consolas", 11, FontStyle.Bold);
    static readonly Color DisagreeBack = Color.FromArgb(255, 230, 160);

    static readonly string[] AllCoins =
    {
        "BTC", "ETH", "SOL", "BNB", "XRP", "NEAR", "ZEC", "TON", "BCH", "ADA", "DOGE", "HYPE"
    };

    void InitPatterns()
    {
        try
        {
            _lab = new CoinPatternLab(DataDir());
            Log($"LINE lab: {_lab.Count} finishes on disk");
        }
        catch (Exception ex)
        {
            _lab = new CoinPatternLab(Path.GetTempPath());
            Log($"LINE lab init fallback: {ex.Message}");
        }
    }

    static string DataDir()
    {
        var exe = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
        if (File.Exists(Path.Combine(exe, ".env"))) return exe;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[]
        {
            Directory.GetCurrentDirectory(),
            Path.Combine(home, "OneDrive", "Desktop", "KalashiWinForms"),
            Path.Combine(home, "OneDrive", "Desktop", "projects", "KalashiWinForms"),
            exe,
        })
        {
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, ".env")))
                return dir;
        }
        return Directory.GetCurrentDirectory();
    }

    void BuildPatternsUi()
    {
        var tab = new TabPage("📐 LINE / PATTERNS");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 26));

        _lblLineLive = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9),
            Text = "LINE = expected 60s settle average − floor_strike (the middle line).\r\n"
                 + "Old Δ Spot was last tick − strike. That lies in the last minute.\r\n"
                 + "Need 12 finished windows before a rule is more than a story.",
        };
        root.Controls.Add(_lblLineLive, 0, 0);

        _pairGrid = MakeReadOnlyGrid(
            ("Pair", 18), ("n", 8), ("Same", 10), ("Split", 10), ("Same %", 12),
            ("Note", 42));
        var pairBox = WrapGrid("SAME SIDE — which coins die together", _pairGrid);
        root.Controls.Add(pairBox, 0, 1);

        _ruleGrid = MakeReadOnlyGrid(
            ("Rule", 46), ("n", 8), ("Hit", 10), ("Hit %", 10), ("Ready", 8), ("Note", 18));
        var ruleBox = WrapGrid("WHAT FINISHES YES / NO", _ruleGrid);
        root.Controls.Add(ruleBox, 0, 2);

        _recentGrid = MakeReadOnlyGrid(("Window", 16), ("Coins", 84));
        var recBox = WrapGrid("RECENT SETTLES (official Kalshi result)", _recentGrid);
        root.Controls.Add(recBox, 0, 3);

        tab.Controls.Add(root);
        _tabControl.TabPages.Add(tab);
        // Grids fill after markets load. Do not Snapshot here.
    }

    static Panel WrapGrid(string title, DataGridView grid)
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var lbl = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            Text = title,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        grid.Dock = DockStyle.Fill;
        p.Controls.Add(grid);
        p.Controls.Add(lbl);
        return p;
    }

    static DataGridView MakeReadOnlyGrid(params (string name, float weight)[] cols)
    {
        var g = new DataGridView
        {
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            ForeColor = Color.Black,
            Font = new Font("Consolas", 9),
            RowTemplate = { Height = 22 },
        };
        foreach (var (name, w) in cols)
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, FillWeight = w });
        EnableDoubleBuffering(g);
        return g;
    }

    void AppendSpot(string coin, DateTime utc, double px)
    {
        InvalidateLines(coin);
        if (px <= 0) return;
        if (!_spotHist.TryGetValue(coin, out var list))
            _spotHist[coin] = list = new List<(DateTime, double)>();
        list.Add((utc, px));
        if (list.Count > 2400)
            list.RemoveRange(0, list.Count - 1800);

        if (_coinStrikes.TryGetValue(coin, out var strike) && strike > 0)
        {
            var bps = (px - strike) / strike * 10_000.0;
            if (Math.Abs(bps) >= 1.5)
            {
                var sign = bps >= 0 ? 1 : -1;
                if (_coinLastSign.TryGetValue(coin, out var prev) && prev != 0 && prev != sign)
                    _coinFlips[coin] = (_coinFlips.TryGetValue(coin, out var f) ? f : 0) + 1;
                _coinLastSign[coin] = sign;
            }
        }
    }

    void SeedHistory(string coin, List<(long ms, double v)> points)
    {
        if (points.Count == 0) return;
        InvalidateLines(coin);
        var cut = DateTime.UtcNow.AddMinutes(-20);
        if (!_spotHist.TryGetValue(coin, out var list))
            _spotHist[coin] = list = new List<(DateTime, double)>();
        foreach (var (ms, v) in points)
        {
            var ts = LineMath.FromUnixMs(ms);
            if (ts < cut || v <= 0) continue;
            if (list.Count == 0 || ts > list[^1].ts.AddMilliseconds(400))
                list.Add((ts, v));
        }
        if (list.Count > 2400)
            list.RemoveRange(0, list.Count - 1800);
    }

    void InvalidateLines(string? coin = null)
    {
        if (coin == null) _lineCache.Clear();
        else _lineCache.Remove(coin);
    }

    LineSnap? ComputeLine(string coin)
    {
        if (_lineCache.TryGetValue(coin, out var cached)) return cached;
        LineSnap? snap = null;
        if (_spotPrices.TryGetValue(coin, out var spot) && spot > 0
            && _coinStrikes.TryGetValue(coin, out var strike) && strike > 0)
        {
            _spotHist.TryGetValue(coin, out var hist);
            var close = _coinCloseTimes.TryGetValue(coin, out var c) ? c : DateTime.MinValue;
            if (close != DateTime.MinValue && close.Kind != DateTimeKind.Utc)
                close = DateTime.SpecifyKind(close, DateTimeKind.Utc);
            snap = LineMath.Compute(spot, strike, DateTime.UtcNow, close, hist ?? new List<(DateTime, double)>());
        }
        _lineCache[coin] = snap;
        return snap;
    }

    string FormatLastDist(string coin)
    {
        var s = ComputeLine(coin);
        return s == null ? "—" : LineMath.Money(s.LastDist, coin);
    }

    string FormatBps(string coin)
    {
        var s = ComputeLine(coin);
        return s == null ? "—" : LineMath.Bps(s.LineBps);
    }

    string FormatSide(string coin)
    {
        var s = ComputeLine(coin);
        return s == null ? "—" : s.LineSide;
    }

    string FormatVsBtc(string coin)
    {
        if (coin == "BTC") return "—";
        var mine = ComputeLine(coin);
        var btc = ComputeLine("BTC");
        if (mine == null || btc == null || string.IsNullOrEmpty(mine.LineSide) || string.IsNullOrEmpty(btc.LineSide))
            return "—";
        return mine.LineSide == btc.LineSide ? "WITH" : "VS";
    }

    void ColorLineCells(DataGridViewRow row, string coin)
    {
        var s = ComputeLine(coin);
        if (s == null) return;
        // Shared Font instances — a new Font per call leaked GDI handles every second.
        // Only assign when different so unchanged cells don't repaint.
        var sideCell = row.Cells["LineSide"];
        var sideColor = s.LineSide == "YES" ? Color.ForestGreen : Color.Firebrick;
        if (sideCell.Style.ForeColor != sideColor) sideCell.Style.ForeColor = sideColor;
        if (sideCell.Style.Font != LineBoldFont) sideCell.Style.Font = LineBoldFont;

        // Reset when they agree again — previously the highlight stuck for the rest of the session
        var lastCell = row.Cells["LastDelta"];
        var lastBack = s.Disagree ? DisagreeBack : Color.Empty;
        if (lastCell.Style.BackColor != lastBack) lastCell.Style.BackColor = lastBack;
        var lineCell = row.Cells["LineDelta"];
        var lineFont = s.Disagree ? LineBoldFont : null;
        if (lineCell.Style.Font != lineFont) lineCell.Style.Font = lineFont;

        var vs = row.Cells["VsBtc"];
        var v = vs.Value?.ToString();
        var vsColor = v == "VS" ? Color.Firebrick : v == "WITH" ? Color.ForestGreen : Color.Gray;
        if (vs.Style.ForeColor != vsColor) vs.Style.ForeColor = vsColor;
    }

    void TickLineLab()
    {
        var now = DateTime.UtcNow;
        foreach (var coin in AllCoins)
        {
            var snap = ComputeLine(coin);
            if (snap == null) continue;
            if (!_coinTickers.TryGetValue(coin, out var ticker) || string.IsNullOrEmpty(ticker))
                continue;

            // checkpoint stamps for the live window
            if (!_stampedMarks.TryGetValue(ticker, out var stamped))
                _stampedMarks[ticker] = stamped = new HashSet<string>();
            if (!_liveMarks.TryGetValue(ticker, out var marks))
                _liveMarks[ticker] = marks = new Dictionary<string, CoinMark>();
            foreach (var (key, tte, _) in CoinPatternLab.MarkDefs)
            {
                if (stamped.Contains(key)) continue;
                if (snap.Tte > tte) continue;
                stamped.Add(key);
                marks[key] = new CoinMark
                {
                    Side = snap.LineSide,
                    Bps = Math.Round(snap.LineBps, 3),
                    Mid = _coinYesMid.TryGetValue(coin, out var mid) ? mid : 0,
                };
            }

            // freeze a few seconds after close so the last 60s average is in
            if (!_coinCloseTimes.TryGetValue(coin, out var close) || close == DateTime.MinValue)
                continue;
            if (close.Kind != DateTimeKind.Utc)
                close = DateTime.SpecifyKind(close, DateTimeKind.Utc);
            if (now >= close.AddSeconds(8))
                FreezeCoin(coin, official: false);
        }

        if (_tickCounter % 5 == 0)
            RefreshLiveLineLabel();
        if (_tickCounter % 15 == 0 && _tabControl.SelectedTab?.Text.Contains("PATTERN") == true)
            RefreshPatternGrids();
    }

    void NoticeTicker(string coin, string ticker, DateTime closeUtc)
    {
        if (string.IsNullOrEmpty(ticker) || ticker == "?") return;
        if (_coinTickers.TryGetValue(coin, out var prev) && prev != ticker && !string.IsNullOrEmpty(prev))
        {
            FreezeCoin(coin, official: false);
            _coinFlips[coin] = 0;
            _coinLastSign[coin] = 0;
            _stampedMarks[ticker] = new HashSet<string>();
        }
        _coinTickers[coin] = ticker;
        if (closeUtc != DateTime.MinValue)
            _coinCloseTimes[coin] = closeUtc;
        InvalidateLines(coin);
    }

    void FreezeCoin(string coin, bool official)
    {
        if (!_coinTickers.TryGetValue(coin, out var ticker) || string.IsNullOrEmpty(ticker))
            return;
        if (_frozenTickers.Contains(ticker) && !official)
            return;
        var snap = ComputeLine(coin);
        if (snap == null || !_coinStrikes.TryGetValue(coin, out var strike) || strike <= 0)
            return;
        _coinCloseTimes.TryGetValue(coin, out var close);
        if (close != DateTime.MinValue && close.Kind != DateTimeKind.Utc)
            close = DateTime.SpecifyKind(close, DateTimeKind.Utc);
        var rec = new CoinFinish
        {
            Ticker = ticker,
            Coin = coin,
            Series = CoinSeries.TryGetValue(coin, out var ser) ? ser : "",
            CloseUtc = close,
            Win = LineMath.WindowKey(close),
            Strike = strike,
            Expiration = snap.ExpectedSettle,
            Result = snap.LineSide.ToLowerInvariant(),
            Official = official,
            LineBps = Math.Round(snap.LineBps, 3),
            LastBps = Math.Round(snap.LastBps, 3),
            LineSide = snap.LineSide,
            YesMid = _coinYesMid.TryGetValue(coin, out var mid) ? mid : 0,
            Flips = _coinFlips.TryGetValue(coin, out var f) ? f : 0,
        };
        if (_liveMarks.TryGetValue(ticker, out var liveMarks))
        {
            foreach (var kv in liveMarks)
                rec.Marks[kv.Key] = kv.Value;
        }
        _lab.Remember(rec);
        _frozenTickers.Add(ticker);
        if (!official)
            _pendingOfficial.Add(ticker);
        Log($"LINE freeze {coin} {ticker} {rec.Result.ToUpper()} Δ={LineMath.Money(snap.LineDist, coin)} ({LineMath.Bps(snap.LineBps)} bps)");
    }

    async Task BackfillPatternsAsync()
    {
        if (_patternsBusy) return;
        _patternsBusy = true;
        try
        {
            Log("LINE lab: pulling last ~20 settled windows per coin…");
            var eventsToMark = new List<(string ev, CoinFinish rec)>();
            foreach (var kvp in CoinSeries)
            {
                try
                {
                    var data = await _rest.GetMarketsAsync(status: "settled", seriesTicker: kvp.Value, limit: 20);
                    var mkts = data["markets"] as JArray;
                    var n = 0;
                    if (mkts != null)
                    {
                        foreach (var raw in mkts.OfType<JObject>())
                        {
                            var rec = CoinPatternLab.FromSettledMarket(raw, kvp.Key, kvp.Value);
                            if (rec == null) continue;
                            _lab.Remember(rec);
                            n++;
                            var ev = raw["event_ticker"]?.Value<string>();
                            if (!string.IsNullOrEmpty(ev) && n <= 8)
                                eventsToMark.Add((ev!, rec));
                        }
                    }
                    Log($"LINE lab: {kvp.Key} +{n} settled");
                }
                catch (Exception ex)
                {
                    Log($"LINE lab {kvp.Key} settled: {ex.Message}");
                }
                await Task.Delay(350);
            }

            Log($"LINE lab: reconstructing T-5m/T-2m/T-60s marks on {eventsToMark.Count} windows…");
            foreach (var (ev, rec) in eventsToMark)
            {
                try
                {
                    var live = await _rest.GetLiveSeriesAsync(ev);
                    CoinPatternLab.ApplyMarksFromTimeseries(rec, live.points);
                    _lab.Remember(rec);
                }
                catch (Exception ex)
                {
                    Log($"LINE marks {rec.Coin} {ev}: {ex.Message}");
                }
                await Task.Delay(350);
            }
            _lab.SaveSummary();
            if (IsHandleCreated)
                BeginInvoke(RefreshPatternGrids);
            Log($"LINE lab ready: {_lab.Count} finishes. Open 📐 LINE / PATTERNS.");
        }
        finally { _patternsBusy = false; }
    }

    async Task ConfirmOfficialAsync()
    {
        if (_pendingOfficial.Count == 0) return;
        var batch = _pendingOfficial.Take(4).ToList();
        foreach (var ticker in batch)
        {
            try
            {
                var data = await _rest.GetMarketAsync(ticker);
                var m = data["market"] as JObject ?? data;
                var result = (m["result"]?.Value<string>() ?? "").Trim().ToLowerInvariant();
                var strike = m["floor_strike"]?.Value<double>() ?? 0;
                var expStr = m["expiration_value"]?.ToString();
                double exp = 0;
                double.TryParse(expStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out exp);
                if (result is not ("yes" or "no") && exp > 0 && strike > 0)
                    result = exp >= strike ? "yes" : "no";
                if (result is not ("yes" or "no")) continue;

                var coin = tickerToCoin(ticker);
                var rec = new CoinFinish
                {
                    Ticker = ticker,
                    Coin = coin,
                    Series = CoinSeries.TryGetValue(coin, out var ser) ? ser : "",
                    CloseUtc = LineMath.ParseUtc(m["close_time"]?.Value<string>() ?? ""),
                    Strike = strike,
                    Expiration = exp,
                    Result = result,
                    Official = true,
                    LineBps = exp > 0 && strike > 0 ? Math.Round((exp - strike) / strike * 10_000.0, 3) : 0,
                    LineSide = result == "yes" ? "YES" : "NO",
                };
                rec.Win = LineMath.WindowKey(rec.CloseUtc);
                _lab.Remember(rec);
                _pendingOfficial.Remove(ticker);
                Log($"LINE official {coin} {ticker} → {result.ToUpper()} exp={exp} strike={strike}");
            }
            catch (Exception ex)
            {
                Log($"LINE official {ticker}: {ex.Message}");
            }
            await Task.Delay(200);
        }
        _lab.SaveSummary();
        RefreshPatternGrids();
    }

    void RefreshLiveLineLabel()
    {
        if (_lblLineLive == null) return;
        var yes = 0;
        var no = 0;
        var with = 0;
        var vs = 0;
        var bits = new List<string>();
        var btc = ComputeLine("BTC");
        foreach (var coin in AllCoins)
        {
            var s = ComputeLine(coin);
            if (s == null) continue;
            if (s.LineSide == "YES") yes++;
            else if (s.LineSide == "NO") no++;
            bits.Add($"{coin} {s.LineSide} {LineMath.Money(s.LineDist, coin)} ({LineMath.Bps(s.LineBps)})");
            if (coin != "BTC" && btc != null && !string.IsNullOrEmpty(btc.LineSide))
            {
                if (s.LineSide == btc.LineSide) with++;
                else vs++;
            }
        }
        var disagree = AllCoins.Select(ComputeLine).Count(s => s?.Disagree == true);
        _lblLineLive.Text =
            $"LIVE  YES {yes} / NO {no}   ·   vs BTC: {with} WITH, {vs} AGAINST   ·   last-tick disagrees with 60s-avg on {disagree} coin(s)\r\n"
            + string.Join("   ", bits) + "\r\n"
            + "Middle line = floor_strike (open 60s avg). Δ LINE = expected close 60s avg − that line. Need 12 windows to trust a rule.";
    }

    void RefreshPatternGrids()
    {
        if (_pairGrid == null || _ruleGrid == null || _recentGrid == null || _lab == null)
            return;
        try
        {
        var (pairs, rules, recent, windows) = _lab.Snapshot();

        _pairGrid.SuspendLayout();
        _pairGrid.Rows.Clear();
        foreach (var p in pairs)
        {
            var note = p.N >= CoinPatternLab.MinTrust
                ? (p.SamePct >= 0.7 ? "often together" : p.SamePct <= 0.4 ? "often split" : "coin-flip together")
                : $"need {CoinPatternLab.MinTrust - p.N} more";
            var i = _pairGrid.Rows.Add($"{p.A} ↔ {p.B}", p.N, p.Same, p.Split, $"{p.SamePct * 100:F0}%", note);
            if (p.N >= CoinPatternLab.MinTrust)
                _pairGrid.Rows[i].Cells["Same %"].Style.ForeColor =
                    p.SamePct >= 0.7 ? Color.ForestGreen : p.SamePct <= 0.4 ? Color.Firebrick : Color.Black;
        }
        _pairGrid.ResumeLayout();

        _ruleGrid.SuspendLayout();
        _ruleGrid.Rows.Clear();
        foreach (var r in rules)
        {
            var pct = r.Pct == null ? "—" : $"{r.Pct * 100:F0}%";
            var ready = r.Ready ? "YES" : $"n<{CoinPatternLab.MinTrust}";
            var i = _ruleGrid.Rows.Add(r.Label, r.N, r.Hit, pct, ready, r.Note);
            if (r.Ready && r.Pct >= 0.7)
                _ruleGrid.Rows[i].DefaultCellStyle.ForeColor = Color.ForestGreen;
            if (r.Ready && r.Pct <= 0.4 && r.N >= CoinPatternLab.MinTrust)
                _ruleGrid.Rows[i].DefaultCellStyle.ForeColor = Color.Firebrick;
        }
        _ruleGrid.ResumeLayout();

        _recentGrid.SuspendLayout();
        _recentGrid.Rows.Clear();
        foreach (var (win, coins) in recent)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(win).UtcDateTime.ToString("HH:mm") + " UTC";
            var line = string.Join("  ", coins.Select(c =>
                $"{c.Coin} {(c.Result == "yes" ? "YES" : "NO")}{(c.Official ? "" : "?")}"));
            _recentGrid.Rows.Add(when, line);
        }
        _recentGrid.ResumeLayout();

        if (_lblLineLive != null)
            RefreshLiveLineLabel();
        _ = windows;
        }
        catch (Exception ex)
        {
            try { Log($"LINE grid: {ex.Message}"); } catch { /* ignore */ }
        }
    }

    async Task FetchAllLiveSpotsAsync()
    {
        // Previously skipped while the settled-history backfill ran (~1 min after launch),
        // leaving 7 coins with no spot price. Request volume is well under the rate limit,
        // so run both; just don't overlap with our own previous poll.
        if (_spotFetchBusy) return;
        _spotFetchBusy = true;
        try
        {
            // Non-CF coins always. CF coins: once to seed the 60s buffer, then
            // again inside the last 75s so the average uses Kalshi's 1s series.
            var want = new List<string>();
            foreach (var coin in MissingFromCf) want.Add(coin);
            foreach (var coin in new[] { "BTC", "ETH", "SOL", "DOGE", "XRP" })
            {
                if (!_seededCf) { want.Add(coin); continue; }
                if (!_coinCloseTimes.TryGetValue(coin, out var close) || close == DateTime.MinValue)
                    continue;
                if (close.Kind != DateTimeKind.Utc) close = DateTime.SpecifyKind(close, DateTimeKind.Utc);
                var tte = (close - DateTime.UtcNow).TotalSeconds;
                if (tte <= 75) want.Add(coin);
            }

            // All coins in parallel; each continuation resumes on the UI thread
            var jobs = want.Distinct().Select(async coin =>
            {
                if (!_coinEventTickers.TryGetValue(coin, out var evt) || string.IsNullOrEmpty(evt))
                    return false;
                try
                {
                    var live = await _rest.GetLiveSeriesAsync(evt, recentOnly: true);
                    if (live.spot <= 0) return false;
                    _spotPrices[coin] = live.spot;
                    SeedHistory(coin, live.points);
                    AppendSpot(coin, DateTime.UtcNow, live.spot);
                    return coin is "BTC" or "ETH" or "SOL" or "DOGE" or "XRP";
                }
                catch { return false; /* next coin */ }
            });
            var seeded = await Task.WhenAll(jobs);
            if (seeded.Any(x => x)) _seededCf = true;
        }
        finally { _spotFetchBusy = false; }
    }
}
