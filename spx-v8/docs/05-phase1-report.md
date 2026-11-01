# Phase 1 report: repository assessment and architecture

Date: 2026-11-01. Everything below was run in this session unless it's marked **not run**.

## Delivered

- v7 assessment (`01-v7-assessment.md`), architecture and migration plan (`02-…`), API contract (`03-api.md`), backlog (`04-backlog.md`).
- .NET 8 clean-architecture solution: Domain, Application, Infrastructure, Api, plus three test projects.
  - **Domain:** quote validation (crossed, zero, negative, missing or stale timestamps, future timestamps, sizes, Greeks presence and age, delta sign); exchange calendar (holidays, early closes, DST, fail-closed coverage); `RiskLimits` with non-loosenable ceilings; `TradeRiskProfile` (planned vs worst-case loss); `ContractSelector` (13 independent rejection reasons, deterministic ranking); hash-chained `AuditRecord`.
  - **Infrastructure:** one Tradier client behind `ReadOnlyMarketDataGuard`; JSON mapping; replay provider and clock; JSONL hash-chained audit log; calendar loader; DI with startup validation.
  - **Api:** health, system, risk limits, calendar session, index quote, 0DTE chain with issues, contract selection (audited), audit read and verify.
- `config/calendar/us-equity-2026.json`, checked against Tradier `markets/calendar`.
- `config/replay/synthetic-0dte-2026-11-02.json`: **synthetic** Black-Scholes chain, reproducible with `research/tools/make_synthetic_snapshot.py`.
- `research/`: `spxquant.bars` (bar contract validation, confirmed-bar filter, complete-bucket 5 m resampling).
- `web/`: React/TS/Vite shell with provenance labels and an in-memory API key; Vitest; Playwright smoke script.
- `docker-compose.yml` + `deploy/*.Dockerfile` (the API image runs the test suite during build; non-root runtime; loopback-only host ports).

## Validation results

| Check | Command | Result |
|---|---|---|
| .NET build (warnings as errors) | `dotnet build` | **Passed** |
| Domain tests | `dotnet test` | **55 passed / 0 failed / 0 skipped** |
| Infrastructure tests | `dotnet test` | **24 passed / 0 / 0** |
| API + architecture tests | `dotnet test` | **21 passed / 0 / 0** |
| Mutation check: guard allows non-GET | manual edit + rerun | Suite **failed as expected** (1 test); restored |
| Mutation check: worst-case cap `>` → `>=` | manual edit + rerun | Suite **failed as expected** (1 test); restored |
| Python research tests | `python3 -m pytest` (pandas 3.0.5) | **10 passed** |
| Web unit tests | `npm test` | **2 passed** |
| Web production build | `npm run build` | **Passed** |
| API Docker image (tests inside the build) | `docker build` | **Passed** (100 tests inside the image) |
| Compose stack api + web | `docker compose up` | **Passed**: API healthy, web served, proxy enforces the key |
| Playwright e2e smoke against Compose | `node web/e2e/smoke.cjs` | **Passed** (also asserts the key isn't stored in web storage) |
| `--profile data` (TimescaleDB, Redis) | — | **Not run**: no Phase 1 code uses them |
| Live Tradier through the v8 API | — | **Not run**: no token in this environment, and the market was closed (Sunday) |

Environment notes: the Docker builds here needed this sandbox's egress proxy injected through a scratch-only Dockerfile variant
(adds a CA certificate). The committed Dockerfiles carry no sandbox-specific settings. On a normal workstation `docker compose up --build` needs no changes.
Docker Hub briefly rate-limited (HTTP 429) one pull; a retry succeeded.

## Findings from running it

1. **No contract is eligible on the synthetic chain** (0 of 110 for CALL and PUT). Every delta-compliant contract violates the $100 caps.
   This is the expected consequence of v7's own rules (assessment D3), not a bug. It needs **your decision D-1**.
2. Running in a container exposed two defects that were fixed before commit: appsettings' loopback `Urls` overrode the container binding,
   and the healthcheck used `/dev/tcp` under `sh` (dash).

## Known limitations

- Phase 1 has **no** signal engine, bars, backtester, risk state, paper positions or execution simulator. Those are Phases 2–6.
- The `greeks.updated_at` time zone (assumed America/New_York) and refresh cadence are **unverified**. If either is wrong, Greeks are rejected as stale or future-dated (fails closed); with an hourly cadence, live selection would reject everything until P2-02 lands.
- The SPX chain root mix (SPXW vs SPX) returned by `symbol=SPX` is unverified. Non-SPXW roots are rejected unless PM settlement is established.
- Calendar months 03, 08 and 10 of 2026 weren't fetched from Tradier. Option-specific closes on early-close days aren't encoded; cash-index times are used, with a 30-minute entry cutoff.
- Fees ($1/contract/side) and stop slippage ($0.05/share) are **estimates**, not a broker schedule.
- The audit log is tamper-evident, not tamper-proof; it's a single-writer process.
- Replay serves one static snapshot; time-indexed replay is P2-07.
- The API key is a single shared secret; there are no user accounts.

## Run it

```bash
cd spx-v8
dotnet test                                    # 100 .NET tests
cd research && python3 -m pytest && cd ..      # needs pandas, pytest
cd web && npm ci && npm test && npm run build && cd ..

# API on synthetic replay (no credentials needed):
export Api__Key=$(python3 -c "import secrets;print(secrets.token_hex(24))")
dotnet run --project src/SpxQuant.Api --no-launch-profile     # http://127.0.0.1:5080
curl -s -H "X-Api-Key: $Api__Key" -X POST -H 'Content-Type: application/json' \
     -d '{"right":"CALL"}' http://127.0.0.1:5080/api/v8/options/select

# Live read-only Tradier (market hours; token from your secret store, never on the command line history):
#   Platform__DataSource=TradierReadOnly Tradier__Token=<from secret store> dotnet run --project src/SpxQuant.Api --no-launch-profile

# Full stack:
cp .env.example .env    # set SPXQ_API_KEY
docker compose up --build          # web http://127.0.0.1:8080, api http://127.0.0.1:5080
SPXQ_API_KEY=... node web/e2e/smoke.cjs    # needs Playwright available to Node
```
