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

⚠️ **Not every heeled product ships a preset.** That was the assumption when
this spec was written and it does not hold: *Charlotte High Heels* (Arryn,
G3F+G8F) contains no pose file at all — zero `.duf` with "pose" in the name —
and its wearable `Charlotte High Heels.duf` keys **every** `lFoot`/`rFoot`/
`lToe`/`rToe` rotation to **0**. That is a RESET, not a pose: it flattens the
foot on load. Read such a file as authored numbers and you get a flat shoe with
a confident-looking `heelPose` of zero. **Non-zero is the test, not presence.**

When there IS a preset it wins — it is the authored answer. Find it next to the
shoe (`*FootPose*.duf`, `Feet Pose/*.duf`) — **it is excluded from the garment
list on purpose** (it is a pose, not a wearable), so nobody trips over it
during `dress`.

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

## 2A. When there is no preset — measure the shoe (automatic)

`Tools/wardrobe/wardrobe/heels.py`. **The pipeline now does this by itself**;
this section is here so the next person knows what the numbers mean, not so
they can be produced by hand.

**Recognising a heel costs nothing.** The `dress` stage fits the shoe to a girl
standing FLAT, so a shoe modelled around a raised heel reaches *below the floor*
by exactly the heel height. `y` in these exports is centimetres above the ground
— the same axis `manifest._zone` slices the body with — so:

```
heel height = −min(y) over the shoe's vertices
```

That is the "her foot pokes through the sole" failure seen from the other side,
and it is already in the FBX we parse. No DAZ round-trip, no product
conventions. A flat shoe dips a centimetre or two for its sole, which is why
the threshold is 2.5 cm (`heels.MIN_HEEL_CM`).

The measured drop **is** the `lift`, so the angle is §2's formula inverted:

```
foot° = asin(heelHeight / 0.11)     toe° = −foot°
```

Checked against the shipped table above: Flair's 0.078 m gives 45.2° (tuned
45.0), Cindy's 0.052 m gives 28.2° (tuned 28.2). The toe is the weaker half of
the model — Flair's authored toe is −40° where the formula says −45° — so it is
the first thing to nudge if the toes look wrong.

Beyond `heels.MAX_FOOT_DEGREES` (55°, steeper than anything the project has)
the pose is clamped and flagged `_review` in the manifest rather than emitted
silently: an absurd angle means the measurement is wrong, not that the shoe is
extraordinary.

**Where it plugs in.** `manifest.propose()` asks `heels.propose()` only for
garments whose inferred slots contain `FootR`/`FootL`, writes the `heelPose`
block, and records `_measured.heelCm` as the evidence — so a wrong guess is
visible next to the number, like the slot list. `manifest.merge()` keeps a
hand-corrected pose verbatim; it adopts a newly measured one only when the
reviewed entry has **no** `heelPose` key, and an explicit `"heelPose": null` is
respected as a decision.

## 3. Runtime — `BodyBones`, and it MUST be LateUpdate

`BodyBones.LateUpdate` applies the pose to the body's own `lFoot`/`rFoot` and
`lToe`/`rToe`, then lifts `hip`.

**Why LateUpdate and not equip time:** the Animator rewrites the legs every
frame, so anything applied earlier is gone before it is ever drawn.

⚠️ **«Nothing accumulates» is only true while the Animator writes** — and a
standing/walking animator runs in `CullUpdateTransforms` (perf, spec §31C.8):
the moment the renderer leaves every camera, the bones freeze. A naive
`hip.position +=` then compounds every rendered frame. That was bug #146: the
hip climbed ~3 wu/s, the skin's culling AABB left the frustum (bounds centre
measured at Y = 84 wu under a girl walking at Y = 1.3), the renderer could
never become visible again, so the animator never woke — she was invisible
FOREVER, until clicking her in the roster made the portrait camera render the
body once and restart the loop. Since then every bone the heel pose touches
(both foot/toe rotations too — `localRotation *=` compounds identically)
remembers what it wrote last frame: if the bone still holds exactly our last
write, we roll back to the remembered base first, then apply afresh. The hip
cache lives in LOCAL space — the actor root moves every render frame, so a
world-space cache would re-base every frame with the previous lift baked in
and accumulate all the same.

**Why the lift goes along `transform.up` and not world up:** the standing pose
follows the actor's own up axis. Sitting, lying, bed and swimming explicitly
fade the complete heel correction out instead of changing that axis.

### 3.1 Posture weight

`NpcActorView` sends posture changes to `BodyBones` as soon as `Sit`, laying /
bed, or swimming changes. `heelPoseWeight` is `1` while standing and moves to
`0` over **0.12 seconds** in those planted-foot postures. The same weight scales
all five corrections together: left/right foot rotation, left/right toe
rotation, and pelvis lift. Returning to standing eases the weight back to `1`.

The current reviewed manifests span **0.0026…0.0983 wu** of lift (maximum foot
pitch is 55°). At weight zero that whole lift is gone, so the authored seat and
bed contact wins. Do not compensate by changing seat geometry: ledge-seat
`lift=0.40 wu`, elevation step `0.55 wu`, and seat back offset `0.45 wu` are
independent spatial constants and remain unchanged.

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
3. **Poses that plant the foot flat** — stump/seat, lying/bed and swimming —
   must drive `heelPoseWeight` to zero. Verify both rotations and lift; checking
   only the pelvis can leave toes visibly twisted through the surface.

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
