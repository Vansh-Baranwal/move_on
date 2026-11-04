# PROJECT BRIEF: Robot Engine (Unity)

> **Read this file first, every session.** It is the source of truth for scope, rules and conventions.
> If anything here conflicts with `API_CONTRACT.md`, the contract wins for API details.
> If anything here conflicts with the official hackathon brief, the official brief wins. Flag the conflict, do not silently resolve it.

Items marked **[ASSUMPTION]** are not in the official brief and were chosen by us. Items marked **[CONFIRM]** need a mentor or teammate answer.

---

## 1. What we are building

A **Unity 3D engine** for the Membrane Robot Hackathon. A separate Python agent (owned by a teammate) calls our local HTTP API, uses Claude with the robot's camera image, and fetches an object to a drop zone.

**Our role (the engine process) owns:**
- The scene: room, objects, drop zone
- The robot, physics, collisions and the state machine
- Camera rendering (the agent camera and the display camera)
- The local HTTP server at `http://localhost:8080`
- The human-facing UI: view switch, live log panel, mini-map, replay, recording mode

**We do not own:** the Python agent, the Claude prompts or the Claude tool definitions.

---

## 2. Hard rules (violating any of these can zero 65% of the score)

### 2.1 No object data may ever leave the engine toward the agent

The agent and Claude must learn about objects **only from `/look` pixels**, plus optional `/depth` and the collision flag.

Never send, expose or log to anything the agent can read:
- Object names, IDs, colours, shapes, sizes, positions, counts
- Distances to objects, or to a named target
- Segmentation masks
- Drop-zone coordinates

This applies to **every** response (`/state`, `/pickup`, `/drop`, errors), headers, filenames, and any text the agent may read. Do not name saved frames after what they contain (use `step_0007.png`, never `red_cube.png`).

`/pickup` returns success or failure only. `/drop` returns only whether the object landed in the zone.

### 2.2 The view toggle never changes what the agent sees

`/look` always renders the **agent camera** (first person), regardless of which view is shown on screen.

### 2.3 Display-only features stay display-only

The log panel, mini-map, replay, picture-in-picture and any debug overlay are UI. None of their data may be reachable through the API except through the endpoints listed in `API_CONTRACT.md`.

### 2.4 Clips must be clean

Phase 2 clips score **0** if any team name, logo or code is visible, or if the clip exceeds 20 seconds. Therefore:
- No team names in project, scene, object or window titles, or in any UI text.
- Recording mode hides every UI element except the command text (first 2 seconds).

---

## 3. Technical specification

### 3.1 Platform

| Item | Value |
|---|---|
| Unity version | **6000.6.8f1** (Unity 6.6). Do not use APIs from older versions without checking. |
| Template | 3D [CONFIRM Built-in vs URP, and keep it consistent] |
| Language | C# |
| Input handling | Both (Input System and legacy) [update if changed] |
| Recording | Unity Recorder package (MP4/H.264, 720p or higher) |
| HTTP server | `System.Net.HttpListener` on a background thread |

### 3.2 Coordinates, units and angles

- Distances in **metres**, angles in **degrees**.
- API coordinates are on the floor plane: `x`, `y`. Origin at the **room centre**. [ASSUMPTION]
- **Heading 0 = +x axis. Positive = clockwise when viewed from above.**
- Suggested mapping to Unity (verify with a test): API `x` = Unity `x`, API `y` = Unity `z`. The forward vector for heading `h` is `(cos h, -sin h)` in API `(x, y)`, and Unity yaw = `h + 90°`.
- Write one helper class for this conversion and use it everywhere. Never convert inline.

### 3.3 Agent camera (`/look`)

| Item | Value |
|---|---|
| Resolution | 640 x 480, JPEG |
| Horizontal FOV | 90° (Unity uses vertical FOV: set **about 73.74°** for a 4:3 aspect) |
| Mount | At the front of the robot, **0.5 m above the floor** |
| Tilt | **10° downward** |
| Rendering | Offscreen `RenderTexture`, independent of the on-screen view |

The FPP display view must match this camera exactly (same position, tilt and FOV).

### 3.4 Scene

- One flat room with four walls and no furniture. Suggested size 10 m x 10 m. [ASSUMPTION]
- **4 to 6 primitive objects** on the floor (cube, sphere, cylinder, cone) in different colours.
  - At least two objects share a colour.
  - At least two objects share a shape.
- Use **high-contrast, clearly distinguishable colours**, since this helps Claude's accuracy.
- One marked **drop zone** (a coloured flat square with a border). Its colour must not match any object colour.
- Good lighting with soft shadows. No object may be hidden by lighting or sit inside a wall.
- Layouts are **seed-based**. The same seed must always produce the same scene.
- Objects must not overlap each other, the walls, the drop zone or the robot's start position.

### 3.5 Robot

- Wheeled robot, with a capsule plus a camera being the minimum. Visible wheels that rotate when moving. [Styling is optional]
- Kinematic motion (no full rigid-body physics). Suggested collider: capsule radius about 0.25 m. [ASSUMPTION]
- Speed: **1 m/s** moving, **90°/s** turning.
- **Pickup:** snaps the nearest object to the robot if it is within **0.5 m** and within **±30° of the robot's heading**. No grasp physics. The held object must be visible in the FPP view while carried.
- **Collision:** stops the move, backs off **0.1 m**, returns `collided=true`, and enters `Blocked`.

### 3.6 State machine

`Idle → Locate → GoToObject → PickUp → Carry → Drop → Done`, plus `Blocked`.

| State | Meaning |
|---|---|
| Idle | Waiting for a command |
| Locate | Command received; Claude is identifying the target |
| GoToObject | Robot is moving toward the object until within pickup reach |
| PickUp | Pickup is attempted |
| Carry | Holding an object and moving toward the zone |
| Drop | Releasing the object |
| Done | Run finished; outcome recorded |
| Blocked | Collision, or no movement for 2 seconds. Back off, turn, retry. **Fail after 3 retries.** |

Run outcomes: `pass`, `fail_wrong_object`, `fail_not_reached`, `fail_collision`.

Exact transition rules are in `API_CONTRACT.md`, section "State machine behaviour".

### 3.7 Time control

- The simulation clock is **frozen while Claude thinks**. The scene advances **only during `/move` and `/turn`**.
- Rendering stays live while frozen. The window must not freeze or stutter.
- The display should run at **30 FPS or more**.
- Use a fixed-timestep loop for motion, so the same inputs always give the same result.

### 3.7.1 Threading (the most common source of crashes)

`HttpListener` runs on a background thread. **Unity objects must only be touched on the main thread.**
- Request handlers must only enqueue work and wait for a result.
- The main thread (`Update`) dequeues and executes work, then completes the response.
- Never call `Camera.Render`, `Transform`, `GameObject` or `Texture2D` APIs from the listener thread.
- Include a timeout on every queued request and return a clear error if it expires.

---

## 4. Human-facing features (display only)

### 4.1 Views

- **FPP:** exactly the agent camera view.
- **TPP:** a smooth chase camera behind and above the robot.
- **Picture-in-picture:** when in TPP, a small live view of the agent camera sits in the top corner, so everyone sees what Claude sees.
- **Switch:** an on-screen button plus a keyboard shortcut, with a smooth blend.
- Switching must never affect `/look`.

### 4.2 Live log panel

- Fed by the agent through `POST /log` (see the contract). It is UI-only.
- Per step it shows: step number, state, tool and args, result, collision flag, short reasoning, retry count and final outcome.
- Collapsible, auto-scrolling and colour-coded: collisions red, success green, Blocked amber.
- Clicking a line shows that step's saved `/look` frame.

### 4.3 Recording mode

- Forces FPP.
- Hides the log panel, buttons, picture-in-picture, mini-map and all other UI.
- Shows only the typed command as text for the first 2 seconds.
- Starts recording when state leaves `Idle`, stops when it reaches `Done`.
- Outputs MP4, 720p or higher, no audio.
- Warns clearly if a clip would exceed 20 seconds (target 12 to 18 seconds).

### 4.4 Mini-map

- An orthographic top-down camera showing the room, the drop zone and the robot's **path trail**.
- UI-only, hidden in recording mode.

### 4.5 Replay mode

- A finished run can be scrubbed, with the log panel and frames synced to each step.
- The engine saves a per-step record: step, pose, state, tool, args, result and the frame.

### 4.6 Not in scope

Reach/cone debug overlay, obstacle spawner (furniture and walls), floor path trail in the 3D view. Do not add these unless the team decides to.

---

## 5. Repository structure

```
/
  docs/
    PROJECT_BRIEF.md
    API_CONTRACT.md
  Assets/
    Scenes/           Main scene only unless told otherwise
    Scripts/
      Api/            HttpListener server, routing, main-thread queue, JSON DTOs
      Sim/            Time stepping, collisions, state machine, run outcome
      Robot/          Robot controller, pickup/drop, wheels
      World/          Room, objects, drop zone, seeded scene generator
      Views/          Agent camera, display camera, TPP, picture-in-picture
      UI/             View toggle, log panel, mini-map, command overlay
      Recording/      Recording mode and Recorder control
      Replay/         Step recording and playback
      Testing/        Seeded runs, fast mode, grader helpers
    Materials/
  .gitignore          Standard Unity ignore
```

Naming: `PascalCase` for classes and files, one main class per file, namespace `RobotEngine.<Folder>`.

---

## 6. Build order

Work in this order. Finish and commit each step before starting the next.

1. Scene, robot and FPP camera
2. HTTP API and time-stepped movement with collisions
3. Pickup, drop and the state machine
4. TPP, view toggle and picture-in-picture
5. Log panel and `POST /log`
6. Recording mode
7. Mini-map and replay
8. Seeded scenes and test mode

**Phase 1 target:** a typed command reaches `Done` with `pass` on at least 3 different commands, with no human input during the run, by hour 14.

---

## 7. Rules for AI coding tools

1. **Read this file and `API_CONTRACT.md` before writing anything.**
2. Work on **one feature at a time**. Do not build ahead of the build order.
3. **Do not add features, endpoints or fields** that are not in these two documents. Propose them instead.
4. Touch only the files the task names. If other files must change, say which and why.
5. State what the human must do in the Unity Editor (components to attach, references to assign, objects to create). Do not assume it has been done.
6. All Unity API calls on the main thread only (section 3.7.1).
7. Keep one coordinate-conversion helper (section 3.2). Do not duplicate the maths.
8. Never include team names, logos or identifying text in any scene object, UI text, file name or window title.
9. After writing code, list **leakage risks** you checked (section 2.1).
10. When fixing a bug, change the minimum needed. Do not refactor unrelated code.
11. Do not edit a file that another tool is currently working on.
12. Add a short comment on any non-obvious maths (FOV conversion, heading to yaw).

### Human responsibilities

- All Unity Editor work: creating objects, assigning references, materials, lighting, UI layout
- Pressing Play, testing, and reporting the **exact** console error text or a screenshot
- Testing the API with the real agent, not just curl
- Committing to Git after each working step
- The final leakage audit, the three clips, mentor sign-off and the demo booking

---

## 8. Definition of done (Phase 1)

- [ ] All endpoints in `API_CONTRACT.md` work and match the documented JSON.
- [ ] `/look` returns a 640 x 480 JPEG from the agent camera in every view mode.
- [ ] Time is frozen between `/move` and `/turn` calls.
- [ ] Collisions stop the robot, back off 0.1 m, set `collided=true` and enter `Blocked`.
- [ ] Pickup (0.5 m, ±30°) and drop work, and the outcomes are correct.
- [ ] The state machine matches section 3.6, including 3 retries then fail.
- [ ] Leakage audit passed on every endpoint, header, filename and log.
- [ ] 3 different commands reach `Done` with `pass`, with no human input.
- [ ] Stable at 30 FPS or more with both cameras active.

---

## 9. Open questions

| # | Question | Owner |
|---|---|---|
| 1 | Exact room size, robot radius and object sizes (our values are assumptions) | Mentor |
| 2 | Is a higher-level `go_to` tool expected, given the stretch goal wording? (The endpoint list has none.) | Mentor |
| 3 | How does the engine know the correct target for a free-text judge command, so it can set `pass` or `fail_wrong_object`? (Our default: an operator selects it in an engine-side referee panel; see the contract.) | Mentor + teammate |
| 4 | What happens on `/drop` outside the zone: end the run or continue? (Our default: continue.) | Mentor |
| 5 | Do the official reference scene description and sample commands change any scene rules? | Team |
