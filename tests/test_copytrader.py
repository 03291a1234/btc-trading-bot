from datetime import datetime, timezone
from pathlib import Path

from copytrader import SIMULATION_FOOTER, journal
from copytrader.cli import main
from copytrader.models import MarketSnapshot, RuleSet, TransactionInput
import pytest

from copytrader.interactive import ask_checklist, step_reviewer
from copytrader.outcome import simulated_pnl
from copytrader.pipeline import Review, RunAborted, enforce, parse_verdict, run
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
    assert enforce("COPY IN SIMULATION", ok, size, [], [])[0] == "COPY IN SIMULATION"
    assert enforce("COPY IN SIMULATION", ok, size, [])[0] == "WAIT"  # checklist not completed
    assert enforce("COPY IN SIMULATION", ok, size, [], ["I reviewed the final decision myself."])[0] == "WAIT"
    assert enforce("COPY IN SIMULATION", {**ok, "risk": "REJECT"}, size, [])[0] == "SKIP"
    assert enforce("COPY IN SIMULATION", {**ok, "risk": "NEEDS REVIEW"}, size, [])[0] == "WAIT"
    assert enforce(None, ok, size, [])[0] == "WAIT"


def test_pipeline_runs_six_agents_and_writes_outputs(tmp_path):
    llm = FakeLLM(APPROVE)
    result = run(clean_tx(), llm, out_dir=tmp_path / "runs", journal_path=tmp_path / "log.csv",
                 now=NOW, log=lambda m: None, checklist=lambda: [])
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
    assert rows[0]["Checklist"] == "8/8 confirmed"


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


def quiet_run(tx, llm, **kw):
    return run(tx, llm, now=NOW, log=lambda m: None, **kw)


def test_step_note_reaches_later_agents():
    llm = FakeLLM(APPROVE)
    notes = {"tracker": Review("continue", note="amount confirmed on Etherscan")}
    quiet_run(clean_tx(), llm, out_dir=None, journal_path=None,
              reviewer=lambda agent, text: notes.get(agent, Review("continue")))
    assert "HUMAN SOURCE CHECK: amount confirmed on Etherscan" in llm.prompts[2]


def test_step_edit_replaces_report_and_verdict():
    llm = FakeLLM(APPROVE)
    edit = {"risk": Review("continue", edited_text="rewritten\nVERDICT: REJECT")}
    result = quiet_run(clean_tx(), llm, out_dir=None, journal_path=None, checklist=lambda: [],
                       reviewer=lambda agent, text: edit.get(agent, Review("continue")))
    assert result.verdicts["risk"] == "REJECT"
    assert result.final_decision == "SKIP"


def test_step_stop_records_skip_and_skips_remaining_agents(tmp_path):
    llm = FakeLLM(APPROVE)
    result = quiet_run(clean_tx(), llm, out_dir=tmp_path / "runs", journal_path=tmp_path / "log.csv",
                       reviewer=lambda agent, text: Review("stop", note="looks like a bridge")
                       if agent == "tracker" else Review("continue"))
    assert len(llm.prompts) == 2
    assert result.final_decision == "SKIP" and result.stopped_after == "tracker"
    assert sorted(p.name for p in result.run_dir.glob("0*")) == ["01 Hunter Report.txt", "02 Tracker Record.txt"]
    row = journal.read(tmp_path / "log.csv")[0]
    assert "looks like a bridge" in row["Review notes"]
    assert row["Checklist"] == "skipped (run stopped)"


def test_step_abort_records_nothing(tmp_path):
    with pytest.raises(RunAborted):
        quiet_run(clean_tx(), FakeLLM(APPROVE), out_dir=tmp_path / "runs",
                  journal_path=tmp_path / "log.csv", reviewer=lambda a, t: Review("abort"))
    assert not (tmp_path / "log.csv").exists() and not (tmp_path / "runs").exists()


def test_interactive_step_reviewer_choices():
    answers = iter(["e", "n", "checked hash"])
    review = step_reviewer(input_fn=lambda _: next(answers), print_fn=lambda *a: None,
                           editor=lambda text: "edited")("hunter", "original")
    assert review == Review("continue", note="checked hash", edited_text="edited")
    answers = iter(["u", ""])
    review = step_reviewer(input_fn=lambda _: next(answers), print_fn=lambda *a: None)("hunter", "x")
    assert review.action == "stop" and review.note == "marked UNKNOWN"


def test_ask_checklist_returns_unconfirmed_items():
    answers = iter(["y"] * 7 + ["n"])
    missed = ask_checklist(input_fn=lambda _: next(answers), print_fn=lambda *a: None)
    assert missed == ["I reviewed the final decision myself."]


def test_simulated_pnl_long_short_and_stop():
    long_row = {"Entry": "2500", "Stop": "2400", "Proposed simulated size": "0.2"}
    assert simulated_pnl(long_row, 2600, False) == pytest.approx(20)
    assert simulated_pnl(long_row, None, True) == pytest.approx(-20)
    short_row = {"Entry": "2500", "Stop": "2600", "Proposed simulated size": "0.2"}
    assert simulated_pnl(short_row, 2400, False) == pytest.approx(20)
    assert simulated_pnl({**long_row, "Proposed simulated size": "NO SIZE"}, 2600, False) is None


def test_outcome_cli_updates_logged_row(tmp_path, capsys):
    log = tmp_path / "log.csv"
    quiet_run(clean_tx(), FakeLLM(APPROVE), out_dir=None, journal_path=log, checklist=lambda: [])
    assert main(["outcome", "--tx", "0xabc", "--price", "2650", "--note", "ran up",
                 "--journal", str(log)]) == 0
    row = journal.read(log)[0]
    assert row["Outcome price"] == "2650.0"
    assert row["Simulated P&L (if copied)"] == "30.00"
    assert row["Outcome notes"] == "ran up"
    assert main(["outcome", "--tx", "0xmissing", "--price", "1", "--journal", str(log)]) == 1


def test_old_log_is_migrated_to_new_columns(tmp_path):
    log = tmp_path / "log.csv"
    log.write_text("Date and time,Boss decision\n2026-10-01,SKIP\n")
    journal.append(log, {"Boss decision": "WAIT"})
    rows = journal.read(log)
    assert [r["Boss decision"] for r in rows] == ["SKIP", "WAIT"]
    assert list(rows[0]) == journal.COLUMNS
