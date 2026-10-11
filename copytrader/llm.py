"""Thin wrapper around the Anthropic SDK used by every agent."""

from __future__ import annotations

import os
from typing import Protocol

DEFAULT_MODEL = os.environ.get("COPYTRADER_MODEL", "claude-opus-5-5")
DEFAULT_EFFORT = os.environ.get("COPYTRADER_EFFORT", "high")


class LLM(Protocol):
    def complete(self, system: str, user: str) -> str: ...


class ClaudeLLM:
    """Calls Claude with adaptive thinking and server-side refusal fallbacks.

    Credentials resolve the usual way (ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN,
    or an `ant auth login` profile).
    """

    def __init__(self, model: str = DEFAULT_MODEL, effort: str = DEFAULT_EFFORT):
        import anthropic

        self.client = anthropic.Anthropic()
        self.model = model
        self.effort = effort

    def complete(self, system: str, user: str) -> str:
        with self.client.beta.messages.stream(
            model=self.model,
            max_tokens=64000,
            thinking={"type": "adaptive"},
            output_config={"effort": self.effort},
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
            # The system rules are identical for every agent, so cache them.
            system=[{"type": "text", "text": system, "cache_control": {"type": "ephemeral"}}],
            messages=[{"role": "user", "content": user}],
        ) as stream:
            response = stream.get_final_message()

        if response.stop_reason == "refusal":
            detail = getattr(response.stop_details, "explanation", None) or "no detail"
            return f"[Agent declined to answer: {detail}]\nVERDICT: UNKNOWN"
        text = "".join(b.text for b in response.content if b.type == "text")
        if response.stop_reason == "max_tokens":
            text += "\n[Output truncated at max_tokens]"
        return text
