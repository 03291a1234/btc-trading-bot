"""Terminal prompts for step-by-step source checks and the final checklist."""

from __future__ import annotations

import os
import subprocess
import tempfile
from typing import Callable, Optional

from .pipeline import Review
from .prompts import AGENTS, FINAL_CHECKLIST

STEP_HELP = (
    "[Enter] continue   [n] add source-check note   [e] edit report   "
    "[u] mark UNKNOWN and stop (SKIP)   [q] quit without recording"
)


def edit_in_editor(text: str) -> str:
    editor = os.environ.get("VISUAL") or os.environ.get("EDITOR") or "vi"
    with tempfile.NamedTemporaryFile("w+", suffix=".txt", delete=False) as f:
        f.write(text)
        path = f.name
    try:
        subprocess.run([*editor.split(), path], check=True)
        with open(path) as f:
            return f.read()
    finally:
        os.unlink(path)


def step_reviewer(input_fn: Callable[[str], str] = input, print_fn: Callable[..., None] = print,
                  editor: Callable[[str], str] = edit_in_editor):
    """After each agent: show the report and ask what to do (the guide's source check)."""

    def review(agent: str, text: str) -> Review:
        spec = AGENTS[agent]
        print_fn(f"\n{'=' * 20} {spec['number']} {spec['title']} {'=' * 20}\n{text}\n")
        print_fn("Source check: open the hash and confirm address, timestamp, asset and amount;\n"
                 "mark market data with its source and time; remove unsupported claims.")
        edited: Optional[str] = None
        while True:
            choice = input_fn(STEP_HELP + "\n> ").strip().lower()
            if choice == "":
                return Review("continue", edited_text=edited)
            if choice == "n":
                return Review("continue", note=input_fn("Note: ").strip(), edited_text=edited)
            if choice == "e":
                edited = editor(edited if edited is not None else text)
                print_fn("Report updated. Choose again.")
            elif choice == "u":
                return Review("stop", note=input_fn("Why is it UNKNOWN? ").strip() or "marked UNKNOWN",
                              edited_text=edited)
            elif choice == "q":
                return Review("abort")
            else:
                print_fn("Unrecognised choice.")

    return review


def ask_checklist(input_fn: Callable[[str], str] = input,
                  print_fn: Callable[..., None] = print) -> list[str]:
    """Ask each final checklist item; returns the items not confirmed."""
    print_fn("\nFinal checklist (answer y to confirm each item):")
    missed = []
    for item in FINAL_CHECKLIST:
        if input_fn(f"  ☐ {item} [y/N] ").strip().lower() not in ("y", "yes"):
            missed.append(item)
    return missed
