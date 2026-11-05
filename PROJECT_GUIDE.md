# Move On Robot Project Guide

## 1. What this project does

Move On is a Unity robot-fetch simulation. A user asks for an object in plain language. The robot observes the room through its camera, searches for the requested object, navigates around obstacles, picks it up, and carries it to the marked drop zone.

The Unity project has a built-in C# brain that can run a command directly from the game window. There is also a Python agent that can control the same Unity engine through HTTP, optionally using Claude, plus a local pixel-based policy for offline runs. A separate voice script can transcribe speech and submit it as a command.

The Unity engine owns the room, robot physics, camera, simulation state, logs, and object interactions. Agent-facing API responses are designed to expose robot state and camera observations without returning object identities or world object coordinates.

## 2. Requirements and startup

- Unity Editor **6000.6.8f1** (see `ProjectSettings/ProjectVersion.txt`).
- Python 3 for the optional Python agent and voice sidecar.
- An Anthropic API key only when using the Claude agent. Keep it in `.env`; do not commit it.

### Start the Unity simulation

1. Open this folder in Unity Hub with the project’s Unity version.
2. Open any scene and press **Play**. `SceneBootstrap` creates the world, robot, cameras, engine, HTTP server, and HUD at runtime.
3. Wait for the Unity Console message that the engine API is listening at `http://localhost:8080/`.
4. Enter a command in the in-game command field and select **Run**. Examples: `Bring me the green cone` or `Find the blue cube`.

The world is generated from a deterministic seed at startup. The current demo room is 10 m square, with colored primitive objects and a cyan drop zone.

### Optional Python agent

Install the dependencies with `pip install -r requirements.txt`. For Claude, copy `.env.example` to `.env` and set `ANTHROPIC_API_KEY` there. Then, while Unity is running:

```bash
python agent/main.py --command "Bring me the green cone"
```

Use the dummy path to exercise the HTTP plumbing without a model call:

```bash
python agent/main.py --dummy
```

The internal Unity brain and the Python agent are separate ways to control the same engine; do not run both against the robot at the same time.

### Optional voice control

With Unity running and microphone/Whisper dependencies installed:

```bash
python tools/voice.py --mic
```

The voice script transcribes locally and posts the text to Unity’s `/voice` endpoint. The HUD’s **Auto-run voice** toggle controls whether received speech starts a run immediately or only fills the command field.

## 3. How a command runs

The built-in brain in `InternalBrain` follows these broad stages:

1. **Parse request:** recognize supported color and shape words, or a “biggest/tallest” request.
2. **Scan:** turn in place and inspect camera frames for matching colored blobs.
3. **Approach:** estimate the target’s world position from the camera ray and route toward a safe standoff point.
4. **Pick up:** confirm the tracked target is within reach and centered in front of the robot; pickup is restricted to that target.
5. **Return:** find or use the remembered position of the cyan drop zone, route there, and drop the object.
6. **Finish:** record the result and show it in the HUD/log.

If a requested object is not found after scanning, the built-in brain reports **“No object matching your request is present here.”** The classifier supports the scene’s red, yellow, green, blue, and cyan pixel bands. Shape words include round/sphere, cube/box, cone, and cylinder; the pixel classifier has limited shape categories, so shape selection depends on the visible silhouette.

## 4. Navigation and movement

`RobotController` owns the root transform and is the source of truth for movement. The robot translates along `transform.forward`; it cannot translate sideways. The autonomous brain turns toward a route waypoint, then issues a positive-distance forward move. A negative distance remains available through the low-level movement API for explicit reverse commands.

`GridPathfinder` runs A* over a 0.25 m grid covering the room. Each candidate cell is checked with the robot capsule against scene colliders. Diagonal steps are rejected when either adjoining cell is blocked. The brain follows short, collision-checked route segments and replans after movement or a collision. If no complete grid route is available, it falls back to local clear-lane steering.

The controller uses a capsule collider of approximately 0.5 m diameter and 0.7 m height, plus capsule casts for travel clearance. On collision, a move stops and backs off by 0.1 m; the brain then changes its steering choice and retries. The path grid assumes the current room bounds of approximately -4.5 m to +4.5 m on X and Z; update it if the room dimensions change.

The replacement `Assets/StreamingAssets/robot.glb` is resized by the loader to the previous model’s visual envelope (about **0.323 × 0.425 × 0.550 m**). Its local +Z front is aligned with the robot’s forward axis. The collision capsule remains independent of the visual mesh, so the mesh size does not enlarge the robot’s physics footprint.

## 5. Vision and visual memory

`AgentCamera` provides a 640 × 480 first-person image and a center-ray depth reading. `RobotVision` performs pixel-based color segmentation and groups adjacent pixels into blobs; it does not use object labels or hidden scene metadata to decide what is visible.

When a blob is observed, the engine can cast a ray through its image location to estimate a world point. The requested blob’s collider is tracked so the robot does not pick up a nearby obstacle instead. If an object is partly occluded, its visible blob and collider ray may not agree; the brain retains the last valid target position and attempts to navigate around the obstruction.

`VisualMemoryDatabase` persists sightings in:

```text
Application.persistentDataPath/move_on/visual_memory.json
```

Each sighting stores a detected color/shape category, an estimated X/Z position, observation count, and last-seen time. It keeps up to 256 entries and ignores sightings older than 30 days when selecting a cached destination. This is a visual-sighting cache, not a complete map or a record of every frame. Unity’s persistent-data location is platform-specific.

## 6. Main code areas

| Path | Responsibility |
|---|---|
| `Assets/Scripts/SceneBootstrap.cs` | Runtime setup; creates the simulation when Play starts. |
| `Assets/Scripts/World/WorldBuilder.cs` | Room, object layout, walls, and drop zone. |
| `Assets/Scripts/World/ConeMesh.cs` | Procedural cone mesh generation. |
| `Assets/Scripts/Robot/RobotController.cs` | Forward/reverse movement, turning, collision checks, pickup, and drop. |
| `Assets/Scripts/Views/AgentCamera.cs` | Agent camera image, depth, and pixel-to-world raycasts. |
| `Assets/Scripts/Sim/RobotVision.cs` | Pixel color segmentation and shape estimates. |
| `Assets/Scripts/Sim/InternalBrain.cs` | Built-in command parser, scan/approach/pickup/return behavior, and step logs. |
| `Assets/Scripts/Sim/GridPathfinder.cs` | A* grid route and waypoint selection. |
| `Assets/Scripts/Sim/VisualMemoryDatabase.cs` | Persistent sightings cache. |
| `Assets/Scripts/Sim/EngineCore.cs` | Engine state, command execution, logs, recording, and API coordination. |
| `Assets/Scripts/Core/GameServer.cs` | Local HTTP listener on port 8080; queues requests onto Unity’s main thread. |
| `Assets/Scripts/Sim/HudDisplay.cs` | Command input, run controls, status, logs, replay, and view controls. |
| `Assets/Scripts/Views/PovRecorder.cs` / `ViewRig.cs` | POV capture and display camera/minimap modes. |
| `agent/` | Optional Python Claude and local-policy clients. |
| `tools/voice.py` | Optional speech-to-text sidecar. |

## 7. HTTP interface

The local server listens at `http://localhost:8080`. The full request/response contract is documented in [`docs/API_CONTRACT.md`](docs/API_CONTRACT.md). Common endpoints are:

| Endpoint | Purpose |
|---|---|
| `GET /health` | Check whether the Unity server is responding. |
| `GET /state` | Read robot pose, heading, state, holding flag, and retry count. |
| `GET /look` | Retrieve a JPEG from the robot camera. |
| `GET /depth` | Read the camera center-ray distance. |
| `POST /move` / `POST /turn` | Move or rotate the robot. |
| `POST /pickup` / `POST /drop` | Pick up the reachable object or release the held object. |
| `POST /run` / `GET /run/status` / `POST /run/stop` | Start and manage the built-in Unity brain. |
| `POST /voice` | Submit recognized speech text to the HUD/brain. |
| `POST /reset` | Reset robot and engine run state. |
| `GET /logs`, `POST /log`, `POST /replay` | Inspect or replay recorded actions. |
| `POST /record/start`, `POST /record/stop` | Control POV capture. |

The HTTP listener runs on the local machine and passes simulation work to Unity’s main thread. Camera/state reads do not move the robot; movement and turn operations advance the simulation while their coroutines execute.

## 8. User interface and recordings

The HUD provides first-person/third-person camera modes, a minimap with the robot trail, a collapsible action log, replay controls, and a command box. Keyboard shortcuts are **V** for view, **M** for minimap, **L** for log visibility, and **R** for replay.

Built-in Unity runs are saved under `runs/platform_<timestamp>/`; the Python agent writes its logs under `logs/run_<timestamp>/` by default. These contain step JSONL, frame images, metadata, and optional POV video frames. `tools/make_clip.py` can assemble saved POV frames into a demo clip. Generated run folders and logs are ignored by Git (see `.gitignore`).

## 9. Troubleshooting and maintenance

- **Server does not start:** check the Unity Console for port 8080 errors and confirm another Unity instance is not already holding the port.
- **Python cannot connect:** start Unity in Play Mode first and check `http://localhost:8080/health`.
- **Claude command fails:** check that `.env` contains a valid `ANTHROPIC_API_KEY`; use `--dummy` to separate API connectivity from model setup.
- **Robot appears to face the wrong way:** confirm the GLB’s front is local +Z and review `GlbYawCorrection` in `RobotController`.
- **Robot clips obstacles:** keep the visual mesh within the previous envelope and keep the capsule/cast settings in `RobotController` aligned with the robot’s real footprint. If changing room size, update `GridPathfinder` bounds.
- **Memory appears stale:** clear `visual_memory.json` under Unity’s persistent data path or call the project’s memory clear routine from code.
- **Review generated changes:** `git diff --check` checks whitespace; the Unity editor should be used to confirm compilation and Play Mode behavior after code or asset changes.

## 10. Further reading

- [`README.md`](README.md): original setup and hackathon overview.
- [`docs/PROJECT_BRIEF.md`](docs/PROJECT_BRIEF.md): project requirements and engine-boundary constraints.
- [`docs/API_CONTRACT.md`](docs/API_CONTRACT.md): detailed API schemas and state behavior.