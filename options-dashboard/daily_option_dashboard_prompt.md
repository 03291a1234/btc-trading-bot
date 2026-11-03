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
