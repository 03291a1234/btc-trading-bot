"""Wallet log / simulated trade journal (CSV), using the guide's columns."""

from __future__ import annotations

import csv
from pathlib import Path
from typing import Callable

COLUMNS = [
    # The guide's wallet log columns.
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
    # Extra columns used by the checklist and the `outcome` command.
    "Entry",
    "Stop",
    "Checklist",
    "Outcome time",
    "Outcome price",
    "Stop hit",
    "Simulated P&L (if copied)",
    "Outcome notes",
    "Run folder",
]


def read(path: str | Path) -> list[dict]:
    path = Path(path)
    if not path.exists():
        return []
    with path.open(newline="") as f:
        return list(csv.DictReader(f))


def write_all(path: str | Path, rows: list[dict]) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=COLUMNS, extrasaction="ignore")
        writer.writeheader()
        for row in rows:
            writer.writerow({c: row.get(c, "") for c in COLUMNS})


def append(path: str | Path, row: dict) -> None:
    path = Path(path)
    if path.exists() and path.stat().st_size > 0:
        with path.open(newline="") as f:
            header = next(csv.reader(f), [])
        if header != COLUMNS:
            # Older log with fewer columns: rewrite it with the current header.
            write_all(path, read(path) + [row])
            return
        with path.open("a", newline="") as f:
            csv.DictWriter(f, fieldnames=COLUMNS, extrasaction="ignore").writerow(
                {c: row.get(c, "") for c in COLUMNS})
        return
    write_all(path, [row])


def update(path: str | Path, select: Callable[[list[dict]], int], changes: dict) -> dict:
    """Apply `changes` to the row whose index `select(rows)` returns; returns that row."""
    rows = read(path)
    index = select(rows)
    rows[index].update(changes)
    write_all(path, rows)
    return rows[index]
