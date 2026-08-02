"""Heeled shoes: recognise them, and work out the foot pose they need.

A heeled shoe is not just a mesh (spec §31B.4C, `HEEL_POSE_SPEC.md`). In DAZ the
product comes with a foot POSE — the heel lifts, the toes bend back — and the
shoe is modelled around that posed foot. Import only the mesh and the girl
stands flat inside a shoe shaped for a raised heel, with her foot poking through
the sole.

Unity already knows what to do with the numbers: `HeelPose` on the `Wear`
component, applied every LateUpdate by `BodyBones`, which pitches lFoot/rFoot
and lToe/rToe and lifts the hip so she does not sink into the ground. What was
missing was anyone to WRITE them — nothing in this package mentioned heels, so
every shoe came out flat-footed. A new pair of heels still needs no C#.

## Recognising one, for free

The export already proves it. Our dressing stage fits the shoe to a girl
standing FLAT, so a shoe modelled for a raised heel reaches below the floor by
exactly the heel height — `y` in these exports is centimetres above the ground
(the same axis `manifest._zone` reads). That is the "foot pokes through the
sole" failure seen from the other side, and it is a measurement:

    heel height = −min(y) over the shoe's vertices

No DAZ round-trip, no product conventions, nothing to install. A flat shoe dips
a centimetre or two for its sole and stays under the threshold.

## Turning a height into a pose

The measured drop IS the `lift` the manifest wants. `HEEL_POSE_SPEC.md` §2
relates lift to angle as `lift = 0.11 · sin(foot°)`, 0.11 m being heel-to-ball
on Genesis 3 Female — so this simply inverts it:

    footDegrees = asin(heelHeight / 0.11)

Both shipped shoes fall out of it exactly: Flair pumps lift 0.078 m → 45.0°,
Cindy boots 0.052 m → 28.2°, which are their tuned values. The toes bend back
by the same amount, keeping the ball of the foot flat on the floor (Cindy:
−27.9° against +28.2°; Flair rounds its toe to −40°).

If the product DOES ship a foot-pose preset, that is the authored answer and
beats any measurement — so it is tried first. Note that a preset keying the foot
to ZERO is a reset, not a pose: Charlotte High Heels does exactly that, and
ships no pose file at all, which is why the measurement path is not a nicety.

Everything here is a PROPOSAL, like the inferred slot list beside it. The axis
stays data because an FBX import can permute a bone's local axes, and the last
word is a live look in WardrobeTest.
"""
from __future__ import annotations

import gzip
import json
import math
from pathlib import Path
from urllib.parse import unquote

# Heel-to-ball lever, metres — what turns a heel height into an angle. NOT a
# guess: HEEL_POSE_SPEC.md §2 states it, and both shipped shoes fall out of it
# exactly (Flair 0.078 m / sin 45° = 0.110; Cindy 0.052 / sin 28.2° = 0.110).
ANKLE_TO_BALL = 0.110

# A sole has thickness, so even flats reach a little below the floor. Below this
# the shoe is flat enough that posing the foot would look worse than leaving it.
MIN_HEEL_CM = 2.5

# Past this a "heel" is a stylised prop rather than a shoe — Flair pumps at 45°
# are the steepest thing the project has. A silently absurd pose is worse than
# none, so this reports instead of guessing.
MAX_FOOT_DEGREES = 55.0

FOOT_SLOTS = ("FootR", "FootL")

_FOOT_CHANNELS = ("lFoot", "rFoot")
_TOE_CHANNELS = ("lToe", "rToe")


# --- the authored answer, when there is one ---------------------------------

def _read_duf(path: Path) -> dict | None:
    """DAZ files are JSON, sometimes gzipped, sometimes not."""
    try:
        raw = path.read_bytes()
    except OSError:
        return None
    if raw[:2] == b"\x1f\x8b":
        try:
            raw = gzip.decompress(raw)
        except OSError:
            return None
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None


def _rotations(doc: dict) -> dict[str, float]:
    """Bone -> X rotation, out of a DAZ file's animation block.

    That block is where a preset stores a pose: one entry per channel, keyed
    `name://@selection/lFoot:?rotation/x/value`, value in the first key.
    """
    out: dict[str, float] = {}
    for entry in doc.get("scene", {}).get("animations", []) or []:
        url = unquote(str(entry.get("url", "")))
        if ":?rotation/x" not in url:
            continue
        bone = url.split("/")[-1].split(":")[0]
        keys = entry.get("keys") or []
        if keys and len(keys[0]) > 1:
            try:
                out[bone] = float(keys[0][1])
            except (TypeError, ValueError):
                continue
    return out


def from_pose_preset(product_root: Path) -> dict | None:
    """The authored pose, if this product ships one.

    Scans the product's `.duf` files for one that actually TURNS the foot. A
    file keying the foot to zero is a reset, not a pose.
    """
    for path in sorted(product_root.rglob("*.duf")):
        doc = _read_duf(path)
        if not doc:
            continue
        rotations = _rotations(doc)
        foot = next((rotations[b] for b in _FOOT_CHANNELS
                     if abs(rotations.get(b, 0.0)) > 1.0), None)
        if foot is None:
            continue
        toe = next((rotations[b] for b in _TOE_CHANNELS if b in rotations), -foot)
        return {
            "foot": round(foot, 2),
            "toe": round(toe, 2),
            "lift": round(ANKLE_TO_BALL * math.sin(math.radians(abs(foot))), 4),
            "axis": [1.0, 0.0, 0.0],
            "_source": f"поза из набора: {path.name}",
        }
    return None


# --- the measurement, which always works ------------------------------------

def heel_drop_cm(points) -> float:
    """How far below the floor the shoe reaches — the heel height, in cm."""
    if not points:
        return 0.0
    return max(0.0, -min(y for _, y, _ in points))


def from_heel_height(centimetres: float) -> dict | None:
    """The pose implied by a measured heel height."""
    if centimetres < MIN_HEEL_CM:
        return None

    metres = centimetres / 100.0
    ratio = min(metres / ANKLE_TO_BALL, 1.0)
    foot = math.degrees(math.asin(ratio))
    if foot > MAX_FOOT_DEGREES:
        return {
            "_review": f"каблук {centimetres:.1f} см даёт {foot:.0f}° — круче, "
                       "чем бывает у обуви; проверьте замер, прежде чем верить",
            "foot": round(min(foot, MAX_FOOT_DEGREES), 2),
            "toe": round(-min(foot, MAX_FOOT_DEGREES), 2),
            "lift": round(metres, 4),
            "axis": [1.0, 0.0, 0.0],
            "_source": f"по высоте каблука {centimetres:.1f} см (ОБРЕЗАНО)",
        }

    return {
        "foot": round(foot, 2),
        # The toes bend back the same amount, keeping the ball flat on the
        # floor. Both tuned shoes do this to within a few degrees.
        "toe": round(-foot, 2),
        "lift": round(metres, 4),
        "axis": [1.0, 0.0, 0.0],
        "_source": f"по высоте каблука {centimetres:.1f} см",
    }


def propose(geometry, slots: list[str],
            product_root: Path | None = None) -> tuple[dict | None, float]:
    """Heel pose for a garment, plus the heel height that was measured.

    Returns (pose or None, heel height in cm) so the caller can record the
    evidence even when the shoe turns out to be flat.
    """
    if not any(slot in slots for slot in FOOT_SLOTS):
        return None, 0.0   # not footwear at all

    drop = heel_drop_cm(getattr(geometry, "points", None))

    if product_root is not None:
        authored = from_pose_preset(product_root)
        if authored:
            return authored, drop

    return from_heel_height(drop), drop
