"""Generate the MagicaCloth paint map for NHS Skirt G3F_27980.

MagicaCloth samples the map with an EXACT point lookup at each render vertex's
uv0 texel (ClothProcess.PaintMapJob): green>32 => Move, else red>32 => Fixed,
anything else => ignored (keeps plain skinning).

Layout decided from the geometry:
  * submesh 0 (the waistband) -> BLACK: it rides the hip rigidly, no sim.
  * the top DELTA metres of the skirt -> RED (fixed anchor ring).
  * the rest of the skirt -> GREEN (free to swing).

The waist is tilted (hips), so the anchor band is measured per AZIMUTH against
the skirt's local top rather than against a flat Y plane -- a flat cut only
caught 21/36 sectors and would have left the low sides unanchored.

The four per-actor meshes share one UV layout (identical uv bounds, identical
welded topology: 27980 verts / 49888 tris), so ONE map serves all of them. The
fixed band is the UNION over the four girls, so every girl's anchor is covered.
"""
import numpy as np
from collections import defaultdict
from PIL import Image
from unity_mesh_parse import parse_mesh

BASE = "/Volumes/ORICO/HexLive/Assets/ImportedActors/Wear/Skirt G3F_27980/Meshes"
GIRLS = ["Marta", "Molly", "Jana", "Jolly"]
SIZE = 1024
SECTORS = 72          # azimuth bins for the local-top profile
DELTA = 0.075         # metres of skirt below its local top that stay anchored
# 0.075, not the 0.030 you would guess from 'just pin the waistband': the pleats
# CONVERGE toward the waist, so up there the folds lie ~6 mm apart (the mesh's own
# edge length) and ANY proxy reduction welds neighbouring pleats into each other --
# which rendered as a shredded band right under the belt. Anchoring that zone is
# also what a real pleated skirt does (the yoke is stitched down), so the welding
# stops mattering: fixed vertices are not simulated.
OUT = "/Volumes/ORICO/HexLive/Assets/ImportedActors/Wear/Skirt G3F_27980/Textures/NHS_Skirt_G3F_27980_PaintMap.png"

RED = np.array([255, 0, 0], np.uint8)
GREEN = np.array([0, 255, 0], np.uint8)
BLACK = np.array([0, 0, 0], np.uint8)


def submesh_tris(m, si):
    sm = m["submeshes"][si]
    start = sm["firstByte"] // 2
    return m["indices"][start:start + sm["indexCount"]].reshape(-1, 3) + sm["baseVertex"]


def sl_of(m, si):
    sm = m["submeshes"][si]
    return slice(sm["firstVertex"], sm["firstVertex"] + sm["vertexCount"])


def azimuth(p, cx, cz):
    return np.arctan2(p[:, 2] - cz, p[:, 0] - cx)


def sector_top(p, cx, cz, sectors=SECTORS):
    """Per-azimuth max Y, circularly smoothed so the band follows the tilt."""
    ang = azimuth(p, cx, cz)
    b = ((ang + np.pi) / (2 * np.pi) * sectors).astype(int) % sectors
    top = np.full(sectors, -np.inf)
    np.maximum.at(top, b, p[:, 1])
    # fill any empty sector from its neighbours, then smooth over +-2 sectors
    for _ in range(sectors):
        if np.isfinite(top).all():
            break
        top = np.where(np.isfinite(top), top,
                       np.maximum(np.roll(top, 1), np.roll(top, -1)))
    sm = np.stack([np.roll(top, k) for k in (-2, -1, 0, 1, 2)])
    return sm.mean(0), b


def texel(uv):
    tx = np.clip(((uv[:, 0] % 1.0) * SIZE).astype(int), 0, SIZE - 1)
    ty = np.clip(((uv[:, 1] % 1.0) * SIZE).astype(int), 0, SIZE - 1)
    return tx, ty


def raster_tris(img, tris, uv, colors):
    """Scan-convert triangles in UV space so no texel between vertices is bare."""
    P = np.stack([(uv[:, 0] % 1.0) * SIZE, (uv[:, 1] % 1.0) * SIZE], 1)
    for t in tris:
        a, b, c = P[t]
        cols = colors[t]
        col = cols[0] if (cols[0] == cols[1]).all() or (cols[0] == cols[2]).all() else cols[1]
        minx, maxx = int(min(a[0], b[0], c[0])), int(max(a[0], b[0], c[0])) + 1
        miny, maxy = int(min(a[1], b[1], c[1])), int(max(a[1], b[1], c[1])) + 1
        if maxx - minx > SIZE // 2 or maxy - miny > SIZE // 2:
            continue
        minx, miny = max(minx, 0), max(miny, 0)
        maxx, maxy = min(maxx, SIZE), min(maxy, SIZE)
        if minx >= maxx or miny >= maxy:
            continue
        xs, ys = np.meshgrid(np.arange(minx, maxx) + 0.5, np.arange(miny, maxy) + 0.5)
        d = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
        if abs(d) < 1e-12:
            continue
        w0 = ((b[1] - c[1]) * (xs - c[0]) + (c[0] - b[0]) * (ys - c[1])) / d
        w1 = ((c[1] - a[1]) * (xs - c[0]) + (a[0] - c[0]) * (ys - c[1])) / d
        inside = (w0 >= -0.02) & (w1 >= -0.02) & (w0 + w1 <= 1.02)
        if inside.any():
            img[miny:maxy, minx:maxx][inside] = col


meshes = {g: parse_mesh(f"{BASE}/{g}.mesh") for g in GIRLS}
fixed_texels, move_texels = set(), set()

for g in GIRLS:
    m = meshes[g]
    b = sl_of(m, 1)
    p = m["pos"][b]
    cx, cz = p[:, 0].mean(), p[:, 2].mean()
    top, bin_of = sector_top(p, cx, cz)
    is_fixed = p[:, 1] >= (top[bin_of] - DELTA)

    tx, ty = texel(m["uv0"][b])
    tid = ty * SIZE + tx
    fixed_texels |= set(tid[is_fixed].tolist())
    move_texels |= set(tid[~is_fixed].tolist())

    # where does the waistband's lower edge sit? the anchor must hide under it
    p0 = m["pos"][sl_of(m, 0)]
    print(f"{g}: skirt y {p[:,1].min():.3f}..{p[:,1].max():.3f}  "
          f"anchor {is_fixed.sum():5d}/{len(p)} ({100*is_fixed.sum()/len(p):.1f}%)  "
          f"anchor y {p[is_fixed][:,1].min():.3f}..{p[is_fixed][:,1].max():.3f}  "
          f"belt y {p0[:,1].min():.3f}..{p0[:,1].max():.3f}")

print(f"\nunion: fixed texels {len(fixed_texels)}, move {len(move_texels)}, "
      f"overlap {len(fixed_texels & move_texels)} (fixed wins)")

# ---- paint ----
img = np.zeros((SIZE, SIZE, 3), np.uint8)          # black = ignore
ref = meshes["Marta"]
uv_all = ref["uv0"]
tx, ty = texel(uv_all)
tid_all = ty * SIZE + tx
colors = np.zeros((len(uv_all), 3), np.uint8)
skirt_idx = np.arange(sl_of(ref, 1).start, sl_of(ref, 1).stop)
in_fixed = np.array([t in fixed_texels for t in tid_all[skirt_idx]])
colors[skirt_idx] = np.where(in_fixed[:, None], RED, GREEN)

raster_tris(img, submesh_tris(ref, 1), uv_all, colors)

# the lookup is an exact point sample: stamp every girl's vertex texels last.
for t in sorted(move_texels - fixed_texels):
    img[t // SIZE, t % SIZE] = GREEN
for t in sorted(fixed_texels):
    img[t // SIZE, t % SIZE] = RED
for g in GIRLS:                                     # waistband stays black
    bx, by = texel(meshes[g]["uv0"][sl_of(meshes[g], 0)])
    img[by, bx] = BLACK

# Unity indexes texture rows BOTTOM-UP (GetPixels32()[y*w+x], y=0 = bottom),
# while a PNG's first row is the top one. Flip on the way out so the texel the
# runtime reads for uv.v is the row this script painted for it.
Image.fromarray(np.flipud(img)).save(OUT)
print(f"\nwrote {OUT} ({SIZE}x{SIZE}, flipped for Unity's bottom-up rows)")

# ---- verify by replaying MagicaCloth's exact lookup ----
print("\nverification (PaintMapJob lookup replayed):")
ok = True
for g in GIRLS:
    m = meshes[g]
    tx, ty = texel(m["uv0"])
    col = img[ty, tx]
    move = col[:, 1] > 32
    fix = (~move) & (col[:, 0] > 32)
    ign = ~(move | fix)
    a, b = sl_of(m, 0), sl_of(m, 1)
    p = m["pos"][b]
    fb = fix[b]
    cx, cz = p[:, 0].mean(), p[:, 2].mean()
    ang = np.degrees(azimuth(p[fb], cx, cz))
    cov = int((np.histogram(ang, bins=36, range=(-180, 180))[0] > 0).sum())
    belt_ok = ign[a].all()
    skirt_ok = ign[b].sum() == 0
    ok &= belt_ok and skirt_ok and cov == 36
    print(f"  {g}: belt ignored {ign[a].sum()}/{a.stop-a.start} | skirt fixed {fb.sum()}, "
          f"move {move[b].sum()}, ignored {ign[b].sum()} | anchor y "
          f"{p[fb][:,1].min():.3f}..{p[fb][:,1].max():.3f} | azimuth {cov}/36")
print("\nALL CHECKS PASS" if ok else "\n*** CHECK FAILED ***")
