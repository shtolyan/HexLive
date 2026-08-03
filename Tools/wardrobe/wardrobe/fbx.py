"""Binary-FBX reader — just enough to inspect a DAZ export without Unity.

Two questions this answers, both of which the pipeline needs before Unity has
ever seen the file:

  * `geometries()` — which garment meshes are in there, how heavy they are, and
    what volume they occupy. The bounding box is how we infer wear slots: a
    mesh spanning y 9..115 cm is leggings, y 123..154 is a cropped top.
  * `material_textures()` — which image each surface actually wants. DAZ never
    embeds textures (the FBX points back into My Library), so this mapping is
    the only reliable source for "material X uses SJSkirt4.jpg, and its cutout
    opacity comes from SJSkirt_T1.jpg".

Format notes: arrays may be deflate-compressed; a negative polygon index marks
the last vertex of a polygon; object names are stored as "name\\x00\\x01Class".
"""
from __future__ import annotations

import collections
import struct
import zlib
from dataclasses import dataclass, field
from pathlib import Path

_SCALARS = {"Y": ("h", 2), "C": ("B", 1), "I": ("i", 4),
            "F": ("f", 4), "D": ("d", 8), "L": ("q", 8)}
_ARRAYS = {"f": "f", "d": "d", "l": "q", "i": "i", "b": "b", "c": "B"}


@dataclass
class Geometry:
    name: str
    verts: int = 0
    polys: int = 0
    bbox: tuple[float, ...] | None = None  # (minx, maxx, miny, maxy, minz, maxz)
    # A sample of the actual vertex positions as (x, y, z) triples. The bounding
    # box alone is a poor description of a garment — a flared skirt's box reaches
    # out to the shoulders, and a PAIR of sleeves has a box spanning the empty
    # chest between them — so slot inference works off the point cloud instead.
    points: list[tuple[float, float, float]] = field(default_factory=list)

    @property
    def tris(self) -> int:
        """Quads dominate DAZ meshes, so this is an estimate, not a promise."""
        return self.polys * 2

    @property
    def height(self) -> float:
        return (self.bbox[3] - self.bbox[2]) if self.bbox else 0.0


@dataclass
class _Node:
    name: str
    props: list = field(default_factory=list)


def _read_props(buf: bytes, pos: int, count: int):
    props = []
    for _ in range(count):
        code = chr(buf[pos]); pos += 1
        if code in _SCALARS:
            fmt, size = _SCALARS[code]
            props.append(struct.unpack_from("<" + fmt, buf, pos)[0]); pos += size
        elif code in "SR":
            n = struct.unpack_from("<I", buf, pos)[0]; pos += 4
            props.append(buf[pos:pos + n]); pos += n
        elif code in _ARRAYS:
            n, encoding, length = struct.unpack_from("<III", buf, pos); pos += 12
            raw = buf[pos:pos + length]; pos += length
            if encoding == 1:
                raw = zlib.decompress(raw)
            props.append(("ARR", _ARRAYS[code], n, raw))
        else:
            raise ValueError(f"неизвестный тип свойства {code!r} на позиции {pos}")
    return props, pos


def _parse(buf: bytes, pos: int, end: int, version: int, out: list) -> int:
    while pos < end:
        if version >= 7500:
            end_offset, nprops, _ = struct.unpack_from("<QQQ", buf, pos); pos += 24
        else:
            end_offset, nprops, _ = struct.unpack_from("<III", buf, pos); pos += 12
        name_len = buf[pos]; pos += 1
        if end_offset == 0:
            return pos
        name = buf[pos:pos + name_len].decode("utf8", "replace"); pos += name_len
        props, pos = _read_props(buf, pos, nprops)
        out.append(_Node(name, props))
        if pos < end_offset:
            pos = _parse(buf, pos, end_offset, version, out)
        pos = end_offset
    return pos


def _unpack(prop):
    if isinstance(prop, tuple) and prop and prop[0] == "ARR":
        _, fmt, count, raw = prop
        size = struct.calcsize(fmt)
        return struct.unpack(f"<{count}{fmt}", raw[:count * size])
    return None


def _text(prop) -> str:
    return prop.decode("utf8", "replace").split("\x00")[0] if isinstance(prop, bytes) else str(prop)


def _nodes(path: Path) -> tuple[list[_Node], int]:
    buf = Path(path).read_bytes()
    version = struct.unpack_from("<I", buf, 23)[0]
    out: list[_Node] = []
    _parse(buf, 27, len(buf), version, out)
    return out, version


def _catalog(nodes: list[_Node]) -> tuple[dict, dict]:
    """`{uid: (class, name)}` plus `{model uid: local translation}`."""
    names: dict = {}
    local: dict = {}
    current = None
    for node in nodes:
        if node.name in ("Geometry", "Model", "Pose") and len(node.props) >= 3:
            current = node.props[0]
            names[current] = (node.name, _text(node.props[1]))
        elif (node.name == "P" and len(node.props) >= 7
              and names.get(current, ("",))[0] == "Model"
              and _text(node.props[0]) == "Lcl Translation"):
            local[current] = tuple(float(v) for v in node.props[4:7])
    return names, local


def _bind_world(nodes: list[_Node]) -> dict:
    """`{model uid: world position}`, read from the export's bind pose.

    A `PoseNode` block is a node id followed by its 4×4 matrix, and the bind
    pose is absolute — so the last row is where that bone stands on the figure.
    """
    world: dict = {}
    current = None
    for node in nodes:
        if node.name == "PoseNode":
            current = None
        elif node.name == "Node" and node.props:
            current = node.props[0]
        elif node.name == "Matrix" and current is not None:
            matrix = _unpack(node.props[0])
            if matrix and len(matrix) >= 15:
                world.setdefault(current, (matrix[12], matrix[13], matrix[14]))
            current = None
    return world


def _placements(nodes: list[_Node]) -> dict[str, tuple[float, float, float]]:
    """Where each mesh's vertices actually sit on the figure.

    A conforming garment is exported in the figure's own space and needs
    nothing. An accessory PARENTED to a bone — glasses, a bowtie, the buttons
    on a pair of suspenders — is not: its vertices are local to its own node,
    so measured raw it sits at the floor. That read as `очки: Foot 100%` in a
    drafted manifest, which is worse than no guess at all, because the zone
    table is the evidence a reviewer is supposed to check the guess against.

    The bone's place comes from the bind pose, which is absolute; the prop's
    offset from it is the chain of `Lcl Translation` in between. Rotation is
    deliberately ignored — this feeds the anatomical zones, which are coarse,
    and nothing hangs off a bone at an angle wide enough to change one.
    """
    names, local = _catalog(nodes)
    world = _bind_world(nodes)

    parent: dict = {}
    model_of: dict[str, int] = {}
    for c in nodes:
        if c.name != "C" or len(c.props) < 3:
            continue
        src, dst = c.props[1], c.props[2]
        kinds = (names.get(src, ("",))[0], names.get(dst, ("",))[0])
        if kinds == ("Model", "Model"):
            parent[src] = dst
        elif kinds == ("Geometry", "Model"):
            model_of[names[src][1]] = dst

    placements: dict[str, tuple[float, float, float]] = {}
    for mesh, uid in model_of.items():
        offset = [0.0, 0.0, 0.0]
        seen: set = set()
        while uid is not None and uid not in seen:
            seen.add(uid)
            if uid in world:  # a bone: absolute, so the walk ends here
                anchor = world[uid]
                offset = [offset[i] + anchor[i] for i in range(3)]
                break
            step = local.get(uid, (0.0, 0.0, 0.0))
            offset = [offset[i] + step[i] for i in range(3)]
            uid = parent.get(uid)
        placements[mesh] = tuple(offset)
    return placements


def geometries(path: Path, sample: int = 20000) -> dict[str, Geometry]:
    """Every mesh in the file, keyed by its DAZ node name.

    `sample` caps how many vertex positions are kept per mesh — enough for the
    shape statistics slot inference needs, without holding a 50 K-vertex pair
    of leggings in memory for every garment in the drop.
    """
    nodes = _nodes(path)[0]
    placements = _placements(nodes)

    result: dict[str, Geometry] = {}
    current: Geometry | None = None
    for node in nodes:
        if node.name == "Geometry" and len(node.props) > 1:
            current = Geometry(_text(node.props[1]))
            result[current.name] = current
        elif node.name == "Model":
            current = None
        elif current is None:
            continue
        elif node.name == "Vertices":
            values = _unpack(node.props[0])
            if values:
                dx, dy, dz = placements.get(current.name, (0.0, 0.0, 0.0))
                current.verts = len(values) // 3
                xs = [v + dx for v in values[0::3]]
                ys = [v + dy for v in values[1::3]]
                zs = [v + dz for v in values[2::3]]
                current.bbox = (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs))
                step = max(1, current.verts // sample) if sample else 1
                current.points = list(zip(xs[::step], ys[::step], zs[::step]))
        elif node.name == "PolygonVertexIndex":
            values = _unpack(node.props[0])
            if values:
                current.polys = sum(1 for i in values if i < 0)
    return result


def material_colors(path: Path) -> dict[str, dict[str, tuple[float, float, float]]]:
    """{model: {material: diffuse colour}} — the colour DAZ set, not a texture.

    Some surfaces ship no albedo map at all: the torn stockings are one flat
    black, and the export says so with `DiffuseColor 0 0 0` and no image. Read
    only in a colour, they came into the game pure white, because white is what
    a material defaults to and nothing had ever contradicted it.
    """
    nodes = _nodes(path)[0]
    names: dict[int, tuple[str, str]] = {}
    colors: dict[int, tuple[float, float, float]] = {}
    current: int | None = None

    for node in nodes:
        if node.name in ("Geometry", "Model", "Material") and len(node.props) >= 3:
            current = node.props[0] if node.name == "Material" else None
            names[node.props[0]] = (node.name, _text(node.props[1]))
        elif (node.name == "P" and current is not None and len(node.props) >= 7
              and _text(node.props[0]) == "DiffuseColor"):
            # First writer wins: DAZ emits `DiffuseColor` and then the legacy
            # `Diffuse` alias with the same value.
            colors.setdefault(current, tuple(float(v) for v in node.props[4:7]))

    result: dict[str, dict[str, tuple[float, float, float]]] = {}
    for c in nodes:
        if c.name != "C" or len(c.props) < 3:
            continue
        src, dst = c.props[1], c.props[2]
        if names.get(src, ("",))[0] != "Material" or names.get(dst, ("",))[0] != "Model":
            continue
        model = names[dst][1]
        model = model[:-6] if model.endswith(".Shape") else model
        if src in colors:
            result.setdefault(model, {})[names[src][1]] = colors[src]
    return result


def material_textures(path: Path) -> dict[str, dict[str, dict[str, str]]]:
    """{model: {material: {channel: absolute texture path}}}.

    Channels seen from DAZ: `DiffuseColor` (albedo) and `TransparentColor`
    (cutout opacity, shipped as a SEPARATE greyscale image that has to be
    composited into the albedo's alpha before Unity can alpha-clip it).
    """
    nodes = _nodes(path)[0]
    names: dict[int, tuple[str, str]] = {}
    files: dict[int, str] = {}
    current_texture: int | None = None

    for node in nodes:
        if node.name in ("Geometry", "Model", "Material", "Texture", "Video") and len(node.props) >= 3:
            uid = node.props[0]
            names[uid] = (node.name, _text(node.props[1]))
            current_texture = uid if node.name in ("Texture", "Video") else None
        elif node.name in ("RelativeFilename", "FileName") and current_texture is not None:
            files.setdefault(current_texture, _text(node.props[0]))

    connections = [(c.props[1], c.props[2], _text(c.props[3]) if len(c.props) > 3 else "")
                   for c in nodes if c.name == "C" and len(c.props) >= 3]
    children = collections.defaultdict(list)
    for src, dst, _ in connections:
        children[dst].append(src)

    def kind(uid) -> str:
        return names.get(uid, ("", ""))[0]

    per_material: dict[int, dict[str, str]] = collections.defaultdict(dict)
    for src, dst, channel in connections:
        if kind(src) == "Texture" and kind(dst) == "Material":
            image = files.get(src)
            if image is None:  # the path may hang off a child Video node
                image = next((files[c] for c in children[src] if c in files), None)
            if image:
                per_material[dst][channel] = image

    result: dict[str, dict[str, dict[str, str]]] = {}
    for src, dst, _ in connections:
        if kind(src) == "Material" and kind(dst) == "Model":
            model = names[dst][1]
            # DAZ exports the mesh node as "<garment>.Shape"; the garment name
            # is what every other stage keys on.
            model = model[:-6] if model.endswith(".Shape") else model
            result.setdefault(model, {})[names[src][1]] = dict(per_material.get(src, {}))
    return result
