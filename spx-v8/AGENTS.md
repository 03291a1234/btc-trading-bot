# Instructions for agents working on SPX Quant Scalper Pro v8

- Read `docs/02-architecture-and-migration.md` (ADRs, open decisions) before changing risk, selection, data or execution behavior.
- **Never** add brokerage order, account or position capability: no port, client method, route or string literal. Keep `ReadOnlyMarketDataGuard`
  limited to `GET /v1/markets/*`. Architecture tests enforce this; don't weaken them.
- `RiskCeilings` are user-confirmed limits. Configuration may only tighten them. Don't raise a ceiling, swap worst-case loss for a stop estimate, or
  resize positions to make a contract eligible. Reject instead. Decision D-1 belongs to the user.
- Keep Domain free of framework and package references. Backtest and paper paths must reuse the same Domain code.
- Label every number with its provenance. Never present synthetic, simulated or in-sample results as observed or out-of-sample.
  Don't fabricate backtest results, and don't claim profitability.
- Indicators and signals may use only bars confirmed at evaluation time. Add a no-repaint test with each indicator.
- Credentials come only from the environment or a secret store; never commit or print them. Keep services on loopback.
- After code changes run `dotnet test`, `python3 -m pytest` (in `research/`) and `npm test` (in `web/`), and report passed, failed, skipped and not-run counts honestly.
