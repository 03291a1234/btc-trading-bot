# Prioritized implementation backlog

Priority: **P0** blocks safety or correctness · **P1** required for the phase's acceptance · **P2** important · **P3** nice to have.
Each phase ends with: compiling code, unit and integration tests, config examples, API contract updates, run instructions,
validation results reported as passed/failed/not-run, and known limitations.

## Phase 1: assessment and architecture ✅ (this delivery)
Details in `05-phase1-report.md`.

## Phase 2: market data ingestion and validation
| ID | P | Item |
|---|---|---|
| P2-01 | P0 | **Live-session Tradier verification** (market hours): SPX chain `root_symbol` mix (SPXW vs SPX), `greeks.updated_at` time zone **and refresh cadence**, `bid_date`/`ask_date` presence, `timesales` 1 m history depth, VIX availability, rate limits. Record findings in a dated evidence file. |
| P2-02 | P0 | If vendor Greeks refresh too slowly: compute Black-Scholes delta, gamma and theta locally from the quote mid and IV (labeled `Estimated`) and compare with vendor values |
| P2-03 | P1 | Bar ingestion: SPX 1 m from Tradier `markets/timesales`, 5 m derived from **complete** 1 m buckets only; gap and duplicate detection; TimescaleDB hypertables |
| P2-04 | P1 | Chain snapshot recorder (every N seconds in session) so replay uses **observed** data; content-hashed files and DB rows |
| P2-05 | P1 | Move the audit log to PostgreSQL, keeping the hash chain; migrations; startup verification |
| P2-06 | P1 | Feed-health service: per-stream staleness, consecutive failures, disconnect detection (feeds Phase 6 kill switches) |
| P2-07 | P1 | Time-indexed replay provider (stream bars and quotes in order with a virtual clock) |
| P2-08 | P2 | Evaluate XSP chain availability and liquidity (Decision D-1a) |
| P2-09 | P2 | Calendar 2027 file, plus a startup cross-check against Tradier `markets/calendar` when live |
| P2-10 | P2 | OpenTelemetry traces and metrics, structured JSON logging |

## Phase 3: indicators and signal engine
| ID | P | Item |
|---|---|---|
| P3-01 | P0 | Indicator library on confirmed bars only (VWAP + slope, EMA 9/21/50, ADX/DI, ATR, realized vol, RSI, momentum acceleration, opening range); golden tests against hand-computed values |
| P3-02 | P0 | No-repaint harness: any indicator evaluated at time t, given the bars ≤ t, must equal the same indicator evaluated later. Property-based test. |
| P3-03 | P1 | Regime classifier (5 m) + entry timing (1 m), including the no-trade regime (G) |
| P3-04 | P1 | Strategies A–F as pure functions `(confirmed bars, regime, option liquidity) → SignalCandidate` with rationale |
| P3-05 | P1 | Score 0–100: raw logit → calibrated probability (isotonic or Platt) fitted on **historical outcomes**; no hand-set weights in production |
| P3-06 | P2 | Python parity tests: the research implementation equals C# outputs on the same replay files |

## Phase 4: historical backtesting framework
| ID | P | Item |
|---|---|---|
| P4-01 | P0 | Event-driven backtester reusing Domain risk, selection and fill code (no separate "backtest math") |
| P4-02 | P0 | Options cost model: entry at ask / exit at bid baseline, spread-aware slippage, per-contract fees as parameters; **no midpoint fills without evidence** |
| P4-03 | P1 | Metrics: win rate, average win/loss, expectancy, PF, max DD, Sharpe/Sortino (where meaningful), recovery factor, holding time, exposure-adjusted return, by hour and by regime |
| P4-04 | P1 | Dataset manifests (hashes, date ranges, provenance) attached to every run |

## Phase 5: options contract selection
| ID | P | Item |
|---|---|---|
| P5-01 | P1 | Calibrated multi-factor ranking (delta proximity, spread/mid, size, IV, gamma/theta, expected slippage), validated against realized execution quality |
| P5-02 | P1 | Implement whichever instrument and structure you choose for D-1 (XSP, verticals, …) |
| P5-03 | P2 | Expected-slippage model from recorded quotes |

## Phase 6: risk engine and execution simulator
| ID | P | Item |
|---|---|---|
| P6-01 | P0 | Stateful risk service (separate from signals): daily realized + unrealized loss, consecutive losses, max positions, aggregate premium, drawdown suspension, cooldowns, no averaging down / martingale / revenge (re-entry rules) |
| P6-02 | P0 | Kill switches: stale feed, disconnect, excessive spread, session cutoff, operator halt; exits continue when entries halt |
| P6-03 | P0 | Simulator: limit orders, partial fills (size-aware), unfilled and cancelled orders, latency, slippage, fees, stop / target / time exits, end-of-session liquidation |
| P6-04 | P0 | v7 parity tests: missed-expiry review, settlement attestation, entry pause, signal-ID dedup across restarts, budget reservation, no lock held across I/O (fixes D4–D6) |
| P6-05 | P1 | Immutable trail: signal → risk decision → order → fill → position change, all in the hash chain |

## Phase 7: real-time dashboard
| ID | P | Item |
|---|---|---|
| P7-01 | P1 | SignalR hub; TradingView Lightweight Charts (candles, VWAP/EMA overlays, signal and stop/target markers) |
| P7-02 | P1 | Panels: overview, options intelligence, strategy analytics (equity, drawdown, distribution, hourly heatmap, regime), trade journal (with chart snapshots and mistake tags), risk control center |
| P7-03 | P1 | Playwright e2e in CI (extends `web/e2e/smoke.cjs`) |

## Phase 8: walk-forward and strategy comparison
| ID | P | Item |
|---|---|---|
| P8-01 | P0 | Walk-forward with frozen parameters per fold; purged and embargoed time-series CV; IS and OOS reported separately |
| P8-02 | P1 | Bootstrap CIs, Monte Carlo trade-sequence shuffles, parameter-stability surfaces, transaction-cost sensitivity |
| P8-03 | P1 | Promotion gate: a strategy is enabled for paper trading only with a stored OOS report meeting pre-registered thresholds |
| P8-04 | P2 | ML baselines (logistic → GBM / RF) for P(target before stop within horizon); adopt only if OOS-calibrated and better than the baseline |

## Phase 9: forward paper validation
| ID | P | Item |
|---|---|---|
| P9-01 | P1 | Pre-registered forward test (frozen config hash, start date, min sample) on Observed data |
| P9-02 | P2 | Optional v7 Pine bridge for comparison (D-3) |

## Phase 10: hardening
| ID | P | Item |
|---|---|---|
| P10-01 | P1 | Audit chain anchoring (periodic hash export), backups, restore drill |
| P10-02 | P1 | Runbooks, alerting, dependency and container scanning, secrets management |
| P10-03 | P2 | Load and soak tests for a full session replay |
