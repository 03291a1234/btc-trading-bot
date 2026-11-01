"""Generate a SYNTHETIC 0DTE SPXW replay snapshot from Black-Scholes.

This is NOT market data. It exists so the API, tests and dashboard can run without vendor access.
Prices are internally consistent model values with a crude spread model; they say nothing about
real liquidity, fills or profitability. A few deliberately defective rows exercise quote validation.

    python3 research/tools/make_synthetic_snapshot.py > config/replay/synthetic-0dte-2026-11-02.json
"""
import json
import math
from datetime import datetime, timedelta, timezone

CAPTURED = datetime(2026, 11, 2, 15, 0, 0, tzinfo=timezone.utc)  # 10:00 ET (EST, UTC-5)
CLOSE = datetime(2026, 11, 2, 21, 0, 0, tzinfo=timezone.utc)     # 16:00 ET
SPOT, SIGMA, RATE = 7580.0, 0.16, 0.04
EXP = "2026-11-02"


def ncdf(x: float) -> float:
    return 0.5 * (1.0 + math.erf(x / math.sqrt(2.0)))


def npdf(x: float) -> float:
    return math.exp(-0.5 * x * x) / math.sqrt(2 * math.pi)


def bs(strike: float, t: float, call: bool):
    d1 = (math.log(SPOT / strike) + (RATE + 0.5 * SIGMA ** 2) * t) / (SIGMA * math.sqrt(t))
    d2 = d1 - SIGMA * math.sqrt(t)
    if call:
        price = SPOT * ncdf(d1) - strike * math.exp(-RATE * t) * ncdf(d2)
        delta = ncdf(d1)
    else:
        price = strike * math.exp(-RATE * t) * ncdf(-d2) - SPOT * ncdf(-d1)
        delta = ncdf(d1) - 1
    gamma = npdf(d1) / (SPOT * SIGMA * math.sqrt(t))
    theta = (-SPOT * npdf(d1) * SIGMA / (2 * math.sqrt(t))) / 365.0
    vega = SPOT * npdf(d1) * math.sqrt(t) / 100.0
    return price, delta, gamma, theta, vega


def ms(dt: datetime) -> str:
    return dt.isoformat().replace("+00:00", "Z")


def quote(strike: float, call: bool, **overrides):
    t = (CLOSE - CAPTURED).total_seconds() / (365.0 * 86400)
    price, delta, gamma, theta, vega = bs(strike, t, call)
    half = max(0.05, min(0.40, 0.02 * price)) / 2
    bid = max(0.0, round(price - half, 2))
    ask = round(price + half, 2) if price > 0.05 else 0.05
    right = "C" if call else "P"
    q = {
        "symbol": f"SPXW261102{right}{int(strike * 1000):08d}",
        "root": "SPXW", "right": "Call" if call else "Put", "expiration": EXP, "strike": strike,
        "bid": bid, "ask": ask, "bidSize": 10, "askSize": 10,
        "bidAt": ms(CAPTURED - timedelta(seconds=2)), "askAt": ms(CAPTURED - timedelta(seconds=2)),
        "volume": 500, "openInterest": 1000,
        "greeks": {"delta": round(delta, 4), "gamma": round(gamma, 5), "theta": round(theta, 3),
                   "vega": round(vega, 3), "midIv": SIGMA, "updatedAt": ms(CAPTURED - timedelta(seconds=60))},
        "isPmSettled": True,
    }
    q.update(overrides)
    return q


options = []
for k in range(7450, 7715, 5):
    options.append(quote(float(k), True))
    options.append(quote(float(k), False))
# Deliberately defective rows (still synthetic):
options.append(quote(7800.0, True, bid=0.30, ask=0.20))                                    # crossed
options.append(quote(7805.0, True, bidAt=ms(CAPTURED - timedelta(minutes=5))))             # stale bid
options.append(quote(7810.0, True, bid=0.0))                                                # zero bid
options.append(quote(7350.0, False, root="SPX", isPmSettled=False,
                     symbol="SPX261102P07350000"))                                          # AM-settled root

snapshot = {
    "schemaVersion": 1,
    "capturedAt": ms(CAPTURED),
    "provenance": "Synthetic",
    "description": "SYNTHETIC Black-Scholes chain (S=7580, sigma=16%) for software testing only. Not market data.",
    "index": {"symbol": "SPX", "last": SPOT, "tradeAt": ms(CAPTURED - timedelta(seconds=1)), "receivedAt": ms(CAPTURED)},
    "options": options,
}
print(json.dumps(snapshot, indent=1))
