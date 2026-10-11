"""Wallet log / simulated trade journal (CSV), using the guide's columns."""

from __future__ import annotations

import csv
from pathlib import Path

COLUMNS = [
    "Date and time",
    "Wallet label",
    "Address",
    "Transaction hash",
    "Asset",
    "Direction",
    "Amount",
    "Source URL",
    "Market snapshot time",
    "Hunter rating",
    "Copier result",
    "Proposed simulated size",
    "Risk verdict",
    "Boss decision",
    "Review notes",
    "Run folder",
]


def append(path: str | Path, row: dict) -> None:
    path = Path(path)
    new = not path.exists() or path.stat().st_size == 0
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=COLUMNS, extrasaction="ignore")
        if new:
            writer.writeheader()
        writer.writerow({c: row.get(c, "") for c in COLUMNS})


def read(path: str | Path) -> list[dict]:
    path = Path(path)
    if not path.exists():
        return []
    with path.open(newline="") as f:
        return list(csv.DictReader(f))
