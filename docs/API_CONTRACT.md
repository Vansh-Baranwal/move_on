# API CONTRACT: Robot Engine

Base URL: `http://localhost:8080`
Format: JSON request and response bodies (`Content-Type: application/json`), except `/look` which returns a JPEG.
Units: metres and degrees. **Heading 0 = +x axis, positive = clockwise from above.** Turn degrees: **positive = right (clockwise)**.

> **Status: DRAFT v0.1. Must be agreed with the agent teammate, then frozen.**
> Changes after freezing need both sides to agree and a version bump at the bottom of this file.
> Items marked **[ASSUMPTION]** are ours, not from the official brief. **[CONFIRM]** needs an answer.

---

## 0. Ground rules

1. **No object data in any response.** No names, IDs, colours, shapes, positions, distances to objects, counts, masks or drop-zone coordinates. This covers success bodies, error messages and headers.
2. The **agent tool endpoints** (section 1) are the only ones Claude's tools may map to.
3. The **harness endpoints** (section 2) are for the agent script, test harness and operator only. They must **never** be exposed as Claude tools, and their responses must never be placed in Claude's context.
4. The simulation clock advances **only** during `/move` and `/turn`.
5. All endpoints return within a few seconds. `/move` and `/turn` return after the motion has finished.
6. One run at a time. Requests are processed one at a time, in order.

---

## 1. Agent tool endpoints

### GET `/state`

Robot status. **No object information.**

Response `200`:
```json
{
  "state": "Locate",
  "x": 0.0,
  "y": 0.0,
  "heading": 0.0,
  "holding": false,
  "retry_count": 0,
  "step": 0
}
```

| Field | Type | Notes |
|---|---|---|
| `state` | string | One of: `Idle`, `Locate`, `GoToObject`, `PickUp`, `Carry`, `Drop`, `Done`, `Blocked` |
| `x`, `y` | number | Robot position in metres, origin at the room centre |
| `heading` | number | Degrees, normalised to `[0, 360)` |
| `holding` | boolean | Whether an object is attached. Says nothing about which one. |
| `retry_count` | integer | Number of times `Blocked` has been entered in this run (0 to 3) |
| `step` | integer | Number of actions executed this run (`/move`, `/turn`, `/pickup`, `/drop`) |

> Reading `/state` does not change state or advance time.

---

### GET `/look`

Returns the robot camera's current view as a **JPEG image** (`Content-Type: image/jpeg`).

- 640 x 480, 90° horizontal FOV, camera 0.5 m above the floor, tilted 10° down.
- Always the **agent camera**, whatever view is on screen.
- Does not advance time and does not change state.
- The agent must send it to Claude as an **image content block**. Take one `/look` after each `/move` or `/turn`.

Response headers: `Content-Type: image/jpeg`, `Content-Length`. No custom headers carrying scene data.

Errors: `503 engine_busy` if a frame cannot be captured in time.

---

### GET `/depth` (optional)

Single-ray distance straight ahead from the camera centre.

Response `200`:
```json
{ "distance": 2.37 }
```

- `distance`: float metres, **maximum 10**. `null` if nothing is within range.
- Does not say what the thing is. Does not advance time.

---

### POST `/move`

Moves the robot forward or backward along its heading.

Request:
```json
{ "distance": 1.5 }
```

| Field | Type | Notes |
|---|---|---|
| `distance` | number | Metres. Positive = forward, negative = reverse. Speed 1 m/s. Allowed range `[-5, 5]` [ASSUMPTION]. |

Response `200`:
```json
{
  "collided": false,
  "distance_moved": 1.5,
  "state": "GoToObject",
  "x": 1.5,
  "y": 0.0,
  "heading": 0.0
}
```

| Field | Type | Notes |
|---|---|---|
| `collided` | boolean | `true` if the robot hit anything (wall or object) and stopped early |
| `distance_moved` | number | Actual signed distance travelled, in metres (after any back-off, see below) |
| `state` | string | State after the call |
| `x`, `y`, `heading` | number | Pose after the call |

**On collision:** the move stops immediately, the robot **backs off 0.1 m**, `collided` is `true`, and the state becomes `Blocked` (`retry_count` increases by 1).

The response never says what was hit.

---

### POST `/turn`

Rotates the robot in place.

Request:
```json
{ "degrees": -35 }
```

| Field | Type | Notes |
|---|---|---|
| `degrees` | number | **Positive = right (clockwise)**, negative = left. Speed 90°/s. Allowed range `[-360, 360]` [ASSUMPTION]. |

Response `200`:
```json
{
  "heading": 325.0,
  "state": "GoToObject",
  "collided": false
}
```

Turning in place does not normally collide. `collided` is included for shape consistency and is `false` unless the robot is wedged.

---

### POST `/pickup`

Attaches the **nearest object within reach** to the robot. Reach is **0.5 m** and within **±30° of the robot's heading**. Snap on contact, no grasp physics.

Request: empty body or `{}`.

Response `200`:
```json
{ "success": true, "state": "Carry" }
```
or
```json
{ "success": false, "state": "GoToObject" }
```

- Returns **success or failure only**. It never identifies the object, and never says why a failure happened (distance, angle or nothing there).
- A failed pickup leaves the state unchanged and still counts as a step.

---

### POST `/drop`

Releases the held object and reports whether it landed in the drop zone.

Request: empty body or `{}`.

Response `200`:
```json
{ "in_zone": true, "state": "Done" }
```

- `in_zone`: boolean. No coordinates, no zone information beyond this.
- Calling `/drop` while `holding` is `false` returns `409 not_holding`.
- The run outcome is **not** returned here. It is available only through the harness endpoint `GET /run/result`.

---

## 2. Harness endpoints (never Claude tools)

These are for the Python script, the test harness and the operator.

### POST `/command`

Starts a run with the plain-language command. Used for the on-screen command overlay and for logging.

Request:
```json
{ "command": "Bring the red cube to the green square." }
```

Response `200`:
```json
{ "state": "Locate", "run_id": "run_0001" }
```

- Moves state `Idle → Locate`. Starts the run clock, the step counter and the recording trigger.
- Returns `409 run_active` if a run is already in progress.
- The engine **does not parse the command**. It stores it only for display and logging (see "Target for grading" below).

---

### POST `/reset`

Resets to `Idle` and builds a new scene.

Request:
```json
{ "seed": 42 }
```

| Field | Type | Notes |
|---|---|---|
| `seed` | integer, optional | Same seed gives the same scene. If omitted, a new random seed is chosen. |
| `target` | object, optional | **Grader use only.** Which object is the correct answer, for testing, e.g. `{"shape":"cube","color":"red"}`. See below. |

Response `200`:
```json
{ "state": "Idle", "seed": 42 }
```

> The response contains **no scene data**. The harness may log the seed, never object details, near anything Claude can read.

---

### GET `/run/result`

Run outcome for the grader and logs. **Never give this to Claude.**

Response `200` (while running):
```json
{ "finished": false }
```
Response `200` (finished):
```json
{
  "finished": true,
  "outcome": "pass",
  "steps": 23,
  "retries": 1,
  "path_length": 7.4,
  "duration_sim_seconds": 18.6
}
```

| `outcome` | Meaning |
|---|---|
| `pass` | The correct object ended in the drop zone |
| `fail_wrong_object` | An object ended in the zone, but not the requested one |
| `fail_not_reached` | The run gave up, hit the step limit, or the object never reached the zone |
| `fail_collision` | 3 retries used up after collisions or no-movement blocks |

---

### POST `/log`

The agent pushes one log entry per step. UI-only: it feeds the live log panel and the replay record.

Request:
```json
{
  "step": 7,
  "timestamp": "2026-11-05T10:15:42.123Z",
  "tool": "move",
  "args": { "distance": 1.2 },
  "result": { "collided": false, "distance_moved": 1.2 },
  "reasoning": "Target is centred in view and about 2 m away. Moving closer.",
  "frame_path": "frames/step_0007.png"
}
```

| Field | Type | Notes |
|---|---|---|
| `step` | integer | Matches the engine's `step` |
| `timestamp` | string | ISO 8601 |
| `tool` | string | `move`, `turn`, `pickup`, `drop` |
| `args` | object | Tool arguments as sent |
| `result` | object | Tool result as returned |
| `reasoning` | string, optional | Short text from Claude. This comes from Claude's reading of the image, not from the engine. |
| `frame_path` | string, optional | Agent-side path. Use neutral names (`step_0007.png`). |

Response `200`: `{ "ok": true }`

- The engine adds the current `state` and `retry_count` to each panel line itself.
- This endpoint is **write-only**. Nothing logged is ever sent back out through other endpoints.
- The engine also keeps its own copy of each `/look` frame by step number, so clicking a log line can show it.

---

## 3. State machine behaviour

The engine owns the state machine. The agent only calls endpoints.

| From | Trigger | To |
|---|---|---|
| `Idle` | `POST /command` | `Locate` |
| `Locate` | first `/move` or `/turn` | `GoToObject` |
| `GoToObject` | `/pickup` called | `PickUp` (momentary) |
| `PickUp` | pickup succeeds | `Carry` |
| `PickUp` | pickup fails | `GoToObject` |
| `Carry` | `/drop` called | `Drop` (momentary) |
| `Drop` | released in the zone | `Done` (outcome recorded) |
| `Drop` | released outside the zone | back to `GoToObject`, `holding=false` [ASSUMPTION, CONFIRM] |
| any moving state | collision, or no movement for 2 s | `Blocked`, `retry_count += 1` |
| `Blocked` | next successful `/move` or `/turn` | the state it came from |
| `Blocked` | `retry_count` reaches 3 and another block occurs | `Done`, outcome `fail_collision` |
| any | step 60 reached | `Done`, outcome `fail_not_reached` |
| any | agent calls `/give-up` (see below) | `Done`, outcome `fail_not_reached` |

**No-movement rule:** a `/move` that results in less than 0.02 m of travel, repeated for the equivalent of 2 seconds of simulated time, counts as a block. [ASSUMPTION]

**Step limit:** soft target 25, hard stop 60. At 60 the engine ends the run itself.

### POST `/give-up` (optional)

Lets the agent end a run voluntarily. Returns `{ "state": "Done" }`. Include only if the team agrees; otherwise the run ends via the step limit.

---

## 4. Target for grading

The engine needs to know the correct object to set `pass` or `fail_wrong_object`, but it does not parse free text. Options, in order of preference [CONFIRM with mentor]:

1. **Test runs:** the harness passes `target` to `POST /reset` (seeded scenes with a known answer).
2. **Live judge commands:** an engine-side **referee panel**, visible only to the operator and hidden in recording mode, where the operator picks the correct object before the run, or marks pass/fail afterwards.
3. If the organisers provide a grading hook, use that instead.

None of this ever reaches the agent.

---

## 5. Errors

All errors use one shape:

```json
{
  "error": {
    "code": "wrong_state",
    "message": "Cannot pick up while no run is active."
  }
}
```

| HTTP | `code` | When |
|---|---|---|
| 400 | `bad_request` | Missing or malformed JSON, or a value out of range |
| 404 | `not_found` | Unknown endpoint |
| 409 | `no_active_run` | A tool endpoint was called while `Idle` |
| 409 | `run_finished` | A tool endpoint was called after `Done` |
| 409 | `run_active` | `/command` called during a run |
| 409 | `not_holding` | `/drop` called with nothing held |
| 503 | `engine_busy` | Main-thread request timed out |

**Error messages must never contain scene information.** Use generic text only.

---

## 6. Typical call sequence

```
POST /reset      {"seed": 42}                              (harness)
POST /command    {"command": "Bring the round red thing"}  (harness)
GET  /look                                                 -> image to Claude
  Claude: turn 35
POST /turn       {"degrees": 35}
GET  /look                                                 -> image to Claude
  Claude: move 2.0
POST /move       {"distance": 2.0}
GET  /look
  Claude: pickup
POST /pickup                                               -> {"success": true}
GET  /look
  ...navigate to zone...
POST /drop                                                 -> {"in_zone": true}
GET  /run/result                                           (harness only)
```

Quick checks with curl:
```bash
curl http://localhost:8080/state
curl http://localhost:8080/look --output test.jpg
curl -X POST http://localhost:8080/turn -H "Content-Type: application/json" -d '{"degrees": 45}'
curl -X POST http://localhost:8080/move -H "Content-Type: application/json" -d '{"distance": 1.0}'
```

---

## 7. Checklist for the agreement with the agent teammate

- [ ] Do you need `distance_moved` and the pose in `/move` and `/turn` responses, or only `collided`?
- [ ] Is `GET /depth` wanted in the first version?
- [ ] Is `/give-up` wanted?
- [ ] Who calls `/reset` and `/command`: the Python script, or the operator through the UI?
- [ ] Who writes the JSONL log: the agent only, with the engine receiving copies through `/log`? (Recommended.)
- [ ] Does the agent keep frames and name them neutrally?
- [ ] Is a `go_to` helper planned on the agent side, built from `/move` and `/turn`?
- [ ] Agreed timeout per request: ______ seconds.

---

## Changelog

| Version | Date | Change |
|---|---|---|
| 0.1 | 2026-11-04 | Initial draft |
