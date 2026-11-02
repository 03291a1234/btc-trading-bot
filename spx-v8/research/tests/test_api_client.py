import json

import pytest

from spxquant.api_client import ApiClient

KEY = "k" * 32


def fake(body):
    seen = {}

    def fetch(req):
        seen["url"] = req.full_url
        seen["key"] = req.get_header("X-api-key")
        return json.dumps(body).encode()

    return fetch, seen


def test_bars_to_dataframe_with_provenance():
    body = {"provenance": "Observed", "gaps": [], "bars": [
        {"start": "2026-11-02T14:30:00+00:00", "open": 1, "high": 2, "low": 0.5, "close": 1.5, "volume": None},
        {"start": "2026-11-02T14:31:00+00:00", "open": 1.5, "high": 2, "low": 1, "close": 1.8, "volume": None}]}
    fetch, seen = fake(body)
    df = ApiClient("http://127.0.0.1:5080/", KEY, fetch).bars("SPX", "1m", "2026-11-02T14:30:00Z")
    assert len(df) == 2 and df.attrs["provenance"] == "Observed"
    assert seen["key"] == KEY
    assert "tf=1m" in seen["url"] and "from=2026-11-02T14%3A30%3A00Z" in seen["url"]


def test_invalid_bars_raise():
    body = {"provenance": "Observed", "bars": [
        {"start": "2026-11-02T14:30:00+00:00", "open": 1, "high": 0.9, "low": 0.5, "close": 1.5, "volume": None}]}
    fetch, _ = fake(body)
    with pytest.raises(ValueError, match="contract violations"):
        ApiClient("http://x", KEY, fetch).bars("SPX", "1m")


def test_short_key_rejected():
    with pytest.raises(ValueError):
        ApiClient("http://x", "short")
