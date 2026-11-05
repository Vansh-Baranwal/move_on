"""Claude provider for the fetch agent."""

from __future__ import annotations

import base64
import os

from prompts import SYSTEM_PROMPT, TOOLS

CLAUDE_DEFAULT_MODEL = "claude-sonnet-5-5"

# The system prompt carries no object data (see prompts.py leakage audit); it is
# static per run, so Claude may cache it freely.
SYSTEM_TEXT = SYSTEM_PROMPT


def create_provider(model: str | None):
    return ClaudeProvider(model)


def default_model() -> str:
    return os.environ.get("ANTHROPIC_MODEL", CLAUDE_DEFAULT_MODEL)


def check_api_key() -> None:
    if not os.environ.get("ANTHROPIC_API_KEY"):
        raise SystemExit(
            "ANTHROPIC_API_KEY is not set. Add your Claude API key to .env "
            "or export it in this terminal."
        )


class ClaudeProvider:
    """Anthropic Messages API - one tool call per step, prompt-cached system."""

    def __init__(self, model: str | None):
        try:
            import anthropic
        except ImportError as error:  # pragma: no cover
            raise SystemExit("pip install anthropic") from error
        self.model = model or default_model()
        self.client = anthropic.Anthropic()
        self.messages: list[dict] = []
        self.totals = {"input": 0, "output": 0, "cache_read": 0, "cache_write": 0}
        self._pending_id = None
        self._image_notes: dict[int, str] = {}

    def decide(self, text: str, jpeg: bytes | None = None):
        content = []
        if jpeg is not None:
            content.append({
                "type": "image",
                "source": {"type": "base64", "media_type": "image/jpeg", "data": base64.b64encode(jpeg).decode()},
            })
        content.append({"type": "text", "text": text})
        self.messages.append({"role": "user", "content": content})
        image_index = len(self.messages) - 1

        response = self.client.messages.create(
            model=self.model,
            max_tokens=4096,
            system=[{"type": "text", "text": SYSTEM_TEXT, "cache_control": {"type": "ephemeral"}}],
            tools=TOOLS,
            messages=prune_anthropic_images(self.messages, self._image_notes),
        )

        usage = response.usage
        step_usage = {
            "input": getattr(usage, "input_tokens", 0),
            "output": getattr(usage, "output_tokens", 0),
            "cache_read": getattr(usage, "cache_read_input_tokens", 0) or 0,
            "cache_write": getattr(usage, "cache_creation_input_tokens", 0) or 0,
        }
        for key in self.totals:
            self.totals[key] += step_usage[key]

        tool_uses = [block for block in response.content if block.type == "tool_use"]
        note = " ".join(getattr(block, "text", "") for block in response.content if block.type == "text").strip()
        self.messages.append({"role": "assistant", "content": response.content})
        if jpeg is not None and note:
            self._image_notes[image_index] = note

        if not tool_uses:
            self.messages.append({"role": "user", "content": [{"type": "text", "text": "Reply with your SEE line and exactly one tool call."}]})
            return "none", {}, step_usage, note

        call = tool_uses[0]
        self._pending_id = call.id
        return call.name, dict(call.input or {}), step_usage, note

    def report(self, result_json: str) -> None:
        self.messages.append({
            "role": "user",
            "content": [{
                "type": "tool_result",
                "tool_use_id": self._pending_id,
                "content": [{"type": "text", "text": result_json}],
            }],
        })


def prune_anthropic_images(messages: list[dict], image_notes: dict[int, str] | None = None) -> list[dict]:
    """Keep only the latest camera image; earlier ones become the model's own
    one-line SEE note for that step - its persistent visual memory."""
    image_notes = image_notes or {}
    last_image = -1
    for index, message in enumerate(messages):
        if message["role"] == "user" and any(
            isinstance(block, dict) and block.get("type") == "image" for block in message["content"]
        ):
            last_image = index

    pruned = []
    for index, message in enumerate(messages):
        if index == last_image or message["role"] != "user":
            pruned.append(message)
            continue
        placeholder = image_notes.get(index) or "[camera frame from an earlier step omitted]"
        content = [
            {"type": "text", "text": f"[earlier frame - you said: {placeholder}]"}
            if isinstance(block, dict) and block.get("type") == "image"
            else block
            for block in message["content"]
        ]
        pruned.append({**message, "content": content})
    return pruned
