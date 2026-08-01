# HexLive — Heeled Shoes & the Raised Heel (spec §31B.4C)

How a heeled shoe gets its foot pose from DAZ into the game. Companion to
`ICON_GENERATION_SPEC.md` and `Tools/wardrobe/README.md`.

---

## 0. The problem, in one paragraph

**A heeled shoe in DAZ is not just a mesh.** The product ships a *foot pose*
preset that plants the ball of the foot and lifts the heel, and the shoe is
modelled around that posed foot. Unity has no such concept, so without this
the girl stands flat inside a shoe shaped for a raised heel: her foot pokes
through the sole and the shoe reads as badly fitted rather than as broken.

The whole fix is **two rotations and a lift**. Nothing else.

## 1. The data lives in the manifest, never in C#

A heeled shoe carries a `heelPose` block in its
`Assets/Editor/WearDrops/<drop>.json` entry:

```json
"heelPose": { "foot": 45.0, "toe": -40.0, "lift": 0.078, "axis": [] }
```

| поле | что это |
|---|---|
| `foot` | plantarflexion — the foot pitches down, heel comes off the ground |
| `toe` | the toes bend the other way so the ball stays flat on the floor |
| `lift` | metres the body rises once standing on the ball instead of the sole |
| `axis` | bone-local rotation axis; **empty = X**, which is what DAZ uses |

Absent block = flat shoe. **A new pair of heels needs no C# whatsoever** —
that is the point of keeping it here.

Shipped values, straight out of the DAZ presets:

| shoe | foot | toe | lift | preset |
|---|---|---|---|---|
| `clothing.pumps_flair` | +45.0° | −40.0° | 0.078 | `Amaranth/Flair/Flair-FootPose.duf` |
| `clothing.boots_cindy` | +28.2° | −27.91° | 0.052 | `Cindy Aurum/Feet Pose/Feet Pose.duf` |

The angles scale with heel height — same shape of pose, different magnitude.

## 2. Getting the numbers out of the product

Every heeled DAZ product ships a foot-pose preset. Find it next to the shoe
(`*FootPose*.duf`, `Feet Pose/*.duf`) — **it is excluded from the garment list
on purpose** (it is a pose, not a wearable), so nobody trips over it during
`dress`.

A `.duf` is JSON, gzipped or not depending on the author's save setting:

```python
import json, gzip, pathlib
raw = pathlib.Path(preset).read_bytes()
try:    text = gzip.decompress(raw).decode("utf-8")
except OSError: text = raw.decode("utf-8")

for a in json.loads(text)["scene"]["animations"]:
    url = a.get("url", "")
    if "rotation" not in url: continue
    tail = url.split("/")[-3:]          # [<bone>:?rotation, x, value]
    bone, axis = tail[0].split(":")[0], tail[1]
    value = a["keys"][0][1]
    if abs(value) > 0.01:
        print(bone, axis, round(value, 2))
```

Expect exactly four non-zero channels: `lFoot`/`rFoot` and `lToe`/`rToe`, all
on X. **The URL shape differs between products** — one had
`name://@selection/lToe:?rotation/x/value`, another nested it deeper — so parse
from the END of the url, not the start.

`lift` is not in the preset. Estimate it geometrically and tune by eye:

```
lift ≈ 0.11 × sin(foot°)        # 0.11 m = heel-to-ball on Genesis 3 Female
```

## 3. Runtime — `BodyBones`, and it MUST be LateUpdate

`BodyBones.LateUpdate` applies the pose to the body's own `lFoot`/`rFoot` and
`lToe`/`rToe`, then lifts `hip`.

**Why LateUpdate and not equip time:** the Animator rewrites the legs every
frame, so anything applied earlier is gone before it is ever drawn. Nothing
accumulates — each frame starts from whatever the animation wrote, and the pose
is re-applied on top.

**Why the lift goes along `transform.up` and not world up:** so it still reads
when she is knocked over or lying down.

**Why the active heel is cached** (`RefreshHeel`, called from `Construct` /
`Equip` / `TakeOff`): `LateUpdate` runs on every dressed body in the colony, so
it must be a field read, not a scan of the wardrobe. Tallest heel wins — only
one shoe can own the foot slots anyway.

Nothing in the project fights this: there is **no foot IK and no ground
snapping** anywhere (`OnAnimatorIK`, `AvatarIKGoal`, ground-snap — zero hits).
That was checked before the feature was written and is the reason it is this
simple. If foot IK is ever added, this is the first thing it will break.

## 4. What to verify on a new pair — do NOT trust the numbers blind

1. **The rotation axis.** DAZ turns the foot about X, but an FBX import may
   permute a bone's local axes. If the heel goes sideways instead of up, set
   `axis` in the manifest — **do not touch the code**.
2. **The lift.** Sinks into the floor → too small; floats → too large. Same
   file, same line.
3. **Poses that plant the foot flat** — sitting, lying, swimming — will look
   wrong with a heel applied. Not handled yet; if it becomes visible, damp the
   pose in those states rather than removing it.

## 5. Traps

- **The foot-pose preset is NOT a garment.** Fitting it during `dress` makes the
  count check fail (`надето N из M`). It stays out of the drop list.
- **A shoe can arrive with no albedo.** The Flair pumps had a default material
  in the scene, so the FBX carried no diffuse map and they rendered pure white —
  in game, not just on the icon. The product's own preset names the texture
  (`flair-shoe.jpg`); stage it and write it into every material of the shoe.
- **Glasses-class props do not work here at all.** Anything that loads as a
  rigid prop rather than a fitted `DzFigure` has no skinning, and the wardrobe
  contract is a `SkinnedMeshRenderer` driven by bones.

---

### Related
`spec.md` §31B.4C, `Tools/wardrobe/README.md` (the DAZ→Unity pipeline),
`ICON_GENERATION_SPEC.md` (the inventory picture),
`Assets/HexLive/UnityPresentation/Wearing/BodyBones.cs` (the runtime).
