#!/usr/bin/env python3
"""Assemble recorded POV frames into the Phase 2 submission clip.

Spec contract: MP4, 720p, 10-20 s, typed command shown as text for the first 2 s,
no team names, logos or code visible.

Usage:
    python tools/make_clip.py logs/run_20261104-183000
    python tools/make_clip.py logs/run_20261104-183000 --out submission_clip.mp4
"""

from __future__ import annotations

import argparse
import json
import textwrap
from pathlib import Path

import imageio.v2 as imageio
import numpy as np
from PIL import Image, ImageDraw, ImageFont

FPS = 20
MIN_SECONDS = 10.0
MAX_SECONDS = 20.0
OVERLAY_SECONDS = 2.0


def load_font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    for name in ("segoeuib.ttf", "arialbd.ttf", "DejaVuSans-Bold.ttf", "Arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def draw_overlay(frame: Image.Image, command: str, font) -> Image.Image:
    overlay = Image.new("RGBA", frame.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    wrapped = textwrap.fill(command, width=58)
    box = draw.multiline_textbbox((24, 14), wrapped, font=font)
    draw.rectangle([0, 0, frame.size[0], box[3] + 28], fill=(0, 0, 0, 190))
    draw.multiline_text((24, 14), wrapped, font=font, fill=(255, 255, 255, 255))
    return Image.alpha_composite(frame.convert("RGBA"), overlay).convert("RGB")


def main() -> int:
    parser = argparse.ArgumentParser(description="Build the Phase 2 POV clip")
    parser.add_argument("run_dir", help="Run directory containing pov/ frames and meta.json")
    parser.add_argument("--out", default=None, help="Output MP4 path (default <run_dir>/clip.mp4)")
    parser.add_argument("--fps", type=int, default=FPS)
    args = parser.parse_args()

    run_dir = Path(args.run_dir)
    frames_dir = run_dir / "pov"
    files = sorted(frames_dir.glob("frame_*.jpg"))
    if not files:
        raise SystemExit(
            f"No POV frames in {frames_dir}. Start a recording session "
            "(POST /record/start or let the agent do it) before running the command."
        )

    meta = {}
    meta_path = run_dir / "meta.json"
    if meta_path.exists():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
    command = meta.get("command", "")

    max_frames = int(args.fps * MAX_SECONDS)
    min_frames = int(args.fps * MIN_SECONDS)
    files = files[:max_frames]
    overlay_frames = int(args.fps * OVERLAY_SECONDS)
    font = load_font(34)
    out_path = Path(args.out) if args.out else run_dir / "clip.mp4"

    writer = imageio.get_writer(out_path, fps=args.fps, codec="libx264", quality=8,
                                pixelformat="yuv420p", macro_block_size=1)
    try:
        for index, file in enumerate(files):
            frame = Image.open(file).convert("RGB")
            if command and index < overlay_frames:
                frame = draw_overlay(frame, command, font)
            writer.append_data(np.asarray(frame))
        # Clips must be 10-20 s; hold the last frame if the run was shorter.
        if len(files) < min_frames:
            tail = Image.open(files[-1]).convert("RGB")
            for _ in range(min_frames - len(files)):
                writer.append_data(np.asarray(tail))
    finally:
        writer.close()

    duration = max(len(files), min_frames) / args.fps
    print(f"Clip written: {out_path} ({duration:.1f} s, 1280x720 @ {args.fps} fps)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
