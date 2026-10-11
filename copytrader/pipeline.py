"""Runs the six agents in order, passing each labelled report to the next."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

from . import SIMULATION_FOOTER, journal
from .llm import LLM
from .models import TransactionInput
from .prompts import AGENT_ORDER, AGENTS, SYSTEM_RULES
from .rules import RuleHit, check
from .sizer import SizeResult, size_position

DECISIONS = ("COPY IN SIMULATION", "WAIT", "SKIP")


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


def enforce(boss: Optional[str], verdicts: dict[str, Optional[str]],
            size: SizeResult, hits: list[RuleHit]) -> tuple[str, list[str]]:
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
    if soft and decision == "COPY IN SIMULATION":
        overrides.append("Downgraded to WAIT: " + "; ".join(soft))
        decision = "WAIT"
    return decision, overrides


def run(tx: TransactionInput, llm: LLM, out_dir: str | Path | None = "runs",
        journal_path: str | Path | None = "wallet_log.csv",
        now: Optional[datetime] = None, log=print) -> RunResult:
    now = now or datetime.now(timezone.utc)
    size = size_position(tx.rules)
    hits = check(tx, size, now=now)

    reports: dict[str, str] = {}
    verdicts: dict[str, Optional[str]] = {}
    for agent in AGENT_ORDER:
        spec = AGENTS[agent]
        log(f"Running agent {spec['number']} {agent.capitalize()}…")
        text = llm.complete(SYSTEM_RULES, build_prompt(agent, tx, reports, size, hits))
        if SIMULATION_FOOTER not in text:
            text = text.rstrip() + "\n\n" + SIMULATION_FOOTER
        reports[agent] = text
        verdicts[agent] = parse_verdict(text, spec["labels"]) if spec["labels"] else None

    boss = verdicts["boss"]
    final, overrides = enforce(boss, verdicts, size, hits)
    result = RunResult(reports, verdicts, size, hits, boss, final, overrides)

    if out_dir is not None:
        stamp = now.strftime("%Y%m%dT%H%M%SZ")
        slug = re.sub(r"[^0-9a-zA-Z]", "", tx.tx_hash)[:10] or "notx"
        run_dir = Path(out_dir) / f"{stamp}_{slug}"
        run_dir.mkdir(parents=True, exist_ok=True)
        for agent in AGENT_ORDER:
            spec = AGENTS[agent]
            (run_dir / f"{spec['number']} {spec['title']}.txt").write_text(reports[agent])
        (run_dir / "input.json").write_text(json.dumps(tx.to_dict(), indent=2, ensure_ascii=False))
        (run_dir / "decision.json").write_text(json.dumps({
            "final_decision": final,
            "boss_decision": boss,
            "overrides": overrides,
            "verdicts": verdicts,
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
            "Run folder": str(result.run_dir or ""),
        })
    return result
