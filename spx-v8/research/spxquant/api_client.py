"""Read-only client for the v8 API (recorded, OBSERVED market data) -> pandas.

    from spxquant.api_client import ApiClient
    api = ApiClient("http://127.0.0.1:5080", key=os.environ["SPXQ_API_KEY"])
    bars = api.bars("SPX", "1m", "2026-11-02T14:30:00Z", "2026-11-02T21:00:00Z")

The key is read from the environment by the caller; never hard-code it.
"""
from __future__ import annotations

import json
import urllib.parse
import urllib.request
from typing import Callable

import pandas as pd

from .bars import validate_bars

Fetch = Callable[[urllib.request.Request], bytes]


def _default_fetch(req: urllib.request.Request) -> bytes:
    with urllib.request.urlopen(req, timeout=30) as resp:  # noqa: S310 (loopback API)
        return resp.read()


class ApiClient:
    def __init__(self, base_url: str, key: str, fetch: Fetch | None = None):
        if len(key.encode()) < 32:
            raise ValueError("API key must be at least 32 bytes")
        self._base = base_url.rstrip("/")
        self._key = key
        self._fetch = fetch or _default_fetch

    def _get(self, path: str, **params) -> dict | list:
        query = urllib.parse.urlencode({k: v for k, v in params.items() if v is not None})
        req = urllib.request.Request(f"{self._base}{path}{'?' + query if query else ''}", headers={"X-Api-Key": self._key})
        return json.loads(self._fetch(req))

    def bars(self, symbol: str, tf: str, start: str | None = None, end: str | None = None, validate: bool = True) -> pd.DataFrame:
        body = self._get(f"/api/v8/market/bars/{symbol}", tf=tf, **{"from": start, "to": end})
        df = pd.DataFrame(body["bars"], columns=["start", "open", "high", "low", "close", "volume"])
        df["start"] = pd.to_datetime(df["start"], utc=True)
        df.attrs.update(provenance=body["provenance"], symbol=symbol, timeframe=tf, gaps=body.get("gaps"))
        if validate and len(df):
            report = validate_bars(df, pd.Timedelta("1min" if tf == "1m" else "5min"))
            if not report.ok:
                raise ValueError(f"bar contract violations: {report.problems}")
        return df

    def snapshot(self, snapshot_id: int) -> dict:
        """One recorded chain snapshot in the replay-file format (provenance Observed)."""
        return self._get(f"/api/v8/market/snapshots/{snapshot_id}")
