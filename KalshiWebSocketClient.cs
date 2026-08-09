using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;

namespace KalashiWinForms;

public class KalshiWebSocketClient : IDisposable
{
    private readonly KalshiConfig _config;
    private readonly KalshiAuth? _auth;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _nextId = 1;
    private int _tickerSid;
    private int _cfSid;

    public event Action<JObject>? TickerUpdated;
    public event Action<string, double>? SpotUpdated;
    public event Action<string>? LogMessage;
    public event Action<Exception>? ConnectionLost;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public KalshiWebSocketClient(KalshiConfig config)
    {
        _config = config;
        if (!string.IsNullOrEmpty(config.KeyId) &&
            (!string.IsNullOrEmpty(config.PrivateKeyPath) || !string.IsNullOrEmpty(config.PrivateKey)))
        {
            _auth = new KalshiAuth(config.KeyId, config.PrivateKeyPath, config.PrivateKey);
        }
    }

    public async Task ConnectAsync(IReadOnlyList<string> marketTickers)
    {
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();

        // Set auth headers on handshake
        if (_auth != null)
        {
            var uri = new Uri(_config.WebSocketUrl);
            var headers = _auth.GetHeaders("GET", uri.AbsolutePath);
            foreach (var h in headers)
                _ws.Options.SetRequestHeader(h.Key, h.Value);
        }

        LogMessage?.Invoke($"Connecting to {_config.WebSocketUrl}...");
        await _ws.ConnectAsync(new Uri(_config.WebSocketUrl), _cts.Token);
        LogMessage?.Invoke("WebSocket connected.");

        // Subscribe to ticker for all markets
        await SubscribeAsync(marketTickers);

        // Subscribe to CF Benchmarks for spot prices (Kalshi native)
        await SubscribeCfBenchmarksAsync();

        // Start receive loop
        _ = ReceiveLoopAsync(_cts.Token);
    }

    private async Task SubscribeAsync(IReadOnlyList<string> marketTickers)
    {
        var cmd = new JObject
        {
            ["id"] = _nextId++,
            ["cmd"] = "subscribe",
            ["params"] = new JObject
            {
                ["channels"] = new JArray("ticker"),
                ["market_tickers"] = new JArray(marketTickers)
            }
        };
        await SendAsync(cmd);
    }

    private async Task SubscribeCfBenchmarksAsync()
    {
        var cmd = new JObject
        {
            ["id"] = _nextId++,
            ["cmd"] = "subscribe",
            ["params"] = new JObject
            {
                ["channels"] = new JArray("cfbenchmarks_value"),
                ["index_ids"] = new JArray("BRTI", "ETHUSD_RTI", "SOLUSD_RTI", "DOGEUSD_RTI", "XRPUSD_RTI")
            }
        };
        await SendAsync(cmd);
    }

    public async Task AddMarketsAsync(IReadOnlyList<string> marketTickers)
    {
        if (marketTickers.Count == 0 || _tickerSid == 0) return;
        var cmd = new JObject
        {
            ["id"] = _nextId++,
            ["cmd"] = "update_subscription",
            ["params"] = new JObject
            {
                ["sid"] = _tickerSid,
                ["market_tickers"] = new JArray(marketTickers),
                ["action"] = "add_markets"
            }
        };
        LogMessage?.Invoke($"WS: adding {marketTickers.Count} markets to ticker sid={_tickerSid}");
        await SendAsync(cmd);
    }

    private async Task SendAsync(JObject message)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var json = message.ToString(Newtonsoft.Json.Formatting.None);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? default);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new StringBuilder();

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    LogMessage?.Invoke("WebSocket closed by server.");
                    break;
                }

                // Handle ping/pong at the control frame level — ClientWebSocket handles pong automatically
                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var msg = messageBuffer.ToString();
                    messageBuffer.Clear();

                    try
                    {
                        var json = JObject.Parse(msg);
                        var type = json["type"]?.Value<string>();

                        if (type == "ticker")
                        {
                            TickerUpdated?.Invoke(json);
                        }
                        else if (type == "cfbenchmarks_value")
                        {
                            var indexId = json["msg"]?["index_id"]?.Value<string>() ?? "";
                            var dataStr = json["msg"]?["data"]?.Value<string>();
                            if (!string.IsNullOrEmpty(indexId) && !string.IsNullOrEmpty(dataStr))
                            {
                                try
                                {
                                    var inner = JObject.Parse(dataStr);
                                    var value = inner["value"]?.Value<double>();
                                    if (value.HasValue)
                                        SpotUpdated?.Invoke(indexId, value.Value);
                                }
                                catch { }
                            }
                        }
                        else if (type == "error")
                        {
                            var code = json["msg"]?["code"]?.Value<int>() ?? 0;
                            var errMsg = json["msg"]?["msg"]?.Value<string>() ?? "";
                            var errId = json["id"]?.Value<int>() ?? 0;
                            LogMessage?.Invoke($"WS Error #{code}: {errMsg} (cmd id={errId})");
                        }
                        else if (type == "subscribed")
                        {
                            var channel = json["msg"]?["channel"]?.Value<string>();
                            var sid = json["msg"]?["sid"]?.Value<int>() ?? 0;
                            if (channel == "ticker") _tickerSid = sid;
                            else if (channel == "cfbenchmarks_value") _cfSid = sid;
                            LogMessage?.Invoke($"Subscribed to {channel} (sid={sid})");
                        }
                        else
                        {
                            // Log other messages for debugging
                            LogMessage?.Invoke($"WS: {msg}");
                        }
                    }
                    catch
                    {
                        // Ignore malformed JSON
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            LogMessage?.Invoke($"WebSocket error: {ex.Message}");
            ConnectionLost?.Invoke(ex);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Receive error: {ex.Message}");
            ConnectionLost?.Invoke(ex);
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { }
        }
        _ws?.Dispose();
        _ws = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
