# 🏛️ KalashiWinForms — 15-Min Crypto Trading

Windows Forms desktop app for trading Kalshi 15-minute crypto markets with real-time WebSocket data.

## Setup

### 1. Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- A Kalshi account with API access (production recommended)

### 2. API Keys
Create a `.env` file in the project root:

```env
KALSHI_ENVIRONMENT=production
KALSHI_KEY_ID=your-kalshi-key-id
KALSHI_PRIVATE_KEY_PATH=~/.key/kalshi/key.pem
```


C:\Users\juan_\.key\kalshi\key.pem

`~` expands to your Windows user folder (`C:\Users\<you>\`). The `.pem` file is your RSA private key downloaded from the Kalshi dashboard.

### 3. Build & Run
```bash
dotnet build
dotnet run
```

## Architecture

| File | Purpose |
|---|---|
| `Program.cs` | Entry point, launches the form |
| `MainForm.cs` | All UI: grid, trading panel, timers, WebSocket wiring |
| `KalshiRestClient.cs` | REST API client (markets, orders, balance, positions, live data) |
| `KalshiWebSocketClient.cs` | WebSocket client (ticker subscriptions, CF Benchmarks spot prices) |
| `KalshiAuth.cs` | RSA-PSS signing for API authentication |
| `KalshiConfig.cs` | Reads `.env`, provides REST/WS URLs for demo vs production |

## Data Sources

| Data | Source | Update Rate |
|---|---|---|
| Market prices (UP%, DOWN%, Volume) | Kalshi WebSocket `ticker` channel | Real-time push |
| Spot prices (BTC, ETH, SOL, DOGE, XRP) | CF Benchmarks via Kalshi WebSocket | Real-time push |
| Spot prices (BNB, ADA, NEAR, TON, BCH, ZEC, HYPE) | Kalshi REST `/live_data/events/{ticker}` | Every 5s |
| Balance | Kalshi REST `/portfolio/balance` | On manual refresh |
| Orders | Kalshi REST `/portfolio/orders` | On manual refresh |
| Countdown timer | Computed locally from `close_time` | Every 1s |
| Orderbook pressure | Computed from REST bid/ask sizes | Every 30s |

## Features

### Market Grid
- **Columns**: Ticker, Coin, UP%, DOWN%, Δ Spot (spot − strike), Volume
- **Coloring**:
  - ≥90% UP → full row green
  - 80–89.99% UP → UP% cell + Coin cell green
  - 10.01–20% UP → DOWN% cell + Coin cell red
  - ≤10% UP → full row red
  - Coin cell also tints based on orderbook pressure (bid size imbalance)
- **Single click** on a row → sets the trading coin combo box
- **Click UP% column** → sets radio to 📈 UP
- **Click DOWN% column** → sets radio to 📉 DOWN
- **Selection auto-clears** on empty clicks

### Top Bar
- Balance / Available
- ⏱ Countdown to next market close (adjusted −6s for server lag)
- Current UTC time
- Refresh button (reloads all REST data)

### Trading Panel (bottom)
- **Coin** dropdown
- **📈 UP / 📉 DOWN** radio
- **Amount ($)** — dollar amount for buy orders
- **Limit (¢)** — optional limit price in cents
- **Profit %** — auto-calculates sell price from entry cost basis
- **MKT** checkbox — enables market sell (ignores limit/profit)
- **🟢 BUY** / **🔴 SELL** buttons

### Logs
- **📜 Logs tab** — full error and event log with timestamps
- **Bottom strip** — shows most recent log entry

## Tabs
1. **📊 Markets** — live market data grid
2. **📋 Orders & Portfolio** — your open orders
3. **📜 Logs** — error and event log (500 entries, scrollable)

## WebSocket Flow
1. Connect to `wss://external-api-ws.kalshi.com/trade-api/ws/v2`
2. Authenticate via RSA-signed headers
3. Subscribe to `ticker` channel for all 12 coin markets
4. Subscribe to `cfbenchmarks_value` for 5 major spot prices
5. Receive real-time updates, update grid cells in-place (no flicker)
6. Auto-reconnect on disconnect with 5s delay
7. Every 30s: check for new markets (15-min rollover), add to subscription

## Coin Order (fixed)
BTC → ETH → SOL → BNB → XRP → NEAR → ZEC → TON → BCH → ADA → DOGE → HYPE
