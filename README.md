# KalashiWinForms — 15-Min Crypto Trading

Windows Forms desktop app for Kalshi 15-minute crypto markets.

**This build fixes the middle line** and adds a **same-side / YES-NO pattern lab** so you can see what actually finishes, not just last tick.

## What was wrong with Δ Spot

Kalshi 15m crypto does **not** settle on the last tick.

From the live contract rules:

> If the **simple average of the sixty seconds** of CF Benchmarks' BRTI **before the close** is at least the **simple average of the sixty seconds** of BRTI **before the open**, the market resolves **Yes**.

That open average **is** `floor_strike`. That is the **middle line**.

The old grid printed `last_tick − floor_strike` with 9 decimals. Two bugs:

1. **Last tick is not the pin.** In the last minute a wick through the line does not flip the contract unless it moves the 60-second average.
2. **BNB / ADA / NEAR / TON / BCH / ZEC / HYPE** asked Kalshi `live_data` for `current_price` / `value` / `spot`. Those fields are not there. The real print is `live_data.details.timeseries[]` `{t, v}`. Those coins were blank or junk.

## What you see now (Markets tab)

| Column | Meaning |
| --- | --- |
| **Δ LINE** | Expected **settlement average − floor_strike**. This is the real distance from the middle line. |
| **Δ last** | Last tick − strike (the old number). Turns amber when it **disagrees** with Δ LINE. |
| **bps** | Δ LINE in basis points. Compare coins on the same scale. |
| **SIDE** | YES if the settle-avg is above the line, NO if below. |
| **vs BTC** | WITH = same side of its own line as BTC. VS = opposite. |

Before the last 60s, last tick ≈ settle avg, so Δ LINE and Δ last match. In the last minute they can split — **trust Δ LINE**.

## LINE / PATTERNS tab

On first launch the app pulls the last ~20 **official settled** windows per coin (so you are not waiting all night) and rebuilds T-5m / T-2m / T-60s marks from Kalshi's historical 1s series.

- **LIVE** — how many coins are YES vs NO right now, and who is WITH / AGAINST BTC.
- **SAME SIDE** — for each pair, how often they finished on the same side of their own line. `n` is finished windows. Need **12** before you treat a % as real.
- **WHAT FINISHES YES / NO** — persistence (already on a side at T-5m / T-2m / T-60s), tape+line agreement, “when last tick disagrees the average wins”, same-as-BTC base rate, last-second crosses.
- **RECENT SETTLES** — official Kalshi `result` per window.

This is a lab, not a 98% costume. A rule with `n<12` is a story. Size like family-medicine money.

Files written next to your `.env` (or the exe):

- `coin_finishes.jsonl` — every settled window
- `coin_patterns.json` — last snapshot

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

`~` expands to your Windows user folder (`C:\Users\<you>\`). The `.pem` file is your RSA private key from the Kalshi dashboard. Never paste the PEM into chat.

### 3. Build & Run

```bat
cd C:\Users\juan_\OneDrive\Desktop\projects\KalashiWinForms
dotnet build
dotnet run
```

Overwrite the old `.cs` files with this folder, keep your existing `.env`.

## Architecture

| File | Purpose |
|---|---|
| `Program.cs` | Entry point |
| `MainForm.cs` | Grid, trading panel, timers, WebSocket |
| `MainForm.Patterns.cs` | LINE columns, live marks, backfill, pattern tab |
| `LineMath.cs` | 60s-average distance from `floor_strike` |
| `CoinPatternLab.cs` | Same-side matrix + YES/NO rules, persisted |
| `KalshiRestClient.cs` | REST (markets, orders, balance, live_data timeseries) |
| `KalshiWebSocketClient.cs` | Ticker + CF Benchmarks spots |
| `KalshiAuth.cs` | RSA-PSS signing |
| `KalshiConfig.cs` | `.env` |

## Coin order

BTC → ETH → SOL → BNB → XRP → NEAR → ZEC → TON → BCH → ADA → DOGE → HYPE
