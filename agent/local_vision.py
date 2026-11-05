"""Pixel analysis for the local agent: colour blobs from /look JPEG frames.

Everything here works from camera pixels only - the same information an LLM
would receive. No engine-side object data is used.
"""

from __future__ import annotations

import io

import numpy as np
from PIL import Image

FRAME_W, FRAME_H = 640, 480
CELL = 8
GRID_W, GRID_H = FRAME_W // CELL, FRAME_H // CELL  # 80 x 60
SKY_ROWS = 130  # walls/sky band; objects sit on the floor below this


def _hue(deg: float) -> int:
    return int(deg / 360.0 * 255)


# PIL HSV hue ranges per colour name. "magenta" is the drop-zone marker.
COLOUR_RANGES: dict[str, list[tuple[int, int]]] = {
    "red": [(_hue(350), 255), (0, _hue(10))],
    "yellow": [(_hue(40), _hue(70))],
    "green": [(_hue(100), _hue(150))],
    "blue": [(_hue(210), _hue(250))],
    "cyan": [(_hue(160), _hue(205))],  # drop zone marker - isolated from everything
}


def _components(cells: np.ndarray) -> list[list[tuple[int, int]]]:
    """4-neighbour connected components on the boolean grid."""
    visited = np.zeros_like(cells, dtype=bool)
    components: list[list[tuple[int, int]]] = []
    height, width = cells.shape
    for sy in range(height):
        for sx in range(width):
            if not cells[sy, sx] or visited[sy, sx]:
                continue
            stack = [(sy, sx)]
            visited[sy, sx] = True
            group: list[tuple[int, int]] = []
            while stack:
                cy, cx = stack.pop()
                group.append((cy, cx))
                for ny, nx in ((cy - 1, cx), (cy + 1, cx), (cy, cx - 1), (cy, cx + 1)):
                    if 0 <= ny < height and 0 <= nx < width and cells[ny, nx] and not visited[ny, nx]:
                        visited[ny, nx] = True
                        stack.append((ny, nx))
            components.append(group)
    return components


def analyse_frame(jpeg: bytes) -> list[dict]:
    """Returns colour blobs: position in frame, size, and a coarse shape guess."""
    hsv = np.asarray(Image.open(io.BytesIO(jpeg)).convert("HSV"), dtype=np.uint8)
    hue, sat, val = hsv[..., 0], hsv[..., 1], hsv[..., 2]
    blobs: list[dict] = []

    for colour, ranges in COLOUR_RANGES.items():
        mask = np.zeros_like(hue, dtype=bool)
        for lo, hi in ranges:
            if lo <= hi:
                mask |= (hue >= lo) & (hue <= hi)
            else:
                mask |= (hue >= lo) | (hue <= hi)
        mask &= sat > 80
        mask &= val > 60
        mask[:SKY_ROWS, :] = False

        grid = (mask[:GRID_H * CELL, :GRID_W * CELL]
                .reshape(GRID_H, CELL, GRID_W, CELL)
                .mean(axis=(1, 3)) > 0.35)

        for group in _components(grid):
            if len(group) < 3:
                continue
            ys = [cell[0] for cell in group]
            xs = [cell[1] for cell in group]
            gx0, gx1 = min(xs), max(xs)
            gy0, gy1 = min(ys), max(ys)
            x0, x1 = gx0 * CELL, (gx1 + 1) * CELL
            y0, y1 = gy0 * CELL, (gy1 + 1) * CELL
            bw, bh = x1 - x0, y1 - y0
            # Shape at pixel resolution: a circle fills ~0.785 of its bbox with
            # aspect ~1; no view of a cube matches both at once.
            px_count = int(mask[y0:y1, x0:x1].sum())
            fill = px_count / float(bw * bh)
            aspect = bw / float(max(1, bh))
            if fill >= 0.68 and 0.8 <= aspect <= 1.25:
                shape = "round"
            elif aspect >= 2.0 or fill < 0.45:
                shape = "tapered"
            else:
                shape = "boxy"
            blobs.append({
                "colour": colour,
                "shape": shape,
                "fill": round(fill, 2),
                "aspect": round(aspect, 2),
                "cx": (sum(xs) / len(xs)) * CELL + CELL / 2,
                "cy": (sum(ys) / len(ys)) * CELL + CELL / 2,
                "top_y": gy0 * CELL,
                "w": bw,
                "h": bh,
                "area": len(group),
            })

    blobs.sort(key=lambda blob: -blob["area"])
    return blobs


def offset_degrees(blob_cx: float) -> float:
    """Horizontal frame offset to a turn angle: 90 deg FOV across 640 px."""
    return (blob_cx - FRAME_W / 2) * (90.0 / FRAME_W)


def physical_height(height_px: float, depth_m: float) -> float:
    """Pinhole estimate of object height in metres. Focal = (W/2)/tan(45 deg) = 320 px."""
    if depth_m <= 0.01:
        return 0.0
    return height_px * depth_m / 320.0
