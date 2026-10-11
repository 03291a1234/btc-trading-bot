"""Input records for one observed transaction plus the user's own rule set."""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Optional

ACTION_TYPES = ("trade", "transfer", "bridge", "deposit", "withdrawal", "internal", "unknown")


@dataclass
class MarketSnapshot:
    price: Optional[float] = None
    liquidity_usd: Optional[float] = None
    spread_pct: Optional[float] = None
    source: str = ""
    time_utc: str = ""  # "YYYY-MM-DD HH:MM"


@dataclass
class RuleSet:
    account_size: Optional[float] = None      # simulated account size
    max_risk_pct: Optional[float] = None      # max risk per idea, percent of account
    entry: Optional[float] = None
    stop: Optional[float] = None
    max_position_pct: float = 100.0           # max exposure as percent of account (100 = no leverage)
    max_snapshot_age_minutes: float = 15.0    # older market data counts as stale


@dataclass
class TransactionInput:
    wallet: str = ""
    wallet_label: str = ""
    source_url: str = ""
    tx_hash: str = ""
    timestamp_utc: str = ""
    asset: str = ""
    direction: str = ""
    amount: str = ""
    # What you confirmed the action was after opening the hash. Anything but
    # "trade" triggers the transfer/bridge/deposit/withdrawal rejection rule.
    action_type: str = "unknown"
    # Plain explanation of token/contract risk. Required for non-native assets.
    token_risk_note: str = ""
    price_at_observation: Optional[float] = None
    history: str = ""  # pasted public history, or see history_file
    history_file: str = ""
    notes: str = ""
    market: MarketSnapshot = field(default_factory=MarketSnapshot)
    rules: RuleSet = field(default_factory=RuleSet)

    @classmethod
    def from_dict(cls, data: dict) -> "TransactionInput":
        data = dict(data)
        market = MarketSnapshot(**(data.pop("market", None) or {}))
        rules = RuleSet(**(data.pop("rules", None) or {}))
        known = {k: v for k, v in data.items() if k in cls.__dataclass_fields__}
        return cls(**known, market=market, rules=rules)

    @classmethod
    def load(cls, path: str | Path) -> "TransactionInput":
        path = Path(path)
        tx = cls.from_dict(json.loads(path.read_text()))
        if tx.history_file and not tx.history:
            hist = Path(tx.history_file)
            if not hist.is_absolute():
                hist = path.parent / hist
            tx.history = hist.read_text()
        return tx

    def to_dict(self) -> dict:
        return asdict(self)


def template() -> dict:
    """A blank input file matching the guide's transaction input template."""
    tx = TransactionInput(
        wallet="0x…",
        wallet_label="",
        source_url="https://etherscan.io/address/…",
        tx_hash="0x…",
        timestamp_utc="YYYY-MM-DD HH:MM",
        asset="ETH",
        direction="sent",
        amount="",
        market=MarketSnapshot(source="", time_utc="YYYY-MM-DD HH:MM"),
    )
    return tx.to_dict()
