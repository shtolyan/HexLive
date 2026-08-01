"""Every machine-specific path the wardrobe pipeline needs, in one place.

Defaults describe the workstation this pipeline was built on; each one can be
overridden with an environment variable so the scripts stay portable. Nothing
here reads a secret — the Telegram token lives in `tools/wardrobe/.env`, which
is gitignored.
"""
from __future__ import annotations

import os
from pathlib import Path


def _path(env: str, default: str) -> Path:
    return Path(os.environ.get(env, default))


# --- the game ---------------------------------------------------------------
PROJECT = _path("HEXLIVE_PROJECT", r"C:\Users\shtolyan\Documents\GitHub\HexLive")
ASSETS = PROJECT / "Assets"
# DAZ FBX drops land here. Gitignored — the drops are big and regenerable.
DROP_DIR = ASSETS / "Temp"
WEAR_IMPORT = ASSETS / "ImportedActors" / "Wear"
WEAR_PREFABS = ASSETS / "Resources" / "HexLive" / "Wear"
# The JSON drop manifests the Unity-side extractor reads (see manifest.py).
DROP_MANIFESTS = ASSETS / "Editor" / "WearDrops"
PREVIEWS = ASSETS / "Resources" / "HexLive" / "WearPreviews"

# --- DAZ Studio -------------------------------------------------------------
DAZ_LIBRARY = _path(
    "DAZ_LIBRARY", r"C:\Users\shtolyan\Documents\DAZ 3D\Studio\My Library")
DAZ_TEXTURES = DAZ_LIBRARY / "Runtime" / "Textures"
DAZ_SCENES = DAZ_LIBRARY / "Scenes"
DAZ_HOST = os.environ.get("DAZ_HOST", "127.0.0.1")
DAZ_PORT = int(os.environ.get("DAZ_PORT", "18811"))
DAZ_TOKEN_FILE = Path(os.path.expanduser("~")) / ".daz3d" / "dazscriptserver_token.txt"

# --- external tools ---------------------------------------------------------
BLENDER = _path(
    "BLENDER_EXE", r"C:\Users\shtolyan\AppData\Local\Programs\Blender\blender.exe")
WINRAR = _path("WINRAR_EXE", r"C:\Program Files\WinRAR\WinRAR.exe")
UNITY = _path(
    "UNITY_EXE", r"C:\Program Files\Unity\Hub\Editor\6000.4.5f1\Editor\Unity.exe")

# --- working dirs -----------------------------------------------------------
WORK = _path("WARDROBE_WORK", str(Path(os.environ.get("TEMP", "/tmp")) / "wardrobe"))
DOWNLOADS = WORK / "downloads"
UNPACKED = WORK / "unpacked"
REPORTS = WORK / "reports"

# --- the cast ---------------------------------------------------------------
# ActorName enum order is a frozen serialization contract (ActorWearTypes.cs);
# the ints are what WearConfig stores, so never renumber these.
ACTOR_IDS = {"Molly": 0, "Jolly": 1, "Marta": 2, "Jana": 5}

# Each girl's base scene. All four are Genesis 3 Female. The scenes still carry
# the previous drop's garments — `dress` strips every follower before fitting,
# so that leftover state does not matter. Note the DOUBLE space in three names:
# that is genuinely how the files are called on disk.
GIRL_SCENES = {
    "Jolly": DAZ_SCENES / "Jolly new.duf",
    "Jana": DAZ_SCENES / "jana  new.duf",
    "Marta": DAZ_SCENES / "marta  new.duf",
    "Molly": DAZ_SCENES / "molly  new.duf",
}

FIGURE_LABEL = "Genesis 3 Female"

# Unity caps wear textures at 2048, so there is no point committing 4K source
# art — we downscale on the way in.
MAX_TEXTURE = int(os.environ.get("WARDROBE_MAX_TEXTURE", "2048"))


def girl_names() -> list[str]:
    return list(GIRL_SCENES)


def check(*required: str) -> list[str]:
    """Return human-readable problems for the named config entries."""
    problems = []
    for name in required:
        value = globals().get(name)
        if isinstance(value, Path) and not value.exists():
            problems.append(f"{name} не найден: {value}")
    return problems
