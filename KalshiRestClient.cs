using Newtonsoft.Json.Linq;

namespace KalashiWinForms;

public class KalshiRestClient
{
    private readonly HttpClient _http;
    private readonly KalshiConfig _config;
    private readonly KalshiAuth? _auth;
    public Action<string>? DebugLog;

    public KalshiRestClient(KalshiConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        if (!string.IsNullOrEmpty(config.KeyId) &&
            (!string.IsNullOrEmpty(config.PrivateKeyPath) || !string.IsNullOrEmpty(config.PrivateKey)))
        {
            _auth = new KalshiAuth(config.KeyId, config.PrivateKeyPath, config.PrivateKey);
        }
    }

    private async Task<JObject> RequestAsync(string method, string path,
        Dictionary<string, string>? queryParams = null, object? body = null)
    {
        var url = $"{_config.RestBaseUrl}{path}";

        if (queryParams is { Count: > 0 })
        {
            var qs = string.Join("&",
                queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url += "?" + qs;
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        if (_auth != null)
        {
            var uri = new Uri(url);
            var headers = _auth.GetHeaders(method, uri.AbsolutePath);
            foreach (var h in headers)
                request.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        if (body != null)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(body);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        // ConfigureAwait(false): JSON parsing runs on the thread pool, not the UI thread.
        // Callers still resume on the UI thread because they await with their own context.
        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = content.Length > 300 ? content[..300] + "…" : content;
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}", null, response.StatusCode);
        }
        return JObject.Parse(content);
    }

    // ── Markets ──

    public Task<JObject> GetMarketsAsync(string? status = null, string? seriesTicker = null, int limit = 100)
    {
        var p = new Dictionary<string, string> { ["limit"] = Math.Min(limit, 500).ToString() };
        if (status != null) p["status"] = status;
        if (seriesTicker != null) p["series_ticker"] = seriesTicker;
        return RequestAsync("GET", "/markets", p);
    }

    public Task<JObject> GetMarketAsync(string ticker) =>
        RequestAsync("GET", $"/markets/{ticker}");

    // ── Live Data (spot prices) ──

    public Task<JObject> GetEventLiveDataAsync(string eventTicker, string? range = null)
    {
        var p = new Dictionary<string, string>();
        if (range != null) p["range"] = range;
        return RequestAsync("GET", $"/live_data/events/{eventTicker}", p);
    }

    // Ranges the API rejected (400/422) — skip them on later calls instead of paying a failed request each time
    private readonly HashSet<string> _badRanges = new();

    /// <summary>
    /// Returns last CF print + the 1-second timeseries
    /// (this is the same family Kalshi averages for settlement).
    /// recentOnly = live polling: the 15min range is ~4x smaller than 1h.
    /// </summary>
    public async Task<(double spot, List<(long ms, double v)> points)> GetLiveSeriesAsync(string eventTicker, bool recentOnly = false)
    {
        var ranges = recentOnly ? new[] { "15min", "1h" } : new[] { "1h", "15min", "1d" };
        Exception? last = null;
        foreach (var rng in ranges)
        {
            lock (_badRanges)
                if (_badRanges.Contains(rng)) continue;
            try
            {
                var data = await GetEventLiveDataAsync(eventTicker, rng).ConfigureAwait(false);
                return LineMath.ParseLive(data);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest
                                                  or System.Net.HttpStatusCode.UnprocessableEntity)
            {
                lock (_badRanges) _badRanges.Add(rng);
                last = ex;
            }
            catch (Exception ex) { last = ex; }
        }
        if (last != null) throw last;
        return (0, new List<(long, double)>());
    }

    // ── Portfolio ──

    public Task<JObject> GetBalanceAsync() =>
        RequestAsync("GET", "/portfolio/balance");

    public async Task<JArray> GetPositionsAsync()
    {
        var data = await RequestAsync("GET", "/portfolio/positions",
            new Dictionary<string, string> { ["limit"] = "100" });
        var positions = data["market_positions"] as JArray;
        if (positions == null || positions.Count == 0)
            DebugLog?.Invoke($"GetPositions: empty! raw={data}");
        else
            DebugLog?.Invoke($"GetPositions: {positions.Count} market positions");
        return positions ?? new JArray();
    }

    public async Task<JArray> GetOrdersAsync(string? status = null, string? marketTicker = null, int limit = 100)
    {
        var p = new Dictionary<string, string> { ["limit"] = limit.ToString() };
        if (status != null) p["status"] = status;
        if (marketTicker != null) p["ticker"] = marketTicker;
        var data = await RequestAsync("GET", "/portfolio/orders", p);
        return data["orders"] as JArray ?? new JArray();
    }

    public async Task<JArray> GetFillsAsync(int limit = 100)
    {
        var data = await RequestAsync("GET", "/portfolio/fills",
            new Dictionary<string, string> { ["limit"] = limit.ToString() });
        return data["fills"] as JArray ?? new JArray();
    }

    // ── Orders (V2 endpoint) ──

    public async Task<JObject> CreateOrderAsync(string ticker, string side, string action, double count,
        int? priceCents = null, string? clientOrderId = null, bool forceLimit = false, int? maxCostCents = null)
    {
        // V2: side is book side (bid/ask), price in dollars, count as fixed-point string
        var bookSide = (side == "yes" && action == "buy") || (side == "no" && action == "sell") ? "bid" : "ask";
        // Market sell uses aggressive price + IOC to fill immediately
        var isAggressive = !forceLimit && ((action == "buy" && priceCents == 99) || (action == "sell" && priceCents == 1) || (action == "sell" && priceCents == 99));

        var body = new Dictionary<string, object>
        {
            ["ticker"] = ticker,
            ["side"] = bookSide,
            ["count"] = count.ToString("F2"),
            ["time_in_force"] = isAggressive ? "immediate_or_cancel" : "good_till_canceled",
            ["self_trade_prevention_type"] = "taker_at_cross"
        };

        if (priceCents.HasValue)
            body["price"] = (priceCents.Value / 100.0).ToString("F4");

        if (action == "sell" && (!priceCents.HasValue || priceCents == 1 || priceCents == 99))
            body["reduce_only"] = true;

        if (maxCostCents.HasValue)
            body["buy_max_cost"] = maxCostCents.Value;

        if (clientOrderId != null)
            body["client_order_id"] = clientOrderId;

        return await RequestAsync("POST", "/portfolio/events/orders", body: body);
    }

    public async Task<JObject> CancelOrderAsync(string orderId) =>
        await RequestAsync("DELETE", $"/portfolio/events/orders/{orderId}");
}
