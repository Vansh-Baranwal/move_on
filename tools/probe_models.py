"""Diagnostic: probe candidate Gemini models with the agent's exact request shape."""
import base64
import io
import json
import os
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "agent"))

from prompts import SYSTEM_PROMPT, TOOLS  # noqa: E402
from PIL import Image  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
    line = line.strip()
    if line and not line.startswith("#") and "=" in line:
        key, _, value = line.partition("=")
        os.environ.setdefault(key.strip(), value.strip().strip('"'))

api_key = os.environ["GEMINI_API_KEY"]
img = Image.open(ROOT / "logs/run_20261104-213905/frames/step_0001.png")
buf = io.BytesIO()
img.save(buf, "JPEG")
b64 = base64.b64encode(buf.getvalue()).decode()

tools = [
    {"type": "function", "function": {"name": t["name"], "description": t["description"], "parameters": t["input_schema"]}}
    for t in TOOLS
]

CANDIDATES = [
    "gemini-4-argon",
    "gemini-3.8-flash",
    "gemini-3.7-flash",
    "gemini-3.6-flash",
    "gemini-3.5-flash",
    "gemini-flash-latest",
    "gemini-3-flash-preview",
]


def probe(model):
    body = {
        "model": model,
        "max_tokens": 4096,
        "tool_choice": "auto",
        "tools": tools,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": "state=Idle x=-3.40 y=-3.40 heading=0.0 holding=False retries=0 depth=2.88m"},
                    {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + b64}},
                ],
            },
        ],
    }
    t0 = time.time()
    req = urllib.request.Request(
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Authorization": "Bearer " + api_key, "Content-Type": "application/json"},
    )
    try:
        result = json.load(urllib.request.urlopen(req, timeout=90))
        elapsed = time.time() - t0
        choice = result["choices"][0]
        tc = choice["message"].get("tool_calls")
        tool = tc[0]["function"]["name"] if tc else None
        args = (tc[0]["function"]["arguments"][:90] if tc else None)
        print(f"OK    {model:28s} {elapsed:5.1f}s  finish={choice['finish_reason']:8s} tool={tool} args={args}")
        return True
    except Exception as error:
        print(f"FAIL  {model:28s} {time.time() - t0:5.1f}s  {error}")
        return False


for model in CANDIDATES:
    probe(model)
