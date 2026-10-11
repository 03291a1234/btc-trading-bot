from datetime import datetime, timezone
from pathlib import Path

from copytrader import SIMULATION_FOOTER, journal
from copytrader.cli import main
from copytrader.models import MarketSnapshot, RuleSet, TransactionInput
from copytrader.pipeline import enforce, parse_verdict, run
from copytrader.rules import check
from copytrader.sizer import size_position

NOW = datetime(2026, 10, 11, 14, 15, tzinfo=timezone.utc)
EXAMPLE = Path(__file__).parent.parent / "examples" / "sample_input.json"


def clean_tx(**overrides) -> TransactionInput:
    tx = TransactionInput(
        wallet="0x1", source_url="https://etherscan.io/tx/0xabc", tx_hash="0xabc",
        timestamp_utc="2026-10-11 14:05", asset="ETH", direction="received", amount="1",
        action_type="trade",
        market=MarketSnapshot(price=2500, liquidity_usd=1e7, time_utc="2026-10-11 14:10"),
        rules=RuleSet(account_size=2000, max_risk_pct=1, entry=2500, stop=2400, max_position_pct=50),
    )
    for k, v in overrides.items():
        setattr(tx, k, v)
    return tx


class FakeLLM:
    def __init__(self, verdicts):
        self.verdicts = verdicts
        self.prompts = []

    def complete(self, system, user):
        self.prompts.append(user)
        role = user.split("\n", 1)[0].removeprefix("Role: ").lower()
        v = self.verdicts.get(role)
        if role == "boss":
            return f"Decision record.\nFINAL DECISION: {v}"
        return f"{role} report" + (f"\nVERDICT: {v}" if v else "")


APPROVE = {"hunter": "WATCH", "copier": "REVIEW", "risk": "APPROVE FOR SIMULATION",
           "boss": "COPY IN SIMULATION"}


def test_sizer_formula():
    s = size_position(RuleSet(account_size=2000, max_risk_pct=1, entry=2500, stop=2400))
    assert s.ok
    assert s.risk_budget == 20
    assert s.position_units == 0.2
    assert s.exposure == 500


def test_sizer_no_size_when_missing_or_over_limit():
    assert not size_position(RuleSet(account_size=2000, max_risk_pct=1, entry=2500)).ok
    tight_stop = RuleSet(account_size=2000, max_risk_pct=1, entry=2500, stop=2499)
    s = size_position(tight_stop)  # 20 units * 2500 = 50,000 exposure on a 2,000 account
    assert not s.ok and "exceeds limit" in s.problems[0]


def test_clean_tx_triggers_no_rules():
    tx = clean_tx()
    assert check(tx, size_position(tx.rules), now=NOW) == []


def test_rejection_rules():
    tx = clean_tx(tx_hash="0x…", action_type="transfer", asset="PEPE")
    tx.market.time_utc = "2026-10-11 12:00"
    rules = {h.rule for h in check(tx, size_position(tx.rules), now=NOW)}
    assert rules == {"no-verified-source", "possible-transfer", "stale-market-data",
                     "unexplained-token-risk"}


def test_parse_verdict_prefers_longest_label():
    labels = ["APPROVE FOR SIMULATION", "NEEDS REVIEW", "REJECT"]
    assert parse_verdict("blah\nVERDICT: NEEDS REVIEW", labels) == "NEEDS REVIEW"
    assert parse_verdict("**Verdict:** reject", labels) == "REJECT"
    assert parse_verdict("no verdict line", labels) is None
    assert parse_verdict("FINAL DECISION: COPY IN SIMULATION",
                         ["COPY IN SIMULATION", "WAIT", "SKIP"]) == "COPY IN SIMULATION"


def test_enforce_hard_and_soft_rules():
    size = size_position(clean_tx().rules)
    ok = {"copier": "REVIEW", "risk": "APPROVE FOR SIMULATION"}
    assert enforce("COPY IN SIMULATION", ok, size, [])[0] == "COPY IN SIMULATION"
    assert enforce("COPY IN SIMULATION", {**ok, "risk": "REJECT"}, size, [])[0] == "SKIP"
    assert enforce("COPY IN SIMULATION", {**ok, "risk": "NEEDS REVIEW"}, size, [])[0] == "WAIT"
    assert enforce(None, ok, size, [])[0] == "WAIT"


def test_pipeline_runs_six_agents_and_writes_outputs(tmp_path):
    llm = FakeLLM(APPROVE)
    result = run(clean_tx(), llm, out_dir=tmp_path / "runs", journal_path=tmp_path / "log.csv",
                 now=NOW, log=lambda m: None)
    assert len(llm.prompts) == 6
    # Each agent sees the earlier reports, and the Boss sees all five.
    assert "hunter report" in llm.prompts[1]
    assert all(f"{r} report" in llm.prompts[5] for r in ("hunter", "tracker", "copier", "sizer", "risk"))
    assert result.final_decision == "COPY IN SIMULATION"
    files = sorted(p.name for p in result.run_dir.iterdir())
    assert "06 Boss Decision.txt" in files and "decision.json" in files
    assert SIMULATION_FOOTER in (result.run_dir / "01 Hunter Report.txt").read_text()
    rows = journal.read(tmp_path / "log.csv")
    assert rows[0]["Boss decision"] == "COPY IN SIMULATION"
    assert rows[0]["Proposed simulated size"] == "0.2"


def test_pipeline_forces_skip_on_rejection_rule(tmp_path):
    result = run(clean_tx(action_type="transfer"), FakeLLM(APPROVE), out_dir=None,
                 journal_path=None, now=NOW, log=lambda m: None)
    assert result.final_decision == "SKIP"
    assert result.overrides


def test_example_input_loads_and_check_cli_flags_it(capsys):
    tx = TransactionInput.load(EXAMPLE)
    assert "0xdef456" in tx.history
    assert main(["check", str(EXAMPLE)]) == 1  # example is labelled action_type=unknown
    assert "possible-transfer" in capsys.readouterr().out
