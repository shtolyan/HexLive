"""Validate the shared HexLive furniture FBX coordinate contract.

Run through Blender, for example:
  blender -b --python verify_furniture_fbx.py -- --fbx path/to/model.fbx
"""

from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

import bpy


EPSILON = 1e-4


def near(value: float, expected: float, tolerance: float = EPSILON) -> bool:
    return abs(value - expected) <= tolerance


def vector_near(values, expected, tolerance: float = EPSILON) -> bool:
    return all(near(float(value), float(target), tolerance)
               for value, target in zip(values, expected))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fbx", required=True)
    parser.add_argument("--profile", choices=("standing", "low", "any"), default="any")
    parser.add_argument("--floor-tolerance", type=float, default=0.10)
    parser.add_argument("--center-tolerance", type=float, default=0.10)
    parser.add_argument("--allow-root-scale", action="store_true",
                        help="Legacy inspection only; never use for a new export.")
    return parser.parse_args(sys.argv[sys.argv.index("--") + 1:])


def fail(errors: list[str], message: str) -> None:
    errors.append(message)


def main() -> int:
    args = parse_args()
    source = Path(args.fbx).resolve()
    if not source.is_file():
        print(f"FAIL: FBX not found: {source}")
        return 2

    header = source.read_bytes()[:64]
    if header.startswith(b"version https://git-lfs.github.com/spec/v1"):
        print(f"FAIL: {source} is a Git LFS pointer, not imported FBX content")
        print("Restore the LFS object or re-export the asset before opening Unity.")
        return 2
    if not header.startswith(b"Kaydara FBX Binary"):
        print(f"FAIL: {source} is not a supported binary FBX")
        return 2

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.fbx(filepath=str(source))

    roots = [obj for obj in bpy.context.scene.objects if obj.parent is None]
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    vertices = [obj.matrix_world @ vertex.co for obj in meshes for vertex in obj.data.vertices]
    errors: list[str] = []

    if len(roots) != 1:
        fail(errors, f"expected one code-facing root, found {len(roots)}")
    if not meshes or not vertices:
        fail(errors, "no renderable mesh vertices")

    for root in roots:
        if not vector_near(root.location, (0.0, 0.0, 0.0)):
            fail(errors, f"root {root.name!r} location is not zero: {tuple(root.location)}")
        if not vector_near(root.rotation_euler, (0.0, 0.0, 0.0)):
            degrees = tuple(round(math.degrees(value), 4) for value in root.rotation_euler)
            fail(errors, f"root {root.name!r} rotation is not zero: {degrees} degrees")
        if not args.allow_root_scale and not vector_near(root.scale, (1.0, 1.0, 1.0)):
            fail(errors, f"root {root.name!r} scale is not one: {tuple(root.scale)}")

    if vertices:
        low = [min(vertex[index] for vertex in vertices) for index in range(3)]
        high = [max(vertex[index] for vertex in vertices) for index in range(3)]
        size = [high[index] - low[index] for index in range(3)]
        center = [(low[index] + high[index]) * 0.5 for index in range(3)]

        if low[2] < -args.floor_tolerance or low[2] > args.floor_tolerance:
            fail(errors, f"floor pivot mismatch: lowest Z={low[2]:.6f} wu")
        if abs(center[1]) > args.center_tolerance:
            fail(errors, f"primary +Y axis is not centered on pivot: center Y={center[1]:.6f} wu")
        if size[1] + EPSILON < size[0]:
            fail(errors, f"primary footprint axis must be local +Y: X={size[0]:.6f}, Y={size[1]:.6f}")
        if args.profile == "standing" and size[2] <= max(size[0], size[1]):
            fail(errors, f"standing furniture is not Z-up: size={tuple(round(v, 6) for v in size)}")
        if args.profile == "low" and size[2] >= max(size[0], size[1]):
            fail(errors, f"low furniture unexpectedly stands on end: size={tuple(round(v, 6) for v in size)}")

        print(f"FBX: {source}")
        print("bounds min:", " ".join(f"{value:.6f}" for value in low))
        print("bounds max:", " ".join(f"{value:.6f}" for value in high))
        print("size wu:   ", " ".join(f"{value:.6f}" for value in size))
        print(f"roots={len(roots)} meshes={len(meshes)} vertices={len(vertices)} profile={args.profile}")

    if errors:
        for error in errors:
            print(f"FAIL: {error}")
        return 1

    print("PASS: shared HexLive furniture coordinate contract")
    return 0


raise SystemExit(main())
