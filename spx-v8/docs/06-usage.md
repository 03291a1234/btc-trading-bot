# How to use SPX Quant Scalper Pro v8 (Phase 2)

v8 is **paper and research only**: it reads market data and never places orders. Phase 2 adds recording of live SPX data
(0DTE SPXW chain snapshots plus 1-minute bars) into TimescaleDB, replay of recorded days, and local Greeks.
There are no signals or trades yet; those start in Phase 3.

Run v7 and v8 side by side if you like: v7 uses port 5050, v8 uses 5080 (API) and 8080 (dashboard).

## 0. One-time setup (Mac)

1. Install **Docker Desktop** and start it. That's all the quick path needs.
   Optional for development: .NET 8 SDK, Node 22, Python 3.11+.
2. Get the code:
   ```bash
   git clone -b ccr-a6397824-p0m8dq https://github.com/03291a1234/btc-trading-bot.git
   cd btc-trading-bot/spx-v8
   ```
3. Create your private settings file:
   ```bash
   cp .env.example .env && chmod 600 .env
   python3 -c "import secrets;print(secrets.token_hex(24))"   # run twice: once for SPXQ_API_KEY, once for SPXQ_DB_PASSWORD
   ```
   Open `.env` in a text editor and paste the two values. `.env` is git-ignored; never commit it or paste it into chat.

## 1. First run: synthetic data (no Tradier needed)

```bash
docker compose up --build -d          # first build takes a few minutes; the API image runs all tests while building
docker compose ps                     # api and timescaledb should show "healthy"
```

Open **http://127.0.0.1:8080**, paste your `SPXQ_API_KEY`, click **Connect**, then **Evaluate 0DTE CALL**.
Every value is labeled **SYNTHETIC**. With your $100 limits, expect "No eligible contract", with the reasons listed.

## 2. Record live SPX data (market days)

Edit `.env`:
```
SPXQ_DATA_SOURCE=TradierReadOnly
TRADIER_TOKEN=<your Tradier production token>
SPXQ_RECORDER=true
```
Then, before 09:28 ET:
```bash
docker compose up -d --build api
```
From 2 minutes before the open to 2 minutes after the close, the recorder automatically saves:
- an **SPX quote + full 0DTE chain snapshot every 15 s**;
- **confirmed 1-minute SPX bars every 60 s**, plus 5-minute bars built only from complete 5-minute blocks. Bars that fail validation are not stored.

It does nothing on weekends and holidays (2026 calendar, checked against Tradier).

Check it is working (in a second terminal):
```bash
source .env   # loads SPXQ_API_KEY into this shell only
H="X-Api-Key: $SPXQ_API_KEY"; A=http://127.0.0.1:5080/api/v8

curl -s -H "$H" $A/market/health                         # streams chain/index/bars1m should be HEALTHY; recorder.last shows each step
curl -s -H "$H" "$A/calendar/crosscheck?year=2026&month=11"   # consistent:true means local calendar matches Tradier
curl -s -H "$H" "$A/market/bars/SPX?tf=5m"               # today's confirmed 5m bars, plus gaps[] for 1m
curl -s -H "$H" -X POST -H 'Content-Type: application/json' -d '{"right":"CALL"}' $A/options/select
```
Missed part of a day? After the close, fill in that day's bars (Tradier keeps recent 1-minute history; how far back is unverified):
```bash
curl -s -H "$H" -X POST "$A/recorder/run?step=bars&date=2026-11-02"
```
Chain snapshots **can't** be backfilled; Tradier has no historical chains. Days you don't record are lost for option replay.

**Disk use:** each chain snapshot holds every 0DTE strike. See the Phase 2 report for the size measured on 2026-11-02 and
plan disk accordingly. To record less often, add `Recorder__ChainIntervalSeconds: "30"` under `api.environment` in `docker-compose.yml`.

## 3. Replay a recorded day

Edit `.env`:
```
SPXQ_DATA_SOURCE=RecordedReplay
SPXQ_REPLAY_START=2026-11-02T15:00:00Z     # UTC; 15:00Z = 10:00 ET in winter, 14:00Z = 10:00 ET in summer
SPXQ_RECORDER=false
```
Then run `docker compose up -d api`. The API now acts as if it is that moment. It only sees snapshots captured at or before the replay time and bars already closed by then, so it can never peek ahead. Values are labeled **Observed** (recorded vendor data).

To export one snapshot as a standalone replay file:
```bash
curl -s -H "$H" "$A/market/snapshots?from=2026-11-02T14:00:00Z&to=2026-11-02T22:00:00Z" | head -c 600   # find ids
curl -s -H "$H" $A/market/snapshots/123 > config/replay/observed-2026-11-02-1000.json
```

## 4. Research in Python

```bash
cd research && pip install pandas numpy pytest
python3 - <<'PY'
import os
from spxquant.api_client import ApiClient
api = ApiClient("http://127.0.0.1:5080", key=os.environ["SPXQ_API_KEY"])
bars = api.bars("SPX", "1m", "2026-11-02T14:30:00Z", "2026-11-02T21:00:00Z")   # validated; raises on bad data
print(bars.attrs["provenance"], len(bars), bars.tail())
PY
```

## 5. Greeks source (`SPXQ_GREEKS_SOURCE`)

| Value | Behavior |
|---|---|
| `LocalEstimated` (**default**) | Computes Black-Scholes Greeks from each contract's bid/ask mid and SPX last, labeled **Estimated**. A contract with a stale quote gets equally stale Greeks and is rejected. |
| `Vendor` | Uses Tradier's Greeks and rejects any older than 5 minutes |
| `VendorThenLocal` | Uses Tradier's Greeks when fresh, otherwise local estimates |

Why local is the default: on 2026-11-02 at 09:36 ET, every SPXW contract's Tradier Greeks were stamped at the **previous
session's close** (Friday 16:00 ET). The vendor delta for a contract was off by 0.10–0.19 from one computed on live quotes.
`Vendor` therefore rejects everything intraday (safe, but useless), and using those deltas would pick the wrong strikes.

## 6. Stop, back up, reset

```bash
docker compose stop                                   # stops everything; data is kept
docker compose exec timescaledb pg_dump -U spxquant spxquant | gzip > spxquant-$(date +%F).sql.gz   # backup
docker compose down                                   # removes containers, keeps data volumes
docker compose down -v                                # DELETES recorded data and audit history; only if you mean it
```

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `401` | Missing or wrong `X-Api-Key` (or the key wasn't pasted in the dashboard) |
| `503` on bars, snapshots or recorder | Storage or data source not configured for that route (e.g. the recorder needs `TradierReadOnly`) |
| API exits at startup | Read `docker compose logs api`. Startup checks fail loudly: missing token, risk limits looser than the ceilings, recorder without Tradier, etc. |
| `502` from select or index | Tradier unreachable or token rejected; `market/health` shows `lastError` |
| Health `UNHEALTHY` for `bars1m` with "bar validation" | Tradier returned a malformed bar. Nothing is stored for that run; it retries next minute. |
| `entryWindowOpen:false` | Outside 09:35 ET to 30 min before the close, a holiday, or a date beyond 2026 (calendar unknown, so it fails closed) |
| Port already in use | Another app is on 5080, 8080 or 5432; stop it or change the host port in `docker-compose.yml` |

## Safety reminders

- Nothing in v8 can place, change or cancel an order. Tradier requests other than `GET /v1/markets/*` are blocked in code.
- All ports listen on `127.0.0.1` only. Don't expose them to the internet.
- Your risk limits can only be tightened in config, not loosened.
