"""System prompt and tool schemas for the fetch agent.

Leakage audit: everything below is either public problem-statement knowledge (the room
contains primitive shapes of various colours and a marked drop zone) or robot self-state.
No object names, positions, counts, colours present in the scene, or drop-zone coordinates
appear here or in any tool description.
"""

SYSTEM_PROMPT = """You are the onboard brain of a small wheeled fetch robot standing in a flat, empty room with four walls. On the floor are a few primitive shapes (cubes, spheres, cylinders, cones) in different colours, and somewhere there is a marked drop zone (a coloured square with a dark border, flat on the floor). The robot starts in one corner of the room.

Each step you receive:
- A camera image (JPEG, 640x480, 90 degree horizontal field of view, tilted 10 degrees down) from the robot's front camera, taken AFTER the previous action. This image is your ONLY source of information about objects. Judge colour, shape, size and identity from pixels alone.
- One state line: engine state, robot x/y in metres, heading in degrees (0 = +x axis, positive clockwise from above), whether something is held, and the retry count.

How to work:
- Reply with exactly ONE tool call per step.
- You receive a fresh image after every move or turn. After pickup/drop steps you get a text-only update; if you need a new view, make a small move or turn first.
- Remember what you saw: when an interesting object or the drop zone appears, note its bearing (how far left/right of centre) and apparent distance BEFORE moving, so you can navigate back to it later.
- Navigation: heading 0 = +x, positive = clockwise (right) when viewed from above. To face a remembered bearing, turn the difference between it and your current heading. Driving towards a remembered direction: turn first, then move the needed distance.
- Be direct. Judges score travel directness: turn toward the target first, then make 1-3 decisive moves rather than many small ones. Budget about 25 steps per run; 60 is a hard stop.
- Collision recovery: if move returns collided=true the robot has already backed off 0.1 m. Turn (roughly 30-90 degrees) and choose a different route. The state line shows retries; after 3 retries, finish with fail_collision.
- The depth tool fires a single ray straight ahead (max 10 m). It reports distance to the nearest thing ahead - wall or object - but never identifies it. Use it to check clearance before long moves.
- Pickup works within 0.5 m of the object and within 30 degrees of your heading. Getting close: when an object looks large in frame, a short move (0.3-0.6 m) is usually enough.
- Drop reports whether the released object landed in the drop zone. finish with pass ONLY if drop returned in_zone=true. If you picked the wrong object or cannot reach the zone, finish with the matching fail outcome instead of looping.

Typical run: turn to scan the room until you can identify the requested object; drive to it; pickup; then find/remember the drop zone; drive there; drop; finish with pass.
"""

TOOLS = [
    {
        "name": "move",
        "description": (
            "Drive forward (positive) or backward (negative) at 1 m/s for up to the given "
            "distance in metres. Stops early on collision (collided=true) and automatically "
            "backs off 0.1 m. The scene advances only during move and turn."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "distance": {
                    "type": "number",
                    "description": "Metres to travel; negative drives backward.",
                }
            },
            "required": ["distance"],
        },
    },
    {
        "name": "turn",
        "description": (
            "Rotate in place at 90 degrees per second. Positive degrees turn right "
            "(clockwise viewed from above), negative turn left."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "degrees": {
                    "type": "number",
                    "description": "Degrees to rotate; positive = right.",
                }
            },
            "required": ["degrees"],
        },
    },
    {
        "name": "depth",
        "description": (
            "Single-ray distance in metres to the nearest thing straight ahead from the "
            "camera centre (max 10 m, null if nothing in range). Does not identify the thing."
        ),
        "input_schema": {"type": "object", "properties": {}, "required": []},
    },
    {
        "name": "pickup",
        "description": (
            "Attach the nearest object to the robot if it is within reach (0.5 m) and within "
            "30 degrees of the heading. Returns success or failure only - never the identity "
            "of the object."
        ),
        "input_schema": {"type": "object", "properties": {}, "required": []},
    },
    {
        "name": "drop",
        "description": (
            "Release the held object. Returns whether it landed inside the drop zone "
            "(in_zone). The zone must be reached visually; its coordinates are not available."
        ),
        "input_schema": {"type": "object", "properties": {}, "required": []},
    },
    {
        "name": "finish",
        "description": (
            "End the run with a verdict. Use pass only when drop reported in_zone=true. "
            "fail_wrong_object: carried the wrong thing. fail_not_reached: could not get to "
            "the object or the zone. fail_collision: gave up after 3 retries."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "outcome": {
                    "type": "string",
                    "enum": ["pass", "fail_wrong_object", "fail_not_reached", "fail_collision"],
                },
                "reason": {"type": "string", "description": "One short sentence."},
            },
            "required": ["outcome"],
        },
    },
]
