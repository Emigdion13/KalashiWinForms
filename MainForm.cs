using Newtonsoft.Json.Linq;
using System.Data;
using System.Reflection;

namespace KalashiWinForms;

public partial class MainForm : Form
{
    private readonly KalshiRestClient _rest;
    private readonly KalshiConfig _config;
    private KalshiWebSocketClient? _wsClient;
    private DataGridView _grid = null!;
    private ComboBox _coinCombo = null!;
    private RadioButton _rbUp = null!, _rbDown = null!;
    private TextBox _txtAmount = null!, _txtPrice = null!, _txtProfit = null!;
    private CheckBox _chkMarketSell = null!;
    private CheckBox _chkPaperTrade = null!;
    private Button _btnBuy = null!, _btnSell = null!, _btnCancelOrders = null!, _btnRefresh = null!;
    private Label _lblBalance = null!, _lblStatus = null!, _lblTimer = null!, _lblUtc = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;
    private DataGridView _ordersGrid = null!;
    private DataGridView _positionsGrid = null!;
    private TabControl _tabControl = null!;
    private bool _isRefreshing;
    private bool _wsFallback;   // WS down → poll REST from the 1s timer and retry WS
    private bool _wsConnecting;
    private int _tickCounter;
    private readonly List<string> _log = new();
    private TextBox _txtLog = null!;
    private TextBox _txtLogFull = null!;
    private readonly Dictionary<string, string> _tickerToCoin = new();
    private readonly Dictionary<string, DateTime> _coinCloseTimes = new();
    private readonly Dictionary<string, double> _coinStrikes = new();
    private readonly Dictionary<string, double> _spotPrices = new();
    private readonly Dictionary<string, double> _orderPressure = new(); // 0-1, >0.5 = UP pressure
    private readonly Dictionary<string, string> _coinEventTickers = new();
    private readonly Dictionary<string, double> _livePrices = new(); // ticker → last price
    private List<(string ticker, string side, double count, double cost)> _cachedPositions = new();

    // CF Benchmarks index → coin (Kalshi native WebSocket)
    private static readonly Dictionary<string, string> SpotToCoin = new()
    {
        ["BRTI"] = "BTC", ["ETHUSD_RTI"] = "ETH", ["SOLUSD_RTI"] = "SOL",
        ["DOGEUSD_RTI"] = "DOGE", ["XRPUSD_RTI"] = "XRP"
    };
    // CryptoCompare symbols for coins not covered by CF Benchmarks
    private static readonly string[] MissingFromCf = { "BNB", "ADA", "NEAR", "TON", "BCH", "ZEC", "HYPE" };

    private static readonly Dictionary<string, string> CoinSeries = new()
    {
        ["BTC"] = "KXBTC15M", ["ETH"] = "KXETH15M", ["SOL"] = "KXSOL15M",
        ["BNB"] = "KXBNB15M", ["XRP"] = "KXXRP15M", ["NEAR"] = "KXNEAR15M",
        ["ZEC"] = "KXZEC15M", ["TON"] = "KXTON15M", ["BCH"] = "KXBCH15M",
        ["ADA"] = "KXADA15M", ["DOGE"] = "KXDOGE15M", ["HYPE"] = "KXHYPE15M"
    };

    public MainForm()
    {
        var envPath = Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath) ?? ".",
            ".env");

        // Fallback: look in this project folder
        if (!File.Exists(envPath))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var dir in new[]
            {
                Directory.GetCurrentDirectory(),
                Path.Combine(home, "OneDrive", "Desktop", "KalashiWinForms"),
                Path.Combine(home, "OneDrive", "Desktop", "projects", "KalashiWinForms"),
            })
            {
                var cand = Path.Combine(dir, ".env");
                if (File.Exists(cand)) { envPath = cand; break; }
            }
        }

        _config = KalshiConfig.FromEnvFile(envPath);
        _rest = new KalshiRestClient(_config);
        _rest.DebugLog = msg => Log(msg);

        InitPatterns();
        InitializeComponent();
        _ = InitializeAsync();
    }

    private void InitializeComponent()
    {
        Text = "Kalashi — 15m + LINE lab";
        Size = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        // ── Top bar ──
        var topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Color.FromArgb(30, 30, 30)
        };

        _lblBalance = new Label
        {
            Text = "💰 Balance: --",
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(10, 8),
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        _lblTimer = new Label
        {
            Text = "⏱ --:--",
            ForeColor = Color.Orange,
            AutoSize = true,
            Location = new Point(350, 8),
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };

        _lblUtc = new Label
        {
            Text = "",
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(480, 8),
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };

        _btnRefresh = new Button
        {
            Text = "🔄 Refresh",
            Location = new Point(820, 4),
            Size = new Size(100, 28),
            FlatStyle = FlatStyle.Flat
        };
        _btnRefresh.Click += async (_, _) => await RefreshAllAsync();

        _chkPaperTrade = new CheckBox
        {
            Text = "🧪 Paper",
            Location = new Point(925, 4),
            Width = 70,
            ForeColor = Color.Cyan,
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            Checked = false
        };

        _lblStatus = new Label
        {
            Text = "",
            ForeColor = Color.LightGray,
            AutoSize = true,
            Location = new Point(620, 8)
        };

        topBar.Controls.Add(_lblBalance);
        topBar.Controls.Add(_lblTimer);
        topBar.Controls.Add(_lblUtc);
        topBar.Controls.Add(_btnRefresh);
        topBar.Controls.Add(_chkPaperTrade);
        topBar.Controls.Add(_lblStatus);

        // ── Tab control ──
        _tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10)
        };

        var marketsTab = new TabPage("📊 Markets");
        var ordersTab = new TabPage("📋 Orders & Portfolio");

        // ── Markets Tab ──
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.WhiteSmoke,
            ForeColor = Color.Black,
            GridColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Consolas", 11),
            RowTemplate = { Height = 28 }
        };

        var right = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight };
        var center = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter };
        _grid.Columns.AddRange(
            new DataGridViewTextBoxColumn { Name = "Ticker", HeaderText = "Ticker", FillWeight = 22 },
            new DataGridViewTextBoxColumn { Name = "UP%", HeaderText = "UP%", FillWeight = 8, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Color.LimeGreen, Alignment = DataGridViewContentAlignment.MiddleCenter } },
            new DataGridViewTextBoxColumn { Name = "Coin", HeaderText = "Coin", FillWeight = 7, DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 11, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleRight } },
            new DataGridViewTextBoxColumn { Name = "DOWN%", HeaderText = "DOWN%", FillWeight = 8, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Color.Tomato, Alignment = DataGridViewContentAlignment.MiddleCenter } },
            new DataGridViewTextBoxColumn { Name = "LineDelta", HeaderText = "Δ LINE", FillWeight = 14, DefaultCellStyle = right },
            new DataGridViewTextBoxColumn { Name = "LastDelta", HeaderText = "Δ last", FillWeight = 12, DefaultCellStyle = right },
            new DataGridViewTextBoxColumn { Name = "Bps", HeaderText = "bps", FillWeight = 7, DefaultCellStyle = right },
            new DataGridViewTextBoxColumn { Name = "LineSide", HeaderText = "SIDE", FillWeight = 7, DefaultCellStyle = center },
            new DataGridViewTextBoxColumn { Name = "VsBtc", HeaderText = "vs BTC", FillWeight = 8, DefaultCellStyle = center },
            new DataGridViewTextBoxColumn { Name = "Volume", HeaderText = "Volume", FillWeight = 8, DefaultCellStyle = right }
        );

        marketsTab.Controls.Add(_grid);

        // ── Positions summary grid (bottom of Markets tab) ──
        _positionsGrid = new DataGridView
        {
            Dock = DockStyle.Bottom,
            Height = 100,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.FromArgb(240, 240, 240),
            ForeColor = Color.Black,
            GridColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Consolas", 9),
            RowTemplate = { Height = 24 }
        };
        _positionsGrid.Columns.AddRange(
            new DataGridViewTextBoxColumn { Name = "PosCoin", HeaderText = "Coin", FillWeight = 8, DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleRight } },
            new DataGridViewTextBoxColumn { Name = "PosSide", HeaderText = "Side", FillWeight = 8, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter } },
            new DataGridViewTextBoxColumn { Name = "PosCount", HeaderText = "Contracts", FillWeight = 12, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } },
            new DataGridViewTextBoxColumn { Name = "PosCost", HeaderText = "Cost", FillWeight = 12, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } },
            new DataGridViewTextBoxColumn { Name = "PosValue", HeaderText = "Value", FillWeight = 12, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } },
            new DataGridViewTextBoxColumn { Name = "PosPnL", HeaderText = "P&L", FillWeight = 12, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } }
        );
        marketsTab.Controls.Add(_positionsGrid);

        // ── Orders Tab ──
        _ordersGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.WhiteSmoke,
            ForeColor = Color.Black,
            GridColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Consolas", 10),
            RowTemplate = { Height = 26 }
        };

        _ordersGrid.Columns.AddRange(
            new DataGridViewTextBoxColumn { Name = "OrderId", HeaderText = "Order ID", FillWeight = 20 },
            new DataGridViewTextBoxColumn { Name = "Ticker", HeaderText = "Ticker", FillWeight = 18 },
            new DataGridViewTextBoxColumn { Name = "Side", HeaderText = "Side", FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "Count", FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "Price", FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", FillWeight = 12 }
        );

        ordersTab.Controls.Add(_ordersGrid);

        // ── Logs Tab ──
        var logsTab = new TabPage("📜 Logs");
        _txtLogFull = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.Orange,
            Font = new Font("Consolas", 9),
            WordWrap = false
        };
        // Lines logged before the textbox existed (e.g. LINE lab init)
        if (_log.Count > 0)
            _txtLogFull.Text = string.Join(Environment.NewLine, _log) + Environment.NewLine;
        logsTab.Controls.Add(_txtLogFull);

        EnableDoubleBuffering(_grid);
        EnableDoubleBuffering(_positionsGrid);
        EnableDoubleBuffering(_ordersGrid);

        _tabControl.TabPages.Add(marketsTab);
        _tabControl.TabPages.Add(ordersTab);
        _tabControl.TabPages.Add(logsTab);
        try { BuildPatternsUi(); }
        catch (Exception ex) { Log($"LINE tab failed to build: {ex.Message}"); }

        // ── Bottom trading panel ──
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 80,
            BackColor = Color.FromArgb(40, 40, 40),
            Padding = new Padding(10)
        };

        _coinCombo = new ComboBox
        {
            Location = new Point(10, 25),
            Width = 80,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };
        _coinCombo.Items.AddRange(CoinSeries.Keys.ToArray());
        _coinCombo.SelectedIndex = 0;

        _rbUp = new RadioButton
        {
            Text = "📈 UP",
            Location = new Point(100, 28),
            Width = 70,
            ForeColor = Color.LimeGreen,
            Checked = true
        };
        _rbDown = new RadioButton
        {
            Text = "📉 DOWN",
            Location = new Point(175, 28),
            Width = 80,
            ForeColor = Color.Tomato
        };

        var lblAmount = new Label
        {
            Text = "Amount ($):",
            Location = new Point(270, 5),
            ForeColor = Color.LightGray,
            AutoSize = true,
            Font = new Font("Segoe UI", 8)
        };
        _txtAmount = new TextBox
        {
            Location = new Point(270, 22),
            Width = 80,
            Text = "10",
            Font = new Font("Segoe UI", 11)
        };

        var lblPrice = new Label
        {
            Text = "Limit (¢, optional):",
            Location = new Point(360, 5),
            ForeColor = Color.LightGray,
            AutoSize = true,
            Font = new Font("Segoe UI", 8)
        };
        _txtPrice = new TextBox
        {
            Location = new Point(360, 22),
            Width = 70,
            Font = new Font("Segoe UI", 11)
        };

        var lblProfit = new Label
        {
            Text = "Profit %:",
            Location = new Point(440, 5),
            ForeColor = Color.LightGray,
            AutoSize = true,
            Font = new Font("Segoe UI", 8)
        };
        _txtProfit = new TextBox
        {
            Location = new Point(440, 22),
            Width = 60,
            Text = "20",
            Font = new Font("Segoe UI", 11)
        };

        _chkMarketSell = new CheckBox
        {
            Text = "MKT",
            Location = new Point(505, 24),
            Width = 50,
            ForeColor = Color.Orange,
            Font = new Font("Segoe UI", 8, FontStyle.Bold)
        };

        _btnBuy = new Button
        {
            Text = "🟢 BUY",
            Location = new Point(560, 20),
            Size = new Size(90, 35),
            BackColor = Color.FromArgb(0, 140, 0),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        _btnBuy.Click += async (_, _) => await PlaceOrderAsync("buy");

        _btnSell = new Button
        {
            Text = "🔴 SELL",
            Location = new Point(660, 20),
            Size = new Size(90, 35),
            BackColor = Color.FromArgb(180, 30, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        _btnSell.Click += async (_, _) => await SellPositionAsync();

        _btnCancelOrders = new Button
        {
            Text = "❌ Cancel",
            Location = new Point(755, 20),
            Size = new Size(80, 35),
            BackColor = Color.FromArgb(100, 100, 100),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8, FontStyle.Bold)
        };
        _btnCancelOrders.Click += async (_, _) => await CancelOrdersAsync();

        var lblHint = new Label
        {
            Text = "Buy: $ amount | Limit: ¢ | Profit % | MKT = market sell | ❌ = cancel open orders",
            Location = new Point(750, 28),
            AutoSize = true,
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8)
        };

        bottomPanel.Controls.AddRange(new Control[] {
            _coinCombo, _rbUp, _rbDown, lblAmount, _txtAmount,
            lblPrice, _txtPrice, lblProfit, _txtProfit, _chkMarketSell, _btnBuy, _btnSell, _btnCancelOrders, lblHint
        });

        // ── Timer (only updates countdown timers, WebSocket handles prices) ──
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => UpdateTimersOnly();
        _refreshTimer.Start();

        // ── Layout ──
        Controls.Add(_tabControl);
        Controls.Add(topBar);
        Controls.Add(bottomPanel);

        // ── Log panel (bottom strip) ──
        _txtLog = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            ReadOnly = true,
            Multiline = false,
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.Orange,
            Font = new Font("Consolas", 8),
            BorderStyle = BorderStyle.None
        };
        Controls.Add(_txtLog);

        _grid.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                var coin = _grid.Rows[e.RowIndex].Cells["Coin"].Value?.ToString();
                if (coin != null && CoinSeries.ContainsKey(coin))
                    _coinCombo.SelectedItem = coin;

                // Set radio button based on which column was clicked
                var colName = _grid.Columns[e.ColumnIndex].Name;
                if (colName == "UP%")
                    _rbUp.Checked = true;
                else if (colName == "DOWN%")
                    _rbDown.Checked = true;
            }
        };

        // Clear selection when clicking empty space
        _grid.MouseDown += (_, e) =>
        {
            var hit = _grid.HitTest(e.X, e.Y);
            if (hit.Type == DataGridViewHitTestType.None)
                _grid.ClearSelection();
        };

        FormClosing += async (_, _) =>
        {
            _refreshTimer.Stop();
            try { _lab.SaveSummary(); } catch { }
            if (_wsClient != null)
                await _wsClient.DisconnectAsync();
            _wsClient?.Dispose();
        };
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.UtcNow:HH:mm:ss}] {msg}";
        _log.Add(line);
        var trimmed = false;
        if (_log.Count > 500)
        {
            // Trim in chunks so the full textbox is only rebuilt once per 100 lines
            _log.RemoveRange(0, _log.Count - 400);
            trimmed = true;
        }
        if (_txtLog != null)
            _txtLog.Text = line;
        if (_txtLogFull != null)
        {
            if (trimmed)
            {
                _txtLogFull.Text = string.Join(Environment.NewLine, _log) + Environment.NewLine;
                _txtLogFull.SelectionStart = _txtLogFull.TextLength;
                _txtLogFull.ScrollToCaret();
            }
            else
            {
                _txtLogFull.AppendText(line + Environment.NewLine);
            }
        }
    }

    private static void EnableDoubleBuffering(DataGridView grid) =>
        typeof(DataGridView)
            .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(grid, true);

    // Writing an unchanged value still repaints the cell — skip those
    private static void SetCell(DataGridViewCell cell, object? value)
    {
        if (!Equals(cell.Value, value))
            cell.Value = value;
    }

    // ── Init ──

    private async Task InitializeAsync()
    {
        await RefreshAllAsync();
        _ = StartWebSocketAsync();
    }

    private async Task StartWebSocketAsync()
    {
        if (_wsConnecting) return;
        _wsConnecting = true;
        try
        {
            // Only the current window's tickers — _tickerToCoin also holds every expired one
            var tickers = _coinTickers.Values.Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
            if (tickers.Count == 0)
            {
                _wsFallback = true;
                return;
            }

            // Drop the old client; its late events are ignored by the ws != _wsClient checks
            _wsClient?.Dispose();
            var ws = new KalshiWebSocketClient(_config);
            _wsClient = ws;
            ws.TickerUpdated += msg => { if (ws == _wsClient) HandleTickerUpdate(msg); };
            ws.SpotUpdated += (key, price) =>
            {
                if (ws != _wsClient) return;
                if (SpotToCoin.TryGetValue(key, out var coin))
                {
                    _spotPrices[coin] = price;
                    AppendSpot(coin, DateTime.UtcNow, price);
                }
            };
            ws.LogMessage += msg =>
            {
                if (ws != _wsClient) return;
                _lblStatus.Text = msg;
                if (msg.Contains("Error") || msg.Contains("error"))
                    Log(msg);
            };
            ws.ConnectionLost += _ =>
            {
                if (ws != _wsClient) return;
                _wsFallback = true;
                _lblStatus.Text = "🔌 WS disconnected — REST fallback, reconnecting...";
            };

            await ws.ConnectAsync(tickers);
            _wsFallback = false;
            _lblStatus.Text = $"⚡ WebSocket live — {tickers.Count} tickers";
        }
        catch (Exception ex)
        {
            _wsFallback = true;
            _lblStatus.Text = $"WS: {ex.Message} (REST fallback active)";
        }
        finally { _wsConnecting = false; }
    }

    private void HandleTickerUpdate(JObject msg)
    {
        try
        {
            var ticker = msg["msg"]?["market_ticker"]?.Value<string>();
            if (ticker == null || !_tickerToCoin.TryGetValue(ticker, out var coin))
                return;

            var m = msg["msg"]!;
            var upPct = SafePct(m["yes_bid_dollars"]);
            var downPct = SafePct(m["no_bid_dollars"]);
            // WebSocket ticker may not have no_bid_dollars; compute it
            if (m["no_bid_dollars"] == null && m["yes_ask_dollars"] != null)
            {
                var yesAsk = m["yes_ask_dollars"]!.Value<double>();
                downPct = $"{(1.0 - yesAsk) * 100:F1}%";
            }
            var last = SafePct(m["price_dollars"] ?? m["last_price_dollars"]);
            var volume = FormatVolume(m["volume_fp"] ?? m["volume"]);

            // Store live price for P&L calculation
            var priceDollars = m["price_dollars"]?.Value<double>();
            if (priceDollars.HasValue)
            {
                _livePrices[ticker] = priceDollars.Value;
                _coinYesMid[coin] = priceDollars.Value;
            }
            else if (double.TryParse(upPct.TrimEnd('%'), out var upV))
                _coinYesMid[coin] = upV / 100.0;

            // Update grid in-place — find row by coin name
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Cells["Coin"].Value?.ToString() == coin)
                {
                    SetCell(row.Cells["UP%"], upPct);
                    SetCell(row.Cells["DOWN%"], downPct);
                    SetCell(row.Cells["Volume"], volume);
                    ApplyRowColor(row, upPct);
                    break;
                }
            }
        }
        catch { }
    }

    private string? _lastTickError;

    // async void timer handler: an exception escaping here kills the whole app, so catch and log
    private async void UpdateTimersOnly()
    {
        try
        {
            await UpdateTimersCoreAsync();
        }
        catch (Exception ex)
        {
            var err = $"{ex.GetType().Name}: {ex.Message}";
            if (err != _lastTickError) // same error every second would flood the log
                Log($"Timer tick error: {err}");
            _lastTickError = err;
        }
    }

    private async Task UpdateTimersCoreAsync()
    {
        _tickCounter++;
        var now = DateTime.UtcNow;
        InvalidateLines(); // time moved — recompute each coin's LINE once for this tick

        // Update UTC time label
        _lblUtc.Text = $"{now:HH:mm:ss} UTC";

        // Find earliest remaining time for countdown label (subtract 6s server lag)
        var timerText = "--:--";
        foreach (var closeDt in _coinCloseTimes.Values)
        {
            var remaining = (int)(closeDt - now).TotalSeconds - 6;
            if (remaining > 0)
            {
                timerText = $"{remaining / 60:D2}:{remaining % 60:D2}";
                break;
            }
            else if (remaining > -66)
            {
                timerText = "rolling...";
            }
        }
        _lblTimer.Text = $"⏱ {timerText}";

        // Grid cells only matter while the Markets tab is on screen
        if (_tabControl.SelectedIndex == 0)
        {
            // Update line distance for all rows (settle-avg, not last tick)
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var coin = row.Cells["Coin"].Value?.ToString();
                if (coin == null) continue;
                SetCell(row.Cells["LineDelta"], FormatSpotDist(coin));
                SetCell(row.Cells["LastDelta"], FormatLastDist(coin));
                SetCell(row.Cells["Bps"], FormatBps(coin));
                SetCell(row.Cells["LineSide"], FormatSide(coin));
                SetCell(row.Cells["VsBtc"], FormatVsBtc(coin));
                ColorLineCells(row, coin);
            }

            // Live P&L in positions grid
            RenderPositionsGrid();
        }

        TickLineLab();

        // WS down: poll markets over REST every 3s and retry the socket every 10s
        if (_wsFallback)
        {
            if (_tickCounter % 3 == 0)
                _ = RefreshMarketsAsync();
            if (_tickCounter % 10 == 0)
                _ = StartWebSocketAsync();
        }

        // Every 30 seconds, check for new markets and refresh balance + orders + positions
        if (_tickCounter % 30 == 0)
        {
            await CheckForNewMarketsAsync();
            _ = RefreshBalanceAsync();
            _ = RefreshOrdersAsync();
            _ = RefreshPositionsGridAsync();
            _ = ConfirmOfficialAsync();
        }

        // Every 5 seconds, fetch Kalshi live_data timeseries (the real CF path).
        // Offset from the 30s batch so the two request bursts don't land in the same second.
        if (_tickCounter % 5 == 2)
            _ = FetchSpotPricesAsync();
    }

    private async Task FetchSpotPricesAsync()
    {
        // Kalshi live_data.details.timeseries[] {t,v} is the CF 1s path.
        // The old code looked for current_price/value/spot — those fields
        // are not there, so BNB/ADA/NEAR/TON/BCH/ZEC/HYPE Δ was blank/wrong.
        await FetchAllLiveSpotsAsync();
    }

    private async Task CheckForNewMarketsAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var tasks = CoinSeries.Select(async kvp =>
            {
                try
                {
                    var data = await _rest.GetMarketsAsync(seriesTicker: kvp.Value, limit: 1, status: "open");
                    var mkts = data["markets"] as JArray;
                    var m = mkts?.FirstOrDefault() as JObject;
                    if (m == null) return null;

                    var closeStr = m["close_time"]?.Value<string>() ?? "";
                    DateTime closeDt = DateTime.MinValue;
                    if (!string.IsNullOrEmpty(closeStr))
                        closeDt = LineMath.ParseUtc(closeStr);

                    return new { Coin = kvp.Key, Ticker = m["ticker"]?.Value<string>() ?? "?", CloseTime = closeDt };
                }
                catch { return null; }
            });

            var results = await Task.WhenAll(tasks);
            var newTickers = new List<string>();

            foreach (var r in results)
            {
                if (r == null || r.Ticker == "?") continue;
                if (!_tickerToCoin.ContainsKey(r.Ticker))
                    newTickers.Add(r.Ticker);
                _tickerToCoin[r.Ticker] = r.Coin;
                if (r.CloseTime != DateTime.MinValue)
                    _coinCloseTimes[r.Coin] = r.CloseTime;
                NoticeTicker(r.Coin, r.Ticker, r.CloseTime);
            }

            if (newTickers.Count > 0 && _wsClient?.IsConnected == true)
            {
                Log($"Adding {newTickers.Count} new tickers to WS");
                try { await _wsClient.AddMarketsAsync(newTickers); }
                catch (Exception ex) { Log($"AddMarkets failed: {ex.Message}"); }
                _lblStatus.Text = $"⚡ +{newTickers.Count} new markets via WS";
            }
        }
        catch { }
        finally { _isRefreshing = false; }
    }

    // ── Refresh ──

    private async Task RefreshAllAsync()
    {
        // Independent requests — run them side by side instead of one after another
        await Task.WhenAll(
            RefreshMarketsAsync(),
            RefreshBalanceAsync(),
            RefreshOrdersAsync(),
            RefreshPositionsGridAsync());
    }

    private async Task RefreshMarketsAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            _lblStatus.Text = $"⏰ {DateTime.UtcNow:HH:mm:ss} UTC — refreshing...";

            var tasks = CoinSeries.Select(async kvp =>
            {
                try
                {
                    var data = await _rest.GetMarketsAsync(seriesTicker: kvp.Value, limit: 1, status: "open");
                    var mkts = data["markets"] as JArray;
                    var m = mkts?.FirstOrDefault() as JObject;
                    if (m == null) return null;

                    var now = DateTime.UtcNow;
                    var closeStr = m["close_time"]?.Value<string>() ?? "";
                    var timer = "";
                    DateTime closeDt = DateTime.MinValue;
                    if (!string.IsNullOrEmpty(closeStr))
                    {
                        closeDt = LineMath.ParseUtc(closeStr);
                        var remaining = (int)(closeDt - now).TotalSeconds;
                        timer = remaining < 0 ? "closed" : $"{remaining / 60}:{remaining % 60:D2}";
                    }

                    var ticker = m["ticker"]?.Value<string>() ?? "?";
                    var strike = m["floor_strike"]?.Value<double>() ?? 0;
                    var eventTicker = m["event_ticker"]?.Value<string>() ?? "";
                    var yesBidSize = m["yes_bid_size_fp"]?.Value<double>() ?? 0;
                    var yesAskSize = m["yes_ask_size_fp"]?.Value<double>() ?? 0;

                    return new
                    {
                        Coin = kvp.Key,
                        Ticker = ticker,
                        UpPct = SafePct(m["yes_bid_dollars"]),
                        DownPct = SafePct(m["no_bid_dollars"]),
                        Edge = FormatEdge(m["yes_bid_dollars"]),
                        Last = SafePct(m["last_price_dollars"]),
                        Volume = FormatVolume(m["volume_fp"] ?? m["volume"]),
                        Timer = timer,
                        CloseTime = closeDt,
                        Strike = strike,
                        EventTicker = eventTicker,
                        YesBidSize = yesBidSize,
                        YesAskSize = yesAskSize
                    };
                }
                catch
                {
                    return null;
                }
            });

            var results = await Task.WhenAll(tasks);
            var rows = results.Where(r => r != null).ToList();

            // Store ticker→coin and close times for WebSocket + timer
            var newTickersForWs = new List<string>();
            // Don't Clear(): a coin whose fetch failed this round keeps its last known close time
            foreach (var r in rows)
            {
                if (r!.Ticker != "?")
                {
                    if (!_tickerToCoin.ContainsKey(r.Ticker))
                        newTickersForWs.Add(r.Ticker);
                    _tickerToCoin[r.Ticker] = r.Coin;
                }
                if (r.CloseTime != DateTime.MinValue)
                    _coinCloseTimes[r.Coin] = r.CloseTime;
                if (r.Strike > 0)
                    _coinStrikes[r.Coin] = r.Strike;
                if (!string.IsNullOrEmpty(r.EventTicker))
                    _coinEventTickers[r.Coin] = r.EventTicker;
                NoticeTicker(r.Coin, r.Ticker, r.CloseTime);
                // Compute orderbook pressure: yes_bid vs yes_ask (ask = selling YES = buying NO)
                var total = r.YesBidSize + r.YesAskSize;
                if (total > 0)
                    _orderPressure[r.Coin] = r.YesBidSize / total;
            }
            InvalidateLines(); // strikes / close times may have changed

            // Save current selection before modifying the grid
            string? selectedCoin = null;
            if (_grid.CurrentRow != null && _grid.CurrentRow.Index >= 0)
                selectedCoin = _grid.CurrentRow.Cells["Coin"].Value?.ToString();

            // Save vertical scroll position
            int scrollIndex = _grid.FirstDisplayedScrollingRowIndex;

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var r in rows)
            {
                var idx = _grid.Rows.Add(
                    r!.Ticker, r.UpPct, r.Coin, r.DownPct,
                    FormatSpotDist(r.Coin), FormatLastDist(r.Coin), FormatBps(r.Coin),
                    FormatSide(r.Coin), FormatVsBtc(r.Coin), r.Volume);
                ApplyRowColor(_grid.Rows[idx], r.UpPct);
                ColorLineCells(_grid.Rows[idx], r.Coin);
            }

            // Restore selection to the same coin
            if (selectedCoin != null)
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.Cells["Coin"].Value?.ToString() == selectedCoin)
                    {
                        row.Selected = true;
                        _grid.CurrentCell = row.Cells[0];
                        break;
                    }
                }
            }

            // Restore scroll position
            if (scrollIndex >= 0 && scrollIndex < _grid.Rows.Count)
                _grid.FirstDisplayedScrollingRowIndex = scrollIndex;

            _grid.ResumeLayout();

            _lblStatus.Text = $"⏰ {DateTime.UtcNow:HH:mm:ss} UTC — {rows.Count} markets";

            if (!_backfillStarted)
            {
                _backfillStarted = true;
                _ = BackfillPatternsAsync();
            }

            // Re-subscribe WebSocket with any new tickers
            if (newTickersForWs.Count > 0 && _wsClient?.IsConnected == true)
            {
                Log($"Manual refresh: adding {newTickersForWs.Count} tickers");
                try { await _wsClient.AddMarketsAsync(newTickersForWs); }
                catch (Exception ex) { Log($"AddMarkets failed: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"❌ {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task RefreshBalanceAsync()
    {
        try
        {
            var data = await _rest.GetBalanceAsync();
            var balance = data["balance"]?.Value<decimal>() ?? 0;
            var avail = data["available_balance"]?.Value<decimal>() ?? balance;
            _lblBalance.Text = $"💰 Balance: ${balance / 100:N2}  |  Available: ${avail / 100:N2}";
        }
        catch
        {
            _lblBalance.Text = "💰 Balance: --";
        }
    }

    private async Task RefreshOrdersAsync()
    {
        try
        {
            var orders = await _rest.GetOrdersAsync(limit: 50);

            // Save current selection
            string? selectedOrderId = null;
            if (_ordersGrid.CurrentRow != null && _ordersGrid.CurrentRow.Index >= 0)
                selectedOrderId = _ordersGrid.CurrentRow.Cells["OrderId"].Value?.ToString();

            _ordersGrid.SuspendLayout();
            _ordersGrid.Rows.Clear();
            foreach (var o in orders)
            {
                var price = o["price"] != null ? $"{(decimal)o["price"]! / 100:F2}¢" : "market";
                _ordersGrid.Rows.Add(
                    o["order_id"]?.Value<string>()?[..Math.Min(20, (o["order_id"]?.Value<string>() ?? "").Length)],
                    o["ticker"]?.Value<string>(),
                    o["side"]?.Value<string>()?.ToUpper(),
                    o["count"]?.Value<int>(),
                    price,
                    o["status"]?.Value<string>()
                );
            }

            // Restore selection
            if (selectedOrderId != null)
            {
                foreach (DataGridViewRow row in _ordersGrid.Rows)
                {
                    if (row.Cells["OrderId"].Value?.ToString() == selectedOrderId)
                    {
                        row.Selected = true;
                        _ordersGrid.CurrentCell = row.Cells[0];
                        break;
                    }
                }
            }

            _ordersGrid.ResumeLayout();
        }
        catch { }
    }

    private async Task RefreshPositionsGridAsync()
    {
        try
        {
            var positions = await _rest.GetPositionsAsync();
            var list = new List<(string ticker, string side, double count, double cost)>();
            foreach (var p in positions)
            {
                var ticker = p["ticker"]?.Value<string>() ?? "";
                var pf = p["position_fp"]?.Value<string>() ?? "0";
                var costStr = p["total_traded_dollars"]?.Value<string>() ?? "0";
                if (!double.TryParse(pf, out var pos) || !double.TryParse(costStr, out var cost))
                    continue;
                var abs = Math.Abs(pos);
                if (abs < 0.001) continue;
                var side = pos > 0 ? "UP" : "DOWN";
                list.Add((ticker, side, abs, cost));
            }
            _cachedPositions = list;
            RenderPositionsGrid();
        }
        catch { }
    }

    private void RenderPositionsGrid()
    {
        // Runs every second: update cells in place, rebuild only when the row count changes
        var positions = _cachedPositions;
        var rebuild = _positionsGrid.Rows.Count != positions.Count;
        _positionsGrid.SuspendLayout();
        if (rebuild)
            _positionsGrid.Rows.Clear();
        for (var i = 0; i < positions.Count; i++)
        {
            var (ticker, side, count, cost) = positions[i];
            var entryPrice = count > 0 ? cost / count : 0;
            var currentPrice = _livePrices.TryGetValue(ticker, out var lp) ? lp : 0;
            double pnl = 0;
            if (currentPrice > 0 && entryPrice > 0)
            {
                if (side == "UP")
                    pnl = (currentPrice - entryPrice) * count;
                else
                    pnl = ((1.0 - currentPrice) - entryPrice) * count;
            }
            var pnlStr = pnl != 0 ? $"{(pnl >= 0 ? "+" : "")}${pnl:F2}" : "—";
            var values = new object[]
            {
                tickerToCoin(ticker), side, $"{count:F2}", $"${cost:F2}", $"${count * (side == "UP" ? currentPrice : 1 - currentPrice):F2}", pnlStr
            };
            if (rebuild)
            {
                _positionsGrid.Rows.Add(values);
            }
            else
            {
                var cells = _positionsGrid.Rows[i].Cells;
                for (var c = 0; c < values.Length; c++)
                    SetCell(cells[c], values[c]);
            }
        }
        _positionsGrid.ResumeLayout();
    }

    private string tickerToCoin(string ticker)
    {
        foreach (var kvp in CoinSeries)
            if (ticker.StartsWith(kvp.Value))
                return kvp.Key;
        return ticker;
    }

    // ── Trading ──

    private async Task CancelOrdersAsync()
    {
        var coin = _coinCombo.SelectedItem?.ToString();
        if (coin == null || !CoinSeries.TryGetValue(coin, out var series))
            return;

        try
        {
            var orders = await _rest.GetOrdersAsync(status: "resting", limit: 100);
            var cancelled = 0;
            foreach (var o in orders)
            {
                var ticker = o["ticker"]?.Value<string>() ?? "";
                if (!ticker.StartsWith(series)) continue;
                var oid = o["order_id"]?.Value<string>();
                if (oid == null) continue;
                await _rest.CancelOrderAsync(oid);
                cancelled++;
                Log($"Cancelled {oid}");
            }
            Log($"Cancel {coin}: {cancelled} orders cancelled");
            MessageBox.Show($"Cancelled {cancelled} open orders for {coin}.", "Cancel Orders",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            Log($"Cancel failed: {ex.Message}");
            MessageBox.Show($"❌ Cancel failed: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task PlaceOrderAsync(string action)
    {
        var coin = _coinCombo.SelectedItem?.ToString();
        if (coin == null || !CoinSeries.TryGetValue(coin, out var series))
        {
            MessageBox.Show("Select a coin.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!double.TryParse(_txtAmount.Text, out var amountDollars) || amountDollars <= 0)
        {
            MessageBox.Show("Enter a valid dollar amount.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var side = _rbUp.Checked ? "yes" : "no";
        var label = side == "yes" ? "UP" : "DOWN";

        int? limitPrice = null;
        if (!string.IsNullOrWhiteSpace(_txtPrice.Text))
        {
            if (int.TryParse(_txtPrice.Text, out var lp))
                limitPrice = lp;
            else
            {
                MessageBox.Show("Limit price must be in cents (e.g., 55).", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        try
        {
            // Find the active ticker
            var data = await _rest.GetMarketsAsync(seriesTicker: series, limit: 1, status: "open");
            var mkts = data["markets"] as JArray;
            var m = mkts?.FirstOrDefault() as JObject;
            if (m == null)
            {
                MessageBox.Show($"No active 15-min market for {coin}.", "Info",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var ticker = m["ticker"]?.Value<string>() ?? "?";

            double count;
            int priceCents;
            double estPrice = 0.50;

            if (limitPrice.HasValue)
            {
                priceCents = limitPrice.Value;
                count = amountDollars * 100.0 / priceCents;
            }
            else
            {
                // Market buy: use actual market ask for count, aggressive floor for limit
                if (side == "yes")
                {
                    estPrice = (double?)m["yes_ask_dollars"] ?? 0.50;
                    priceCents = 99;
                }
                else
                {
                    // Buying NO: expected price = 1 - yes_bid (approx), or no_ask if available
                    estPrice = (double?)m["no_ask_dollars"] ?? (1.0 - ((double?)m["yes_bid_dollars"] ?? 0.50));
                    priceCents = 1;
                }
                count = amountDollars / Math.Max(estPrice, 0.01);
            }

            if (count < 0.01) count = 0.01;

            var cost = limitPrice.HasValue
                ? count * priceCents / 100.0
                : count * estPrice;  // market: use expected price, not aggressive ceiling
            var orderLabel = limitPrice.HasValue ? $"LIMIT @ {priceCents}¢" : $"MARKET (~${cost:F2})";
            var msg = $"ORDER PREVIEW — {coin} {label}\n\n" +
                      $"Ticker: {ticker}\n" +
                      $"Type: {orderLabel}\n" +
                      $"Contracts: {count:F2} @ ~{estPrice * 100:F0}¢\n" +
                      $"Est. Cost: ${cost:F2}";

            var result = MessageBox.Show(msg, $"Confirm Order{(_chkPaperTrade.Checked ? " [PAPER]" : "")}",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            if (_chkPaperTrade.Checked)
            {
                Log($"🧪 PAPER BUY {coin} {label}: {count:F2} @ {priceCents}¢ — NOT SENT");
                MessageBox.Show($"🧪 PAPER TRADE — No order placed.\n{coin} {label}: {count:F2} @ {priceCents}¢", "Paper Trade",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var orderResult = await _rest.CreateOrderAsync(ticker, side, "buy", count, priceCents,
                maxCostCents: (int)(amountDollars * 100));
            var oid = orderResult["order_id"]?.Value<string>() ?? "?";
            var filled = orderResult["fill_count"]?.Value<string>() ?? "0";
            var remaining = orderResult["remaining_count"]?.Value<string>() ?? "0";
            Log($"BUY {coin} {label}: {count:F2} @ {priceCents}¢ → {oid} (filled={filled}, rem={remaining})");

            MessageBox.Show($"✅ {coin} {label} order placed!\nID: {oid}\nFilled: {filled}", "Success",
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            // Brief wait for position to settle, then refresh
            await Task.Delay(500);
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            Log($"BUY FAILED: {ex.Message}");
            MessageBox.Show($"❌ Order failed: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SellPositionAsync()
    {
        var coin = _coinCombo.SelectedItem?.ToString();
        if (coin == null || !CoinSeries.TryGetValue(coin, out var series))
            return;

        var side = _rbUp.Checked ? "yes" : "no";
        var label = side == "yes" ? "UP" : "DOWN";

        // Market sell: ignore limit/profit, sell at market
        if (_chkMarketSell.Checked)
        {
            try
            {
                var data = await _rest.GetMarketsAsync(seriesTicker: series, limit: 1, status: "open");
                var mkts = data["markets"] as JArray;
                var m = mkts?.FirstOrDefault() as JObject;
                if (m == null) return;

                // Market sell — aggregate ALL positions for this series
                var positions = await _rest.GetPositionsAsync();
                Log($"SELL {coin}: API returned {positions.Count} positions, series={series}");
                var posList = new List<(string ticker, string posSide, double count)>();
                foreach (var p in positions)
                {
                    var pt = p["ticker"]?.Value<string>() ?? "";
                    var pf = p["position_fp"]?.Value<string>() ?? "0";
                    Log($"  pos: ticker={pt} fp={pf}");
                    if (double.TryParse(pf, out var pd) && pt.StartsWith(series) && Math.Abs(pd) > 0.001)
                    {
                        var ps = pd > 0 ? "yes" : "no"; // position side from sign
                        posList.Add((pt, ps, Math.Abs(pd)));
                    }
                }
                if (posList.Count == 0)
                {
                    MessageBox.Show($"No position found for {coin}.", "Info",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                double total = posList.Sum(x => x.count);
                var msg = $"MARKET SELL {coin}\n\nPositions: {posList.Count}\nTotal: {total:F2} contracts";
                var result = MessageBox.Show(msg, $"Confirm Market Sell{(_chkPaperTrade.Checked ? " [PAPER]" : "")}",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result != DialogResult.Yes) return;

                if (_chkPaperTrade.Checked)
                {
                    Log($"🧪 PAPER MKT SELL {coin}: {total:F2} contracts — NOT SENT");
                    MessageBox.Show($"🧪 PAPER TRADE — No order placed.", "Paper Trade",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                foreach (var (pt, ps, cnt) in posList)
                {
                    // Sell price based on position side: YES→1¢ (ask), NO→99¢ (bid)
                    var sellPrice = ps == "yes" ? 1 : 99;
                    var orderResult = await _rest.CreateOrderAsync(pt, ps, "sell", cnt, sellPrice);
                    var oid = orderResult["order_id"]?.Value<string>() ?? "?";
                    Log($"MKT SELL {coin} on {pt}: {cnt:F2} @ {sellPrice}¢ → {oid}");
                }
                Log($"MKT SELL {coin} DONE: {total:F2} total across {posList.Count} positions");
                MessageBox.Show($"✅ MARKET SELL {coin}: {total:F2} contracts sold", "Success",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                Log($"MKT SELL FAILED: {ex.Message}");
                MessageBox.Show($"❌ Market sell failed: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        // Limit/profit sell — same position aggregation as market sell
        try
        {
        int? limitPrice = null;
        if (!string.IsNullOrWhiteSpace(_txtPrice.Text) && int.TryParse(_txtPrice.Text, out var lp))
            limitPrice = lp;

        var positions = await _rest.GetPositionsAsync();
        var posList = new List<(string ticker, string posSide, double count, double cost)>();
        foreach (var p in positions)
        {
            var pt = p["ticker"]?.Value<string>() ?? "";
            var pf = p["position_fp"]?.Value<string>() ?? "0";
            var costStr = p["market_exposure_dollars"]?.Value<string>()
                       ?? p["total_traded_dollars"]?.Value<string>() ?? "0";
            if (double.TryParse(pf, out var pd) && double.TryParse(costStr, out var cost) && pt.StartsWith(series) && Math.Abs(pd) > 0.001)
                posList.Add((pt, pd > 0 ? "yes" : "no", Math.Abs(pd), Math.Abs(cost)));
        }

        if (posList.Count == 0)
        {
            MessageBox.Show($"No position found for {coin}.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (limitPrice == null && !string.IsNullOrWhiteSpace(_txtProfit.Text) && double.TryParse(_txtProfit.Text, out var profitPct) && profitPct > 0)
        {
            // Compute profit price per position
            // YES position: sell YES at entry * (1+%), capped at 99
            // NO position: sell NO = buy YES at 100 - entry*(1+%), min 1
            foreach (var (pt, ps, cnt, cost) in posList)
            {
                var entryCents = cnt > 0 ? cost / cnt * 100 : 0;
                var targetCents = (int)Math.Ceiling(entryCents * (1 + profitPct / 100));
                limitPrice = ps == "yes"
                    ? Math.Min(targetCents, 98)
                    : Math.Max(100 - targetCents, 2);
            }
        }

        if (limitPrice == null)
        {
            MessageBox.Show("Enter a limit price in cents to sell (e.g., 70), or set Profit %.", "Sell",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        double totalCnt = posList.Sum(x => x.count);
        double totalCost = posList.Sum(x => x.cost);
        var proceeds = totalCnt * limitPrice.Value / 100.0;
        var posSide = posList.First().posSide;
        var orderDesc = posSide == "no"
            ? $"Limit BUY YES @ {limitPrice}¢ (sell NO @ {100 - limitPrice}¢)"
            : $"Limit sell YES @ {limitPrice}¢";
        var msg = $"SELL {coin} ({posSide})\n\nPositions: {posList.Count}\nTotal: {totalCnt:F2} contracts\n{orderDesc}\nCost basis: ${totalCost:F2}\nEst. Proceeds: ${proceeds:F2}";

        var result = MessageBox.Show(msg, $"Confirm Sell{(_chkPaperTrade.Checked ? " [PAPER]" : "")}",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes) return;

        if (_chkPaperTrade.Checked)
        {
            Log($"🧪 PAPER SELL {coin}: {totalCnt:F2} @ {limitPrice}¢ — NOT SENT");
            MessageBox.Show("🧪 PAPER TRADE — No order placed.", "Paper Trade", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var (pt, ps, cnt, cost) in posList)
        {
            Log($"SELL {coin}: ticker={pt} side={ps} cnt={cnt:F2} cost=${cost:F4} price={limitPrice}¢");
            var orderResult = await _rest.CreateOrderAsync(pt, ps, "sell", cnt, limitPrice, forceLimit: true);
            Log($"  → response: {orderResult}");
        }
        Log($"SELL {coin} DONE: {totalCnt:F2} total across {posList.Count} positions");
        MessageBox.Show($"✅ SELL {coin}: {totalCnt:F2} contracts sold", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            Log($"SELL FAILED: {ex.Message}");
            MessageBox.Show($"❌ Sell failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Helpers ──

    private void ApplyRowColor(DataGridViewRow row, string upPctStr)
    {
        // Clear any existing cell-level coloring first
        row.Cells["UP%"].Style.BackColor = Color.Empty;
        row.Cells["DOWN%"].Style.BackColor = Color.Empty;
        row.Cells["Coin"].Style.BackColor = Color.Empty;

        if (double.TryParse(upPctStr.TrimEnd('%'), out var upPct))
        {
            if (upPct >= 90)
            {
                // Full row green — strong UP dominance
                row.DefaultCellStyle.BackColor = Color.FromArgb(200, 255, 200);
            }
            else if (upPct <= 10)
            {
                // Full row red — strong DOWN dominance
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 200, 200);
            }
            else if (upPct >= 80)
            {
                // Cell-level: UP% and Coin cells green
                row.DefaultCellStyle.BackColor = Color.White;
                row.Cells["UP%"].Style.BackColor = Color.FromArgb(200, 255, 200);
                row.Cells["Coin"].Style.BackColor = Color.FromArgb(200, 255, 200);
            }
            else if (upPct <= 20)
            {
                // Cell-level: DOWN% and Coin cells red
                row.DefaultCellStyle.BackColor = Color.White;
                row.Cells["DOWN%"].Style.BackColor = Color.FromArgb(255, 200, 200);
                row.Cells["Coin"].Style.BackColor = Color.FromArgb(255, 200, 200);
            }
            else
            {
                row.DefaultCellStyle.BackColor = Color.White;
            }
        }
    }

    private string FormatSpotDist(string coin)
    {
        // Δ LINE = expected 60s settle average − floor_strike (middle line).
        var s = ComputeLine(coin);
        return s == null ? "—" : LineMath.Money(s.LineDist, coin);
    }

    private static string FormatEdge(JToken? yesBid)
    {
        if (yesBid == null) return "N/A";
        var edgeCents = (int)Math.Round((yesBid.Value<double>() - 0.50) * 100);
        return edgeCents >= 0 ? $"+{edgeCents}¢" : $"{edgeCents}¢";
    }

    private static string SafePct(JToken? token)
    {
        if (token == null) return "N/A";
        var val = token.Value<double>();
        return $"{val * 100:F1}%";
    }

    private static string FormatVolume(JToken? token)
    {
        if (token == null) return "N/A";
        var val = token.Value<double>();
        return val >= 1_000_000 ? $"{val / 1_000_000:F1}M" :
               val >= 1_000 ? $"{val / 1_000:F1}K" :
               $"{val:F0}";
    }
}
