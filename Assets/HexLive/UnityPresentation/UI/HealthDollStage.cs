using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// Spec §57: the limb-health "body doll". Renders a bind-pose clone of the
    /// selected character's skin mesh into a RenderTexture for the health
    /// window — every vertex tinted by its body zone's HP (green → yellow →
    /// red, StarCraft damage-readout style), severed limbs collapsed to stumps
    /// and painted dark. The doll is built once per actor mesh from the same
    /// Resources prefab the world spawns, classified per-vertex by dominant
    /// skin bone (the same Genesis3 segments SkinTexturePainter targets), and
    /// lives on the hidden "Portrait" layer far below the map, filmed by its
    /// own camera — the world never sees it.
    /// </summary>
    public sealed class HealthDollStage : MonoBehaviour
    {
        private const int TextureWidth = 384;
        private const int TextureHeight = 512;

        private static readonly Color Backdrop = new(0.10f, 0.12f, 0.14f, 1f);
        private static readonly Vector3 StagePosition = new(0f, -260f, 0f);

        /// <summary>Sim zone order — matches the BodyPart enum names.</summary>
        public static readonly string[] ZoneOrder =
        {
            "Head", "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR"
        };

        // HP ramp endpoints (the CharacterPanel palette) + the dead stump.
        private static readonly Color HpGood = new(0.36f, 0.72f, 0.33f);
        private static readonly Color HpWarn = new(0.910f, 0.698f, 0.235f);
        private static readonly Color HpCrit = new(0.910f, 0.341f, 0.310f);
        private static readonly Color Stump = new(0.24f, 0.17f, 0.17f);

        // Walking a skin bone UP the Genesis3 hierarchy, the first of these
        // ancestors decides the zone. Arms/legs/neck are checked before the
        // torso chain simply because they appear deeper — first hit wins.
        // ("hip" is the skeleton root, so every bone resolves eventually.)
        private static readonly Dictionary<string, int> ZoneRootBones = new()
        {
            ["neckLower"] = 0,   // Head (neck + head + face bones)
            ["abdomenUpper"] = 1, // Torso (chest, collars, shoulders' girdle)
            ["abdomenLower"] = 2, // Pelvis
            ["pelvis"] = 2,
            ["hip"] = 2,
            ["lShldrBend"] = 3,  // ArmL
            ["rShldrBend"] = 4,  // ArmR
            ["lThighBend"] = 5,  // LegL
            ["rThighBend"] = 6,  // LegR
        };

        // Same non-skin submesh filter NpcActorView uses for the tan tint —
        // eyes/lashes/mouth/nails drop out of the doll (tiny holes read fine
        // on the hologram; tinting them read wrong on the live body already).
        private static readonly string[] NonSkinMaterialHints =
        {
            "cornea", "sclera", "iris", "pupil", "eye", "moist", "socket", "lash",
            "tear", "hair", "tooth", "teeth", "gum", "tongue", "mouth", "nail",
            "lacrimal", "brow"
        };

        // Spec §50 parity with NpcActorView: a severed zone collapses this
        // distal bone's sub-tree, leaving an above-elbow/above-knee stump.
        private static readonly Dictionary<string, string> SeveredDistalBone = new()
        {
            ["ArmL"] = "lForearmBend",
            ["ArmR"] = "rForearmBend",
            ["LegL"] = "lShin",
            ["LegR"] = "rShin"
        };

        private RenderTexture _texture;
        private Camera _camera;
        private bool _active;
        private bool _maskResolved;

        private string _actorMesh;
        private GameObject _doll;
        private SkinnedMeshRenderer _dollSkin;
        private Mesh _dollMesh;
        private int[] _vertexZone;
        private Color32[] _colorScratch;
        private readonly Dictionary<string, (Transform bone, Vector3 scale)> _distalBones = new();

        private bool _framed;
        private Vector3 _focus;

        private string _zoneSig;
        private readonly float[] _zoneHp = new float[ZoneOrder.Length];
        private readonly bool[] _zoneSevered = new bool[ZoneOrder.Length];
        private readonly bool[] _zoneBandaged = new bool[ZoneOrder.Length];

        public RenderTexture Texture => _texture;

        /// <summary>Green → yellow → red HP readout; dark for a lost limb.</summary>
        public static Color StatusColor(float hp, bool severed)
        {
            if (severed)
            {
                return Stump;
            }

            hp = Mathf.Clamp01(hp);
            return hp >= 0.5f
                ? Color.Lerp(HpWarn, HpGood, (hp - 0.5f) * 2f)
                : Color.Lerp(HpCrit, HpWarn, hp * 2f);
        }

        private void Awake()
        {
            // Far below the map — the main camera culls the Portrait layer
            // anyway (PrototypeRuntimeBootstrap), this is belt-and-braces.
            transform.position = StagePosition;

            _texture = new RenderTexture(TextureWidth, TextureHeight, 16, RenderTextureFormat.ARGB32)
            {
                name = "HealthDoll",
                antiAliasing = 2
            };
            _texture.Create();

            var camGo = new GameObject("HealthDollCamera");
            camGo.transform.SetParent(transform, false);

            _camera = camGo.AddComponent<Camera>();
            _camera.targetTexture = _texture;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Backdrop;
            _camera.fieldOfView = 26f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 60f;
            _camera.enabled = false;
        }

        /// <summary>The window is open — render (and spin) only while shown.</summary>
        public void SetActive(bool active)
        {
            _active = active;
            if (!active && _camera != null)
            {
                _camera.enabled = false;
            }
        }

        /// <summary>Build (or reuse) the doll for this actor mesh.</summary>
        public void SetTarget(string actorMesh)
        {
            if (string.IsNullOrEmpty(actorMesh) || actorMesh == _actorMesh)
            {
                return;
            }

            _actorMesh = actorMesh;
            BuildDoll(actorMesh);
        }

        /// <summary>
        /// Per-tick zone state, straight off the NpcSnapshot lists:
        /// "Zone=0.87" body parts, severed zone names, bandaged zones
        /// ("Zone" leaf wrap / "Zone|g" gauze — the doll shows both the same).
        /// </summary>
        public void SetZones(
            IReadOnlyList<string> bodyParts,
            IReadOnlyList<string> severedParts,
            IReadOnlyList<string> bandagedZones)
        {
            if (_dollMesh == null || _vertexZone == null)
            {
                return;
            }

            // Cheap change gate — repaint only when the readout changes.
            var sig = $"{Join(bodyParts)}#{Join(severedParts)}#{Join(bandagedZones)}";
            if (sig == _zoneSig)
            {
                return;
            }

            _zoneSig = sig;

            for (var i = 0; i < ZoneOrder.Length; i++)
            {
                _zoneHp[i] = 1f;
                _zoneSevered[i] = false;
                _zoneBandaged[i] = false;
            }

            if (bodyParts != null)
            {
                foreach (var entry in bodyParts)
                {
                    var eq = entry.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    var zone = ZoneIndex(entry[..eq]);
                    if (zone >= 0 &&
                        float.TryParse(entry[(eq + 1)..], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var hp))
                    {
                        _zoneHp[zone] = hp;
                    }
                }
            }

            if (severedParts != null)
            {
                foreach (var entry in severedParts)
                {
                    var zone = ZoneIndex(entry);
                    if (zone >= 0)
                    {
                        _zoneSevered[zone] = true;
                    }
                }
            }

            if (bandagedZones != null)
            {
                foreach (var entry in bandagedZones)
                {
                    var bar = entry.IndexOf('|');
                    var zone = ZoneIndex(bar > 0 ? entry[..bar] : entry);
                    if (zone >= 0)
                    {
                        _zoneBandaged[zone] = true;
                    }
                }
            }

            RepaintDoll();
        }

        private static string Join(IReadOnlyList<string> list)
        {
            if (list == null || list.Count == 0)
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < list.Count; i++)
            {
                sb.Append(list[i]).Append(';');
            }

            return sb.ToString();
        }

        private static int ZoneIndex(string name)
        {
            for (var i = 0; i < ZoneOrder.Length; i++)
            {
                if (ZoneOrder[i] == name)
                {
                    return i;
                }
            }

            return -1;
        }

        // ── doll construction ─────────────────────────────────────────────

        private void BuildDoll(string actorMesh)
        {
            if (_doll != null)
            {
                Destroy(_doll);
                _doll = null;
            }

            if (_dollMesh != null)
            {
                Destroy(_dollMesh);
                _dollMesh = null;
            }

            _dollSkin = null;
            _vertexZone = null;
            _colorScratch = null;
            _distalBones.Clear();
            _framed = false;
            _zoneSig = null; // force a repaint with the next SetZones

            var prefab = Resources.Load<GameObject>($"HexLive/Actors/{actorMesh}");
            if (prefab == null)
            {
                return;
            }

            _doll = Instantiate(prefab, transform);
            _doll.name = $"Doll_{actorMesh}";
            _doll.transform.localPosition = Vector3.zero;
            _doll.transform.localRotation = Quaternion.identity;

            // Pose: one evaluated frame of the prefab's own locomotion
            // controller (its default Idle state) — a natural stance with the
            // arms relaxed at the sides. Manually swinging the shoulder bones
            // out of the T-pose warped the shoulders/elbows (Genesis skinning
            // wants its authored poses), so the idle clip does it instead;
            // then the Animator dies and the bones keep the sampled pose.
            foreach (var animator in _doll.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController != null)
                {
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.Update(0f);
                }

                animator.enabled = false;
                Destroy(animator);
            }

            // The primary Genesis figure (same heuristic as NpcActorView):
            // prefer a "Genesis"-named skin, tie-break on vertex count.
            SkinnedMeshRenderer primary = null;
            var primaryGenesis = false;
            foreach (var skin in _doll.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin.sharedMesh == null)
                {
                    continue;
                }

                var isGenesis = skin.name.Contains("Genesis") || skin.sharedMesh.name.Contains("Genesis");
                var better = primary == null ||
                    (isGenesis && !primaryGenesis) ||
                    (isGenesis == primaryGenesis &&
                     skin.sharedMesh.vertexCount > primary.sharedMesh.vertexCount);
                if (better)
                {
                    primary = skin;
                    primaryGenesis = isGenesis;
                }
            }

            if (primary == null || !primary.sharedMesh.isReadable)
            {
                Destroy(_doll);
                _doll = null;
                return;
            }

            // Only the skin renders — hair cards, eyelash planes etc. are
            // either separate renderers (disabled here) or non-skin submeshes
            // (dropped below).
            foreach (var renderer in _doll.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = renderer == primary;
            }

            _dollSkin = primary;
            _dollMesh = BuildSkinOnlyMesh(primary, out _vertexZone);
            if (_dollMesh == null)
            {
                Destroy(_doll);
                _doll = null;
                _dollSkin = null;
                return;
            }

            primary.sharedMesh = _dollMesh;
            var dollMaterial = new Material(Shader.Find("HexLive/HealthDoll")) { name = "HealthDoll" };
            var slots = new Material[_dollMesh.subMeshCount];
            for (var i = 0; i < slots.Length; i++)
            {
                slots[i] = dollMaterial;
            }

            primary.sharedMaterials = slots;
            primary.updateWhenOffscreen = true; // correct bounds for framing

            // Spec §50 stump bones, cached with their rest scale so a doll
            // reused across characters can grow a limb back.
            foreach (var pair in SeveredDistalBone)
            {
                var bone = FindDeep(_doll.transform, pair.Value);
                if (bone != null)
                {
                    _distalBones[pair.Key] = (bone, bone.localScale);
                }
            }

            var layer = LayerMask.NameToLayer("Portrait");
            if (layer >= 0)
            {
                SetLayerDeep(_doll.transform, layer);
            }

            _colorScratch = new Color32[_dollMesh.vertexCount];
        }

        // One combined skin-only submesh + a per-vertex zone id derived from
        // the dominant skinning bone (barycentric-precision is overkill for a
        // 7-zone readout; the dominant bone matches the sim's hit zones).
        private static Mesh BuildSkinOnlyMesh(SkinnedMeshRenderer skin, out int[] vertexZone)
        {
            vertexZone = null;
            var source = skin.sharedMesh;
            var materials = skin.sharedMaterials;

            var skinTriangles = new List<int>(source.triangles.Length);
            var buffer = new List<int>();
            var subMeshes = Mathf.Min(source.subMeshCount, materials.Length);
            for (var i = 0; i < subMeshes; i++)
            {
                if (!IsSkinSlot(materials[i]))
                {
                    continue;
                }

                buffer.Clear();
                source.GetTriangles(buffer, i);
                skinTriangles.AddRange(buffer);
            }

            if (skinTriangles.Count == 0)
            {
                return null;
            }

            // Bone index → zone id, walked up the transform hierarchy once.
            var bones = skin.bones;
            var boneZone = new int[bones.Length];
            for (var i = 0; i < bones.Length; i++)
            {
                boneZone[i] = 1; // default Torso
                for (var t = bones[i]; t != null; t = t.parent)
                {
                    if (ZoneRootBones.TryGetValue(t.name, out var zone))
                    {
                        boneZone[i] = zone;
                        break;
                    }
                }
            }

            var weights = source.boneWeights;
            vertexZone = new int[source.vertexCount];
            for (var v = 0; v < vertexZone.Length; v++)
            {
                if (v >= weights.Length)
                {
                    break;
                }

                var w = weights[v];
                var best = w.boneIndex0;
                var bestWeight = w.weight0;
                if (w.weight1 > bestWeight) { best = w.boneIndex1; bestWeight = w.weight1; }
                if (w.weight2 > bestWeight) { best = w.boneIndex2; bestWeight = w.weight2; }
                if (w.weight3 > bestWeight) { best = w.boneIndex3; }

                vertexZone[v] = best >= 0 && best < boneZone.Length ? boneZone[best] : 1;
            }

            var mesh = Instantiate(source);
            mesh.name = $"{source.name}_HealthDoll";
            mesh.subMeshCount = 1;
            mesh.SetTriangles(skinTriangles, 0);
            return mesh;
        }

        private static bool IsSkinSlot(Material material)
        {
            if (material == null || string.IsNullOrEmpty(material.name))
            {
                return false;
            }

            var name = material.name.ToLowerInvariant();
            foreach (var hint in NonSkinMaterialHints)
            {
                if (name.Contains(hint))
                {
                    return false;
                }
            }

            return true;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                {
                    return t;
                }
            }

            return null;
        }

        private static void SetLayerDeep(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (var i = 0; i < root.childCount; i++)
            {
                SetLayerDeep(root.GetChild(i), layer);
            }
        }

        // ── painting ──────────────────────────────────────────────────────

        private void RepaintDoll()
        {
            var zoneColors = new Color32[ZoneOrder.Length];
            for (var i = 0; i < ZoneOrder.Length; i++)
            {
                var color = StatusColor(_zoneHp[i], _zoneSevered[i]);
                if (_zoneBandaged[i] && !_zoneSevered[i])
                {
                    // A wrapped zone reads pale — the bandage over the readout.
                    color = Color.Lerp(color, Color.white, 0.30f);
                }

                zoneColors[i] = color;
            }

            for (var v = 0; v < _colorScratch.Length; v++)
            {
                _colorScratch[v] = zoneColors[_vertexZone[v]];
            }

            _dollMesh.colors32 = _colorScratch;

            // Collapse / restore the stump bones (same trick as the live body:
            // near-zero scale — exact zero NaNs the skinning).
            foreach (var pair in _distalBones)
            {
                var severed = _zoneSevered[ZoneIndex(pair.Key)];
                pair.Value.bone.localScale = severed
                    ? new Vector3(0.0001f, 0.0001f, 0.0001f)
                    : pair.Value.scale;
            }
        }

        // ── filming ───────────────────────────────────────────────────────

        private void LateUpdate()
        {
            if (_camera == null)
            {
                return;
            }

            if (!_active || _dollSkin == null)
            {
                _camera.enabled = false;
                return;
            }

            if (!_maskResolved)
            {
                var layer = LayerMask.NameToLayer("Portrait");
                if (layer >= 0)
                {
                    _camera.cullingMask = 1 << layer;
                    _maskResolved = true;
                }
            }

            // Frame on the first frame with live skinned bounds: camera in
            // front of the figure, whole body + margin in view.
            var bounds = _dollSkin.bounds;
            if (!_framed && bounds.extents.y > 0.01f)
            {
                _focus = bounds.center;
                var distance = bounds.extents.y /
                    Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.12f + bounds.extents.z;
                // The figure faces +Z (same convention PortraitStage leans on),
                // so the camera starts out in FRONT and the spin does the rest.
                _camera.transform.position = _focus + _doll.transform.forward * distance;
                _camera.transform.rotation = Quaternion.LookRotation(_focus - _camera.transform.position, Vector3.up);
                _framed = true;
            }

            _camera.enabled = _framed;
        }

        private void OnDestroy()
        {
            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
            }

            if (_dollMesh != null)
            {
                Destroy(_dollMesh);
            }
        }
    }
}
