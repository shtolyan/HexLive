#!/usr/bin/env python3
"""Wound stamp art pipeline (spec 40.8-D v5).

Regenerates the gash stamp set used by SkinTexturePainter:
  wound_scratch.png    albedo (opaque dark-red core, small splatter halo)
  wound_scratch_n.png  relief AUTHORED FROM A MASK, not luminance: SDF ->
                       V-groove inside the cut + swollen ridge outside
  wound_scratch_g.png  wet-gloss shape (alpha = smoothness 0..1)
  blood_splat_g.png    wet-gloss for the droplet splat (alpha^2 of its albedo)

Commands:
  gen    ask fal.ai flux/schnell for gash candidates (needs FAL_KEY env var;
         the key is PRIVATE - never hardcode it here)
  build  post-process a candidate (or, with --no-fal, the existing
         wound_scratch.png) into the four stamp PNGs

Outputs land in --outdir for eyeball review; copy into
Assets/Resources/HexLive/Decals/ by hand (wound_scratch*.png overwrite in
place and keep their GUIDs; the *_g.png files are NEW - pre-create .meta
files with sRGBTexture: 0 before Unity imports them).
"""

import argparse
import io
import json
import os
import sys
import urllib.request

import numpy as np
from PIL import Image, ImageFilter

DECALS = os.path.join(os.path.dirname(__file__), "..",
                      "Assets", "Resources", "HexLive", "Decals")

PROMPTS = {
    "a": ("professional SFX makeup prosthetic wound, extreme close-up of a "
          "single deep laceration gash running diagonally across the frame, "
          "interior filled with opaque dark red coagulated blood, glossy wet "
          "surface, swollen inflamed pink ridge along both cut edges, a few "
          "thin dried blood streaks, isolated on plain white background, "
          "even studio light, photorealistic macro detail"),
    "b": ("three parallel jagged claw laceration cuts, professional SFX "
          "makeup prosthetic wound, deep torn skin gashes filled with opaque "
          "dark red coagulated blood, wet glossy surface, swollen inflamed "
          "edges, isolated on plain white background, photorealistic macro "
          "detail, even studio light"),
    # --- extra wound shapes for variety (spec 40.8-D v5) ---
    # BLOOD ONLY: no skin/flesh, no bruised rings, no punctures — those bake
    # a fixed skin tone into the stamp (wrong on other NPCs) and read as
    # plastic prosthetics. `slash` was the keeper: a wet dark-red gash-shaped
    # pool that lands on ANY skin colour. These vary the SHAPE only.
    "slash": (
        "professional SFX makeup prosthetic wound, one long clean deep slash "
        "cut curving across the frame, gaping opaque dark red interior, wet "
        "glossy blood, swollen pink cut edges, isolated on plain white "
        "background, even studio light, photorealistic macro detail"),
    "streak": (
        "a diagonal streak of wet fresh blood in the shape of a deep cut, "
        "glossy dark red with a near-black pooled center, ragged torn splatter "
        "edges, a few thin blood drips running off, isolated on pure white "
        "background, top-down macro photograph, no skin, no flesh"),
    "fork": (
        "a forked branching gash of wet fresh blood, glossy dark crimson with "
        "darker pooled centers, ragged edges and fine spatter, a few drips, "
        "isolated on plain white background, top-down macro photo, no skin, "
        "no flesh"),
    "hook": (
        "a hooked crescent gash of wet glossy blood, deep dark red almost "
        "black in the pooled center, torn irregular edges, small scattered "
        "droplets around it, isolated on plain white background, top-down "
        "macro photograph, no skin, no flesh"),
    "torn": (
        "a short jagged torn gash filled with wet dark red blood, glossy "
        "pooled darker center, splattered ragged edges, several thin drips, "
        "isolated on plain white background, top-down macro photo, no skin, "
        "no flesh"),
    "smear": (
        "a long smear of wet fresh blood dragged across the frame like a deep "
        "graze, glossy dark red, denser pooled core tapering to thin streaks "
        "and droplets, isolated on plain white background, top-down macro "
        "photo, no skin, no flesh"),
}


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def gaussian(arr, radius):
    """FFT gaussian blur (PIL can't filter float modes). Wraps at the
    borders — stamp content stays clear of them."""
    if radius <= 0:
        return arr.copy()
    fy = np.fft.fftfreq(arr.shape[0])[:, None]
    fx = np.fft.rfftfreq(arr.shape[1])[None, :]
    g = np.exp(-2.0 * (np.pi * float(radius)) ** 2 * (fx ** 2 + fy ** 2))
    return np.fft.irfft2(np.fft.rfft2(arr) * g, s=arr.shape)


def binary_filter(mask, size, cls):
    img = Image.fromarray((mask * 255).astype(np.uint8))
    return np.asarray(img.filter(cls(size)), dtype=np.uint8) > 127


def dilate(mask, size=3):
    return binary_filter(mask, size, ImageFilter.MaxFilter)


def erode(mask, size=3):
    return binary_filter(mask, size, ImageFilter.MinFilter)


def close_mask(mask, size=3):
    return erode(dilate(mask, size), size)


def fill_holes(mask):
    """A glossy wound interior reflects white and fails the red key — but
    it's ENCLOSED by the dark rim. Flood the outside from the borders;
    anything not reached is interior and joins the mask."""
    outside = np.zeros_like(mask)
    outside[0, :] = outside[-1, :] = True
    outside[:, 0] = outside[:, -1] = True
    outside &= ~mask
    while True:
        grown = dilate(outside, 5) & ~mask
        if (grown == outside).all():
            return mask | ~grown
        outside = grown


def reconstruct(seed, region):
    """Keep only the parts of `region` connected to `seed` — kills the
    faint disconnected junk the chroma key lets through (knuckle shadows,
    stray specks)."""
    cur = seed & region
    while True:
        grown = dilate(cur, 5) & region
        if (grown == cur).all():
            return cur
        cur = grown


def iter_distance(mask, max_px):
    """Distance (px) from each pixel to `mask`, by counting 3x3 dilation
    rounds - no scipy/cv2 on this box; quantization smooths out under the
    later gaussian blur. Pixels beyond max_px get max_px."""
    dist = np.full(mask.shape, float(max_px))
    cur = mask.copy()
    dist[cur] = 0.0
    for step in range(1, max_px):
        # Alternate square/cross growth ~ octagonal metric (a plain 3x3
        # MaxFilter is chebyshev — visibly square terraces on the ridge).
        if step % 2 == 0:
            grown = dilate(cur, 3)
        else:
            img = Image.fromarray((cur * 255).astype(np.uint8))
            k = ImageFilter.Kernel((3, 3), [0, 1, 0, 1, 1, 1, 0, 1, 0], 1)
            grown = np.asarray(img.filter(k), dtype=np.uint8) > 0
        ring = grown & ~cur
        if not ring.any():
            break
        dist[ring] = float(step)
        cur = grown
    return dist


# ---- fal.ai ----

def fal_generate(prompt, out_path):
    key = os.environ.get("FAL_KEY")
    if not key:
        sys.exit("FAL_KEY env var not set (the key lives OUTSIDE the repo)")
    body = json.dumps({
        "prompt": prompt,
        "image_size": "square_hd",
        "num_images": 1,
        "num_inference_steps": 4,
        "enable_safety_checker": False,
    }).encode()
    req = urllib.request.Request(
        "https://fal.run/fal-ai/flux/schnell", data=body,
        headers={"Authorization": f"Key {key}",
                 "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as resp:
        payload = json.load(resp)
    url = payload["images"][0]["url"]
    with urllib.request.urlopen(url, timeout=180) as resp:
        data = resp.read()
    Image.open(io.BytesIO(data)).convert("RGB").save(out_path)
    print(f"generated {out_path}")


# ---- albedo post-process ----

def build_albedo(src_img, size=1024):
    """RGB(A) candidate -> RGBA stamp: chroma-keyed alpha, OPAQUE core,
    trimmed halo, darkened core. Returns (rgba float [0..1], core mask)."""
    src = src_img.convert("RGBA").resize((size, size), Image.LANCZOS)
    arr = np.asarray(src, dtype=np.float64) / 255.0
    rgb, src_alpha = arr[..., :3], arr[..., 3]

    lum = rgb @ np.array([0.299, 0.587, 0.114])
    red_dom = rgb[..., 0] - np.maximum(rgb[..., 1], rgb[..., 2])

    # Redness in, brightness out (white background and pale skin drop away).
    alpha = smoothstep(0.08, 0.26, red_dom) * (1.0 - smoothstep(0.5, 0.9, lum))
    alpha *= src_alpha  # respect any alpha the source already carries

    # Blood is SATURATED red (R >> G+B); brown skin shadow is not. This is
    # what separates knuckle creases from gore.
    red_ratio = rgb[..., 0] / (rgb[..., 1] + rgb[..., 2] + 0.02)
    alpha *= smoothstep(1.1, 1.6, red_ratio)

    # The gash core: strongly red AND dark. Force it fully opaque - the
    # see-through core is exactly what read as "transparent wound".
    core = (red_dom > 0.15) & (lum < 0.35) & (src_alpha > 0.5) & (red_ratio > 1.3)
    core = close_mask(core, 5)
    core &= dilate(erode(core, 3), 5)  # drop 1px specks, keep bodies
    # A wet interior reflects white and fails the red key — but it is
    # enclosed by the dark rim: fill it back in.
    core = fill_holes(core)
    alpha = np.where(core, 1.0, alpha)

    # Everything not CONNECTED to a gash body is key noise (knuckle
    # shadows, stray skin texture) — drop it entirely.
    connected = reconstruct(core, dilate(alpha > 0.05, 3))
    alpha = np.where(connected | core, alpha, 0.0)

    # Keep the splatter halo modest: outside the dilated core it fades.
    near_core = dilate(core, 3)
    for _ in range(11):  # ~35 px reach
        near_core = dilate(near_core, 5)
    alpha = np.where(near_core, alpha, alpha * 0.25)

    # Kill the BAKED specular highlights: the photo's white glints are now
    # provided dynamically by the painted gloss channel — static white both
    # doubles the shine and survives dark tan tints that swallow the blood
    # colour (they read as white smears on dark skin). Replace them with
    # the surrounding blood colour, keep 15% for texture.
    w_spec = smoothstep(0.35, 0.6, lum) * (1.0 - smoothstep(1.2, 1.8, red_ratio))
    w_spec *= np.maximum(alpha, core)
    red_w = np.maximum(alpha, core) * (1.0 - w_spec) + 1e-4
    blood = np.dstack([gaussian(rgb[..., c] * red_w, 6.0) for c in range(3)])
    blood /= gaussian(red_w, 6.0)[..., None]
    rgb = rgb * (1.0 - 0.85 * w_spec[..., None]) + \
        np.clip(blood, 0.0, 1.0) * (0.85 * w_spec[..., None])

    # Blood must survive the tan/skin tint: NpcActorView multiplies the
    # whole painted slot by _BaseColor (measured ~(0.47, 0.31, 0.23) on a
    # tanned NPC), so the stamp is authored BRIGHTER than screen-final —
    # a 0.28-red "dried blood" core lands at 0.13 and vanishes on dark
    # skin. Core aims at fresh arterial red instead; the tint does the
    # darkening in-game.
    target = np.array([0.55, 0.04, 0.05])
    core_soft = gaussian(core.astype(np.float64), 2.0)[..., None]
    rgb = rgb * (1.0 - 0.5 * core_soft) + target * (0.5 * core_soft)
    boost = 1.0 + 0.3 * np.maximum(alpha, core)
    rgb[..., 0] = np.clip(rgb[..., 0] * boost, 0.0, 1.0)

    # Bleed content colour under the transparent texels: bilinear/mip
    # sampling straddles the alpha edge and would drag the WHITE source
    # background in as a pale fringe otherwise.
    a = gaussian(alpha, 1.0)
    w = gaussian(a, 8.0)
    bleed = np.dstack([gaussian(rgb[..., c] * a, 8.0) for c in range(3)])
    bleed /= np.maximum(w, 1e-4)[..., None]
    mixw = np.clip(a * 4.0, 0.0, 1.0)[..., None]
    rgb = rgb * mixw + np.clip(bleed, 0.0, 1.0) * (1.0 - mixw)

    # Crop to content: the generated frame is mostly empty margin, which
    # shrinks the painted gash to a third of the stamp's world size (the
    # 9 cm stamp carried ~3 cm of art). Square-crop around the alpha bbox
    # with a small pad, then back to full resolution.
    ys, xs = np.where(a > 0.05)
    if len(ys) > 0:
        # Percentile bbox: a stray drip dot far from the gashes must not
        # stretch the frame back out (min/max would).
        w8 = a[ys, xs]
        order_y = np.argsort(ys); cum_y = np.cumsum(w8[order_y]); cum_y /= cum_y[-1]
        order_x = np.argsort(xs); cum_x = np.cumsum(w8[order_x]); cum_x /= cum_x[-1]
        pad = int(size * 0.05)
        y0 = max(int(ys[order_y][np.searchsorted(cum_y, 0.005)]) - pad, 0)
        y1 = min(int(ys[order_y][np.searchsorted(cum_y, 0.995)]) + pad, size - 1)
        x0 = max(int(xs[order_x][np.searchsorted(cum_x, 0.005)]) - pad, 0)
        x1 = min(int(xs[order_x][np.searchsorted(cum_x, 0.995)]) + pad, size - 1)
        side = max(y1 - y0, x1 - x0)
        cy, cx = (y0 + y1) // 2, (x0 + x1) // 2
        y0 = int(np.clip(cy - side // 2, 0, size - side))
        x0 = int(np.clip(cx - side // 2, 0, size - side))
        rgba = np.dstack([rgb, a])[y0:y0 + side, x0:x0 + side]
        img = Image.fromarray((np.clip(rgba, 0, 1) * 255).astype(np.uint8), "RGBA")
        rgba = np.asarray(img.resize((size, size), Image.LANCZOS),
                          dtype=np.float64) / 255.0
        cimg = Image.fromarray((core[y0:y0 + side, x0:x0 + side] * 255).astype(np.uint8))
        core = np.asarray(cimg.resize((size, size), Image.LANCZOS),
                          dtype=np.uint8) > 127
        return rgba, core

    return np.dstack([rgb, a]), core


# ---- relief from mask (groove + swollen ridge) ----

def build_relief(albedo, core, size=1024):
    """Height field from the core-mask SDF: V-groove inside the cut, a
    swollen skin ridge just outside it - then central-difference normals.
    ~9 cm stamp at 1024 px = 0.088 mm/px."""
    # BROAD features: the 1024px art lands in only ~160 texels of the
    # 2048 slot (8 cm stamp) — a 10 px wall would collapse into 1.5
    # texels and vanish under mips. Widths sized to survive 6x downsample.
    w_in = 26       # groove half-width, px
    ridge_r = 40    # ridge crest distance from the cut edge, px
    ridge_sigma = 26
    ridge_h = 0.5

    d_out = iter_distance(core, 48)              # distance TO the core
    d_in = iter_distance(~core, w_in + 6)        # depth INTO the core

    h = np.zeros(core.shape)
    h -= smoothstep(0.0, float(w_in), d_in) * core          # groove sinks in
    ridge = ridge_h * np.exp(-((d_out - ridge_r) ** 2) / (2.0 * ridge_sigma ** 2))
    h += ridge * ~core                                       # swelling rises

    # Organic micro-relief inside the cut from the albedo's own shading.
    lum = albedo[..., :3] @ np.array([0.299, 0.587, 0.114])
    h += (lum - lum[core].mean()) * 0.10 * core if core.any() else 0.0

    h = gaussian(h, 3.0)  # kill the distance-transform stair-step

    # Normals: steepest groove wall ~60 deg. Image i grows DOWN (= -v).
    dh_dj = np.gradient(h, axis=1)
    dh_di = np.gradient(h, axis=0)
    grad_max = max(np.abs(dh_dj).max(), np.abs(dh_di).max(), 1e-6)
    s = 1.7 / grad_max
    nx = -s * dh_dj
    ny = s * dh_di
    nz = np.ones_like(h)
    norm = np.sqrt(nx * nx + ny * ny + nz * nz)
    n = np.dstack([nx / norm, ny / norm, nz / norm]) * 0.5 + 0.5

    coverage = np.clip(np.abs(h) * 3.0, 0.0, 1.0)
    coverage = np.maximum(coverage, core.astype(np.float64))
    coverage = gaussian(coverage, 2.0)

    # Outside the relief the RGB MUST be flat (0.5, 0.5, 1): DrawTexture
    # alpha-blend feathers toward whatever RGB sits under zero alpha.
    flat = np.array([0.5, 0.5, 1.0])
    n = n * coverage[..., None] + flat * (1.0 - coverage[..., None])

    return np.dstack([n, coverage])


# ---- gloss shapes ----

def build_gloss(albedo, core):
    """Alpha = wet-shape 0..1 (the shader scales by _GlossMax): the blood
    core glistens fully, streaks/splatter by their own opacity squared."""
    wet = gaussian(core.astype(np.float64), 4.0)
    a = np.clip(np.maximum(wet, albedo[..., 3] ** 2), 0.0, 1.0)
    return np.dstack([np.zeros((*a.shape, 3)), a])


def build_splat_gloss(splat_path):
    src = Image.open(splat_path).convert("RGBA")
    arr = np.asarray(src, dtype=np.float64) / 255.0
    a = arr[..., 3] ** 2  # concentrate gloss in the droplet bodies
    return np.dstack([np.zeros((*a.shape, 3)), a])


def save(arr, path):
    Image.fromarray((np.clip(arr, 0.0, 1.0) * 255).astype(np.uint8),
                    "RGBA").save(path)
    print(f"wrote {path}")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    sub = ap.add_subparsers(dest="cmd", required=True)

    g = sub.add_parser("gen", help="generate gash candidates via fal.ai")
    g.add_argument("--outdir", required=True)
    g.add_argument("--variant", choices=sorted(PROMPTS), action="append")

    b = sub.add_parser("build", help="candidate -> stamp set")
    b.add_argument("--outdir", required=True)
    b.add_argument("--src", help="candidate image (RGB ok)")
    b.add_argument("--no-fal", action="store_true",
                   help="post-process the existing committed wound_scratch.png")
    b.add_argument("--splat", default=os.path.join(DECALS, "blood_splat.png"))
    b.add_argument("--name", default="wound_scratch",
                   help="output basename: writes <name>.png + <name>_g.png "
                        "(default wound_scratch, which also emits the legacy "
                        "_n relief + blood_splat_g)")
    b.add_argument("--preview", action="store_true",
                   help="also write <name>_preview.png composited over tanned "
                        "skin (the in-game tint) for eyeballing")

    args = ap.parse_args()
    os.makedirs(args.outdir, exist_ok=True)

    if args.cmd == "gen":
        for v in (args.variant or sorted(PROMPTS)):
            fal_generate(PROMPTS[v],
                         os.path.join(args.outdir, f"gash_candidate_{v}.png"))
        return

    src_path = (os.path.join(DECALS, "wound_scratch.png") if args.no_fal
                else args.src)
    if not src_path:
        sys.exit("build needs --src or --no-fal")
    src = Image.open(src_path)

    albedo, core = build_albedo(src)
    if not core.any():
        sys.exit(f"no opaque gash core detected in {src_path} - bad candidate")
    save(albedo, os.path.join(args.outdir, f"{args.name}.png"))
    save(build_gloss(albedo, core),
         os.path.join(args.outdir, f"{args.name}_g.png"))
    # Wounds no longer use relief (UV-seam artifacts) — the legacy default
    # name still emits it + the droplet-splat gloss so a plain `build` keeps
    # reproducing the shipped set; named variants are albedo + gloss only.
    if args.name == "wound_scratch":
        save(build_relief(albedo, core),
             os.path.join(args.outdir, "wound_scratch_n.png"))
        save(build_splat_gloss(args.splat),
             os.path.join(args.outdir, "blood_splat_g.png"))
    if args.preview:
        skin = np.zeros_like(albedo[..., :3])
        skin[:] = (0.75, 0.62, 0.55)      # daz albedo before tint
        a = albedo[..., 3:4]
        comp = (albedo[..., :3] * a + skin * (1.0 - a)) * np.array([0.47, 0.31, 0.23])
        Image.fromarray((np.clip(comp, 0.0, 1.0) * 255).astype(np.uint8)).save(
            os.path.join(args.outdir, f"{args.name}_preview.png"))
        print(f"wrote {args.name}_preview.png")
    print(f"core coverage: {core.mean() * 100:.1f}% of texels")


if __name__ == "__main__":
    main()
