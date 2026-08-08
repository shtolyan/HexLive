"""Extract embedded GLB textures needed by native Player prefabs.

Run with Blender:
  blender --background --python Tools/extract_world_prop_textures.py -- \
    --repo <repo> --id tool.machete
"""
from pathlib import Path
import argparse
import sys
import bpy


TARGETS = {
    "tool.bottle": ("tool.bottle.glb", "tool_bottle_albedo.png"),
    "tool.machete": ("tool.machete.glb", "tool_machete_albedo.png"),
}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--id", action="append", dest="ids", required=True,
                        choices=sorted(TARGETS))
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    for entry_id in args.ids:
        source_name, target_name = TARGETS[entry_id]
        source = args.repo / "Assets/ArtSource/WorldProps" / source_name
        target = args.repo / "Assets/Resources/HexLive/Objects" / target_name
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.gltf(filepath=str(source))
        images = [image for image in bpy.data.images if image.name != "Render Result"]
        if len(images) != 1:
            raise RuntimeError(f"Expected one {entry_id} image, found {len(images)}")
        target.parent.mkdir(parents=True, exist_ok=True)
        images[0].save_render(str(target))
        if not target.exists() or target.stat().st_size == 0:
            raise RuntimeError(f"{entry_id} albedo extraction produced no PNG")
        print(f"[world-prop-texture] {source.name} -> {target.name} ({target.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
