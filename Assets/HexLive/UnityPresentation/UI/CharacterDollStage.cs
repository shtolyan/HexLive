using System;
using System.Collections.Generic;
using System.Globalization;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Wearing;
using RootMotion.FinalIK;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    public enum CharacterDollMode
    {
        Hidden,
        Inventory,
        Health
    }

    /// <summary>
    /// One persistent clone of the selected live NPC shared by inventory and
    /// health. Mode changes only switch two renderers on the same skeleton;
    /// clothing, hair, held props and fitted prosthetics are never rebuilt just
    /// because another character window was opened.
    /// </summary>
    public sealed class CharacterDollStage : MonoBehaviour
    {
        private const int TextureWidth = 512;
        private const int TextureHeight = 768;
        private const float DollFramePadding = 1.18f;
        private const float StageSeparation = 80f;
        private const float SignatureIntervalSeconds = 0.5f;
        private const int SettleFrames = 2;
        private static readonly int SpeedParam = Animator.StringToHash("Speed");
        private static readonly int IdleState = Animator.StringToHash("Base Layer.Idle");
        private static readonly Vector3 StagePosition = new(StageSeparation, -260f, 0f);
        private static readonly Color Backdrop = new(0.035f, 0.047f, 0.055f, 0f);

        public static readonly string[] ZoneOrder =
        {
            "Head", "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR"
        };

        private static readonly Color HpGood = new(0.36f, 0.72f, 0.33f);
        private static readonly Color HpWarn = new(0.910f, 0.698f, 0.235f);
        private static readonly Color HpCrit = new(0.910f, 0.341f, 0.310f);
        private static readonly Color Stump = new(0.24f, 0.17f, 0.17f);

        private static readonly Dictionary<string, int> ZoneRootBones = new()
        {
            ["neckLower"] = 0,
            ["abdomenUpper"] = 1,
            ["abdomenLower"] = 2,
            ["pelvis"] = 2,
            ["hip"] = 2,
            ["lShldrBend"] = 3,
            ["rShldrBend"] = 4,
            ["lThighBend"] = 5,
            ["rThighBend"] = 6
        };

        private static readonly string[] NonSkinMaterialHints =
        {
            "cornea", "sclera", "iris", "pupil", "eye", "moist", "socket", "lash",
            "tear", "hair", "tooth", "teeth", "gum", "tongue", "mouth", "nail",
            "lacrimal", "brow"
        };

        private static readonly Dictionary<string, string> SeveredDistalBone = new()
        {
            ["ArmL"] = "lForearmBend",
            ["ArmR"] = "rForearmBend",
            ["LegL"] = "lShin",
            ["LegR"] = "rShin"
        };

        private readonly Dictionary<Renderer, string> _wornByRenderer = new();
        private readonly HashSet<string> _wantedWorn = new();
        private readonly List<Renderer> _rendererScratch = new();
        private readonly List<Material> _materialScratch = new();
        private readonly List<SkinnedMeshRenderer> _cloneSkins = new();
        private readonly HashSet<int> _sourceMaterialIds = new();
        private readonly Dictionary<string, DollFraming> _framingByActor = new();
        private readonly Dictionary<string, (Transform bone, Vector3 scale)> _distalBones = new();
        private readonly float[] _zoneHp = new float[ZoneOrder.Length];
        private readonly bool[] _zoneSevered = new bool[ZoneOrder.Length];
        private readonly bool[] _zoneBandaged = new bool[ZoneOrder.Length];

        private RenderTexture _texture;
        private Camera _camera;
        private Transform _modelPivot;
        private Transform _gazeTarget;
        private GameObject _clone;
        private Animator _animator;
        private LookAtIK _lookAt;
        private Light _keyLight;
        private Light _rimLight;
        private SkinnedMeshRenderer _normalBodyRenderer;
        private SkinnedMeshRenderer _healthBodyRenderer;
        private Mesh _healthMesh;
        private Material _healthMaterial;
        private int[] _vertexZone;
        private Color32[] _colorScratch;

        private CharacterDollMode _mode;
        private int _npcId = -1;
        private string _actorMesh = string.Empty;
        private int _sourceRootId;
        private int _sourceVisualSignature;
        private int _zoneSignature;
        private bool _zoneSignatureValid;
        private float _yaw;
        private string _buildError = string.Empty;
        private int _cloneGeneration;
        private float _nextSignatureTime;
        private int _settleFrames;
        private bool _renderDirty;

        /// <summary>
        /// The photo point of one actor: where the portrait camera stands and
        /// what it looks at, in stage-local space. Measured once from the
        /// actor's own body in the frozen Idle pose, then reused for every
        /// later clone of that actor so the doll cannot drift between two
        /// openings of the window.
        /// </summary>
        private readonly struct DollFraming
        {
            public DollFraming(Vector3 focus, Vector3 eye)
            {
                Focus = focus;
                Eye = eye;
            }

            public Vector3 Focus { get; }
            public Vector3 Eye { get; }
        }

        public RenderTexture Texture => _texture;
        public CharacterDollMode Mode => _mode;
        public bool HealthOverlayAvailable => _healthBodyRenderer != null;
        public string BuildError => _buildError;
        public int CloneInstanceId => _clone != null ? _clone.GetInstanceID() : 0;
        public int CloneGeneration => _cloneGeneration;

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
            transform.position = StagePosition;
            for (var i = 0; i < ZoneOrder.Length; i++)
            {
                _zoneHp[i] = 1f;
            }

            _texture = new RenderTexture(
                TextureWidth, TextureHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "CharacterDoll",
                antiAliasing = 2,
                useMipMap = false
            };
            _texture.Create();

            _modelPivot = new GameObject("CharacterDollModel").transform;
            _modelPivot.SetParent(transform, false);
            _modelPivot.gameObject.SetActive(false);
            _gazeTarget = new GameObject("CharacterDollGaze").transform;
            _gazeTarget.SetParent(transform, false);

            var cameraObject = new GameObject("CharacterDollCamera");
            cameraObject.transform.SetParent(transform, false);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.targetTexture = _texture;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Backdrop;
            _camera.fieldOfView = 24f;
            _camera.nearClipPlane = 0.03f;
            _camera.farClipPlane = 30f;
            _camera.allowHDR = true;
            // The doll is a frozen photo: the camera is armed only for the
            // frames whose portrait changed, instead of re-rendering a still
            // image every frame the window is open.
            _camera.enabled = false;

            _keyLight = CreateLight(
                "CharacterDollKey", LightType.Spot,
                new Color(1f, 0.82f, 0.64f), 4.2f, 14f, 58f);
            _rimLight = CreateLight(
                "CharacterDollRim", LightType.Point,
                new Color(0.34f, 0.62f, 1f), 2.8f, 9f, 0f);
            SetStageEnabled(false);
        }

        public void SetMode(CharacterDollMode mode)
        {
            if (_mode == mode)
            {
                return;
            }

            _mode = mode;
            ApplyMode();
            if (_clone != null)
            {
                FrameClone();
            }
        }

        public void SetTarget(int npcId, string actorMesh, IReadOnlyList<string> wornItems)
        {
            actorMesh ??= string.Empty;
            var changedTarget = _npcId != npcId ||
                                !string.Equals(_actorMesh, actorMesh, StringComparison.Ordinal);
            _npcId = npcId;
            _actorMesh = actorMesh;

            _wantedWorn.Clear();
            if (wornItems != null)
            {
                foreach (var worn in wornItems)
                {
                    if (!string.IsNullOrEmpty(worn))
                    {
                        _wantedWorn.Add(worn);
                    }
                }
            }

            if (!changedTarget)
            {
                return;
            }

            _yaw = 0f;
            if (_modelPivot != null)
            {
                _modelPivot.localRotation = Quaternion.identity;
            }
            _sourceRootId = 0;
            _sourceVisualSignature = 0;
            DestroyClone();
        }

        public void Rotate(float degrees)
        {
            _yaw = Mathf.Repeat(_yaw + degrees, 360f);
            if (_modelPivot != null)
            {
                _modelPivot.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            }
            _renderDirty = true;
        }

        public bool TryPickWorn(Vector2 localPoint, Vector2 viewportSize, out string definitionId)
        {
            definitionId = string.Empty;
            if (_mode == CharacterDollMode.Hidden || _camera == null || _clone == null ||
                viewportSize.x <= 1f || viewportSize.y <= 1f)
            {
                return false;
            }

            var textureAspect = TextureWidth / (float)TextureHeight;
            var viewAspect = viewportSize.x / viewportSize.y;
            var imageSize = viewAspect > textureAspect
                ? new Vector2(viewportSize.y * textureAspect, viewportSize.y)
                : new Vector2(viewportSize.x, viewportSize.x / textureAspect);
            var imageOrigin = (viewportSize - imageSize) * 0.5f;
            if (localPoint.x < imageOrigin.x || localPoint.y < imageOrigin.y ||
                localPoint.x > imageOrigin.x + imageSize.x ||
                localPoint.y > imageOrigin.y + imageSize.y)
            {
                return false;
            }

            var viewport = new Vector3(
                Mathf.Clamp01((localPoint.x - imageOrigin.x) / imageSize.x),
                Mathf.Clamp01(1f - (localPoint.y - imageOrigin.y) / imageSize.y),
                0f);
            var ray = _camera.ViewportPointToRay(viewport);
            var nearest = float.MaxValue;
            foreach (var pair in _wornByRenderer)
            {
                var renderer = pair.Key;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    !renderer.bounds.IntersectRay(ray, out var distance) || distance >= nearest)
                {
                    continue;
                }

                nearest = distance;
                definitionId = pair.Value;
            }

            return definitionId.Length > 0;
        }

        public void SetZones(
            IReadOnlyList<string> bodyParts,
            IReadOnlyList<string> severedParts,
            IReadOnlyList<string> bandagedZones,
            IReadOnlyList<BodyPartConditionSnapshot> conditions = null)
        {
            var signature = 17;
            signature = HashEntries(signature, bodyParts);
            signature = HashEntries(signature * 31 + 7, severedParts);
            signature = HashEntries(signature * 31 + 13, bandagedZones);
            if (conditions != null)
            {
                foreach (var condition in conditions)
                {
                    if (condition == null) continue;
                    signature = signature * 31 + (int)condition.Part;
                    signature = signature * 31 + condition.Health.GetHashCode();
                    signature = signature * 31 + condition.CriticalTrauma.GetHashCode();
                    signature = signature * 31 + condition.Severed.GetHashCode();
                    signature = signature * 31 + (condition.BandageKind?.GetHashCode() ?? 0);
                    signature = signature * 31 + (condition.Prosthetic?.DefinitionId?.GetHashCode() ?? 0);
                }
            }

            if (_zoneSignatureValid && signature == _zoneSignature)
            {
                return;
            }

            _zoneSignature = signature;
            _zoneSignatureValid = true;
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
                    if (entry == null) continue;
                    var equals = entry.IndexOf('=');
                    if (equals <= 0) continue;
                    var zone = ZoneIndex(entry[..equals]);
                    if (zone >= 0 && float.TryParse(
                            entry[(equals + 1)..], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var hp))
                    {
                        _zoneHp[zone] = hp;
                    }
                }
            }

            if (conditions != null)
            {
                foreach (var condition in conditions)
                {
                    if (condition == null) continue;
                    var zone = ZoneIndex(condition.Part.ToString());
                    if (zone < 0) continue;
                    _zoneHp[zone] = condition.Health - condition.CriticalTrauma;
                    _zoneSevered[zone] = condition.Severed;
                    _zoneBandaged[zone] = !string.IsNullOrEmpty(condition.BandageKind);
                }
            }

            if (severedParts != null)
            {
                foreach (var entry in severedParts)
                {
                    var zone = ZoneIndex(entry);
                    if (zone >= 0) _zoneSevered[zone] = true;
                }
            }

            if (bandagedZones != null)
            {
                foreach (var entry in bandagedZones)
                {
                    if (entry == null) continue;
                    var separator = entry.IndexOf('|');
                    var zone = ZoneIndex(separator > 0 ? entry[..separator] : entry);
                    if (zone >= 0) _zoneBandaged[zone] = true;
                }
            }

            RepaintHealthMesh();
        }

        private void LateUpdate()
        {
            if (_mode == CharacterDollMode.Hidden || _npcId < 0 || _camera == null)
            {
                SetStageEnabled(false);
                return;
            }

            var source = NpcActorView.FindLiveBodyRoot(_npcId);
            if (source == null)
            {
                SetStageEnabled(false);
                return;
            }

            if (_clone == null || _sourceRootId != source.GetInstanceID())
            {
                BuildClone(source, SourceVisualSignature(source));
            }
            else if (Time.unscaledTime >= _nextSignatureTime)
            {
                // Walking the dressed hierarchy is by far the most expensive
                // thing this stage does, and it used to run every frame just
                // to notice a wardrobe change. The doll is a still photo: half
                // a second of latency on a new hat is invisible, the per-frame
                // scan was not.
                _nextSignatureTime = Time.unscaledTime + SignatureIntervalSeconds;
                var signature = SourceVisualSignature(source);
                if (signature != _sourceVisualSignature)
                {
                    BuildClone(source, signature);
                }
            }

            if (_clone == null)
            {
                SetStageEnabled(false);
                return;
            }

            SetStageEnabled(true);
            // A render texture that renders once can also lose its contents
            // once — on a device reset or a focus change the portrait would
            // stay blank forever with nothing to redraw it.
            if (_texture != null && !_texture.IsCreated())
            {
                _texture.Create();
                _renderDirty = true;
            }

            if (_settleFrames > 0)
            {
                _settleFrames--;
                ApplySeveredBonesAfterAnimator();
                _renderDirty = true;
                if (_settleFrames == 0)
                {
                    FreezeClone();
                }
            }

            // URP has no supported manual Camera.Render(), so the camera is
            // armed for exactly the frames whose portrait actually changed and
            // disarmed again on the next one. A still doll costs nothing.
            _camera.enabled = _renderDirty;
            _renderDirty = false;
        }

        /// <summary>
        /// Stops every per-frame cost the clone still carries once its single
        /// Idle frame is on screen: the Animator retargets the whole rig even
        /// at speed 0, and <c>updateWhenOffscreen</c> skins every mesh whether
        /// or not a camera renders the stage.
        /// </summary>
        private void FreezeClone()
        {
            if (_animator != null) _animator.enabled = false;
            if (_lookAt != null) _lookAt.enabled = false;
            if (_clone != null)
            {
                foreach (var follower in
                         _clone.GetComponentsInChildren<FittedProstheticPoseFollower>(true))
                {
                    follower.enabled = false;
                }
            }

            foreach (var skin in _cloneSkins)
            {
                if (skin == null) continue;
                skin.updateWhenOffscreen = false;
                // The pose can never change again, so one generous envelope
                // keeps the frozen doll from being culled without asking Unity
                // to re-skin it for a bounds recompute.
                var bounds = skin.localBounds;
                bounds.Expand(bounds.size.magnitude);
                skin.localBounds = bounds;
            }
        }

        private void BuildClone(Transform source, int visualSignature)
        {
            DestroyClone();
            _modelPivot.gameObject.SetActive(false);
            CacheSourceMaterials(source);

            _clone = Instantiate(source.gameObject, _modelPivot, false);
            _clone.name = $"Character Doll NPC {_npcId}";
            _clone.transform.localPosition = Vector3.zero;
            _clone.transform.localRotation = Quaternion.identity;
            _modelPivot.localRotation = Quaternion.Euler(0f, _yaw, 0f);

            var layer = LayerMask.NameToLayer("Portrait");
            if (layer >= 0)
            {
                SetLayerDeep(_clone.transform, layer);
                _camera.cullingMask = 1 << layer;
                _keyLight.cullingMask = 1 << layer;
                _rimLight.cullingMask = 1 << layer;
            }

            MapWornRenderers(source, _clone.transform);
            DisableCloneBehaviour();

            _animator = _clone.GetComponentInChildren<Animator>(true);
            if (_animator != null)
            {
                _animator.enabled = true;
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.Rebind();
                _animator.SetFloat(SpeedParam, 0f);
                if (_animator.HasState(0, IdleState))
                {
                    _animator.Play(IdleState, 0, 0f);
                }
                _animator.Update(0f);
                // The portrait is a deterministic studio pose, not another
                // live actor. Keeping the cloned Animator ticking lets copied
                // sitting/lying parameters transition it away from the Idle
                // pose after FrameClone has already fixed the camera.
                _animator.speed = 0f;
            }

            _lookAt = _clone.GetComponentInChildren<LookAtIK>(true);
            if (_lookAt != null)
            {
                _lookAt.enabled = true;
                _lookAt.solver.target = _gazeTarget;
                _lookAt.solver.IKPositionWeight = 1f;
                _lookAt.solver.headWeight = 0.08f;
                _lookAt.solver.eyesWeight = 1f;
                _lookAt.solver.bodyWeight = 0f;
                _lookAt.solver.clampWeight = 0.5f;
                _lookAt.solver.clampWeightEyes = 0.2f;
            }

            foreach (var follower in _clone.GetComponentsInChildren<FittedProstheticPoseFollower>(true))
            {
                follower.enabled = true;
                follower.RefreshNow();
            }

            _cloneSkins.Clear();
            _clone.GetComponentsInChildren(true, _cloneSkins);
            foreach (var skin in _cloneSkins)
            {
                // Correct bounds while the clone is still being posed; the
                // settle window turns this back off once the pose is final.
                skin.updateWhenOffscreen = true;
            }

            try
            {
                BuildHealthRenderer();
            }
            catch (Exception exception)
            {
                // The HP overlay is optional presentation. A malformed mesh or
                // renderer must never abort clone finalisation: that used to
                // leave _sourceRootId unset and rebuild the entire doll every
                // LateUpdate, producing a purple/empty RenderTexture.
                DestroyHealthOverlay();
                SetBuildError($"Health renderer failed: {exception.Message}");
            }
            if (_healthBodyRenderer != null && !_cloneSkins.Contains(_healthBodyRenderer))
            {
                _cloneSkins.Add(_healthBodyRenderer);
            }

            CacheDistalBones();
            RepaintHealthMesh();
            _sourceRootId = source.GetInstanceID();
            _sourceVisualSignature = visualSignature;
            _cloneGeneration++;
            _settleFrames = SettleFrames;
            _renderDirty = true;
            ApplyMode();
            FrameClone();
        }

        private void CacheSourceMaterials(Transform source)
        {
            // Instantiate copies the per-NPC material instances the skin and
            // garment painters own, so a rebuild leaks a whole material set
            // unless the clone's own copies are destroyed with it. Anything
            // the live actor still references is recorded here and never
            // touched — destroying a shared instance would strip the colonist
            // herself.
            _sourceMaterialIds.Clear();
            _rendererScratch.Clear();
            source.GetComponentsInChildren(true, _rendererScratch);
            foreach (var renderer in _rendererScratch)
            {
                if (renderer == null) continue;
                renderer.GetSharedMaterials(_materialScratch);
                foreach (var material in _materialScratch)
                {
                    if (material != null) _sourceMaterialIds.Add(material.GetInstanceID());
                }
            }
        }

        private void BuildHealthRenderer()
        {
            _buildError = string.Empty;
            if (!ActorBodyResolver.TryResolve(_clone, out _normalBodyRenderer, out var error))
            {
                SetBuildError(error);
                return;
            }

            var sourceMesh = _normalBodyRenderer.sharedMesh;
            if (!sourceMesh.isReadable)
            {
                SetBuildError($"FBX mesh '{sourceMesh.name}' is not Read/Write enabled.");
                return;
            }

            var template = Resources.Load<Material>("HexLive/UI/HealthDoll");
            var shader = template != null ? template.shader : Shader.Find("HexLive/HealthDoll");
            if (shader == null || !string.Equals(
                    shader.name, "HexLive/HealthDoll", StringComparison.Ordinal) ||
                !shader.isSupported)
            {
                SetBuildError("Shader HexLive/HealthDoll is unavailable or unsupported.");
                return;
            }

            _healthMesh = Instantiate(sourceMesh);
            _healthMesh.name = $"{sourceMesh.name}_CharacterHealth";
            _vertexZone = ClassifyVertices(_normalBodyRenderer, _healthMesh);
            _colorScratch = new Color32[_healthMesh.vertexCount];
            _healthMaterial = template != null ? new Material(template) : new Material(shader);
            _healthMaterial.name = "CharacterDollHealth";

            // Unity permits only one Renderer on a GameObject. Keep the second
            // renderer on an identity child while binding it to the exact same
            // skeleton; its object-to-world matrix therefore matches the body.
            var overlayObject = new GameObject("CharacterDollHealthBody");
            overlayObject.transform.SetParent(_normalBodyRenderer.transform, false);
            overlayObject.layer = _normalBodyRenderer.gameObject.layer;
            _healthBodyRenderer = overlayObject.AddComponent<SkinnedMeshRenderer>();
            _healthBodyRenderer.enabled = false;
            _healthBodyRenderer.sharedMesh = _healthMesh;
            _healthBodyRenderer.bones = _normalBodyRenderer.bones;
            _healthBodyRenderer.rootBone = _normalBodyRenderer.rootBone;
            _healthBodyRenderer.localBounds = _normalBodyRenderer.localBounds;
            _healthBodyRenderer.updateWhenOffscreen = true;
            _healthBodyRenderer.shadowCastingMode = _normalBodyRenderer.shadowCastingMode;
            _healthBodyRenderer.receiveShadows = _normalBodyRenderer.receiveShadows;
            _healthBodyRenderer.renderingLayerMask = _normalBodyRenderer.renderingLayerMask;

            var sourceMaterials = _normalBodyRenderer.sharedMaterials;
            var materials = new Material[_healthMesh.subMeshCount];
            var healthSlots = 0;
            for (var i = 0; i < materials.Length; i++)
            {
                var sourceMaterial = i < sourceMaterials.Length ? sourceMaterials[i] : null;
                if (IsSkinSlot(sourceMaterial))
                {
                    materials[i] = _healthMaterial;
                    healthSlots++;
                }
                else
                {
                    materials[i] = sourceMaterial;
                }
            }

            if (healthSlots == 0)
            {
                Destroy(_healthBodyRenderer.gameObject);
                _healthBodyRenderer = null;
                SetBuildError($"FBX mesh '{sourceMesh.name}' has no skin material slots.");
                return;
            }

            _healthBodyRenderer.sharedMaterials = materials;
        }

        private void ApplyMode()
        {
            var visible = _mode != CharacterDollMode.Hidden && _clone != null;
            if (_modelPivot != null)
            {
                _modelPivot.gameObject.SetActive(visible);
            }

            var useHealth = visible && _mode == CharacterDollMode.Health &&
                            _healthBodyRenderer != null;
            if (_normalBodyRenderer != null)
            {
                _normalBodyRenderer.enabled = visible && !useHealth;
            }
            if (_healthBodyRenderer != null)
            {
                _healthBodyRenderer.enabled = useHealth;
            }

            _renderDirty = true;
            SetStageEnabled(visible);
        }

        private void SetStageEnabled(bool enabled)
        {
            // The camera is armed by LateUpdate only for a changed frame.
            // Animator and IK belong to the settle window and must never be
            // revived after FreezeClone.
            if (!enabled && _camera != null) _camera.enabled = false;
            if (_keyLight != null) _keyLight.enabled = enabled;
            if (_rimLight != null) _rimLight.enabled = enabled;
        }

        private void DisableCloneBehaviour()
        {
            foreach (var behaviour in _clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                behaviour.enabled = false;
            }

            foreach (var collider in _clone.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
            }
            foreach (var body in _clone.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.detectCollisions = false;
            }
            foreach (var particles in _clone.GetComponentsInChildren<ParticleSystem>(true))
            {
                particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private void MapWornRenderers(Transform source, Transform clone)
        {
            _wornByRenderer.Clear();
            var sourceWears = source.GetComponentsInChildren<Wear>(true);
            var cloneWears = clone.GetComponentsInChildren<Wear>(true);
            var count = Mathf.Min(sourceWears.Length, cloneWears.Length);
            for (var i = 0; i < count; i++)
            {
                var definitionId = sourceWears[i].DefinitionId;
                if (string.IsNullOrEmpty(definitionId) || !_wantedWorn.Contains(definitionId))
                {
                    continue;
                }

                foreach (var renderer in cloneWears[i].GetComponentsInChildren<Renderer>(true))
                {
                    _wornByRenderer[renderer] = definitionId;
                }
            }
        }

        private void CacheDistalBones()
        {
            _distalBones.Clear();
            foreach (var pair in SeveredDistalBone)
            {
                var bone = FindDeep(_clone.transform, pair.Value);
                if (bone != null)
                {
                    _distalBones[pair.Key] = (bone, bone.localScale);
                }
            }
        }

        private void ApplySeveredBonesAfterAnimator()
        {
            foreach (var pair in _distalBones)
            {
                var zone = ZoneIndex(pair.Key);
                pair.Value.bone.localScale = zone >= 0 && _zoneSevered[zone]
                    ? Vector3.one * 0.0001f
                    : pair.Value.scale;
            }
        }

        private void RepaintHealthMesh()
        {
            if (_healthMesh == null || _vertexZone == null || _colorScratch == null)
            {
                return;
            }

            var zoneColors = new Color32[ZoneOrder.Length];
            for (var i = 0; i < ZoneOrder.Length; i++)
            {
                var color = StatusColor(_zoneHp[i], _zoneSevered[i]);
                if (_zoneBandaged[i] && !_zoneSevered[i])
                {
                    color = Color.Lerp(color, Color.white, 0.30f);
                }
                zoneColors[i] = color;
            }

            for (var vertex = 0; vertex < _colorScratch.Length; vertex++)
            {
                _colorScratch[vertex] = zoneColors[_vertexZone[vertex]];
            }
            _healthMesh.colors32 = _colorScratch;
            ApplySeveredBonesAfterAnimator();
            _renderDirty = true;
        }

        private static int[] ClassifyVertices(SkinnedMeshRenderer skin, Mesh mesh)
        {
            var bones = skin.bones;
            var boneZones = new int[bones.Length];
            for (var i = 0; i < bones.Length; i++)
            {
                boneZones[i] = 1;
                for (var cursor = bones[i]; cursor != null; cursor = cursor.parent)
                {
                    if (ZoneRootBones.TryGetValue(cursor.name, out var zone))
                    {
                        boneZones[i] = zone;
                        break;
                    }
                }
            }

            var result = new int[mesh.vertexCount];
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var allWeights = mesh.GetAllBoneWeights();
            if (bonesPerVertex.Length != mesh.vertexCount)
            {
                for (var i = 0; i < result.Length; i++) result[i] = 1;
                return result;
            }

            var offset = 0;
            for (var vertex = 0; vertex < result.Length; vertex++)
            {
                var count = bonesPerVertex[vertex];
                var zone = 1;
                if (count > 0)
                {
                    var dominant = allWeights[offset].boneIndex;
                    if (dominant >= 0 && dominant < boneZones.Length)
                    {
                        zone = boneZones[dominant];
                    }
                }
                result[vertex] = zone;
                offset += count;
            }
            return result;
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
                if (name.Contains(hint)) return false;
            }
            return true;
        }

        private void FrameClone()
        {
            _modelPivot.gameObject.SetActive(true);
            // One photo point per actor. It is measured on that actor's first
            // clone and reused forever after, so nothing a colonist puts on,
            // loses or bleeds can move the camera: rebuilds stopped being able
            // to re-aim the portrait, which is what made the doll jump.
            if (_actorMesh.Length > 0 &&
                _framingByActor.TryGetValue(_actorMesh, out var cached))
            {
                ApplyFraming(cached);
                return;
            }

            if (!TryMeasureFraming(out var framing))
            {
                return;
            }

            if (_actorMesh.Length > 0)
            {
                _framingByActor[_actorMesh] = framing;
            }
            ApplyFraming(framing);
        }

        /// <summary>
        /// Measures the actor's photo point from its own body in the frozen
        /// Idle pose — never from clothes, hair or props (they come and go),
        /// never through the player's yaw, and never with an amputation
        /// applied (a lost leg must not zoom the portrait in).
        /// </summary>
        private bool TryMeasureFraming(out DollFraming framing)
        {
            framing = default;
            var pivotRotation = _modelPivot.localRotation;
            _modelPivot.localRotation = Quaternion.identity;
            RestoreDistalBones();

            var hasBounds = false;
            var bounds = new Bounds();
            // Renderer.bounds can still contain the source's last sitting or
            // lying pose; localBounds is only an authored culling envelope
            // with large invisible margins. Baking the rebound Idle clone
            // gives the actual visible geometry instead.
            if (_normalBodyRenderer != null &&
                TryGetStableVisualWorldBounds(_normalBodyRenderer, out var bodyBounds))
            {
                bounds = bodyBounds;
                hasBounds = true;
            }
            else
            {
                // No resolved body (an overlay build error): fall back to the
                // whole clone so the window still frames something.
                _rendererScratch.Clear();
                _clone.GetComponentsInChildren(true, _rendererScratch);
                foreach (var renderer in _rendererScratch)
                {
                    if (renderer == null || !renderer.enabled ||
                        renderer is ParticleSystemRenderer) continue;
                    if (!TryGetStableVisualWorldBounds(renderer, out var rendererBounds)) continue;
                    if (!hasBounds)
                    {
                        bounds = rendererBounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(rendererBounds);
                    }
                }
            }

            ApplySeveredBonesAfterAnimator();
            _modelPivot.localRotation = pivotRotation;
            if (!hasBounds)
            {
                return false;
            }

            var focus = bounds.center;
            var halfFov = _camera.fieldOfView * Mathf.Deg2Rad * 0.5f;
            var verticalDistance = bounds.extents.y / Mathf.Tan(halfFov);
            var horizontalTangent = Mathf.Tan(halfFov) * (TextureWidth / (float)TextureHeight);
            var horizontalDistance = bounds.extents.x / Mathf.Max(0.001f, horizontalTangent);
            // Perspective fitting must include the half-depth nearest to the
            // camera. Ignoring it made deeper rigs project larger than the X/Y
            // fit predicted (female heads/hair clipped while the male doll fit).
            var distance = Mathf.Max(
                1f,
                bounds.extents.z +
                Mathf.Max(verticalDistance, horizontalDistance) * DollFramePadding);
            var eye = focus + Vector3.forward * distance;
            framing = new DollFraming(
                transform.InverseTransformPoint(focus),
                transform.InverseTransformPoint(eye));
            return true;
        }

        private void ApplyFraming(DollFraming framing)
        {
            var focus = transform.TransformPoint(framing.Focus);
            var eye = transform.TransformPoint(framing.Eye);
            _camera.transform.SetPositionAndRotation(
                eye, Quaternion.LookRotation(focus - eye, Vector3.up));
            _gazeTarget.position = eye;

            _keyLight.transform.position = focus + new Vector3(-2.1f, 2.4f, 3.2f);
            _keyLight.transform.rotation = Quaternion.LookRotation(
                focus - _keyLight.transform.position, Vector3.up);
            _rimLight.transform.position = focus + new Vector3(2.2f, 1.6f, -1.7f);
            _renderDirty = true;
        }

        private void RestoreDistalBones()
        {
            foreach (var pair in _distalBones)
            {
                if (pair.Value.bone != null) pair.Value.bone.localScale = pair.Value.scale;
            }
        }

        private static bool TryGetStableVisualWorldBounds(
            Renderer renderer, out Bounds worldBounds)
        {
            worldBounds = default;
            Bounds local;
            Mesh bakedMesh = null;
            if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
            {
                bakedMesh = new Mesh { name = "CharacterDollFraming" };
                skin.BakeMesh(bakedMesh, false);
                local = bakedMesh.bounds;
            }
            else
            {
                local = renderer.localBounds;
            }

            var min = local.min;
            var max = local.max;
            if (local.size.sqrMagnitude <= 0.00000001f ||
                !float.IsFinite(min.x) || !float.IsFinite(min.y) || !float.IsFinite(min.z) ||
                !float.IsFinite(max.x) || !float.IsFinite(max.y) || !float.IsFinite(max.z))
            {
                if (bakedMesh != null) Destroy(bakedMesh);
                return false;
            }

            var matrix = renderer.localToWorldMatrix;
            var first = matrix.MultiplyPoint3x4(min);
            worldBounds = new Bounds(first, Vector3.zero);
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++)
            {
                var corner = new Vector3(
                    x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z);
                worldBounds.Encapsulate(matrix.MultiplyPoint3x4(corner));
            }

            if (bakedMesh != null) Destroy(bakedMesh);
            return true;
        }

        private int SourceVisualSignature(Transform source)
        {
            _rendererScratch.Clear();
            source.GetComponentsInChildren(true, _rendererScratch);
            unchecked
            {
                var hash = 17;
                foreach (var renderer in _rendererScratch)
                {
                    // Blood and impact VFX are particle renderers that appear
                    // on a bone and vanish on their own timer. Counting them
                    // rebuilt the entire doll twice per wound — the loudest
                    // source of both the stutter and the framing jumps.
                    if (renderer == null || renderer is ParticleSystemRenderer) continue;
                    hash = hash * 31 + renderer.GetInstanceID();
                    hash = hash * 31 + renderer.enabled.GetHashCode();
                    hash = hash * 31 + renderer.gameObject.activeSelf.GetHashCode();
                    if (renderer is SkinnedMeshRenderer skin)
                    {
                        hash = hash * 31 + (skin.sharedMesh != null
                            ? skin.sharedMesh.GetInstanceID()
                            : 0);
                    }
                    // The sharedMaterials getter allocates a fresh array per
                    // renderer; this list is reused instead.
                    renderer.GetSharedMaterials(_materialScratch);
                    foreach (var material in _materialScratch)
                    {
                        hash = hash * 31 + (material != null ? material.GetInstanceID() : 0);
                    }
                }
                return hash;
            }
        }

        private void SetBuildError(string error)
        {
            _buildError = error ?? string.Empty;
            Debug.LogWarning($"[CharacterDoll] health overlay unavailable for " +
                             $"actor={_actorMesh} npc={_npcId}: {_buildError}");
        }

        private void DestroyHealthOverlay()
        {
            if (_healthBodyRenderer != null)
            {
                Destroy(_healthBodyRenderer.gameObject);
                _healthBodyRenderer = null;
            }
            if (_healthMesh != null)
            {
                Destroy(_healthMesh);
                _healthMesh = null;
            }
            if (_healthMaterial != null)
            {
                Destroy(_healthMaterial);
                _healthMaterial = null;
            }
            _vertexZone = null;
            _colorScratch = null;
        }

        private Light CreateLight(
            string objectName,
            LightType type,
            Color color,
            float intensity,
            float range,
            float spotAngle)
        {
            var lightObject = new GameObject(objectName);
            lightObject.transform.SetParent(transform, false);
            var light = lightObject.AddComponent<Light>();
            light.type = type;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.Soft;
            if (type == LightType.Spot) light.spotAngle = spotAngle;
            return light;
        }

        private void DestroyClone()
        {
            _wornByRenderer.Clear();
            _distalBones.Clear();
            _animator = null;
            _lookAt = null;
            _normalBodyRenderer = null;
            _zoneSignatureValid = false;
            _buildError = string.Empty;

            DestroyHealthOverlay();
            if (_clone != null)
            {
                ReleaseCloneMaterials();
                _clone.SetActive(false);
                Destroy(_clone);
                _clone = null;
            }
            _cloneSkins.Clear();
            _settleFrames = 0;
        }

        private void ReleaseCloneMaterials()
        {
            _rendererScratch.Clear();
            _clone.GetComponentsInChildren(true, _rendererScratch);
            foreach (var renderer in _rendererScratch)
            {
                if (renderer == null) continue;
                renderer.GetSharedMaterials(_materialScratch);
                foreach (var material in _materialScratch)
                {
                    if (material == null ||
                        _sourceMaterialIds.Contains(material.GetInstanceID()))
                    {
                        continue;
                    }
                    Destroy(material);
                }
            }
        }

        private static int HashEntries(int hash, IReadOnlyList<string> list)
        {
            if (list == null) return hash;
            for (var i = 0; i < list.Count; i++)
            {
                hash = hash * 31 + (list[i]?.GetHashCode() ?? 0);
            }
            return hash;
        }

        private static int ZoneIndex(string name)
        {
            for (var i = 0; i < ZoneOrder.Length; i++)
            {
                if (string.Equals(ZoneOrder[i], name, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(child.name, name, StringComparison.Ordinal)) return child;
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

        private void OnDestroy()
        {
            DestroyClone();
            if (_camera != null) _camera.targetTexture = null;
            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
            }
        }
    }
}
