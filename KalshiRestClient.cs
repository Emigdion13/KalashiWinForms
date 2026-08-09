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

        var request = new HttpRequestMessage(new HttpMethod(method), url);

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

        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        return JObject.Parse(content);
    }

    // ── Markets ──

    public async Task<JObject> GetMarketsAsync(string? status = null, string? seriesTicker = null, int limit = 100)
    {
        var p = new Dictionary<string, string> { ["limit"] = Math.Min(limit, 500).ToString() };
        if (status != null) p["status"] = status;
        if (seriesTicker != null) p["series_ticker"] = seriesTicker;
        return await RequestAsync("GET", "/markets", p);
    }

    public async Task<JObject> GetMarketAsync(string ticker) =>
        await RequestAsync("GET", $"/markets/{ticker}");

    // ── Live Data (spot prices) ──

    public async Task<JObject> GetEventLiveDataAsync(string eventTicker, string? range = null)
    {
        var p = new Dictionary<string, string>();
        if (range != null) p["range"] = range;
        return await RequestAsync("GET", $"/live_data/events/{eventTicker}", p);
    }

    // ── Portfolio ──

    public async Task<JObject> GetBalanceAsync() =>
        await RequestAsync("GET", "/portfolio/balance");

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
