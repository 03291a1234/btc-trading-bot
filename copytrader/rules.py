"""The guide's automatic rejection rules, checked in code before any agent runs."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Optional

from .models import TransactionInput
from .sizer import SizeResult

NATIVE_ASSETS = {"ETH"}
TIME_FORMATS = ("%Y-%m-%d %H:%M", "%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%SZ", "%Y-%m-%dT%H:%M:%S")


@dataclass
class RuleHit:
    rule: str
    detail: str

    def __str__(self) -> str:
        return f"{self.rule}: {self.detail}"


def parse_utc(value: str) -> Optional[datetime]:
    for fmt in TIME_FORMATS:
        try:
            return datetime.strptime(value.strip(), fmt).replace(tzinfo=timezone.utc)
        except (ValueError, AttributeError):
            continue
    return None


def _is_placeholder(value: str) -> bool:
    v = (value or "").strip()
    return not v or "…" in v or v.endswith("...") or v in {"0x", "YYYY-MM-DD HH:MM"}


def check(tx: TransactionInput, size: SizeResult, now: Optional[datetime] = None) -> list[RuleHit]:
    now = now or datetime.now(timezone.utc)
    hits: list[RuleHit] = []

    if _is_placeholder(tx.tx_hash) or _is_placeholder(tx.source_url):
        hits.append(RuleHit("no-verified-source", "transaction hash or source URL is missing"))

    if tx.action_type != "trade":
        hits.append(RuleHit(
            "possible-transfer",
            f"action_type is '{tx.action_type}'; it may be a transfer, bridge, deposit, "
            "withdrawal, or internal wallet move rather than a trade",
        ))

    m = tx.market
    snap_time = parse_utc(m.time_utc)
    if m.price is None or m.liquidity_usd is None or snap_time is None:
        hits.append(RuleHit("missing-market-data", "current price, liquidity, or snapshot time is missing"))
    else:
        age_min = (now - snap_time).total_seconds() / 60
        if age_min > tx.rules.max_snapshot_age_minutes:
            hits.append(RuleHit(
                "stale-market-data",
                f"snapshot is {age_min:.0f} min old (limit {tx.rules.max_snapshot_age_minutes:.0f} min)",
            ))
        elif age_min < -5:
            hits.append(RuleHit("stale-market-data", "snapshot time is in the future"))

    if not size.ok:
        hits.append(RuleHit("position-exceeds-limit", "; ".join(size.problems) or "NO SIZE"))

    if tx.asset.strip().upper() not in NATIVE_ASSETS and not tx.token_risk_note.strip():
        hits.append(RuleHit(
            "unexplained-token-risk",
            f"asset '{tx.asset}' is not native ETH and no token_risk_note explains its contract risk",
        ))

    return hits
