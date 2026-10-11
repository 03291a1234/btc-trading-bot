"""Runs the six agents in order, passing each labelled report to the next."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Optional

from . import SIMULATION_FOOTER, journal
from .llm import LLM
from .models import TransactionInput
from .prompts import AGENT_ORDER, AGENTS, FINAL_CHECKLIST, SYSTEM_RULES
from .rules import RuleHit, check
from .sizer import SizeResult, size_position

DECISIONS = ("COPY IN SIMULATION", "WAIT", "SKIP")


@dataclass
class Review:
    """What the human decided after reading one agent's report."""
    action: str  # "continue", "stop" (mark UNKNOWN, record SKIP) or "abort" (record nothing)
    note: str = ""
    edited_text: Optional[str] = None


class RunAborted(Exception):
    pass


Reviewer = Callable[[str, str], Review]
# Returns the checklist items left unconfirmed, or None if the checklist was not completed.
Checklist = Callable[[], Optional[list[str]]]


@dataclass
class RunResult:
    reports: dict[str, str]
    verdicts: dict[str, Optional[str]]
    size: SizeResult
    rule_hits: list[RuleHit]
    boss_decision: Optional[str]
    final_decision: str
    overrides: list[str] = field(default_factory=list)
    run_dir: Optional[Path] = None
    stopped_after: Optional[str] = None
    checklist_missed: Optional[list[str]] = None


def source_material(tx: TransactionInput) -> str:
    data = tx.to_dict()
    data.pop("history_file", None)
    history = data.pop("history", "") or "(no earlier history supplied)"
    return (
        "<source_material>\n"
        "<transaction>\n" + json.dumps(data, indent=2, ensure_ascii=False) + "\n</transaction>\n"
        "<wallet_history>\n" + history + "\n</wallet_history>\n"
        "</source_material>"
    )


def parse_verdict(text: str, labels: list[str]) -> Optional[str]:
    """Read the 'VERDICT:' (or 'FINAL DECISION:') line; longest label wins ties."""
    for line in reversed(text.splitlines()):
        m = re.match(r"\W*(?:VERDICT|FINAL DECISION)\W*:\s*(.+)", line.strip(), re.IGNORECASE)
        if not m:
            continue
        value = m.group(1).upper()
        for label in sorted(labels, key=len, reverse=True):
            if label in value:
                return label
    return None


def build_prompt(agent: str, tx: TransactionInput, reports: dict[str, str],
                 size: SizeResult, hits: list[RuleHit]) -> str:
    spec = AGENTS[agent]
    parts = [f"Role: {agent.capitalize()}", spec["prompt"]]
    if spec["labels"]:
        parts.append("State your label on its own line as 'VERDICT: <" + "|".join(spec["labels"]) + ">'.")
    parts.append(source_material(tx))
    if agent in ("sizer", "risk", "boss"):
        parts.append("<deterministic_size_calculation>\n" + size.worksheet() + "\n</deterministic_size_calculation>")
    if agent in ("risk", "boss"):
        rules_text = "\n".join(f"- {h}" for h in hits) or "- none triggered"
        parts.append("<automatic_rejection_rules_triggered>\n" + rules_text + "\n</automatic_rejection_rules_triggered>")
    for prior in AGENT_ORDER:
        if prior == agent:
            break
        title = f"{AGENTS[prior]['number']} {AGENTS[prior]['title']}"
        parts.append(f'<report title="{title}">\n{reports[prior]}\n</report>')
    return "\n\n".join(parts)


def checklist_status(missed: Optional[list[str]]) -> str:
    if missed is None:
        return "not completed"
    total = len(FINAL_CHECKLIST)
    if not missed:
        return f"{total}/{total} confirmed"
    return f"{total - len(missed)}/{total} confirmed; missing: " + " | ".join(missed)


def enforce(boss: Optional[str], verdicts: dict[str, Optional[str]],
            size: SizeResult, hits: list[RuleHit],
            checklist_missed: Optional[list[str]] = None) -> tuple[str, list[str]]:
    """Apply the guide's hard rules on top of the Boss agent's decision."""
    decision = boss or "WAIT"
    overrides = [] if boss else ["Boss decision could not be parsed; defaulting to WAIT"]
    if hits or verdicts.get("risk") == "REJECT":
        if decision != "SKIP":
            overrides.append("Automatic rejection rule or Risk REJECT forces SKIP")
        return "SKIP", overrides
    soft = []
    if verdicts.get("risk") != "APPROVE FOR SIMULATION":
        soft.append(f"Risk verdict is {verdicts.get('risk') or 'missing'}")
    if verdicts.get("copier") != "REVIEW":
        soft.append(f"Copier result is {verdicts.get('copier') or 'missing'}")
    if not size.ok:
        soft.append("Sizer returned NO SIZE")
    if checklist_missed is None or checklist_missed:
        soft.append(f"final checklist {checklist_status(checklist_missed)}")
    if soft and decision == "COPY IN SIMULATION":
        overrides.append("Downgraded to WAIT: " + "; ".join(soft))
        decision = "WAIT"
    return decision, overrides


def _with_footer(text: str) -> str:
    return text if SIMULATION_FOOTER in text else text.rstrip() + "\n\n" + SIMULATION_FOOTER


def run(tx: TransactionInput, llm: LLM, out_dir: str | Path | None = "runs",
        journal_path: str | Path | None = "wallet_log.csv",
        now: Optional[datetime] = None, log=print,
        reviewer: Optional[Reviewer] = None,
        checklist: Optional[Checklist] = None) -> RunResult:
    """Run the six agents.

    `reviewer` is called after each agent (the guide's source check) and may add a
    note, replace the report, stop the run, or abort it. `checklist` is called
    before the decision is recorded; without it the checklist counts as not
    completed, so the decision cannot be COPY IN SIMULATION.
    """
    now = now or datetime.now(timezone.utc)
    size = size_position(tx.rules)
    hits = check(tx, size, now=now)

    reports: dict[str, str] = {}
    verdicts: dict[str, Optional[str]] = {}
    stopped: Optional[tuple[str, str]] = None
    for agent in AGENT_ORDER:
        spec = AGENTS[agent]
        log(f"Running agent {spec['number']} {agent.capitalize()}…")
        text = _with_footer(llm.complete(SYSTEM_RULES, build_prompt(agent, tx, reports, size, hits)))
        review = reviewer(agent, text) if reviewer else Review("continue")
        if review.action == "abort":
            raise RunAborted(f"stopped by user after agent {spec['number']}; nothing recorded")
        if review.edited_text is not None:
            text = _with_footer(review.edited_text)
        if review.note:
            text += f"\n\nHUMAN SOURCE CHECK: {review.note}"
        reports[agent] = text
        verdicts[agent] = parse_verdict(text, spec["labels"]) if spec["labels"] else None
        if review.action == "stop":
            stopped = (agent, review.note)
            break

    boss = verdicts.get("boss")
    missed: Optional[list[str]] = None
    if stopped:
        final = "SKIP"
        overrides = [f"Marked UNKNOWN and stopped after {stopped[0]}: {stopped[1]}"]
    else:
        missed = checklist() if checklist else None
        final, overrides = enforce(boss, verdicts, size, hits, missed)
    result = RunResult(reports, verdicts, size, hits, boss, final, overrides,
                       stopped_after=stopped[0] if stopped else None, checklist_missed=missed)
    checklist_text = "skipped (run stopped)" if stopped else checklist_status(missed)

    if out_dir is not None:
        stamp = now.strftime("%Y%m%dT%H%M%SZ")
        slug = re.sub(r"[^0-9a-zA-Z]", "", tx.tx_hash)[:10] or "notx"
        run_dir = Path(out_dir) / f"{stamp}_{slug}"
        run_dir.mkdir(parents=True, exist_ok=True)
        for agent, text in reports.items():
            spec = AGENTS[agent]
            (run_dir / f"{spec['number']} {spec['title']}.txt").write_text(text)
        (run_dir / "input.json").write_text(json.dumps(tx.to_dict(), indent=2, ensure_ascii=False))
        (run_dir / "decision.json").write_text(json.dumps({
            "final_decision": final,
            "boss_decision": boss,
            "overrides": overrides,
            "verdicts": verdicts,
            "stopped_after": result.stopped_after,
            "checklist": checklist_text,
            "automatic_rejections": [str(h) for h in hits],
            "size": {**size.__dict__},
            "note": SIMULATION_FOOTER,
        }, indent=2))
        result.run_dir = run_dir

    if journal_path is not None:
        journal.append(journal_path, {
            "Date and time": now.strftime("%Y-%m-%d %H:%M UTC"),
            "Wallet label": tx.wallet_label,
            "Address": tx.wallet,
            "Transaction hash": tx.tx_hash,
            "Asset": tx.asset,
            "Direction": tx.direction,
            "Amount": tx.amount,
            "Source URL": tx.source_url,
            "Market snapshot time": tx.market.time_utc,
            "Hunter rating": verdicts.get("hunter") or "",
            "Copier result": verdicts.get("copier") or "",
            "Proposed simulated size": f"{size.position_units:.6g}" if size.ok else "NO SIZE",
            "Risk verdict": verdicts.get("risk") or "",
            "Boss decision": final,
            "Review notes": "; ".join(overrides),
            "Entry": "" if tx.rules.entry is None else tx.rules.entry,
            "Stop": "" if tx.rules.stop is None else tx.rules.stop,
            "Checklist": checklist_text,
            "Run folder": str(result.run_dir or ""),
        })
    return result
