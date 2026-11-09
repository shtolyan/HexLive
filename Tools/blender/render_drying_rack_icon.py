"""Render the real completed rack with the shared inventory photographer.

HangerTemplate is a clone source hidden by BedAssembly, not an occupied slot.
All camera, material and lighting settings belong to render_item_icon.py.
Pass that renderer's ordinary FBX/output/--install arguments after --.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "rack_icon_photographer", Path(__file__).resolve().parents[1] / "render_item_icon.py")
photographer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(photographer)
load = photographer.load

def completed_rack(source):
    meshes = load(source)
    visible = []
    for mesh in meshes:
        owner = mesh
        while owner is not None and owner.name != "HangerTemplate":
            owner = owner.parent
        mesh.hide_render = owner is not None
        if owner is None:
            visible.append(mesh)
    return visible

photographer.load = completed_rack
parse_args = photographer.parse_args
def absolute_args():
    source, destination, options = parse_args()
    return str(Path(source).resolve()), str(Path(destination).resolve()), options
photographer.parse_args = absolute_args
photographer.main()
