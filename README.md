# Membrane Robot — Fetch Engine + LLM Agent

Unity 6 (6000.6.8f1) game engine + Python LLM agent implementing the Membrane
Robot Hackathon track: type a plain-language command, the model finds the object
from camera pixels alone, drives the wheeled robot to it, picks it up, carries it
to the drop zone and releases it.

**LLM backend: Groq (Llama 4 Scout) by default for now; Claude/Sonnet is kept
behind `--provider claude` for the switch back — see "LLM provider" below.**

## Quick start

```bash
# 1. Python deps (agent + clip tooling)
pip install -r requirements.txt

# 2. Start the engine
#    Open this project in Unity, press Play (any scene — the engine self-bootstraps),
#    and wait for "Robot engine API listening on http://localhost:8080/".
#    Headless alternative:
#    Unity.exe -batchmode -projectPath . -executeMethod RobotEngine.EditorTools.EngineServe.Run

# 3. Plumbing test — zero API cost, touches every endpoint:
python agent/main.py --dummy

# 4. Real run (needs GROQ_API_KEY in the environment):
export GROQ_API_KEY="gsk_..."
python agent/main.py --command "Bring the round red thing."

# 5. Phase 2 clip from the recorded POV frames (MP4 720p, 10–20 s, command overlay):
python tools/make_clip.py logs/<run_dir>
```

## LLM provider

| Provider | Command | API key env var | Default model |
|---|---|---|---|
| Groq (default) | `--provider groq` | `GROQ_API_KEY` | `meta-llama/llama-4-scout-17b-16e-instruct` |
| Claude | `--provider claude` | `ANTHROPIC_API_KEY` | `claude-sonnet-5-5` |

- Override the model with `--model` or `GROQ_MODEL` / `ANTHROPIC_MODEL`.
  Available Groq models: `curl https://api.groq.com/openai/v1/models -H "Authorization: Bearer $GROQ_API_KEY"`
  — pick a **vision + tool-calling** model.
- Both providers share the same loop, prompt, tools and logging; the switch is
  one flag. The agent sends the newest camera image each decision and keeps the
  history text-only (one line per earlier step) on either backend.

Every run writes `logs/<run_dir>/` with `log.jsonl` (one line per step: step,
timestamp, tool, args, result, frame_path, token usage), `frames/step_NNNN.png`
(the exact JPEG Claude saw) and `pov/` (720p POV frames). `meta.json` records the
command, outcome and step count.

## Architecture

```
┌───────────────────────── Unity engine (this project) ─────────────────────────┐
│  WorldBuilder      flat 10 m room, 5 primitives (2 red, 2 cubes), drop zone   │
│  RobotController   1 m/s move, 90°/s turn, capsule-cast collisions,           │
│                    0.1 m auto-backoff, snap pickup (0.5 m, ±30°)              │
│  AgentCamera       640×480 JPEG, 90° hFOV, 0.5 m high, 10° down; depth ray    │
│  EngineCore        state machine (Idle/Locate/GoToObject/PickUp/Carry/Drop/   │
│                    Done/Blocked), retry counter, main-thread dispatch         │
│  GameServer        HttpListener on localhost:8080 — blocks per request, so    │
│                    the scene advances only during /move /turn /drop           │
│  PovRecorder       separate 720p camera → frames for the Phase 2 clip         │
│  HudDisplay        game-view status strip (never captured by the POV camera)  │
└───────────────────────────────────────────────────────────────────────────────┘
            ▲ HTTP (localhost:8080)                    │ frames/state
            └──────────────┬───────────────────────────┘
┌───────────────────────── Python agent (agent/) ───────────────────────────────┐
│  main.py           LLM tool-use loop (Groq default, Claude optional): one     │
│                    call per step, image after every move/turn, history as     │
│                    one line per earlier step, prompt caching on Claude        │
│  prompts.py        system prompt + tool schemas (leakage-audited)             │
│  engine_client.py  typed wrapper over the 7 endpoints + /reset /record/*      │
└───────────────────────────────────────────────────────────────────────────────┘
```

## API

Spec endpoints (identical contracts to the problem statement):

| Endpoint | Behaviour |
|---|---|
| `GET /state` | `{state, x, y, heading, holding, retries}` — no object data |
| `GET /look` | 640×480 JPEG (quality 85) from the robot camera |
| `GET /depth` | `{depth}` single ray, metres, max 10, `null` if nothing in range |
| `POST /move {distance}` | 1 m/s, negative reverses; stops on collision, backs off 0.1 m, returns `{collided, moved, retries}` |
| `POST /turn {degrees}` | 90°/s, positive = right; returns `{turned, retries}` |
| `POST /pickup` | nearest object within 0.5 m and ±30°; `{success}` only |
| `POST /drop` | releases object; `{in_zone}` reports drop-zone landing |

Operational extras (agent-only, carry no object data): `GET /health`,
`POST /reset`, `POST /record/start {command, frames_dir?}`, `POST /record/stop`.

## Engine-boundary compliance (the 0-score rule)

No object names, ids, colours, shapes, positions, counts, distances-to-target,
segmentation masks or drop-zone coordinates ever leave the engine. Enforced by:

- `/state`, `/depth`, `/pickup` responses are fixed schemas over robot self-state.
- System prompt and tool descriptions contain only public problem-statement
  knowledge (the room has primitives and a drop zone) — no scene specifics.
- `Assets/Editor/EngineSmokeTest.cs` audits every text endpoint against a
  forbidden-word list (`obj_`, shape names, colour names, `zone_pos`, …) and
  fails the build if any leaks. Run it headless:
  `Unity.exe -batchmode -projectPath . -executeMethod RobotEngine.EditorTools.EngineSmokeTest.Run`
  → writes `smoke_test_result.txt` (46 checks green at time of writing).

## Cost discipline

One image per decision (sent as an image content block, never a text description),
earlier images replaced by a one-line note, 25-step soft budget (`--max-steps`,
hard cap 60). Token totals are printed per run and stored in `meta.json`. Use
`--dummy` while iterating on HTTP plumbing. On Claude the system prompt is sent
with `cache_control: ephemeral`; Groq caches prompts server-side.

## Judging map

| Criterion (weight) | Where it comes from |
|---|---|
| Fetch works, 40 % | live demo; directness encouraged by the prompt's turn-then-move guidance |
| Image-driven agent, 25 % | `logs/*/log.jsonl` + `frames/*.png` pair every decision with the exact pixels the model saw |
| User verdict, 20 % | `tools/make_clip.py` output: POV camera only, no HUD, command overlay 2 s, 10–20 s |
| Stretch, 15 % | furniture/walls: add cubes in `WorldBuilder.Build()`; path-planning prompt variant: see `prompts.py` |

## Repo layout

```
Assets/Scripts/          engine code (RuntimeInitializeOnLoad bootstrap — no scene setup)
Assets/Editor/           EngineSmokeTest (endpoint + leakage audit), EngineServe (headless)
agent/                   Claude agent: main loop, prompts, engine client
tools/make_clip.py       POV frames → Phase 2 MP4
requirements.txt         anthropic, requests, pillow, numpy, imageio(+ffmpeg)
```
