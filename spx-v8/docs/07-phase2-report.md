# Phase 2 report: market data ingestion and validation

Date: 2026-11-02 (a regular session). Instrument decision: **SPX/SPXW**. Everything below was run in this session unless marked **not run**.

## Delivered

| Area | What |
|---|---|
| Domain | `BarValidator` (alignment, duplicates, OHLC consistency, gaps), `BarAggregator` (5 m only from **complete, elapsed** 1 m buckets), `BlackScholes` (price, Greeks, implied vol; outputs labeled `Estimated`), `ChainSnapshot`; audit timestamps truncated to µs |
| Application | `MarketDataRecorder` (chain snapshots + confirmed 1 m bars; invalid batches aren't stored), per-stream `MarketDataHealth`, `CalendarCrossCheck`, Greeks policy (`LocalEstimated` / `Vendor` / `VendorThenLocal`) |
| Infrastructure | Tradier `timesales`, `clock`, `calendar` (all behind the read-only guard); PostgreSQL/TimescaleDB store with embedded migrations (hypertables when Timescale is present; CHECK constraints on bars); append-only Postgres audit log (trigger blocks UPDATE/DELETE/TRUNCATE; advisory lock serializes appends); content-hashed snapshots verified on read; `RecordedReplayProvider` (as-of reads only); recorder hosted service |
| API | `/market/health`, `/market/bars/{symbol}`, `/market/snapshots`, `/market/snapshots/{id}` (replay-file export), `/recorder/run`, `/calendar/crosscheck` |
| Ops | Compose now includes TimescaleDB by default; CI runs the Postgres tests against a TimescaleDB service |
| Research / web | `spxquant.api_client` (bars → validated DataFrame); dashboard market-health panel; Greeks source shown on selections |
| Docs | **[How to use it](06-usage.md)**, updated API contract |

## Live Tradier verification (2026-11-02, 09:36 ET, read-only)

Read-only Tradier calls: SPX 0DTE chain with Greeks, and quotes for SPX and VIX. The raw chain was parsed by **v8's own parser and selector**.
The vendor data is kept out of this public repository.

| Question (backlog P2-01) | Observed | Consequence |
|---|---|---|
| Which roots does `symbol=SPX&expiration=today` return? | **516 contracts, all `SPXW`**, `expiration_type: weeklys`; 0 rows skipped by the parser | PM-settled filter is correct; no AM `SPX` rows on this date |
| Are `bid_date` / `ask_date` present? | Present on all 516 (epoch ms) | Freshness check is usable |
| How fresh are quotes? | Median `bid_date` age **28 s**, p90 377 s; only **232 / 516** had both sides ≤ 15 s old | Timestamps appear to change only when the price changes. The 15 s rule (a v7 decision) rejects about half the chain. **Kept as is**; revisit only with your approval. |
| Greeks timestamp and cadence | **484 / 516** stamped `2026-10-30 20:00:06`; 32 had no Greeks | Read as UTC that is **Friday's 16:00:06 ET close**: Tradier's Greeks are an end-of-day snapshot and stale all session. The Phase 1 default zone (Eastern) was wrong but failed closed. **Default changed to UTC.** |
| Are vendor deltas usable intraday? | 7630 C: vendor 0.30 vs local 0.40; 7610 P: vendor −0.61 vs local −0.42 | **Default Greeks source changed to `LocalEstimated`** (labeled Estimated) |
| What does 0.40 Δ cost? | 7630 C bid/ask 12.00 / 12.10 (**$1,212** worst case); 7610 P 12.90 / 13.00 (**$1,302**) | Confirms decision D-1 with real data: under $100 caps **0 / 516 eligible** with either Greeks source |
| SPX index quote | last 7618.28; bid/ask 7612.53 / 7624.37 | Index bid/ask still not meaningful; v8 uses `last` |
| VIX quote | last 19.04, bid/ask 0 / 0 | Use `last` only |
| Storage per snapshot (TimescaleDB, real chain) | 246 KB JSON → **31 KB stored** (TOAST compression), 20 round trips hash-verified | ≈ **200 MB/day at 15 s**, 100 MB/day at 30 s |

**Not verified live:** `markets/timesales` (no read-only MCP tool exposes it, and v8 has no token here), so the time-sales parser relies on Tradier's documented shape.
Also unverified: `markets/clock`, how far back 1 m history goes, and rate limits. First live run of the recorder: check `GET /api/v8/market/health` and `/market/bars/SPX` (see [usage](06-usage.md)).

## Validation results

| Check | Result |
|---|---|
| `dotnet test` with `SPXQ_TEST_PG` (TimescaleDB 2.17 / PG 16) | **147 passed, 0 failed, 0 skipped**: Domain 70, Application 10, Infrastructure 36 (7 Postgres), Api 31 (1 Postgres end-to-end replay) |
| `dotnet test` without a database | 139 passed, **8 skipped** (Postgres tests report Skipped, never Passed), 0 failed |
| Python `pytest` | **13 passed** |
| Web `npm test` / `npm run build` | **3 passed** / build passed |
| API Docker image (tests inside the build) | Passed |
| Compose (api + timescaledb + web) | Passed: migrations applied, audit row in Postgres, chain intact, all services healthy |
| Playwright e2e | **Passed** against the Compose API through the Vite dev server. The rebuilt *web image* could not be built here (Docker Hub HTTP 429 for `nginx`, a sandbox egress limit); its Dockerfile is unchanged since Phase 1, when it built. |
| Live recorder against Tradier | **Not run**: no token in this environment. The parser and selector were run on a live chain pulled via the read-only connector. |

## Defects found and fixed during Phase 2

1. **Audit hash broke after a Postgres round trip.** .NET timestamps carry 100 ns ticks while `timestamptz` stores µs, so verification failed. Found by the end-to-end replay test; fixed in the domain (truncate before hashing), with a regression test.
2. **Greeks timestamp zone wrong** (Eastern assumed; it's UTC), found by the live check. It had failed closed (stale or future-dated), so no wrong Greeks were ever accepted.

## Known limitations

- **Nothing is eligible under the current caps** (D-1, still open after choosing SPX).
- Local Greeks use Black-Scholes with q = 0 and r = 4% (configurable), and time to the 16:00 ET close (or the early close). They are model estimates.
- The recorder can't backfill option chains (Tradier has no historical chains). Unrecorded days are lost for option replay.
- Timesales parsing, the clock and calendar endpoints, and rate limits are unverified live (above).
- Single-node Postgres, no compression policy or retention yet (P10). A backup is a manual `pg_dump` (see usage).
- 15 s quote freshness removes about half of a live chain; deliberately unchanged.
