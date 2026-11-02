# API contract (v8)

Base URL: `http://127.0.0.1:5080` locally (`dotnet run`), or through Compose on the same host port. JSON uses camelCase and string enums.

**Auth:** every `/api/*` route requires `X-Api-Key: <key>` (≥ 32 bytes, configured as `Api__Key`). A missing or wrong key gets `401` with an empty body.
`/health/*` routes are anonymous. Every response carries `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and `X-Execution-Mode: PAPER_ONLY`.

**Provenance** values: `Observed` (vendor), `Estimated` (model or assumption), `Simulated` (paper engine), `Synthetic` (fixture, not market data).

## Implemented (Phases 1–2)

| Method | Path | Purpose |
|---|---|---|
| GET | `/health/live` | Process is up |
| GET | `/health/ready` | Config loaded: data source, calendar coverage, market-data health (`NO_DATA_YET` / `HEALTHY` / `UNHEALTHY`). Does **not** prove vendor connectivity. |
| GET | `/api/v8/system` | `execution: "PAPER_ONLY"`, `liveOrderCapability: false`, data source and provenance, clock (and whether it's a replay clock), freshness limits |
| GET | `/api/v8/risk/limits` | `effective` limits, the `ceilings` config can't exceed, `feeProvenance: "Estimated"` |
| GET | `/api/v8/calendar/session?date=yyyy-MM-dd` | Session (status `Open` / `EarlyClose` / `Closed` / `Unknown`, open, close, entry cutoff, liquidation time, source) plus `entryAllowedNow` and the reason. A bad date format returns `400`. |
| GET | `/api/v8/market/index/{SPX\|VIX}` | `{ quote: {symbol,last,tradeAt,receivedAt}, provenance, valid, problems[] }`. Index bid/ask are deliberately not exposed. Upstream failure returns `502`. |
| GET | `/api/v8/options/chain/0dte?right=CALL\|PUT` | Today's (ET) chain from the active source; each row has `quote` plus `issues[]` (`QuoteIssue` names) |
| POST | `/api/v8/options/select` | Body `{ "right": "CALL" \| "PUT" }`. Evaluates hard constraints, ranks eligible contracts, appends a `CONTRACT_SELECTION` audit record. **Creates no order or position.** |
| GET | `/api/v8/market/health` | **(Phase 2)** Per-stream health (`chain`, `index`, `bars1m`, `calendar`): status, last success/failure, consecutive failures, last error; recorder state and its last result per step |
| GET | `/api/v8/market/bars/{SPX\|VIX}?tf=1m\|5m&from&to` | **(Phase 2)** Stored bars, confirmed at the current (or replay) clock only; ≤ 31 days. For today's 1m range also `gaps[]` (missing elapsed minutes). `503` without storage. |
| GET | `/api/v8/market/snapshots?from&to&limit` | **(Phase 2)** Recorded chain snapshot index `{id, underlying, expiration, capturedAt, source, provenance, optionCount, indexLast, contentSha256}` |
| GET | `/api/v8/market/snapshots/{id}` | **(Phase 2)** One snapshot in the replay-file format (`schemaVersion`, `capturedAt`, `provenance`, `index`, `options`). Content hash verified on read; tampering returns `500`. |
| POST | `/api/v8/recorder/run?step=chain\|bars[&date=yyyy-MM-dd]` | **(Phase 2)** Run one recorder step now (bars for a past date backfills that day). `503` unless TradierReadOnly + Postgres; `502` on vendor failure. |
| GET | `/api/v8/calendar/crosscheck?year&month` | **(Phase 2)** Local calendar vs Tradier `markets/calendar`: `{consistent, discrepancies[{date, local, broker}]}`. TradierReadOnly only. |
| GET | `/api/v8/audit?after={seq}&limit={1..1000}` | Audit records `{sequence, at, kind, subject, payloadJson, previousHash, hash}` |
| GET | `/api/v8/audit/verify` | `200 {intact:true}`, or `409 {intact:false, error}` if the on-disk chain was edited, truncated or reordered |

### `POST /api/v8/options/select` response

```jsonc
{
  "evaluatedAt": "2026-11-02T15:00:00+00:00",   // time the chain arrived (not request start)
  "expiration": "2026-11-02", "right": "CALL",
  "entryWindowOpen": true, "entryWindowReason": "OK",   // or MARKET_CLOSED, CALENDAR_UNKNOWN, BEFORE_ENTRY_WINDOW, AFTER_ENTRY_CUTOFF
  "dataSource": "Replay", "quoteProvenance": "Synthetic", "riskProvenance": "Estimated",
  "auditSequence": 1,
  "greeksSource": "Vendor", "estimatedGreeksCount": 0,   // Phase 2: Vendor | LocalEstimated | VendorThenLocal
  "received": 110, "eligibleCount": 0,
  "summary": "No eligible contract among 110; rejected rather than loosening limits.",
  "selected": null,                // or a contract object (below)
  "topEligible": [],               // up to 5, in rank order
  "rejectionCounts": {             // independent: one contract can count under several reasons
    "WrongExpiration": 0, "NotPmSettled": 1, "WrongRight": 54, "InvalidQuote": 22, "StaleQuote": 1, "StaleGreeks": 0,
    "InsufficientLiquidity": 0, "DeltaOutOfRange": 100, "SpreadTooWide": 0, "PremiumExposureExceeded": 72,
    "PlannedLossExceeded": 64, "WorstCaseLossExceeded": 72, "StopInsideSpread": 0
  },
  "note": "Evaluation only. No order or paper position is created in Phase 1. ..."
}
```

Contract object: `symbol, root, right, strike, expiration, bid, ask, spread, bidSize, askSize, volume, openInterest,
greeks{delta,gamma,theta,vega,midIv,updatedAt}, greeksProvenance (Observed|Estimated), deltaDistance, risk{quantity, entryPricePerShare, stopTriggerPerShare,
targetPerShare, entryDebit, plannedLoss, worstCaseLoss, roundTripFees}` (all risk values in $, `Estimated`).

Errors: `400` invalid `right`; `502` market data unavailable (an audit record `SELECTION_FAILED` is written).

## Planned (later phases): contract sketch, subject to change

| Phase | Method | Path | Notes |
|---|---|---|---|
| 3 | GET | `/api/v8/signals/current` | Per strategy A–G: state, 0–100 calibrated score, feature contributions, regime |
| 3 | GET | `/api/v8/signals/history?from&to&strategy` | Every evaluated signal, accepted or not |
| 4/8 | POST | `/api/v8/research/backtests` | Starts a run (dataset hash, params, cost model); returns a run ID |
| 4/8 | GET | `/api/v8/research/backtests/{id}` | IS and OOS reported **separately**; walk-forward folds; bootstrap CIs |
| 6 | GET | `/api/v8/risk/state` | Daily budget used, remaining capacity, consecutive losses, cooldowns, kill switches |
| 6 | POST | `/api/v8/risk/kill-switch` | Operator halt (entries only; exits and monitoring continue) |
| 6 | GET | `/api/v8/paper/positions`, `/api/v8/paper/orders`, `/api/v8/paper/fills` | All `Simulated` |
| 7 | WS | `/hubs/live` (SignalR) | `quote`, `bar`, `signal`, `riskState`, `position`, `fill`, `metrics` messages |
| 9 | POST | `/api/v8/signals/external` | Optional HMAC bridge for v7 gateway alerts (Decision D-3) |

v7 route compatibility: v8 doesn't re-expose v7's `/api/paper/*` or `/api/market/*` routes. v7 keeps serving them until the Phase 6 parity gate.
