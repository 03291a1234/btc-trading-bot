"""Command line entry point: `python -m copytrader <command>`."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from . import SIMULATION_FOOTER, journal
from .llm import DEFAULT_EFFORT, DEFAULT_MODEL
from .models import TransactionInput, template
from .prompts import REVIEW_COACH, SYSTEM_RULES
from .rules import check
from .sizer import size_position


def cmd_template(args) -> int:
    text = json.dumps(template(), indent=2, ensure_ascii=False)
    if args.out:
        Path(args.out).write_text(text + "\n")
        print(f"Wrote {args.out}. Fill it in from the transaction page on Etherscan.")
    else:
        print(text)
    return 0


def cmd_fetch(args) -> int:
    from .etherscan import fetch_transactions, history_text

    rows = fetch_transactions(args.address, limit=args.limit)
    text = history_text(args.address, rows)
    if args.out:
        Path(args.out).write_text(text + "\n")
        print(f"Wrote {len(rows)} transactions to {args.out}")
    else:
        print(text)
    return 0


def cmd_check(args) -> int:
    tx = TransactionInput.load(args.input)
    size = size_position(tx.rules)
    hits = check(tx, size)
    print(size.worksheet())
    print()
    if hits:
        print("Automatic rejection rules triggered (decision will be SKIP):")
        for h in hits:
            print(f"  - {h}")
    else:
        print("No automatic rejection rules triggered.")
    print(f"\n{SIMULATION_FOOTER}")
    return 1 if hits else 0


def cmd_run(args) -> int:
    from .llm import ClaudeLLM
    from .pipeline import run

    tx = TransactionInput.load(args.input)
    llm = ClaudeLLM(model=args.model, effort=args.effort)
    result = run(tx, llm, out_dir=args.out_dir,
                 journal_path=None if args.no_journal else args.journal,
                 log=lambda m: print(m, file=sys.stderr))
    print(result.reports["boss"])
    print("\n" + "=" * 60)
    print("Verdicts: " + ", ".join(f"{k}={v}" for k, v in result.verdicts.items() if v))
    if result.rule_hits:
        print("Automatic rejections: " + "; ".join(str(h) for h in result.rule_hits))
    for o in result.overrides:
        print(f"Override: {o}")
    print(f"FINAL SIMULATED DECISION: {result.final_decision}")
    if result.run_dir:
        print(f"Reports saved in: {result.run_dir}")
    print(SIMULATION_FOOTER)
    return 0


def cmd_review(args) -> int:
    rows = journal.read(args.journal)
    if len(rows) < 5 and not args.force:
        print(f"Only {len(rows)} simulations logged; the weekly review needs at least five "
              "(use --force to run anyway).")
        return 1
    from .llm import ClaudeLLM

    log_csv = Path(args.journal).read_text()
    prompt = (f"Role: Review coach\n\n{REVIEW_COACH}\n\n"
              f"<decision_log format=\"csv\">\n{log_csv}\n</decision_log>")
    print(ClaudeLLM(model=args.model, effort=args.effort).complete(SYSTEM_RULES, prompt))
    return 0


def main(argv=None) -> int:
    p = argparse.ArgumentParser(prog="copytrader", description="Claude Copy Trader Team (simulation only)")
    sub = p.add_subparsers(dest="command", required=True)

    s = sub.add_parser("template", help="write a blank transaction input file")
    s.add_argument("--out", "-o")
    s.set_defaults(func=cmd_template)

    s = sub.add_parser("fetch", help="fetch public history for an address from Etherscan")
    s.add_argument("address")
    s.add_argument("--limit", type=int, default=50)
    s.add_argument("--out", "-o")
    s.set_defaults(func=cmd_fetch)

    s = sub.add_parser("check", help="run sizing and automatic rejection rules only (no Claude)")
    s.add_argument("input")
    s.set_defaults(func=cmd_check)

    for name, func, help_ in (("run", cmd_run, "run all six agents on one transaction"),
                              ("review", cmd_review, "weekly review of the decision log")):
        s = sub.add_parser(name, help=help_)
        if name == "run":
            s.add_argument("input")
            s.add_argument("--out-dir", default="runs")
            s.add_argument("--no-journal", action="store_true")
        else:
            s.add_argument("--force", action="store_true")
        s.add_argument("--journal", default="wallet_log.csv")
        s.add_argument("--model", default=DEFAULT_MODEL)
        s.add_argument("--effort", default=DEFAULT_EFFORT,
                       choices=["low", "medium", "high", "xhigh", "max"])
        s.set_defaults(func=func)

    args = p.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
