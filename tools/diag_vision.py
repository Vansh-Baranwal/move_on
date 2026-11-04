"""Diagnostic: does gemini-3.5-flash actually see the camera frames?"""
import base64
import io
import json
import os
import sys
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "agent"))

from prompts import SYSTEM_PROMPT, TOOLS  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
    line = line.strip()
    if line and not line.startswith("#") and "=" in line:
        key, _, value = line.partition("=")
        os.environ.setdefault(key.strip(), value.strip().strip('"'))

api_key = os.environ["GEMINI_API_KEY"]
frame = ROOT / "logs/run_20261104-222449/frames/step_0004.png"
img = Image.open(frame) if False else None
from PIL import Image  # noqa: E402

img = Image.open(frame)
buf = io.BytesIO()
img.convert("RGB").save(buf, "JPEG")
b64 = base64.b64encode(buf.getvalue()).decode()
print(f"testing with {frame.name} ({len(buf.getvalue()) // 1024} KB jpeg)")

tools = [
    {"type": "function", "function": {"name": t["name"], "description": t["description"], "parameters": t["input_schema"]}}
    for t in TOOLS
]


def call(tag, body_extra, with_tools):
    import time
    body = {
        "model": "gemini-3.5-flash",
        "max_tokens": 4096,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT if with_tools else
             "You are a vision assistant. Describe images precisely."},
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": body_extra},
                    {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + b64}},
                ],
            },
        ],
    }
    if with_tools:
        body["tools"] = tools
        body["tool_choice"] = "auto"
    req = urllib.request.Request(
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Authorization": "Bearer " + api_key, "Content-Type": "application/json"},
    )
    result = None
    for attempt in range(6):
        try:
            result = json.load(urllib.request.urlopen(req, timeout=120))
            break
        except Exception as error:
            print(f"  attempt {attempt + 1} failed: {error}; retrying in 5s")
            import time
            time.sleep(5)
    if result is None:
        print(f"--- {tag} --- FAILED after retries")
        return
    message = result["choices"][0]["message"]
    tc = message.get("tool_calls")
    text = (message.get("content") or "")[:400]
    print(f"--- {tag} ---")
    print(f"text: {text if text.strip() else '(empty)'}")
    if tc:
        for item in tc:
            print(f"tool_call: {item['function']['name']} {item['function']['arguments'][:120]}")


call("VISION, no tools: what do you see?", "Describe every object you can see in this image: colours, shapes, and where they are in the frame. Be specific.", False)
call("AGENT context, with tools: decide + explain", "state=Idle x=-2.20 y=-3.40 heading=20.0 holding=False retries=0 depth=2.88m\nThe command is: Bring the round red thing. Reply with one short sentence about what you see, then make your tool call.", True)
