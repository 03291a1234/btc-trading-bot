"""Deterministic simulated position sizing.

Simulated risk budget = account size × max risk %.
Simulated position size = risk budget ÷ |entry − stop|.
This is a planning calculation, not a recommendation; fees, slippage,
liquidity and the possibility of total loss must be checked separately.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional

from .models import RuleSet


@dataclass
class SizeResult:
    ok: bool
    risk_budget: Optional[float] = None
    risk_per_unit: Optional[float] = None
    position_units: Optional[float] = None
    exposure: Optional[float] = None
    exposure_limit: Optional[float] = None
    problems: list[str] = field(default_factory=list)

    @property
    def label(self) -> str:
        return "SIZED" if self.ok else "NO SIZE"

    def worksheet(self) -> str:
        lines = [
            "Formula: risk_budget = account_size × max_risk_pct / 100",
            "         position_units = risk_budget ÷ |entry − stop|",
            "         exposure = position_units × entry",
            f"Risk budget:     {_fmt(self.risk_budget)}",
            f"Risk per unit:   {_fmt(self.risk_per_unit)}",
            f"Position units:  {_fmt(self.position_units)}",
            f"Total exposure:  {_fmt(self.exposure)}",
            f"Exposure limit:  {_fmt(self.exposure_limit)}",
            f"Result:          {self.label}",
        ]
        lines += [f"Problem: {p}" for p in self.problems]
        return "\n".join(lines)


def _fmt(v: Optional[float]) -> str:
    return "n/a" if v is None else f"{v:,.6f}".rstrip("0").rstrip(".")


def size_position(rules: RuleSet) -> SizeResult:
    missing = [
        name for name in ("account_size", "max_risk_pct", "entry", "stop")
        if getattr(rules, name) is None
    ]
    if missing:
        return SizeResult(ok=False, problems=[f"missing input: {', '.join(missing)}"])

    problems = []
    if rules.account_size <= 0:
        problems.append("account_size must be positive")
    if not 0 < rules.max_risk_pct <= 100:
        problems.append("max_risk_pct must be between 0 and 100")
    if rules.entry <= 0 or rules.stop <= 0:
        problems.append("entry and stop must be positive")
    if rules.entry == rules.stop:
        problems.append("entry equals stop; risk per unit is zero")
    if problems:
        return SizeResult(ok=False, problems=problems)

    risk_budget = rules.account_size * rules.max_risk_pct / 100
    risk_per_unit = abs(rules.entry - rules.stop)
    units = risk_budget / risk_per_unit
    exposure = units * rules.entry
    limit = rules.account_size * rules.max_position_pct / 100
    result = SizeResult(
        ok=True,
        risk_budget=risk_budget,
        risk_per_unit=risk_per_unit,
        position_units=units,
        exposure=exposure,
        exposure_limit=limit,
    )
    if exposure > limit:
        result.ok = False
        result.problems.append(
            f"exposure {_fmt(exposure)} exceeds limit {_fmt(limit)} "
            f"({rules.max_position_pct}% of account)"
        )
    return result
