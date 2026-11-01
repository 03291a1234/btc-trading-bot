"""Bar data contract and look-ahead protection.

A bar is identified by its *start* time (UTC). It becomes usable only once it has closed:
``start + duration <= as_of``. Any feature computed at time t may only see bars confirmed at t.
"""
from __future__ import annotations

from dataclasses import dataclass, field

import pandas as pd

REQUIRED = ("start", "open", "high", "low", "close")


@dataclass
class BarReport:
    rows: int
    problems: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return not self.problems


def validate_bars(df: pd.DataFrame, duration: pd.Timedelta) -> BarReport:
    """Return every contract violation; never repairs data silently."""
    report = BarReport(rows=len(df))
    missing = [c for c in REQUIRED if c not in df.columns]
    if missing:
        report.problems.append(f"missing columns: {missing}")
        return report
    start = pd.to_datetime(df["start"], utc=True, errors="coerce")
    if start.isna().any():
        report.problems.append(f"{int(start.isna().sum())} unparseable start timestamps")
    if start.duplicated().any():
        report.problems.append(f"{int(start.duplicated().sum())} duplicate bar starts")
    if not start.dropna().is_monotonic_increasing:
        report.problems.append("bar starts are not monotonically increasing")
    # pandas 3 defaults to microsecond resolution; normalize before integer arithmetic.
    aligned = (start.dropna().dt.as_unit("ns").astype("int64") % int(duration.value)) == 0
    if not aligned.all():
        report.problems.append(f"{int((~aligned).sum())} bars not aligned to {duration}")
    o, h, l, c = (pd.to_numeric(df[k], errors="coerce") for k in ("open", "high", "low", "close"))
    if pd.concat([o, h, l, c], axis=1).isna().any(axis=None):
        report.problems.append("non-numeric or missing OHLC values")
    if ((h < o) | (h < c) | (h < l)).any():
        report.problems.append(f"{int(((h < o) | (h < c) | (h < l)).sum())} bars with high below open/close/low")
    if ((l > o) | (l > c)).any():
        report.problems.append(f"{int(((l > o) | (l > c)).sum())} bars with low above open/close")
    if (pd.concat([o, h, l, c], axis=1) <= 0).any(axis=None):
        report.problems.append("non-positive prices")
    if "volume" in df.columns and (pd.to_numeric(df["volume"], errors="coerce") < 0).any():
        report.problems.append("negative volume")
    return report


def confirmed_bars(df: pd.DataFrame, as_of: pd.Timestamp, duration: pd.Timedelta) -> pd.DataFrame:
    """Bars fully closed at ``as_of``. The forming bar is excluded (prevents repainting/leakage)."""
    start = pd.to_datetime(df["start"], utc=True)
    as_of = pd.Timestamp(as_of)
    as_of = as_of.tz_localize("UTC") if as_of.tzinfo is None else as_of.tz_convert("UTC")
    return df.loc[start + duration <= as_of]


def resample_confirmed(one_minute: pd.DataFrame, rule: str = "5min") -> pd.DataFrame:
    """Aggregate 1-minute bars into higher-timeframe bars, keeping only *complete* buckets.

    A 5-minute bar is emitted only if all five constituent minutes are present, so a partially
    formed higher-timeframe bar can never be used for regime classification.
    """
    df = one_minute.copy()
    df["start"] = pd.to_datetime(df["start"], utc=True)
    df = df.set_index("start").sort_index()
    agg = {"open": "first", "high": "max", "low": "min", "close": "last"}
    if "volume" in df.columns:
        agg["volume"] = "sum"
    grouped = df.resample(rule, label="left", closed="left")
    out = grouped.agg(agg)
    expected = int(pd.Timedelta(rule) / pd.Timedelta("1min"))
    out = out[grouped["close"].count() == expected]
    return out.reset_index()
