"""Parse a Unity text .mesh (serializedVersion 11/12) into positions/uv0/indices.

Streams inside `_typelessdata` are packed sequentially, each stream start
aligned to 16 bytes. Channel table gives stream/offset/format/dimension.
"""
import re
import numpy as np

FMT_SIZE = {0: 4, 1: 2, 2: 1, 3: 1, 4: 2, 10: 4, 11: 4, 12: 2, 13: 2}  # float32, float16, unorm8, ...


def _hex_field(text, key):
    """Grab a long single-line hex blob field value."""
    m = re.search(r'^\s*' + key + r':\s*([0-9a-fA-F]*)\s*$', text, re.M)
    return bytes.fromhex(m.group(1)) if m else b''


def parse_mesh(path):
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        text = f.read()

    name = re.search(r'^\s*m_Name:\s*(.*)$', text, re.M).group(1).strip()

    # --- submeshes ---
    submeshes = []
    for blk in re.finditer(
        r'-\s+serializedVersion:\s*2\s*\n\s*firstByte:\s*(\d+)\s*\n\s*indexCount:\s*(\d+)\s*\n'
        r'\s*topology:\s*(\d+)\s*\n\s*baseVertex:\s*(\d+)\s*\n\s*firstVertex:\s*(\d+)\s*\n'
        r'\s*vertexCount:\s*(\d+)', text):
        submeshes.append(dict(firstByte=int(blk.group(1)), indexCount=int(blk.group(2)),
                              baseVertex=int(blk.group(4)), firstVertex=int(blk.group(5)),
                              vertexCount=int(blk.group(6))))

    vcount = int(re.search(r'm_VertexCount:\s*(\d+)', text).group(1))
    index_format = int(re.search(r'm_IndexFormat:\s*(\d+)', text).group(1))

    # --- channel table ---
    chan_block = text.split('m_Channels:', 1)[1].split('m_DataSize:', 1)[0]
    channels = []
    for c in re.finditer(r'-\s+stream:\s*(\d+)\s*\n\s*offset:\s*(\d+)\s*\n\s*format:\s*(\d+)\s*\n\s*dimension:\s*(\d+)', chan_block):
        channels.append(dict(stream=int(c.group(1)), offset=int(c.group(2)),
                             format=int(c.group(3)), dim=int(c.group(4))))

    data = _hex_field(text, '_typelessdata')

    # stride per stream
    strides = {}
    for ch in channels:
        if ch['dim'] == 0:
            continue
        end = ch['offset'] + FMT_SIZE[ch['format']] * ch['dim']
        strides[ch['stream']] = max(strides.get(ch['stream'], 0), end)

    # stream start offsets, 16-byte aligned
    starts, cursor = {}, 0
    for s in sorted(strides):
        starts[s] = cursor
        cursor += strides[s] * vcount
        cursor = (cursor + 15) & ~15

    def read_channel(idx):
        ch = channels[idx]
        if ch['dim'] == 0:
            return None
        assert ch['format'] == 0, f'channel {idx}: only float32 supported, got {ch["format"]}'
        base = starts[ch['stream']] + ch['offset']
        stride = strides[ch['stream']]
        out = np.empty((vcount, ch['dim']), dtype=np.float32)
        raw = np.frombuffer(data, dtype=np.uint8)
        for d in range(ch['dim']):
            off = base + d * 4
            byts = np.lib.stride_tricks.as_strided(
                raw[off:], shape=(vcount, 4), strides=(stride, 1)).copy()
            out[:, d] = byts.view(np.float32).ravel()
        return out

    pos = read_channel(0)
    nrm = read_channel(1)
    uv0 = read_channel(4)

    idx_raw = _hex_field(text, 'm_IndexBuffer')
    dt = np.uint16 if index_format == 0 else np.uint32
    indices = np.frombuffer(idx_raw, dtype=dt).astype(np.int64)

    return dict(name=name, pos=pos, nrm=nrm, uv0=uv0, indices=indices,
                submeshes=submeshes, vcount=vcount, data_size=len(data))


if __name__ == '__main__':
    import sys
    m = parse_mesh(sys.argv[1])
    print(f"{m['name']}: {m['vcount']} verts, {len(m['indices'])} indices, data {m['data_size']}")
    print('pos min', m['pos'].min(0), 'max', m['pos'].max(0))
    print('uv0 min', m['uv0'].min(0), 'max', m['uv0'].max(0))
    for i, sm in enumerate(m['submeshes']):
        v0, vc = sm['firstVertex'], sm['vertexCount']
        p = m['pos'][v0:v0 + vc]
        u = m['uv0'][v0:v0 + vc]
        print(f"  submesh {i}: verts {v0}..{v0+vc-1}  y {p[:,1].min():.4f}..{p[:,1].max():.4f}"
              f"  uv x {u[:,0].min():.4f}..{u[:,0].max():.4f} y {u[:,1].min():.4f}..{u[:,1].max():.4f}")
