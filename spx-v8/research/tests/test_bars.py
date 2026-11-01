import pandas as pd
import pytest

from spxquant.bars import confirmed_bars, resample_confirmed, validate_bars

ONE = pd.Timedelta("1min")


def bars(n=10, start="2026-11-02T14:30:00Z"):
    t = pd.date_range(start, periods=n, freq="1min")
    return pd.DataFrame({"start": t, "open": 100.0, "high": 101.0, "low": 99.0, "close": 100.5, "volume": 10})


def test_clean_bars_pass():
    assert validate_bars(bars(), ONE).ok


@pytest.mark.parametrize("mutate,needle", [
    (lambda d: d.drop(columns=["close"]), "missing columns"),
    (lambda d: pd.concat([d, d.iloc[[0]]]), "duplicate"),
    (lambda d: d.iloc[::-1], "monotonic"),
    (lambda d: d.assign(high=98.0), "high below"),
    (lambda d: d.assign(low=102.0, high=103.0), "low above"),
    (lambda d: d.assign(volume=-1), "negative volume"),
    (lambda d: d.assign(start=d["start"] + pd.Timedelta("10s")), "not aligned"),
])
def test_violations_reported(mutate, needle):
    report = validate_bars(mutate(bars()), ONE)
    assert not report.ok
    assert any(needle in p for p in report.problems), report.problems


def test_forming_bar_is_excluded():
    df = bars(3)
    as_of = pd.Timestamp("2026-11-02T14:32:30Z")  # third bar (14:32) is still forming
    assert len(confirmed_bars(df, as_of, ONE)) == 2
    assert len(confirmed_bars(df, pd.Timestamp("2026-11-02T14:33:00Z"), ONE)) == 3


def test_resample_drops_incomplete_buckets():
    df = bars(12)  # 14:30..14:41 -> two complete 5m bars, one partial
    five = resample_confirmed(df)
    assert list(five["start"].dt.strftime("%H:%M")) == ["14:30", "14:35"]
    assert (five["volume"] == 50).all()
    gap = df.drop(index=[6])  # remove 14:36 -> second bucket incomplete
    assert list(resample_confirmed(gap)["start"].dt.strftime("%H:%M")) == ["14:30"]
