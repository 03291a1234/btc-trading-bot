# SPX Quant Scalper Pro v8

Research-driven SPX/SPXW 0DTE options **paper-trading and research** platform, the successor to SPX Adaptive Scalper Pro v7
(`03291a1234/spx-v7`).

> **Paper and research only.** v8 has no brokerage order capability: no order, account or position code exists, and an outbound
> guard blocks any Tradier request that isn't a `GET /v1/markets/*`. Nothing here promises profitability. Every number is labeled
> Observed, Estimated, Simulated or Synthetic.

**Status: Phase 2 of 10 complete.** Phase 1 delivered the core: assessment, risk, selection and quote rules, calendar, audit chain, API, web shell and Compose.
Phase 2 adds live SPX data recording into TimescaleDB, replay of recorded days with no look-ahead, local Greeks, and a calendar cross-check.
There is no signal engine or backtester yet. **Start with [How to use it](docs/06-usage.md).**

| Doc | Contents |
|---|---|
| [01 v7 assessment](docs/01-v7-assessment.md) | Reusable code, 18 defects (3 high), architecture gaps |
| [02 Architecture and migration](docs/02-architecture-and-migration.md) | ADRs, repository layout, **decisions needed from you**, migration steps |
| [03 API](docs/03-api.md) | Implemented and planned endpoints |
| [04 Backlog](docs/04-backlog.md) | Prioritized items for Phases 2–10 |
| [05 Phase 1 report](docs/05-phase1-report.md) | Validation results, limitations, run instructions |
| [06 How to use it](docs/06-usage.md) | Step-by-step: synthetic demo, live recording, replay, research, backup |
| [07 Phase 2 report](docs/07-phase2-report.md) | Live Tradier findings (2026-11-02), validation results, limitations |

## Quick start

```bash
dotnet test                                                    # .NET 8 SDK
export Api__Key=$(python3 -c "import secrets;print(secrets.token_hex(24))")
dotnet run --project src/SpxQuant.Api --no-launch-profile     # synthetic replay, http://127.0.0.1:5080
# or: cp .env.example .env && docker compose up --build        # web on http://127.0.0.1:8080
```

## Defaults carried over from v7 (configurable only to be **stricter**)

Target |Δ| 0.40 ± 0.15 · max spread $0.50/share · planned loss ≤ $100 and worst-case loss ≤ $100 per trade including estimated fees ·
aggregate premium ≤ $100 · daily loss ≤ $200 · 1 open position · 3 trades/day · ×100 multiplier · quotes ≤ 15 s old ·
entries 09:35 ET until 30 min before close · SPXW (PM-settled) same-day expiry only.

**Instrument: SPX/SPXW (decided 2026-11-02).** On the live chain that morning, a 0.40-delta 0DTE SPXW cost $12–13 per share,
about $1,200–1,300 worst case per contract, so under the $100 caps nothing qualifies. How to reconcile SPX with the caps is still open
(see the Phase 2 report). Until then the system rejects rather than loosening limits.
