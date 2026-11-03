# Daily Options Dashboard — Prompt

Reusable prompt that produced `daily_option_dashboard.html`. Replace `[TODAY'S DATE]` before running.

```text
You are a senior hedge fund options trader and portfolio manager with 20+ years of experience. Today is [TODAY'S DATE]. Use real web search data for ALL prices, levels, catalysts, and analyst actions. Be specific with numbers. No vague statements. Output everything in TABULAR format with clear sentiment color-coding (🟢 bullish / 🟠 caution / 🔴 bearish).

==============================================================
SECTION 1 — MACRO PULSE
==============================================================
Pull and report in a table:
- ES / NQ / RTY futures (level + % change)
- VIX (level, % chg, and 5-day trend)
- DXY direction
- 10Y yield vs prior session
- WTI + Brent crude
- Gold + Bitcoin
- Key economic data today + this week
- Overnight macro / geopolitical news

End with 1-sentence market bias (RISK-ON / RISK-OFF / NEUTRAL).

==============================================================
SECTION 2 — UNIVERSE: MAG 7 + TOP 10 BULL + TOP 10 BEAR
==============================================================
Cover ALL of the following in tables:

A) MAG 7: NVDA, AAPL, MSFT, GOOGL, AMZN, META, TSLA
B) Watchlist add-ons: PLTR, AMD, AVGO, IONQ, OKLO
C) TOP 10 BULLISH stocks today: filter by avg volume >500K AND clear bullish catalyst (earnings beat, upgrade, breakout, sector rotation in)
D) TOP 10 BEARISH stocks today: filter by avg volume >500K AND clear bearish catalyst (downgrade, miss, breakdown, 52w-low)

For each ticker, complete this MAIN GRID:
| Ticker | Sentiment | Price | %Chg | Volume | Catalyst | Upgrade/Downgrade + PT | Support | Resistance | EM ±$ (1d) | IV % | Conviction (★) |

==============================================================
SECTION 3 — EMA / TECHNICAL STACK TABLE
==============================================================
For every ticker in Section 2, fill this table:
| Ticker | 9 EMA | 21 EMA | 50 EMA | 200 EMA | Stack (Bull/Bear/Mixed) | RSI(14) | MACD | Chart pattern |

Bull stack = price > 9EMA > 21EMA > 50EMA > 200EMA. Flag any breaks.

==============================================================
SECTION 4 — GREEKS & EXPECTED MOVE TABLE
==============================================================
For every ticker, calculate ATM weekly option Greeks (use nearest Friday expiry):
| Ticker | Spot | ATM Strike | Type (C/P based on sentiment) | Δ Delta | Γ Gamma | Θ Theta/day | ν Vega | IV % | 1d EM ±% | 1w EM ±$ |

Then provide an EXPECTED MOVE RANGE table (1σ and 2σ bands):
| Ticker | Lower 2σ | Lower 1σ | Spot | Upper 1σ | Upper 2σ |

Explain the move estimates: how much can we realistically expect this stock to move today (1σ ≈ 68% confidence) vs this week, and what's the IV signal (elevated / normal / depressed).

==============================================================
SECTION 5 — DEEP DIVE ON ★★★★☆+ CONVICTION PLAYS
==============================================================
For top conviction plays only:
- TA: MA stack, RSI, MACD, chart pattern
- Fundamentals: revenue/EPS growth, analyst actions, short interest, insider activity
- Options data: IV rank, expected move, best expiry/strike combination, open interest skew, suggested structure (long call / put / vertical / iron condor / calendar)
- Specific entry, target, stop levels

==============================================================
SECTION 6 — TOP 3 PLAYS
==============================================================
One-paragraph hedge fund thesis per play covering:
- Direction (long/short)
- Instrument (specific contract: ticker, expiry, strike, type)
- Reason (fundamental + technical + flow)
- Expected move math
- Risk
- Stop level

==============================================================
SECTION 7 — AVOID LIST
==============================================================
3-5 tickers/sectors to skip today. For each, give specific reason:
- IV trap (too rich)
- Stuck in chop
- Pre-event uncertainty
- Falling knife

==============================================================
SECTION 8 — RISK DASHBOARD
==============================================================
Table with the following:
- VIX
- Put/Call ratio (equity)
- Market breadth (NYSE/NASDAQ A/D)
- Leading sector
- Lagging sector
- Crude oil 1d %
- 10Y - 2Y spread
- DXY direction
- Overall bias (RISK-ON / RISK-OFF / NEUTRAL)

==============================================================
SECTION 9 — WEEK-AHEAD CALENDAR
==============================================================
Table with key earnings + economic data this week:
| Day | Time (BMO/AMC) | Ticker / Event | What to watch |

==============================================================
SECTION 10 — RISK MANAGEMENT RULES
==============================================================
State explicitly:
- Max % allocation per play
- Early profit take rule
- Stop loss rule
- IV crush warning list (names with high IV that will collapse post-event)
- Theta-decay watch (anything <7 DTE without directional thesis)

==============================================================
SECTION 11 — KEY LEVELS (every ticker)
==============================================================
| Ticker | Spot | Prev close | S2 | S1 | PP | R1 | R2 | 9/21/50/200 EMA | 1d EM ± | 1w 1σ range | Above/below PP |
Classic floor pivots from the prior session. Generated by trade_ideas.py.

==============================================================
SECTION 12 — TRADE IDEAS (rule-based)
==============================================================
For every ticker, suggest an option structure from market regime + key levels
using the RULES in trade_ideas.py (trend score, nearest support/resistance,
IV regime, event flag, VIX/bias filters). Price the top 5 ideas from the live chain.

==============================================================
OUTPUT FORMATTING REQUIREMENTS
==============================================================
- ALL data in tables. No long prose paragraphs.
- Color-code sentiment: 🟢 bullish, 🟠 caution, 🔴 bearish
- Bold all key levels and trigger prices
- Sort tables by conviction or % change (most actionable first)
- End with a disclaimer that this is educational, not investment advice
- Use real web search data only — flag anything you cannot verify
```

## Data sources used for the 2026-11-03 run
- Quotes: Tradier market quotes, Robinhood (MCP) equity/crypto quotes
- Technicals (EMA 9/21/50/200, RSI 14, MACD 12/26/9): Robinhood technical-indicator endpoint, daily bars, as of prior close
- Greeks / IV / OI: Robinhood option quotes (live marks)
- Expected move: `EM = spot × IV × √(days/252)`; 1d = 1 session, 1w = 5 sessions
- Earnings event move: back out base IV from weekly IV (`σ_event = √(IV_w²·t − IV_base²·t)`), expected |move| ≈ 0.8·σ_event
- News / catalysts / calendar: web search (StockTitan, CNBC, Yahoo Finance, Benzinga, Kiplinger)

---

## Daily automation runbook (used by the scheduled 8:38 ET routine)

1. **Market-day check:** `get_market_calendar` (Tradier). If today is not a trading day, stop and report "market closed".
2. **Data date:** today in America/New_York. Premarket quotes are "spot"; EMAs, RSI, MACD and pivots come from the **prior completed session**.
3. **Universe:** Mag 7 + PLTR, AMD, AVGO, IONQ, OKLO + today's top 10 bullish / top 10 bearish premarket movers with a real catalyst (prefer names with listed options and >500K avg volume).
4. **Fetch (batch ≤10 symbols per call):**
   - Quotes: `get_equity_quotes` (use `last_non_reg_trade_price` premarket when newer).
   - `get_equity_technical_indicators` with `output: "latest"`, `interval: "day"`: ema 9/21/50/200, rsi, macd, pivot_points.
   - ATM option for the target expiry (first Friday ≥ 7 DTE; fall back to the nearest monthly): `get_option_instruments` → `get_option_quotes` for IV/Greeks/OI.
   - Macro: VIX, sector ETFs, yields/DXY/oil/gold/BTC; news, analyst actions and calendar via web search.
5. **Write** `options-dashboard/data/YYYY-MM-DD.json` in the schema documented at the top of `trade_ideas.py` (set `event: true` for earnings before the target expiry and `liquid: false` when ATM OI < 100 or bid/ask spread > 15% of mid).
6. **Build HTML:** copy the previous day's `daily_option_dashboard.html` as the template (keep the CSS, tab bar and script), replace Sections 1–10 content with today's data, then run
   `python3 options-dashboard/trade_ideas.py options-dashboard/data/YYYY-MM-DD.json --inject options-dashboard/daily_option_dashboard.html`
   which adds/replaces the **Key Levels** and **Trade Ideas** tabs.
7. **Price the top 5 ideas** (highest |trend score|, liquid, not "No trade") from the live chain and put debit/credit, max gain/loss and breakeven into Section 6 (Top 3) / Section 5.
8. **Verify:** open the page in headless Chromium; no JS errors; every tab shows.
9. **Save** a copy to `options-dashboard/reports/YYYY-MM-DD.html`, commit `data/`, `reports/` and `daily_option_dashboard.html`, and push to branch `daily-options-dashboard`.
10. Flag every unverified number with ⚠️. End with the educational-only disclaimer.
