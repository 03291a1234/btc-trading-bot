# SPX Quant Scalper Pro v8

Research-driven SPX/SPXW 0DTE options **paper-trading and research** platform, the successor to SPX Adaptive Scalper Pro v7
(`03291a1234/spx-v7`).

> **Paper and research only.** v8 has no brokerage order capability: no order, account or position code exists, and an outbound
> guard blocks any Tradier request that isn't a `GET /v1/markets/*`. Nothing here promises profitability. Every number is labeled
> Observed, Estimated, Simulated or Synthetic.

**Status: Phase 1 of 10 complete** (assessment, architecture, ported risk, selection and quote validation, calendar, audit chain,
replay, API, web shell, Compose). There is no signal engine or backtester yet. See [the Phase 1 report](docs/05-phase1-report.md).

| Doc | Contents |
|---|---|
| [01 v7 assessment](docs/01-v7-assessment.md) | Reusable code, 18 defects (3 high), architecture gaps |
| [02 Architecture and migration](docs/02-architecture-and-migration.md) | ADRs, repository layout, **decisions needed from you**, migration steps |
| [03 API](docs/03-api.md) | Implemented and planned endpoints |
| [04 Backlog](docs/04-backlog.md) | Prioritized items for Phases 2–10 |
| [05 Phase 1 report](docs/05-phase1-report.md) | Validation results, limitations, run instructions |

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

Under these rules a 0.40-delta SPXW contract almost never qualifies. That's an open decision for you (D-1 in the migration plan), and the system rejects rather than loosening limits.
