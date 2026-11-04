#!/usr/bin/env python3
"""Membrane robot agent: LLM tool-use loop over the Unity engine HTTP API.

Runs on Groq by default (OpenAI-compatible); switch back to Claude with
--provider claude. Usage:

    python agent/main.py --command "Bring the round red thing."
    python agent/main.py --provider claude --command "Bring the round red thing."
    python agent/main.py --dummy            # exercises every endpoint, no API cost

Per run it writes logs/<run_id>/log.jsonl with one line per step (step, timestamp,
tool, args, result, frame_path) and saves each camera frame as a PNG beside the log.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from engine_client import EngineClient, EngineError  # noqa: E402
from providers import check_api_key, create_provider, default_model  # noqa: E402

MAX_STEPS_HARD = 60
RUN_OUTCOMES = ("pass", "fail_wrong_object", "fail_not_reached", "fail_collision")
MOVEMENT_TOOLS = ("move", "turn")


def load_env_file(path: Path) -> None:
    """Load KEY=VALUE pairs from a .env file into os.environ (never overriding
    variables already set in the shell). Lets you keep API keys out of both
    the terminal and the repository."""
    if not path.exists():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key, value = key.strip(), value.strip().strip('"').strip("'")
        if key and key not in os.environ:
            os.environ[key] = value


load_env_file(Path(__file__).resolve().parent.parent / ".env")


class RunLogger:
    """JSONL log + PNG frames for one run, per the spec's logging contract."""

    def __init__(self, run_dir: Path):
        self.run_dir = run_dir
        self.frames_dir = run_dir / "frames"
        self.frames_dir.mkdir(parents=True)
        self.log_path = run_dir / "log.jsonl"
        self.step = 0

    def save_frame(self, jpeg_bytes: bytes) -> str:
        from PIL import Image
        import io
        self.step += 1
        path = self.frames_dir / f"step_{self.step:04d}.png"
        Image.open(io.BytesIO(jpeg_bytes)).save(path)  # frames saved as PNG beside the log
        return str(path.relative_to(self.run_dir))

    def write(self, record: dict) -> None:
        with self.log_path.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")

    def event(self, tool: str, args: dict, result, frame_path: str | None = None, usage: dict | None = None, note: str | None = None):
        record = {
            "step": self.step,
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "tool": tool,
            "args": args,
            "result": result,
            "frame_path": frame_path,
        }
        if usage:
            record["usage"] = usage
        if note:
            record["note"] = note
        self.write(record)


def push_step(engine: EngineClient, state: dict, tool: str, args: dict, result,
              frame_path: str | None, note: str | None):
    """Mirror one step into the engine's live log panel (UI-only, best-effort)."""
    try:
        engine.push_log({
            "step": state.get("step", 0),
            "state": state.get("state", ""),
            "tool": tool,
            "args": json.dumps(args or {}),
            "result": json.dumps(result),
            "collided": bool(isinstance(result, dict) and result.get("collided", False)),
            "note": note or "",
            "retries": int(state.get("retries", 0)),
            "outcome": (result or {}).get("outcome", "") if isinstance(result, dict) else "",
            "frame_path": frame_path or "",
            "x": float(state.get("x", 0.0)),
            "y": float(state.get("y", 0.0)),
            "heading": float(state.get("heading", 0.0)),
            "holding": bool(state.get("holding", False)),
        })
    except Exception:
        pass


def run_llm(engine: EngineClient, logger: RunLogger, command: str, provider, max_steps: int):
    """Shared tool-use loop: observation -> one tool call -> execute -> report."""
    outcome, reason = "fail_not_reached", "step budget exhausted"
    totals = provider.totals
    attach_image = True  # first decision always carries a frame

    for _ in range(max_steps):
        state = engine.state()
        try:
            depth = engine.depth()
        except EngineError:
            depth = None
        state_line = (
            f"state={state['state']} x={state['x']:.2f} y={state['y']:.2f} "
            f"heading={state['heading']:.1f} holding={state['holding']} retries={state['retries']}"
        )
        state_line += " depth=null" if depth is None else f" depth={depth:.2f}m"
        state["step"] = logger.step + 1

        if attach_image:
            jpeg = engine.look()
            frame_path = logger.save_frame(jpeg)
        else:
            jpeg = None
            frame_path = None
            state_line += " (no new image this step; move or turn to refresh the view)"

        tool_name, tool_args, usage, note = provider.decide(state_line, jpeg)

        if tool_name == "none":
            print(f"  step {logger.step}: (no tool call) SEE: {note[:120]}")
            logger.event("none", {}, {"error": "no tool call"}, frame_path=frame_path, usage=usage)
            push_step(engine, state, "none", {}, {"error": "no tool call"}, frame_path, note)
            continue

        result, finished, moved = execute_tool(engine, tool_name, tool_args)
        print(f"  step {logger.step}: {tool_name} {tool_args} -> {json.dumps(result)[:90]}")
        if note:
            print(f"           SEE: {note[:140]}")
        logger.event(tool_name, tool_args, result, frame_path=frame_path, usage=usage, note=note)
        push_step(engine, state, tool_name, tool_args, result, frame_path, note)
        provider.report(json.dumps(result))

        if finished:
            outcome = result.get("outcome", "fail_not_reached")
            reason = result.get("reason", "")
            break
        # Fresh image next step only after an actual movement, per the spec's control loop.
        attach_image = moved

    totals = provider.totals
    return outcome, reason, totals


def execute_tool(engine: EngineClient, name: str, args: dict):
    """Returns (result_dict, finished, moved)."""
    try:
        if name == "move":
            return engine.move(float(args["distance"])), False, True
        if name == "turn":
            return engine.turn(float(args["degrees"])), False, True
        if name == "depth":
            return {"depth": engine.depth()}, False, False
        if name == "pickup":
            return engine.pickup(), False, False
        if name == "drop":
            return engine.drop(), False, False
        if name == "finish":
            outcome = args.get("outcome", "fail_not_reached")
            if outcome not in RUN_OUTCOMES:
                outcome = "fail_not_reached"
            return {"outcome": outcome, "reason": args.get("reason", "")}, True, False
        return {"error": f"unknown tool: {name}"}, False, False
    except (EngineError, KeyError, TypeError, ValueError) as error:
        return {"error": str(error)}, False, False


def run_dummy(engine: EngineClient, logger: RunLogger):
    """Scripted plumbing test that touches every endpoint. Zero API cost."""
    script = [
        ("turn", {"degrees": 45}),
        ("move", {"distance": 2.0}),
        ("depth", {}),
        ("move", {"distance": 20.0}),   # expected to hit the wall and back off
        ("turn", {"degrees": 90}),
        ("move", {"distance": 1.0}),
        ("pickup", {}),
        ("drop", {}),
        ("finish", {"outcome": "fail_not_reached", "reason": "dummy run for HTTP plumbing"}),
    ]
    for name, args in script:
        frame_path = logger.save_frame(engine.look())
        if name == "depth":
            result = {"depth": engine.depth()}
        elif name == "finish":
            result = dict(args)
        elif name == "move":
            result = engine.move(args["distance"])
        elif name == "turn":
            result = engine.turn(args["degrees"])
        else:
            result = getattr(engine, name)()
        logger.event(name, args, result, frame_path=frame_path)
        push_step(engine, {"step": logger.step, "state": "Idle", "retries": 0, "x": 0.0,
                           "y": 0.0, "heading": 0.0, "holding": False},
                  name, args, result, frame_path, None)
        print(f"  {name} {args} -> {result}")
    return "fail_not_reached", "dummy run", {}


def main() -> int:
    parser = argparse.ArgumentParser(description="Membrane robot fetch agent")
    parser.add_argument("--command", help='Plain-language command, e.g. "Bring the round red thing."')
    parser.add_argument("--provider", choices=("local", "claude"), default="local",
                        help="Brain: local pixel-analysis policy (no API) or Claude")
    parser.add_argument("--model", default=None, help="Override the provider's default model")
    parser.add_argument("--max-steps", type=int, default=25, help="Soft target 25; hard stop 60.")
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--dummy", action="store_true", help="Scripted plumbing test, no API calls.")
    parser.add_argument("--no-record", action="store_true", help="Skip POV recording (logging only).")
    parser.add_argument("--out", default=None, help="Run directory override (default logs/<timestamp>).")
    args = parser.parse_args()

    if not args.dummy and not args.command:
        parser.error("--command is required unless --dummy is used")
    max_steps = min(max(1, args.max_steps), MAX_STEPS_HARD)
    model = args.model or default_model(args.provider)

    engine = EngineClient(args.base_url)
    engine.wait_ready()

    run_dir = Path(args.out) if args.out else Path("logs") / datetime.now().strftime("run_%Y%m%d-%H%M%S")
    run_dir.mkdir(parents=True, exist_ok=True)
    logger = RunLogger(run_dir)
    print(f"Run directory: {run_dir}")
    print(f"Provider: {args.provider}  model: {model}")

    command = args.command or "(dummy run)"
    engine.reset()
    if not args.no_record:
        pov_dir = str((run_dir / "pov").resolve())
        engine.record_start(command, frames_dir=pov_dir)

    try:
        if args.dummy:
            outcome, reason, totals = run_dummy(engine, logger)
        else:
            check_api_key(args.provider)
            provider = create_provider(args.provider, model, command=command)
            outcome, reason, totals = run_llm(engine, logger, command, provider, max_steps)
    finally:
        if not args.no_record:
            try:
                engine.record_stop()
            except EngineError as error:
                print(f"record_stop failed: {error}")

    steps = logger.step
    (run_dir / "meta.json").write_text(json.dumps({
        "command": command,
        "outcome": outcome,
        "reason": reason,
        "steps": steps,
        "provider": args.provider,
        "model": None if args.dummy else model,
        "finished_utc": datetime.now(timezone.utc).isoformat(),
    }, indent=2) + "\n", encoding="utf-8")

    logger.write({"event": "run_summary", "outcome": outcome, "reason": reason, "steps": steps,
                  "usage_totals": totals})
    push_step(engine, {"step": steps, "state": outcome, "retries": 0, "x": 0, "y": 0,
                       "heading": 0, "holding": False},
              "run_summary", {}, {"outcome": outcome, "reason": reason}, None, reason)
    print(f"Outcome: {outcome} ({reason}) after {steps} steps")
    if totals:
        print(f"Tokens: input {totals['input']} (cached read {totals['cache_read']}, "
              f"write {totals['cache_write']}), output {totals['output']}")
    return 0 if outcome == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
