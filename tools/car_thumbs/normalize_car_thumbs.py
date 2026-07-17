#!/usr/bin/env python3
"""Normalize car-picker thumbnails for the Session menu (SessionMenu.razor).

Problem this solves
-------------------
The first-generation thumbnails were hand-cropped from docs/images/roster.png at
256x256 each, with wildly inconsistent car framing: the kart tiny in a sea of
grey, the pickup over-zoomed to the edges, hatch/coupe cropped at the sides. On
top of that the picker tile is a landscape ~1.806:1 frame using
`background-size: cover`, which crops a square (1:1) source ~22% off the top and
bottom -- so even a well-framed square looked wrong in the tile.

Fix (source-image layer)
------------------------
Compose each car straight from the full roster render (docs/images/roster.png,
1600x900, one 800x450 cell per car) onto a canvas whose aspect EXACTLY matches
the tile thumb (130x72 -> output 390x216, precisely 3x, so `cover` renders with
zero crop), with the car centered and filling a consistent fraction of the
frame. Sourcing from the roster (not pre-cropped squares) matters: the frame
window needs real background pixels AROUND the car to zoom out into; a tight
crop has none, and edge replication can only smear, not reveal. Any window
pixels beyond the roster's edges are edge-replicated (valid -- the render
background is horizontally-uniform sky/ground).

The CSS is intentionally left unchanged: with the source aspect matched to the
tile, the existing `background-size: cover; background-position: center` is
correct, and the built-in margin absorbs any sub-pixel tile-aspect drift
without ever touching the car.

Adding a roster car
-------------------
Re-render docs/images/roster.png with the new car in a cell (or extend the
grid), add its car-pixel bounding box to BOX below (roster coordinates:
x0,x1,y0,y1 -- tune by eye), and re-run:
    PYTHONUTF8=1 python tools/car_thumbs/normalize_car_thumbs.py
Output overwrites Assets/ui/cars/<id>.png (covered by the sbproj Resources glob
`ui/cars/*.png`; do NOT add new paths -- keep id -> path derivation intact).
"""
from __future__ import annotations
import os
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROSTER = os.path.abspath(os.path.join(HERE, "..", "..", "docs", "images", "roster.png"))
OUT = os.path.abspath(os.path.join(HERE, "..", "..", "Assets", "ui", "cars"))

OUT_W, OUT_H = 390, 216          # exactly 3x the 130x72 tile thumb -> cover never crops
A = OUT_W / OUT_H                 # 1.80555...
WFRAC, HFRAC = 0.70, 0.78        # car fills <=70% width OR <=78% height (whichever binds)
                                  # (zoomed out from 0.82/0.90 per feel session 2026-07-16)

# Hand-tuned car bounding boxes in ROSTER coordinates: (x0, x1, y0, y1).
# roster.png is a 2x2 grid of 800x450 cells: hatch TL, coupe TR, kart BL, pickup BR.
BOX = {
    "hatch":  (155,  585, 190, 437),
    "coupe":  (930, 1400, 213, 432),
    "kart":   (253,  527, 588, 868),
    "pickup": (860, 1465, 605, 897),
}

# Vertical sampling clamp per car (roster rows): [just below the cell's horizon
# line, just above the cell's bottom edge]. A zoomed-out window would otherwise
# cross the horizon into sky (or into the neighbouring cell); clamping replicates
# the uniform ground band instead, keeping every thumb's backdrop clean grey.
YCLAMP = {
    "hatch":  (160, 447),
    "coupe":  (160, 447),
    "kart":   (610, 897),
    "pickup": (610, 897),
}


def render(rgb: np.ndarray, box: tuple[int, int, int, int],
           yclamp: tuple[int, int]) -> np.ndarray:
    h, w, _ = rgb.shape
    x0, x1, y0, y1 = box
    cw, ch = x1 - x0 + 1, y1 - y0 + 1
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    fw = max(cw / WFRAC, ch / HFRAC * A)   # frame width honoring aspect + fill caps
    fh = fw / A
    fx0, fy0 = cx - fw / 2.0, cy - fh / 2.0
    # nearest-sample the crop window into OUT_W x OUT_H, clamping indices to the
    # source bounds (edge replication extends the uniform background gradient) and
    # rows to the car's ground band (see YCLAMP).
    xs = np.clip(np.round(fx0 + (np.arange(OUT_W) + 0.5) * (fw / OUT_W) - 0.5).astype(int), 0, w - 1)
    ys_raw = np.round(fy0 + (np.arange(OUT_H) + 0.5) * (fh / OUT_H) - 0.5).astype(int)
    ys = np.clip(ys_raw, yclamp[0], yclamp[1])
    out = rgb[np.ix_(ys, xs)]
    # Rows sampled past the bottom of the ground band would replicate car/shadow
    # pixels (the roster render ends at the car's wheels) -- fill them with the
    # clean ground color sampled from the render's bottom corners instead. At the
    # 130x72 display size the flat fill is indistinguishable from the gradient.
    overflow = ys_raw > yclamp[1]
    if overflow.any():
        corners = np.concatenate([out[-8:, :8].reshape(-1, 3), out[-8:, -8:].reshape(-1, 3)])
        out[overflow, :] = np.median(corners, axis=0).astype(int)
    return out.astype(np.uint8)


def main() -> None:
    os.makedirs(OUT, exist_ok=True)
    rgb = np.asarray(Image.open(ROSTER).convert("RGB")).astype(np.int32)
    for cid, box in BOX.items():
        out = render(rgb, box, YCLAMP[cid])
        # high-quality downscale of the sampled window for crisp final pixels
        Image.fromarray(out).resize((OUT_W, OUT_H), Image.LANCZOS).save(
            os.path.join(OUT, f"{cid}.png"))
        print(f"{cid:8} -> {OUT_W}x{OUT_H}  (car {box})")
    print("done")


if __name__ == "__main__":
    main()
