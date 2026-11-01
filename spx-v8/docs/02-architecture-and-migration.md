# v8 architecture and migration plan

## Architecture decisions (ADRs)

| ID | Decision | Rationale |
|---|---|---|
| ADR-1 | **Clean architecture:** `Domain` ← `Application` ← `Infrastructure` ← `Api`. Domain has no framework or package references. | Risk, selection, calendar and audit logic is pure and unit-testable, and backtests and live paper use **the same code**. Enforced by `ArchitectureTests`. |
| ADR-2 | **No order capability, enforced three ways:** (1) no order, account or position port exists; (2) the outbound `ReadOnlyMarketDataGuard` allows only `GET https://…/v1/markets/*`; (3) architecture tests fail on order-like members or brokerage route literals. | v7 relied on convention (D12). Mutation-tested: weakening the guard fails the suite. |
| ADR-3 | **Ceilings vs limits:** `RiskCeilings` (code constants equal to the v7-confirmed values) bound `RiskLimits` (config). Loosening config fails startup. | Fails loudly instead of v7's silent clamp (D13). Raising a ceiling needs a reviewed code change. |
| ADR-4 | **Planned loss ≠ worst-case loss.** Both are computed per contract and both are gated: planned = stop-based (entry − (stop − slippage)) × 100 + fees; worst case = full premium + fees. | The brief requires the distinction. Gating worst case at $100 preserves the v7 decision; stops can gap on 0DTE. |
| ADR-5 | **Provenance on every number** (`Observed` / `Estimated` / `Simulated` / `Synthetic`), exposed in API DTOs and the UI. | Acceptance criterion 10. |
| ADR-6 | **Data-driven exchange calendar.** Dates outside coverage are `Unknown` and treated as non-trading. | Replay and backtests can't use a live broker clock (D14). Fails closed instead of guessing. |
| ADR-7 | **Replay is a first-class data source** with its own clock. The API runs with no credentials. | CI, tests, research and demos without vendor access (D10). |
| ADR-8 | **Append-only, SHA-256 hash-chained audit log.** Phase 1 uses JSONL; Phase 2 moves it to PostgreSQL with the same chain and verification. | Tamper-evident, not tamper-proof. External anchoring is in Phase 10. |
| ADR-9 | **Independent rejection reasons** per contract, not sequential survivor counts. | Diagnosability (D8). |
| ADR-10 | **Python owns research** (features, validation, ML); **C# owns runtime** (signals, risk, simulation, API). Signal definitions are specified once and implemented in C#; Python reproduces them and parity-tests against C# outputs on the same replay data. | Avoids a backtest/live divergence. ML models are exported as calibrated coefficients or tables, not opaque runtime calls. |
| ADR-11 | **Loopback by default.** In Compose, host ports bind to `127.0.0.1`. An API key (≥ 32 bytes, constant-time compare) is required on `/api/*`, and the browser holds it in memory only. | Carries v7's security posture into a multi-container setup. |
| ADR-12 | **An LLM never authorizes trades.** No LLM sits in the signal → risk → simulator path. | Requirement 9. |

## Repository structure (v8)

```text
spx-v8/
├── SpxQuant.sln, Directory.Build.props (net8.0, nullable, warnings-as-errors), global.json
├── src/
│   ├── SpxQuant.Domain/          pure: Market (quotes, bars, validation), Calendar, Risk, Options (selector), Audit (hash chain), Common (provenance, ET time)
│   ├── SpxQuant.Application/     ports (IMarketDataProvider, IAuditLog, IClock, ITradingCalendarProvider) + use cases (ContractSelectionService, MarketDataHealth)
│   ├── SpxQuant.Infrastructure/  Tradier (guarded GET client + JSON mapping), Replay, JSONL audit, calendar loader, clocks, DI
│   └── SpxQuant.Api/             minimal-API host, auth middleware, endpoints, appsettings
├── tests/                        Domain / Infrastructure / Api (+ architecture) xUnit suites
├── research/                     Python package `spxquant` (bar contracts, look-ahead guards) + tools (synthetic fixture generator)
├── web/                          React 18 + TypeScript + Vite dashboard shell, Vitest, Playwright e2e smoke
├── config/calendar/              exchange calendars (2026, Tradier-verified)
├── config/replay/                replay snapshots (currently one SYNTHETIC fixture)
├── deploy/                       Dockerfiles (API image runs the full test suite during build)
├── docker-compose.yml            api + web; `--profile data` adds TimescaleDB + Redis (used from Phase 2)
└── docs/                         assessment, this plan, API contract, backlog, Phase 1 report
```

Phase modules to add, in their layers: `Domain/Indicators`, `Domain/Signals`, `Domain/Execution` (fill model),
`Application/Backtest`, `Application/Risk` (stateful risk service), `Infrastructure/Persistence` (EF Core / Npgsql),
`Infrastructure/Streaming` (SignalR hub), `research/spxquant/{features,validation,models}`.

## Decisions only you can make (blocking for later phases, not for Phase 1)

**D-1. The 0.40-delta / $100 incompatibility (blocks Phases 5, 6 and 9).** A 0DTE 0.40 Δ SPXW long option costs roughly $300–1,500 worst case.
Under the current rules v8, like v7, will reject nearly every candidate (the synthetic replay shows exactly this: 0 of 110 eligible).
Options, none of which v8 will choose for you:

| Option | Effect | Caveats |
|---|---|---|
| (a) Trade **XSP** (Mini-SPX, 1/10 SPX notional, cash-settled, European) at 0.40 Δ | Premiums ~1/10, so the $100 cap becomes satisfiable | Thinner liquidity and relatively wider spreads; daily-expiration availability and Tradier chain coverage **must be verified** |
| (b) **Defined-risk SPXW debit verticals** (max loss = net debit ≤ $100) | Worst case is bounded by construction | 5-point strikes make ≤ $1.00 debits near 0.40 Δ uncommon; two legs double the costs |
| (c) Gate on **planned (stop) loss only**, allowing worst case > $100 | Many SPXW contracts become eligible | **Reverses a decision you confirmed in v7**; gap risk on 0DTE is real |
| (d) Keep SPXW and the caps, and **lower target delta** (~0.05–0.10) | Eligible contracts exist | Completely different payoff profile (lottery-like); 0.40 Δ was your spec |

My recommendation is to evaluate (a) first: in Phase 2, verify XSP chain availability and liquidity against real data. Until you decide, v8 keeps rejecting rather than loosening.

**D-2. Where v8 lives.** This session could push only to `03291a1234/btc-trading-bot` (branch `ccr-a6397824-p0m8dq`), so v8 is
self-contained under `spx-v8/` there. Recommended: move it into `spx-v7` (as `v8/` or a new branch) or a new `spx-v8`
repo with `git subtree split --prefix spx-v8`. Nothing in `spx-v8/` depends on the rest of that repo.

**D-3. Pine strategies.** v8 builds native C# signals (A–G). Do you want the existing Z4/Z7/HAMA Pine alerts bridged in
as a seventh "external" signal for side-by-side forward comparison (Phase 9)? The HAMA B export shows PF 0.93, so I wouldn't promote it.

## Migration plan (v7 → v8)

1. **Freeze v7** at `65757c0` as the reference. Keep running it for supervised paper sessions until v8 Phase 6 reaches parity. No new features go into v7.
2. **Phase 1 (done here):** skeleton, ported selection, quote and risk rules, calendar, read-only guard, audit chain, replay, API, web shell, Compose.
3. **Phase 2:** live-session verification of Tradier (chain roots, Greeks cadence and time zone, timesales/history entitlements); bar ingestion (1 m / 5 m SPX, VIX) into TimescaleDB; recording of option chain snapshots so replay data is **observed**, not synthetic; feed-health metrics.
4. **Phase 6 parity gate:** port the v7 ledger semantics (missed-expiry review, operator settlement attestation, entry pause, signal-ID dedup across restarts, daily budget reservation). v7's test cases carry over as acceptance tests. Only then retire v7's backend.
5. **Data migration:** v7 `paper-data/trades.json` (v2 schema) is imported read-only into a `legacy_v7_trades` table and tagged `Simulated/v7`. It is **never** mixed into v8 strategy statistics.
6. **Gateway:** v7 `gateway/` stays where it is. Phase 9 optionally points it at a v8 `/api/v8/signals/external` endpoint (D-3).
