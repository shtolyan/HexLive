#!/usr/bin/env python3
"""Unity text `.mesh` (YAML) -> Wavefront OBJ (+ MTL), no Unity needed.

Used by the item-icon pipeline (see ICON_GENERATION_SPEC.md) to get a garment
mesh out of `Assets/ImportedActors/Wear/<item>/Meshes/<Actor>.mesh` and into
headless Blender.

    python3 Tools/unity_mesh_to_obj.py <in.mesh> <out.obj> [albedo.jpg]

The albedo argument is optional; when given, an `.mtl` next to the OBJ points
`map_Kd` at it (copy the texture next to the OBJ first — the path is written
verbatim).

Format notes (learned the hard way, both `serializedVersion` 10 and 12 seen):
  * `m_VertexData` holds a channel table — stream / offset / format / dimension.
    Channel 0 = position float3, 1 = normal float3, 2 = tangent, 4 = uv0 float2.
    NEVER assume a stride; add up the channels of each stream.
  * Streams are packed sequentially inside `_typelessdata`, each stream start
    aligned UP to 16 bytes.
  * `m_IndexBuffer` is hex; uint16 when `m_IndexFormat: 0`, else uint32. A
    submesh's `firstByte` is a byte offset into that buffer.
  * Unity is left-handed, OBJ/Blender right-handed -> mirror X and reverse the
    triangle winding, or the mesh renders inside-out.
"""
import os
import re
import struct
import sys

FLOAT32 = 0
COMPONENT_SIZE = 4  # every channel we read is 4 bytes per component


def parse(path):
    txt = open(path, encoding="utf-8", errors="ignore").read()

    subs = []
    sm_block = txt[txt.find("m_SubMeshes:"):txt.find("m_Shapes:")]
    for m in re.finditer(
        r"firstByte: (\d+)\s+indexCount: (\d+)\s+topology: (\d+)\s+baseVertex: (\d+)\s+"
        r"firstVertex: (\d+)\s+vertexCount: (\d+)", sm_block):
        subs.append(dict(firstByte=int(m.group(1)), indexCount=int(m.group(2)),
                         topology=int(m.group(3)), baseVertex=int(m.group(4)),
                         firstVertex=int(m.group(5)), vertexCount=int(m.group(6))))

    vd = txt[txt.find("m_VertexData:"):]
    vcount = int(re.search(r"m_VertexCount: (\d+)", vd).group(1))
    chan_block = vd[vd.find("m_Channels:"):vd.find("m_DataSize:")]
    channels = [dict(stream=int(m.group(1)), offset=int(m.group(2)),
                     format=int(m.group(3)), dim=int(m.group(4)))
                for m in re.finditer(
                    r"stream: (\d+)\s+offset: (\d+)\s+format: (\d+)\s+dimension: (\d+)",
                    chan_block)]
    data = bytes.fromhex(re.search(r"_typelessdata: ([0-9a-f]+)", vd).group(1))

    strides = {}
    for c in channels:
        if c["dim"]:
            strides[c["stream"]] = strides.get(c["stream"], 0) + c["dim"] * COMPONENT_SIZE

    starts, off = {}, 0
    for s in sorted(strides):
        starts[s] = off
        off = (off + strides[s] * vcount + 15) & ~15

    def read(idx, dim):
        c = channels[idx]
        base, stride = starts[c["stream"]] + c["offset"], strides[c["stream"]]
        return [struct.unpack_from("<" + "f" * dim, data, base + i * stride)
                for i in range(vcount)]

    pos = read(0, 3)
    nrm = read(1, 3) if channels[1]["dim"] else None
    uv = read(4, 2) if len(channels) > 4 and channels[4]["dim"] else None

    ibuf = bytes.fromhex(re.search(r"m_IndexBuffer: ([0-9a-f]+)", txt).group(1))
    fmt16 = int(re.search(r"m_IndexFormat: (\d+)", txt).group(1)) == 0
    return subs, pos, nrm, uv, ibuf, fmt16


def write_obj(path, subs, pos, nrm, uv, ibuf, fmt16, mtl=None):
    with open(path, "w") as f:
        if mtl:
            f.write(f"mtllib {mtl}\n")
        f.write("o item\n")
        for p in pos:
            f.write(f"v {-p[0]:.6f} {p[1]:.6f} {p[2]:.6f}\n")  # mirrored X
        for t in uv or ():
            f.write(f"vt {t[0]:.6f} {t[1]:.6f}\n")
        for n in nrm or ():
            f.write(f"vn {-n[0]:.6f} {n[1]:.6f} {n[2]:.6f}\n")
        if mtl:
            f.write("usemtl mat\n")
        step, code = (2, "<H") if fmt16 else (4, "<I")
        for s in subs:
            for k in range(0, s["indexCount"], 3):
                tri = [struct.unpack_from(code, ibuf, s["firstByte"] + (k + j) * step)[0]
                       + s["baseVertex"] + 1 for j in range(3)]
                a, b, c = tri[2], tri[1], tri[0]  # reversed winding for the mirror
                if uv and nrm:
                    f.write(f"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}\n")
                elif nrm:
                    f.write(f"f {a}//{a} {b}//{b} {c}//{c}\n")
                else:
                    f.write(f"f {a} {b} {c}\n")


def main():
    src, dst = sys.argv[1], sys.argv[2]
    tex = sys.argv[3] if len(sys.argv) > 3 else None
    subs, pos, nrm, uv, ibuf, fmt16 = parse(src)
    mtl = None
    if tex:
        mtl = os.path.basename(dst).replace(".obj", ".mtl")
        with open(os.path.join(os.path.dirname(dst) or ".", mtl), "w") as f:
            f.write("newmtl mat\nKa 0 0 0\nKd 1 1 1\nKs 0 0 0\nd 1\nillum 2\n")
            f.write(f"map_Kd {tex}\n")
    write_obj(dst, subs, pos, nrm, uv, ibuf, fmt16, mtl)
    print(f"{dst}: {len(pos)} verts, {sum(s['indexCount'] for s in subs) // 3} tris, "
          f"uv={'yes' if uv else 'NO'}")


if __name__ == "__main__":
    main()
