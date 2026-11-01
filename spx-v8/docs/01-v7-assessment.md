# v7 repository assessment

**Subject:** `03291a1234/spx-v7` @ `65757c0` (main, 2026-10-23, "Compare HAMA entry research exports…").
**Method:** full read of the integrated backend (`spx-v7-phase1/SpXScalper.Api`, ~1,100 lines C#), tests (~1,040 lines),
docs, scripts and research folders; `dotnet test` run on a fresh clone; read-only Tradier calls to check calendar and quote shape.

## Baseline evidence

| Check | Result |
|---|---|
| `dotnet test` (v7, .NET 8.0.133) | **143 passed, 0 failed, 0 skipped** |
| Tradier `GET /v1/markets/calendar` 2026 (Jan, Feb, Apr–Jul, Sep, Nov, Dec) | Matches the NYSE schedule; early closes 13:00 on 2026-11-27 and 2026-12-24 |
| Tradier `GET /v1/markets/quotes?symbols=SPX,VIX` (Sunday 2026-11-01) | SPX `last` 7580.07; **SPX bid/ask 7524.56 / 7624.67 — not usable**; VIX bid/ask 0/0 |
| Tradier SPX option chain shape (`root_symbol` SPXW vs SPX, `greeks.updated_at` time zone and cadence) | **Not checked** — market closed and the full chain was too large to pull here. Must be checked during a live session (backlog P2-01). |

## What v7 is

A loopback ASP.NET Core 8 minimal API that **receives** signals (TradingView Pine alerts via an HMAC gateway, or by hand),
selects one 0DTE SPX/SPXW option, and keeps a local paper ledger. Signals are computed **outside** the system, in Pine
(Z4_VWAP, Z7_VWAP, HAMA v6.9.1 A/B). There is no indicator code, no bar data, no backtester and no research pipeline in C#.
The research folders hold TradingView exports and descriptive Python scripts; the HAMA B export (62 trades,
PF 0.934, net −9.03 index points) **does not show an edge**, and v7's own docs say so.

## Reusable (ported or kept as reference)

| v7 asset | v8 disposition |
|---|---|
| Hard risk decisions in `AGENTS.md` / `PaperSettings` (0.40 Δ, $0.50 spread, $100 caps, $200 daily, 1 position, 3 trades/day, ×100) | **Ported** as `RiskLimits` defaults plus `RiskCeilings` that config can't loosen |
| `OptionSelector` rules (expiry = today ET, SPXW/PM-only, sign-matched delta ± 0.15, spread, sizes, OI ≥ 10, vol ≥ 1, fee-inclusive max loss) | **Ported and extended** into `ContractSelector` (independent rejection reasons, planned vs worst-case loss, Greeks freshness, stop-inside-spread) |
| `QuoteSafety` (both timestamps required, ≤ 15 s, never future) | **Ported** into `QuoteValidator` (adds crossed, zero/negative, size, Greeks age, clock-skew tolerance) |
| `TradierReadOnlyClient` JSON mapping (array-or-object `option`, epoch-ms `bid_date`/`ask_date`) | **Ported** into `TradierJson` (keeps nulls instead of coercing, maps Greeks timestamp) |
| Loopback binding, constant-time secret compare, no-store headers, body limits | **Kept** as API defaults |
| HMAC TradingView gateway (`gateway/`), Pine sources, HAMA research scripts | **Kept in v7.** These are an *external signal source* in v8; they can be bridged in Phase 9 to compare against v8-native signals. They aren't needed for Phases 1–8. |
| Paper ledger: atomic write, schema checks, settlement attestation, review-required state for missed expiry | **Design reference** for Phase 6. v8 moves this to PostgreSQL with the hash-chained audit trail added in Phase 1. |
| 143 tests | Edge cases worth keeping (wrong expiry, missing Greeks, replay after restart, budget reservation) are carried into v8's backlog as test cases for Phase 6. |

## Defects and risks found

Severity: **H** can produce wrong risk or wrong data silently. **M** gives wrong or misleading output, or breaks operations. **L** is hygiene.

| # | Sev | Where (v7) | Finding | v8 status |
|---|---|---|---|---|
| D1 | **H** | `PaperEngine.cs:124` + `OptionSelector.cs` | **The stop can sit inside the spread.** Entry is at the ask and the stop fires when bid ≤ 0.70 × ask, with spreads up to $0.50 allowed on premiums ≤ $0.98. Any contract whose spread is > 30% of the ask (e.g. 0.60 / 0.90) stops out on the first 5-second monitor tick, for a certain loss. | **Fixed:** `StopInsideSpread` rejection + tests |
| D2 | **H** | `TradierReadOnlyClient.cs:43` | **Greeks freshness is never checked.** Delta is read without `greeks.updated_at`. Vendor Greeks may refresh much less often than quotes, so a stale delta can drive contract selection. | **Fixed:** `StaleGreeks` rejection; the timestamp time zone is configurable and fails closed (see L-limits) |
| D3 | **H** (design) | `Contracts.cs:2`, `OptionSelector.cs:27–28` | **0.40 Δ and the $100 worst-case cap don't fit together for SPX.** A 0.40 Δ 0DTE SPXW typically costs $3–15/share, i.e. $300–1,500 worst case. v7 correctly rejects these, so in practice it **almost never trades**. That's a strategy-design gap, not a code bug, and only the user can decide it (see migration plan, Decision D-1). | Preserved (rejects); surfaced in the API and dashboard |
| D4 | M | `PaperEngine.cs:96–97` | `_gate` is held across two Tradier HTTP calls (up to 12 s timeout each). This blocks the monitor, exits and every read endpoint during upstream latency. | Phase 6: no lock held over I/O |
| D5 | M | `PaperEngine.cs:243,264,286,311` | Sync `SemaphoreSlim.Wait()` on request threads (thread-pool starvation risk). `Summary()` reads state outside the lock. | Phase 6 |
| D6 | M | `PaperEngine.cs:164` | Monitor calls `markets/clock` for **every open position every 5 s before** checking the local exit window, so it polls the vendor all weekend while a position is open. | Phase 6 |
| D7 | M | `PaperEngine.cs:142, 202` | Every rejection rewrites the **whole** ledger JSON (all positions plus all events). Cost grows O(n) per decision, and the event list is unbounded. | Fixed for audit (append-only JSONL); Phase 2 moves to Postgres |
| D8 | M | `OptionSelector.cs` | Rule counts are sequential survivors, so a contract failing two rules shows only the first. That's misleading for diagnosis. | **Fixed:** independent per-reason counts |
| D9 | M | `Program.cs:24, 39` | Two Tradier clients with different base-URL conventions (`/v1/...` absolute vs `v1/` relative). This is the source of past 400/401 confusion. | **Fixed:** one client |
| D10 | M | `Program.cs:17` | Startup **requires** a production token, so the API can't run offline, in CI or in historical replay. | **Fixed:** `Replay` mode needs no token |
| D11 | M | `LegacyEndpoints.cs` `/api/market/quote` | Passes through raw SPX bid/ask, which are meaningless for an index (observed 7524/7624 around 7580). | **Fixed:** `IndexQuote` keeps `last` only |
| D12 | M | whole repo | The "no live orders" safety rests on convention only. Nothing stops a future change from calling `/v1/accounts/{id}/orders`. | **Fixed:** outbound `ReadOnlyMarketDataGuard` + architecture tests + mutation-checked |
| D13 | M | `RiskConfiguration.cs:17–19` | Loosening config is silently clamped, so the operator never learns their config was ignored. Most limits aren't configurable at all. | **Fixed:** all limits bind; loosening fails startup with a message |
| D14 | M | `MarketHours.cs` | Entry and exit windows are hard-coded weekday times; holidays and early closes rely only on the live broker clock, which isn't available in replay or backtest. | **Fixed:** data-driven calendar (Tradier-verified 2026); unknown dates fail closed |
| D15 | M | `PaperLedger.cs` | The audit log isn't tamper-evident; a deleted or edited event goes unnoticed. | **Fixed:** SHA-256 hash chain, verified on startup and on demand |
| D16 | L | `spx-v7-phase1/SpXScalper.Api/publish/` | Compiled binaries (`.dll`, `.pdb`) committed to git (180 KB, stale per README). | Not carried over |
| D17 | L | `TradierMarketClient` is in the global namespace; one 170-line `Program.cs` holds the routes, auth and HTTP proxying | Not carried over |
| D18 | L | Repo layout `spx-v7-phase1/…` nested under the root solution, with no layering. Domain logic depends on `HttpClient`. | Replaced by the clean-architecture layout |

## Architecture gaps relative to the v8 brief

| v8 requirement | v7 state |
|---|---|
| Native signal engine (VWAP, EMA, ADX, ATR, RSI, ORB, regimes) | **Absent:** Pine only, outside the system |
| Bars (1 m / 5 m), VIX, history, replay | **Absent** |
| Backtester, walk-forward, purged CV, bootstrap, Monte Carlo | **Absent:** descriptive CSV scripts only |
| Calibrated 0–100 score, ML baselines | **Absent** |
| Independent risk service: consecutive losses, cooldowns, drawdown suspension, stale-feed / disconnect kill switches | Partial: daily budget, position and trade caps, entry pause |
| Execution simulator: limit orders, partial fills, latency, slippage | **Absent:** instant fill at ask/bid |
| PostgreSQL / Timescale, Redis, SignalR, OpenTelemetry | **Absent:** JSON files, inline-HTML dashboard |
| React dashboard with charts | **Absent:** inline HTML string |
| Docker Compose | **Absent** |
| Observed / estimated / simulated labeling | Partial (`LOCAL_PAPER_ONLY` strings) |
