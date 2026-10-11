"""Read-only fetch of public Ethereum transaction history via the Etherscan API.

Needs only a free Etherscan API key (ETHERSCAN_API_KEY). No wallet access.
"""

from __future__ import annotations

import json
import os
import urllib.parse
import urllib.request
from datetime import datetime, timezone

API_URL = "https://api.etherscan.io/v2/api"
WEI_PER_ETH = 10**18


def fetch_transactions(address: str, limit: int = 50, api_key: str | None = None) -> list[dict]:
    api_key = api_key or os.environ.get("ETHERSCAN_API_KEY")
    if not api_key:
        raise RuntimeError("Set ETHERSCAN_API_KEY to fetch public history from Etherscan.")
    params = {
        "chainid": 1,
        "module": "account",
        "action": "txlist",
        "address": address,
        "startblock": 0,
        "endblock": 99999999,
        "page": 1,
        "offset": limit,
        "sort": "desc",
        "apikey": api_key,
    }
    url = f"{API_URL}?{urllib.parse.urlencode(params)}"
    with urllib.request.urlopen(url, timeout=30) as resp:
        payload = json.load(resp)
    if payload.get("status") != "1":
        if payload.get("message") == "No transactions found":
            return []
        raise RuntimeError(f"Etherscan error: {payload.get('message')}: {payload.get('result')}")
    return [summarize(address, tx) for tx in payload["result"]]


def summarize(address: str, tx: dict) -> dict:
    """Reduce a raw Etherscan row to the fields the agents need.

    A plain ETH send with no call data is labelled "transfer"; a contract call is
    labelled "unknown" because only a human reading the hash can confirm a trade.
    """
    addr = address.lower()
    direction = "sent" if tx.get("from", "").lower() == addr else "received"
    calldata = tx.get("input", "0x")
    ts = datetime.fromtimestamp(int(tx["timeStamp"]), tz=timezone.utc)
    return {
        "tx_hash": tx["hash"],
        "timestamp_utc": ts.strftime("%Y-%m-%d %H:%M"),
        "from": tx.get("from", ""),
        "to": tx.get("to", ""),
        "direction": direction,
        "value_eth": int(tx.get("value", "0")) / WEI_PER_ETH,
        "method": tx.get("functionName", "").split("(")[0] or ("transfer" if calldata == "0x" else "unknown"),
        "action_type_guess": "transfer" if calldata in ("", "0x") else "unknown",
        "failed": tx.get("isError") == "1",
        "source_url": f"https://etherscan.io/tx/{tx['hash']}",
    }


def history_text(address: str, rows: list[dict]) -> str:
    lines = [
        f"Public transaction history for {address} (source: https://etherscan.io/address/{address})",
        "timestamp_utc | direction | value_eth | method | action_type_guess | failed | tx_hash",
    ]
    for r in rows:
        lines.append(
            f"{r['timestamp_utc']} | {r['direction']} | {r['value_eth']:.6f} | {r['method']} | "
            f"{r['action_type_guess']} | {r['failed']} | {r['tx_hash']}"
        )
    return "\n".join(lines)
