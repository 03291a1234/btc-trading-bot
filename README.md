# Claude Copy Trader Team

A simulation-first command line tool built from the *SEB AI Setup Guide: Claude Copy Trader Team*.
Six Claude agents review one public Ethereum transaction in order and record a **simulated**
`COPY IN SIMULATION`, `WAIT`, or `SKIP` decision.

> **Simulation only. Not financial advice.** This tool never asks for or uses a private key,
> seed phrase, exchange password, or brokerage login, and it cannot place trades. The guide's
> "$2K to $11K" carousel figure is an unverified third-party claim; nothing here reproduces or
> promises it.

| Agent | Role | Job | Label |
| --- | --- | --- | --- |
| 01 | Hunter | Reviews the public wallet history | WATCH / RESEARCH MORE / IGNORE |
| 02 | Tracker | Records the newly observed transaction | — |
| 03 | Copier | Checks whether the original setup still exists | REVIEW / SKIP |
| 04 | Sizer | Simulated position size within your limits | NO SIZE if limits broken |
| 05 | Risk | Rejects trades with clear red flags | APPROVE FOR SIMULATION / NEEDS REVIEW / REJECT |
| 06 | Boss | Combines the reports | COPY IN SIMULATION / WAIT / SKIP |

Every agent gets the guide's Project Instructions as its system prompt, the transaction as
untrusted source material, and the earlier agents' reports, exactly as the guide's
"how to pass work between agents" section describes.

## What code enforces (not left to the model)

- **Sizing** is computed deterministically: `risk budget = account × max risk %`,
  `position = risk budget ÷ |entry − stop|`. Missing inputs, or exposure above
  `max_position_pct` of the account, give `NO SIZE`.
- **Automatic rejection rules** from the guide are checked before any agent runs:
  no verified hash/source URL; the action may be a transfer/bridge/deposit/withdrawal/internal move
  (anything but `action_type: "trade"`); missing or stale price/liquidity data; position over your
  limit; unexplained token/contract risk for non-ETH assets. Any hit forces **SKIP**.
- **Boss guardrails**: Risk `REJECT` forces SKIP. A `COPY IN SIMULATION` is downgraded to WAIT if
  Risk did not approve, Copier did not say REVIEW, or Sizer returned NO SIZE. An unreadable decision
  defaults to WAIT.

## Setup

```bash
pip install -r requirements.txt
export ANTHROPIC_API_KEY=...        # or `ant auth login`
export ETHERSCAN_API_KEY=...        # optional, only for `fetch`
```

Uses `claude-opus-5-5` with adaptive thinking at `high` effort by default; override with
`--model` / `--effort` or `COPYTRADER_MODEL` / `COPYTRADER_EFFORT`. Server-side refusal fallbacks
are enabled (`fallbacks: "default"`).

## Daily loop

```bash
# 1. Alert: Etherscan Watch List emails you when a watched address moves.
# 2. Pull the public history (read-only) and make an input file.
python -m copytrader fetch 0xADDRESS -o history.txt
python -m copytrader template -o tx.json
#    Fill tx.json from the transaction page: hash, time, asset, amount, and set
#    "history_file": "history.txt". Set "action_type" to "trade" only after you
#    confirmed it on Etherscan. Add a current market snapshot and your own limits.

# 3. Verify sizing and rejection rules without calling Claude.
python -m copytrader check tx.json

# 4–5. Run all six agents and record the simulated decision.
python -m copytrader run tx.json
```

Each run writes `runs/<time>_<hash>/` containing `01 Hunter Report.txt` … `06 Boss Decision.txt`,
`input.json`, and `decision.json`, and appends a row to `wallet_log.csv` with the guide's columns
(date, wallet label, address, hash, asset, direction, amount, source URL, snapshot time, Hunter
rating, Copier result, proposed size, Risk verdict, Boss decision, review notes).

```bash
# 6. After at least five simulations, run the weekly review coach.
python -m copytrader review
```

Try it with the bundled example (fake data): `python -m copytrader check examples/sample_input.json`.

## What this does not do

- Identify the person behind a wallet, or prove a transfer was a trade.
- Connect to a wallet, exchange, or brokerage, or execute anything live.
- Guarantee that copying public activity will work. Review every source yourself.

## Tests

```bash
pip install pytest && python -m pytest -q
```
