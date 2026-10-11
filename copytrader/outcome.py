"""Step 6 of the daily loop: compare a logged decision with what happened."""

from __future__ import annotations

from typing import Optional


def _num(value: str) -> Optional[float]:
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def simulated_pnl(row: dict, outcome_price: Optional[float], stop_hit: bool) -> Optional[float]:
    """P&L had the simulated position been taken, whatever the decision was.

    Long when stop < entry, short when stop > entry. A hit stop loses the full
    risk budget (fees and slippage ignored).
    """
    entry, stop, units = _num(row.get("Entry")), _num(row.get("Stop")), _num(row.get("Proposed simulated size"))
    if entry is None or stop is None or units is None or entry == stop:
        return None
    if stop_hit:
        return -units * abs(entry - stop)
    if outcome_price is None:
        return None
    side = 1 if stop < entry else -1
    return side * units * (outcome_price - entry)


def find_row(rows: list[dict], tx_hash: Optional[str] = None, row_number: Optional[int] = None) -> int:
    """Row numbers are 1-based; a hash picks its most recent row."""
    if row_number is not None:
        if not 1 <= row_number <= len(rows):
            raise ValueError(f"row {row_number} is out of range (log has {len(rows)} rows)")
        return row_number - 1
    for i in range(len(rows) - 1, -1, -1):
        if rows[i].get("Transaction hash", "").lower() == (tx_hash or "").lower():
            return i
    raise ValueError(f"no logged decision for transaction {tx_hash}")
