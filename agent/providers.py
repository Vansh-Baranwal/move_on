"""LLM providers for the fetch agent.

Both providers speak the same protocol: the agent feeds each step's observation
(state line + optional JPEG frame) and gets back exactly one tool call, then
reports the tool result. Switching engines is a --provider flag, nothing else.

- GroqProvider (default): OpenAI-compatible endpoint, vision via data-URL images.
- ClaudeProvider: Anthropic Messages API with prompt caching, kept for when the
  team shifts back to Sonnet.
"""

from __future__ import annotations

import base64
import json
import os

from prompts import SYSTEM_PROMPT, TOOLS

GROQ_BASE_URL = "https://api.groq.com/openai/v1"
GROQ_DEFAULT_MODEL = "meta-llama/llama-4-scout-17b-16e-instruct"
CLAUDE_DEFAULT_MODEL = "claude-sonnet-5-5"

# The system prompt carries no object data (see prompts.py leakage audit); it is
# static per run, so providers may cache it freely.
SYSTEM_TEXT = SYSTEM_PROMPT


def create_provider(provider: str, model: str | None):
    if provider == "groq":
        return GroqProvider(model)
    if provider == "claude":
        return ClaudeProvider(model)
    raise ValueError(f"unknown provider: {provider}")


def default_model(provider: str) -> str:
    if provider == "groq":
        return os.environ.get("GROQ_MODEL", GROQ_DEFAULT_MODEL)
    return os.environ.get("ANTHROPIC_MODEL", CLAUDE_DEFAULT_MODEL)


def check_api_key(provider: str) -> None:
    needed = "GROQ_API_KEY" if provider == "groq" else "ANTHROPIC_API_KEY"
    if not os.environ.get(needed):
        raise SystemExit(
            f"{needed} is not set. Export it in this terminal first, e.g.:\n"
            f"  Git Bash: export {needed}=\"...\"\n"
            f"  CMD:      set {needed}=..."
        )


class ClaudeProvider:
    """Anthropic Messages API - one tool call per step, prompt-cached system."""

    def __init__(self, model: str | None):
        try:
            import anthropic
        except ImportError as error:  # pragma: no cover
            raise SystemExit("pip install anthropic") from error
        self.model = model or default_model("claude")
        self.client = anthropic.Anthropic()
        self.messages: list[dict] = []
        self.totals = {"input": 0, "output": 0, "cache_read": 0, "cache_write": 0}
        self._pending_id = None

    def decide(self, text: str, jpeg: bytes | None = None):
        content = []
        if jpeg is not None:
            content.append({
                "type": "image",
                "source": {"type": "base64", "media_type": "image/jpeg", "data": base64.b64encode(jpeg).decode()},
            })
        content.append({"type": "text", "text": text})
        self.messages.append({"role": "user", "content": content})

        response = self.client.messages.create(
            model=self.model,
            max_tokens=1024,
            system=[{"type": "text", "text": SYSTEM_TEXT, "cache_control": {"type": "ephemeral"}}],
            tools=TOOLS,
            messages=prune_anthropic_images(self.messages),
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
        self.messages.append({"role": "assistant", "content": response.content})

        if not tool_uses:
            self.messages.append({"role": "user", "content": [{"type": "text", "text": "Reply with exactly one tool call."}]})
            return "none", {}, step_usage

        call = tool_uses[0]
        self._pending_id = call.id
        return call.name, dict(call.input or {}), step_usage

    def report(self, result_json: str) -> None:
        self.messages.append({
            "role": "user",
            "content": [{
                "type": "tool_result",
                "tool_use_id": self._pending_id,
                "content": [{"type": "text", "text": result_json}],
            }],
        })

    @property
    def totals(self_):  # pragma: no cover - kept for symmetry
        return self_.totals


class GroqProvider:
    """OpenAI-compatible chat completions against Groq; vision via data-URL images."""

    def __init__(self, model: str | None):
        try:
            from openai import OpenAI
        except ImportError as error:  # pragma: no cover
            raise SystemExit("pip install openai") from error
        self.model = model or default_model("groq")
        self.client = OpenAI(base_url=GROQ_BASE_URL, api_key=os.environ["GROQ_API_KEY"])
        self.openai_tools = [
            {
                "type": "function",
                "function": {
                    "name": tool["name"],
                    "description": tool["description"],
                    "parameters": tool["input_schema"],
                },
            }
            for tool in TOOLS
        ]
        self.messages: list[dict] = [{"role": "system", "content": SYSTEM_TEXT}]
        self.totals = {"input": 0, "output": 0, "cache_read": 0, "cache_write": 0}
        self._pending_id = None

    def decide(self, text: str, jpeg: bytes | None = None):
        parts = [{"type": "text", "text": text}]
        if jpeg is not None:
            encoded = base64.b64encode(jpeg).decode()
            parts.append({"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{encoded}"}})
        self.messages.append({"role": "user", "content": parts})

        response = self.client.chat.completions.create(
            model=self.model,
            max_tokens=1024,
            tools=self.openai_tools,
            tool_choice="auto",
            messages=prune_openai_images(self.messages),
        )

        usage = response.usage
        step_usage = {
            "input": getattr(usage, "prompt_tokens", 0),
            "output": getattr(usage, "completion_tokens", 0),
            "cache_read": 0,
            "cache_write": 0,
        }
        for key in self.totals:
            self.totals[key] += step_usage[key]

        message = response.choices[0].message
        assistant = {"role": "assistant", "content": message.content}
        if message.tool_calls:
            assistant["tool_calls"] = [
                {
                    "id": call.id,
                    "type": "function",
                    "function": {"name": call.function.name, "arguments": call.function.arguments},
                }
                for call in message.tool_calls
            ]
        self.messages.append(assistant)

        if not message.tool_calls:
            self.messages.append({"role": "user", "content": "Reply with exactly one tool call."})
            return "none", {}, step_usage

        call = message.tool_calls[0]
        self._pending_id = call.id
        try:
            args = json.loads(call.function.arguments or "{}")
        except json.JSONDecodeError:
            args = {}
        if not isinstance(args, dict):
            args = {}
        return call.function.name, args, step_usage

    def report(self, result_json: str) -> None:
        self.messages.append({"role": "tool", "tool_call_id": self._pending_id, "content": result_json})


def prune_anthropic_images(messages: list[dict]) -> list[dict]:
    """Keep only the latest camera image; earlier ones become one-line notes."""
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
        content = [
            {"type": "text", "text": "[camera frame from an earlier step omitted]"}
            if isinstance(block, dict) and block.get("type") == "image"
            else block
            for block in message["content"]
        ]
        pruned.append({**message, "content": content})
    return pruned


def prune_openai_images(messages: list[dict]) -> list[dict]:
    """Same one-image rule for the OpenAI message format."""
    last_image = -1
    for index, message in enumerate(messages):
        if message["role"] == "user" and isinstance(message["content"], list) and any(
            part.get("type") == "image_url" for part in message["content"]
        ):
            last_image = index

    pruned = []
    for index, message in enumerate(messages):
        if index == last_image or message["role"] != "user" or not isinstance(message["content"], list):
            pruned.append(message)
            continue
        content = [
            {"type": "text", "text": "[camera frame from an earlier step omitted]"}
            if part.get("type") == "image_url"
            else part
            for part in message["content"]
        ]
        pruned.append({**message, "content": content})
    return pruned
