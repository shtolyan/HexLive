#!/usr/bin/env python3
"""Build the shared eye-colour texture set (spec §85).

The four actresses each shipped their OWN 2048² Daz eye map — iris colour was
therefore welded to the skin set, and a girl could not have her mother's face
with her father's eyes. This script cuts that tie: it takes ONE base eye map
and recolours only its iris discs, producing a family of maps that are
byte-identical everywhere except the iris.

Layout of a Daz Genesis3 eye map (verified on all four actresses):

    top half     the sclera pair (white of the eye, veins, wet rim)
    bottom half  the iris pair   — left disc at (506, 1525) r≈451,
                                   right disc at (1527, 1530) r≈451

So the recolour is a masked operation on two circles. Everything else — sclera,
veins, the off-disc background — is copied through untouched, which is what
makes the whole set share one sclera and one wet rim.

The recolour itself is LUMINANCE → RAMP, never a hue rotation. The base iris is
a desaturated blue-grey; rotating its hue would carry that residual blue into
every result and green would come out teal. Reading luminance and re-mapping it
through a 4-stop gradient keeps the radial fibre detail (which lives entirely in
luminance) and gives the palette full authority over the colour.

It also writes the SHARED eye materials, because the palette list and the
material set have to agree exactly — a colour with no material is a girl with
no eyes, and the two drifting apart is precisely the failure this puts in one
file. Unity need not be running: .meta files are authored here with
deterministic guids (md5 of the asset path), the way the headless tool pipeline
already does it.

    python3 Tools/make_eye_textures.py            # textures + materials
    python3 Tools/make_eye_textures.py --contact  # + a preview sheet
"""

import argparse
import hashlib
import os
import re
import shutil
import sys

import numpy as np
from PIL import Image

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Jolly's map: the crispest fibres and the most neutral base of the four, which
# makes it the one that recolours without a colour cast of its own.
BASE = os.path.join(
    REPO, "Assets/ImportedActors/Actors/Jolly/Textures/G3G8CS71Eye.jpg")

OUT_DIR = os.path.join(REPO, "Assets/HexLive/Art/Eyes/Textures")

# The material set is copied from Molly's, verbatim but for the texture: all
# four actresses carry byte-identical eye material PARAMETERS (smoothness 0.516,
# alpha-clipped iris at queue 2450 …) and differ only in the map, which is what
# makes "one shared set, swap the texture" honest rather than a redesign of how
# the eyes shade.
DONOR_MATERIALS = os.path.join(
    REPO, "Assets/ImportedActors/Actors/Molly/Materials")
DONOR_EYE_MAP_GUID = "2c2a13b66e1fc97469d38d5c92a1671c"

MATERIALS_ROOT = os.path.join(REPO, "Assets/HexLiveContent/RuntimeSource/Eyes")

# …with ONE exception to "verbatim". The Daz import left `_Metallic: 1` on the
# iris of all four actresses (the male outsider came in at 0, which is how the
# defect stayed invisible). Under URP metallic 1 means the base map stops being
# albedo and becomes the specular colour, so the iris renders as coloured
# chrome — the shine reads as demonic, and it is the ONLY eye material affected:
# sclera, pupil, cornea and the wet film all ship at 0 already.
#
# An iris is dielectric tissue and physically wants 0. It is left slightly above
# it because at a clean 0 the eye also loses the gleam that made it читаемым at
# portrait distance — the cornea in front is transparent with smoothness 0 and
# contributes no highlight of its own, so today the iris IS the specular. This
# is the dial to turn if it still shines: 0.0 = matte tissue, 1.0 = the chrome
# it shipped as.
IRIS_METALLIC = 0.2

# Materials that carry the eye map — one copy per colour.
TINTED_MATERIALS = ("Irises", "Sclera")
# Materials with no texture at all: pure black pupil, clear cornea, wet film.
# One copy for everybody, which is the "reuse everywhere" half of the ask.
COMMON_MATERIALS = ("Pupils", "Cornea", "EyeMoisture")

META_TEXTURE = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 12
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMasterTextureLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 0
    wrapV: 0
    wrapW: 0
  nPOTScale: 1
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 0
  spriteTessellationDetail: -1
  textureType: 0
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 3
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
    bones: []
    spriteID:
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
    nameFileIdTable: {{}}
  spritePackingTag:
  pSDRemoveMatte: 0
  pSDShowRemoveMatteOption: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""

META_MATERIAL = """fileFormatVersion: 2
guid: {guid}
NativeFormatImporter:
  externalObjects: {{}}
  mainObjectFileID: 2100000
  userData:
  assetBundleName:
  assetBundleVariant:
"""

META_FOLDER = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

# (centre_x, centre_y, radius) of the two iris discs in the 2048² map.
IRIS_DISCS = ((506, 1525, 451), (1527, 1530, 451))

# Luminance in the base disc runs 0 (pupil) → 0.64 (brightest fibre); anchoring
# the ramp at 0.60 keeps the top stop reachable instead of unused headroom.
LUMA_CEILING = 0.60

# Gradient stop positions, shared by every palette so the palettes stay
# comparable: pupil/limbal black, the fibre body, and the bright inner ring.
STOPS = (0.0, 0.30, 0.62, 1.0)

# Eye colours. Four stops each, dark → light, as sRGB hex. The ids are what the
# simulation rolls (ColonistAppearance.EyeColors) — renaming one is a save and
# wire change, so treat them as data, not labels.
PALETTES = {
    # The cool half is deliberately a notch less saturated than the ramp wants:
    # at full chroma blue and green read as contact lenses. The warm half needs
    # no such restraint — brown eyes ARE that saturated.
    "blue":       ("#05080e", "#193b5c", "#4478a0", "#c2d8e8"),
    "blue_green": ("#040a0b", "#12403d", "#3a827a", "#bcd8cf"),
    "green":      ("#050803", "#20381a", "#5c7f3f", "#c5d5a6"),
    "hazel":      ("#0a0703", "#3a2a10", "#8a6a2c", "#e3d09b"),
    "amber":      ("#0d0602", "#4a2c07", "#a86e18", "#f0d190"),
    "brown":      ("#080402", "#2d1a0a", "#6d4522", "#c8a273"),
    "dark_brown": ("#050301", "#1c1006", "#452a12", "#9b7448"),
    "grey":       ("#06080a", "#1e262b", "#6a7681", "#d8dee2"),
}


def hex_rgb(value):
    value = value.lstrip("#")
    return np.array([int(value[i:i + 2], 16) for i in (0, 2, 4)], np.float32) / 255.0


def disc_mask(height, width):
    """Union of the two iris discs, feathered by two pixels at the rim.

    The feather matters: a hard edge would leave a one-pixel ring of the base
    colour where the recoloured disc meets the untouched background, and at
    portrait distance that ring reads as a bad matte.
    """
    yy, xx = np.mgrid[0:height, 0:width].astype(np.float32)
    mask = np.zeros((height, width), np.float32)
    for cx, cy, radius in IRIS_DISCS:
        d = np.hypot(xx - cx, yy - cy)
        mask = np.maximum(mask, np.clip((radius - d) / 2.0, 0.0, 1.0))
    return mask


def ramp(t, palette):
    """Map t ∈ [0,1] through the 4-stop gradient, linearly between stops."""
    colors = [hex_rgb(c) for c in palette]
    out = np.empty(t.shape + (3,), np.float32)
    out[:] = colors[0]
    for i in range(len(STOPS) - 1):
        lo, hi = STOPS[i], STOPS[i + 1]
        seg = np.clip((t - lo) / (hi - lo), 0.0, 1.0)[..., None]
        out = np.where((t >= lo)[..., None], colors[i] * (1 - seg) + colors[i + 1] * seg, out)
    return out


def build(base_rgb, mask, palette):
    luma = (0.2126 * base_rgb[..., 0] +
            0.7152 * base_rgb[..., 1] +
            0.0722 * base_rgb[..., 2])
    t = np.clip(luma / LUMA_CEILING, 0.0, 1.0)
    tinted = ramp(t, palette)
    m = mask[..., None]
    return np.clip(base_rgb * (1 - m) + tinted * m, 0.0, 1.0)


def asset_guid(path):
    """A stable guid for an asset that Unity has never seen.

    Derived from the path relative to the repo, so re-running this script
    rewrites the same guids and every material that points at a texture keeps
    pointing at it. Unity only cares that a guid is 32 hex digits and unique.
    """
    rel = os.path.relpath(path, REPO).replace(os.sep, "/")
    return hashlib.md5(("hexlive.eyes:" + rel).encode()).hexdigest()


def write_meta(path, template):
    with open(path + ".meta", "w") as handle:
        handle.write(template.format(guid=asset_guid(path)))


def ensure_folder(path):
    os.makedirs(path, exist_ok=True)
    if not os.path.exists(path + ".meta"):
        write_meta(path, META_FOLDER)


def write_materials(texture_guids):
    """Materials: `Eyes/Common/` for the colourless three, `Eyes/<id>/` for the two
    that carry the map. The view merges the two folders by material NAME, the
    same lookup ApplySkinSet already uses (§74.2), so nothing here depends on
    submesh order."""
    ensure_folder(MATERIALS_ROOT)

    ensure_folder(os.path.join(MATERIALS_ROOT, "Common"))
    for name in COMMON_MATERIALS:
        src = os.path.join(DONOR_MATERIALS, name + ".mat")
        dst = os.path.join(MATERIALS_ROOT, "Common", name + ".mat")
        shutil.copyfile(src, dst)
        write_meta(dst, META_MATERIAL)

    for color, guid in texture_guids.items():
        folder = os.path.join(MATERIALS_ROOT, color)
        ensure_folder(folder)
        for name in TINTED_MATERIALS:
            with open(os.path.join(DONOR_MATERIALS, name + ".mat")) as handle:
                body = handle.read()
            if DONOR_EYE_MAP_GUID not in body:
                sys.exit(f"{name}.mat no longer references the donor eye map — "
                         f"the material set moved, fix DONOR_EYE_MAP_GUID")
            body = body.replace(DONOR_EYE_MAP_GUID, guid)
            if name == "Irises":
                body, hits = re.subn(r"- _Metallic: [-\d.]+",
                                     f"- _Metallic: {IRIS_METALLIC}", body)
                if hits != 1:
                    sys.exit(f"Irises.mat has {hits} _Metallic entries, expected 1")
            dst = os.path.join(folder, name + ".mat")
            with open(dst, "w") as handle:
                handle.write(body)
            write_meta(dst, META_MATERIAL)

    print(f"materials   → {os.path.relpath(MATERIALS_ROOT, REPO)}/"
          f"{{Common,{','.join(PALETTES)}}}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--contact", action="store_true",
                        help="also write a preview sheet of every iris")
    parser.add_argument("--out", default=OUT_DIR)
    parser.add_argument("--textures-only", action="store_true",
                        help="skip the material set (preview runs)")
    args = parser.parse_args()

    if not os.path.exists(BASE):
        sys.exit(f"base eye map missing: {BASE}")

    base = np.asarray(Image.open(BASE).convert("RGB"), np.float32) / 255.0
    height, width, _ = base.shape
    mask = disc_mask(height, width)
    install = not args.textures_only and os.path.abspath(args.out) == OUT_DIR
    if install:
        ensure_folder(os.path.dirname(args.out))
        ensure_folder(args.out)
    else:
        os.makedirs(args.out, exist_ok=True)

    previews = []
    texture_guids = {}
    for name, palette in PALETTES.items():
        rgb = build(base, mask, palette)
        image = Image.fromarray((rgb * 255.0 + 0.5).astype(np.uint8), "RGB")
        path = os.path.join(args.out, f"eye_{name}.jpg")
        # JPEG at q95 matches how every Daz map in this project already ships,
        # and the iris is fibre noise — there is no flat gradient to band.
        image.save(path, quality=95, subsampling=0)
        if install:
            write_meta(path, META_TEXTURE)
            texture_guids[name] = asset_guid(path)
        print(f"{name:11s} → {path}  ({os.path.getsize(path) / 1024:.0f} KB)")
        if args.contact:
            previews.append((name, image.crop((55, 1074, 957, 1976)).resize((256, 256))))

    if install:
        write_materials(texture_guids)

    if previews:
        sheet = Image.new("RGB", (256 * len(previews), 256))
        for i, (_, thumb) in enumerate(previews):
            sheet.paste(thumb, (i * 256, 0))
        sheet_path = os.path.join(args.out, "_contact.png")
        sheet.save(sheet_path)
        print(f"contact sheet → {sheet_path}")


if __name__ == "__main__":
    main()
