"""LLM providers for the fetch agent.

Both providers speak the same protocol: the agent feeds each step's observation
(state line + optional JPEG frame) and gets back exactly one tool call, then
reports the tool result. Switching engines is a --provider flag, nothing else.

- GeminiProvider (default): Google's OpenAI-compatible endpoint, vision + tools,
  generous free tier. Chosen because Groq currently serves no vision models.
- GroqProvider: same OpenAI-compatible shape, kept ready for when Groq ships a
  vision model again (its catalog is text-only right now).
- ClaudeProvider: Anthropic Messages API with prompt caching, kept for when the
  team shifts back to Sonnet.
"""

from __future__ import annotations

import base64
import json
import os

from prompts import SYSTEM_PROMPT, TOOLS

GROQ_BASE_URL = "https://api.groq.com/openai/v1"
GROQ_DEFAULT_MODEL = "meta-llama/llama-4-scout-17b-16e-instruct"  # vision models currently absent from Groq
GEMINI_BASE_URL = "https://generativelanguage.googleapis.com/v1beta/openai/"
# Verified live Nov 2026: 3.5-flash answers vision+tool requests; the 3.6-3.8
# generations were returning 503 (overloaded) and 4-argon is rate-gated on the
# free tier. Override with --model or GEMINI_MODEL when that changes.
GEMINI_DEFAULT_MODEL = "gemini-3.5-flash"
CLAUDE_DEFAULT_MODEL = "claude-sonnet-5-5"

PROVIDER_KEYS = {"gemini": "GEMINI_API_KEY", "groq": "GROQ_API_KEY", "claude": "ANTHROPIC_API_KEY"}

# The system prompt carries no object data (see prompts.py leakage audit); it is
# static per run, so providers may cache it freely.
SYSTEM_TEXT = SYSTEM_PROMPT


def create_provider(provider: str, model: str | None):
    if provider == "gemini":
        return OpenAICompatProvider(
            GEMINI_BASE_URL,
            os.environ[PROVIDER_KEYS["gemini"]],
            model or default_model("gemini"),
        )
    if provider == "groq":
        return OpenAICompatProvider(
            GROQ_BASE_URL,
            os.environ[PROVIDER_KEYS["groq"]],
            model or default_model("groq"),
        )
    if provider == "claude":
        return ClaudeProvider(model)
    raise ValueError(f"unknown provider: {provider}")


def default_model(provider: str) -> str:
    if provider == "gemini":
        return os.environ.get("GEMINI_MODEL", GEMINI_DEFAULT_MODEL)
    if provider == "groq":
        return os.environ.get("GROQ_MODEL", GROQ_DEFAULT_MODEL)
    return os.environ.get("ANTHROPIC_MODEL", CLAUDE_DEFAULT_MODEL)


def check_api_key(provider: str) -> None:
    needed = PROVIDER_KEYS[provider]
    if not os.environ.get(needed):
        raise SystemExit(
            f"{needed} is not set. Export it in this terminal first, e.g.:\n"
            f"  PowerShell: $env:{needed}=\"...\"\n"
            f"  Git Bash:   export {needed}=\"...\"\n"
            f"  Permanent:  setx {needed} \"...\"  (then reopen the terminal)"
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

    @property
    def totals(self_):  # pragma: no cover - kept for symmetry
        return self_.totals


class OpenAICompatProvider:
    """OpenAI-compatible chat completions (Gemini, Groq, ...); vision via data-URL images."""

    def __init__(self, base_url: str, api_key: str, model: str):
        try:
            from openai import OpenAI
        except ImportError as error:  # pragma: no cover
            raise SystemExit("pip install openai") from error
        self.model = model
        # Bounded retries with backoff so outages surface as errors instead of
        # the agent sitting silently for minutes.
        self.client = OpenAI(base_url=base_url, api_key=api_key, timeout=120.0, max_retries=3)
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
        self._image_notes: dict[int, str] = {}

    def decide(self, text: str, jpeg: bytes | None = None):
        parts = [{"type": "text", "text": text}]
        if jpeg is not None:
            encoded = base64.b64encode(jpeg).decode()
            parts.append({"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{encoded}"}})
        self.messages.append({"role": "user", "content": parts})
        image_index = len(self.messages) - 1

        response = self.client.chat.completions.create(
            model=self.model,
            # Thinking models (Gemini 3.x) spend completion tokens on reasoning
            # before the tool call; keep the ceiling generous.
            max_tokens=4096,
            tools=self.openai_tools,
            tool_choice="auto",
            messages=prune_openai_images(self.messages, self._image_notes),
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
        note = (message.content or "").strip()
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
        if jpeg is not None and note:
            self._image_notes[image_index] = note

        if not message.tool_calls:
            self.messages.append({"role": "user", "content": "Reply with your SEE line and exactly one tool call."})
            return "none", {}, step_usage, note

        call = message.tool_calls[0]
        self._pending_id = call.id
        try:
            args = json.loads(call.function.arguments or "{}")
        except json.JSONDecodeError:
            args = {}
        if not isinstance(args, dict):
            args = {}
        return call.function.name, args, step_usage, note

    def report(self, result_json: str) -> None:
        self.messages.append({"role": "tool", "tool_call_id": self._pending_id, "content": result_json})


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


def prune_openai_images(messages: list[dict], image_notes: dict[int, str] | None = None) -> list[dict]:
    """Same one-image rule for the OpenAI message format."""
    image_notes = image_notes or {}
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
        placeholder = image_notes.get(index) or "[camera frame from an earlier step omitted]"
        content = [
            {"type": "text", "text": f"[earlier frame - you said: {placeholder}]"}
            if part.get("type") == "image_url"
            else part
            for part in message["content"]
        ]
        pruned.append({**message, "content": content})
    return pruned
