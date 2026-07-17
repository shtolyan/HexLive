"""Flatten the realistic wolf fur texture into the game's flat low-poly cartoon style.

Pipeline: downscale -> dilate island colors into background (seam safety) ->
heavy median smoothing (kills fur noise) -> numpy k-means palette quantize ->
slight saturation boost -> save PNG.
"""
import numpy as np
from PIL import Image, ImageFilter, ImageEnhance

SRC = "/Volumes/ORICO/HexLive/Assets/ANIMALS FULL PACK/Forest Animals Pack/Wolf/Textures/T_Wolf_BaseColorAlpha.tga"
OUT = "/private/tmp/claude-501/-Volumes-ORICO-HexLive/7052710b-d3bd-45b0-b763-a4f98fc86441/scratchpad/wolf_flat.png"
PREVIEW = "/private/tmp/claude-501/-Volumes-ORICO-HexLive/7052710b-d3bd-45b0-b763-a4f98fc86441/scratchpad/wolf_flat_preview.png"

SIZE = 1024
K = 9
SAT = 1.2

im = Image.open(SRC).convert("RGBA").resize((SIZE, SIZE), Image.LANCZOS)
rgba = np.asarray(im).astype(np.uint8)
rgb = rgba[..., :3].copy()
alpha = rgba[..., 3]

# UV-island mask: background is near-black
lum = rgb.astype(np.int32).sum(axis=2)
mask = lum > 24

# Dilate island colors outward into the background so smoothing/mips don't pull black
work = rgb.copy()
m = mask.copy()
for _ in range(24):
    if m.all():
        break
    grown = np.zeros_like(m)
    acc = np.zeros(rgb.shape, dtype=np.float32)
    cnt = np.zeros(m.shape, dtype=np.float32)
    for dy, dx in ((1,0),(-1,0),(0,1),(0,-1)):
        sm = np.roll(m, (dy, dx), axis=(0, 1))
        sc = np.roll(work, (dy, dx), axis=(0, 1))
        new = sm & ~m
        acc[new] += sc[new]
        cnt[new] += 1
        grown |= sm
    fill = (~m) & (cnt > 0)
    work[fill] = (acc[fill] / cnt[fill][..., None]).astype(np.uint8)
    m |= fill

sm = Image.fromarray(work)
# Heavy smoothing: median passes flatten fur strands into blobs, then a big
# gaussian keeps only large-scale shading (dark back / light belly)
for size in (9, 9, 9):
    sm = sm.filter(ImageFilter.MedianFilter(size))
sm = sm.filter(ImageFilter.GaussianBlur(9.0))
for size in (9, 7):
    sm = sm.filter(ImageFilter.MedianFilter(size))
sm = ImageEnhance.Color(sm).enhance(SAT)
smooth = np.asarray(sm).astype(np.float32)

# k-means over island pixels only
pix = smooth[mask].reshape(-1, 3)
rng = np.random.default_rng(7)
sample = pix[rng.choice(len(pix), min(120_000, len(pix)), replace=False)]

# init: k-means++ style greedy far points
cent = [sample[rng.integers(len(sample))]]
for _ in range(K - 1):
    d = np.min([((sample - c) ** 2).sum(1) for c in cent], axis=0)
    cent.append(sample[np.argmax(d * rng.random(len(sample)))])
cent = np.array(cent, dtype=np.float32)
for _ in range(30):
    dist = ((sample[:, None, :] - cent[None]) ** 2).sum(2)
    lab = dist.argmin(1)
    newc = np.array([sample[lab == i].mean(0) if (lab == i).any() else cent[i] for i in range(K)])
    if np.abs(newc - cent).max() < 0.5:
        cent = newc
        break
    cent = newc

flat = smooth.reshape(-1, 3)
dist = ((flat[:, None, :] - cent[None]) ** 2).sum(2)
lab_img = dist.argmin(1).reshape(smooth.shape[:2]).astype(np.uint8)

# consolidate speckles: mode filter over the LABEL image so tiny islands merge
lab_pil = Image.fromarray(lab_img, "L")
for _ in range(3):
    lab_pil = lab_pil.filter(ImageFilter.ModeFilter(9))
lab_img = np.asarray(lab_pil)
q = cent[lab_img.reshape(-1)].reshape(smooth.shape).astype(np.uint8)

# Repaint the eye — quantization flattens it away. Original iris center is at
# (768, 275) in the 4096 source; head UV is shared by both sides of the face.
from PIL import ImageDraw
q_img = Image.fromarray(q)
draw = ImageDraw.Draw(q_img)
s = SIZE / 4096.0
ex, ey = 768 * s, 275 * s
def ellipse(cx, cy, r, color):
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), fill=color)
ellipse(ex, ey, 10 * SIZE / 1024, (42, 36, 28))     # dark socket outline
ellipse(ex, ey, 6.5 * SIZE / 1024, (201, 138, 58))  # amber iris
ellipse(ex, ey, 3 * SIZE / 1024, (20, 16, 12))      # pupil
q = np.asarray(q_img)

out = np.dstack([q, alpha])
Image.fromarray(out, "RGBA").save(OUT)
Image.fromarray(q).resize((1024, 1024)).save(PREVIEW)
print("palette:")
for c in cent:
    print("  ", [int(v) for v in c])
print("saved", OUT)
