using System;
using System.Collections.Generic;
using System.Globalization;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Wearing;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

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
        // 768 * 7/6. The extra rows are the headroom below: they are added as
        // PIXELS, not taken from the body, so opening the frame upward leaves
        // the figure at exactly the same on-screen scale. Both viewports derive
        // their box from this aspect, and the windows are anchored at their
        // bottom, so the feet do not move when the shutter opens higher.
        private const int TextureHeight = 896;
        // A portrait is framed on HEIGHT. Headroom is what keeps hair, hats and
        // the crown of the head inside the frame; the old single symmetric pad
        // spent half of itself under the feet and cropped the head instead.
        private const float HeadroomFraction = 0.30f;
        private const float FootroomFraction = 0.03f;
        private const string StudioPosePath = "HexLive/Poses/Female Standing Pose";
        private const float StageSeparation = 80f;
        private const float SignatureIntervalSeconds = 0.5f;
        private const int SettleFrames = 2;
        private static readonly int SpeedParam = Animator.StringToHash("Speed");
        private static readonly int IdleState = Animator.StringToHash("Base Layer.Idle");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int TearAmountId = Shader.PropertyToID("_TearAmount");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        private static readonly int MetallicGlossMapId = Shader.PropertyToID("_MetallicGlossMap");
        private static readonly int TearMaskTexId = Shader.PropertyToID("_TearMaskTex");
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

        private static AnimationClip _studioPose;
        private static bool _studioPoseTried;

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
        private readonly Dictionary<Renderer, MeshCollider> _wornPickColliders = new();
        private readonly List<Mesh> _wornPickMeshes = new();
        private readonly HashSet<string> _wantedWorn = new();
        private readonly List<Renderer> _rendererScratch = new();
        private readonly List<Renderer> _cloneRendererScratch = new();
        private readonly List<Material> _materialScratch = new();
        private readonly List<Material> _cloneMaterialScratch = new();
        private readonly List<SurfaceBinding> _surfaceBindings = new();
        private MaterialPropertyBlock _surfaceBlock;
        private readonly List<SkinnedMeshRenderer> _cloneSkins = new();
        private Wear[] _cloneWears = Array.Empty<Wear>();
        private readonly HashSet<Wear> _equippedCloneWears = new();
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
        private VisualWearLayer _visibleWearLayer = VisualWearLayer.Bags;
        private int _npcId = -1;
        private string _actorMesh = string.Empty;
        private int _sourceRootId;
        private int _sourceVisualSignature;
        private int _sourceSurfaceSignature;
        private int _zoneSignature;
        private bool _zoneSignatureValid;
        private float _yaw;
        private string _buildError = string.Empty;
        private int _cloneGeneration;
        private float _nextSignatureTime;
        private int _settleFrames;
        private bool _renderDirty;

        private readonly struct SurfaceBinding
        {
            public SurfaceBinding(Renderer source, Renderer clone, int slotCount)
            {
                Source = source;
                Clone = clone;
                SlotCount = slotCount;
            }

            public Renderer Source { get; }
            public Renderer Clone { get; }
            public int SlotCount { get; }
        }

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
            // MaterialPropertyBlock allocates a native Unity object, so it
            // cannot be created by a MonoBehaviour field initializer. Doing
            // that aborts construction and leaves every later field null.
            _surfaceBlock = new MaterialPropertyBlock();
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

        /// <summary>
        /// Changes only the presentation cut of the already-built inventory
        /// doll. No source actor, equipment state or cloned hierarchy is
        /// touched, so browsing the four clothing layers is allocation-free
        /// after the small renderer pass below.
        /// </summary>
        public void SetVisibleWearLayer(VisualWearLayer layer)
        {
            if (_visibleWearLayer == layer)
            {
                return;
            }

            _visibleWearLayer = layer;
            if (_clone != null && _mode == CharacterDollMode.Inventory)
            {
                ApplyWearLayerVisibility();
                _renderDirty = true;
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
            foreach (var pair in _wornPickColliders)
            {
                var renderer = pair.Key;
                var collider = pair.Value;
                if (renderer == null || collider == null || !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy ||
                    !collider.Raycast(ray, out var hit, _camera.farClipPlane) ||
                    hit.distance >= nearest)
                {
                    continue;
                }

                nearest = hit.distance;
                definitionId = _wornByRenderer[renderer];
            }

            // A runtime garment without usable mesh data remains selectable by
            // its bounds. Exact triangle hits always win over this fallback, so
            // a large leggings AABB can no longer steal hover from a visible bra.
            foreach (var pair in _wornByRenderer)
            {
                var renderer = pair.Key;
                if (_wornPickColliders.ContainsKey(renderer) || renderer == null ||
                    !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
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
                else
                {
                    // Instantiate copies material references but not the
                    // per-submesh MaterialPropertyBlock used by tanning,
                    // wetness and garment condition. Painted skin textures
                    // can also finish one slot at a time after the still
                    // portrait was taken. Synchronise those surfaces on the
                    // existing clone and take one new photo; never rebuild the
                    // skeleton for a colour or texture update.
                    SynchronizeSurfaceState(force: false);
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

            BuildWornPickColliders();
        }

        private void BuildClone(Transform source, int visualSignature)
        {
            DestroyClone();
            _modelPivot.gameObject.SetActive(false);
            CacheSourceMaterials(source);

            // §31.10A / bug #132: BodyBones applies heel lift directly to the
            // live hip in LateUpdate. Instantiate therefore copies a skeleton
            // already raised above its neutral photo point. Remember the exact
            // blended offset before cloning; the portrait removes it before
            // evaluating the fixed studio pose.
            var sourceBodyBones = source.GetComponentInChildren<BodyBones>(true);
            var copiedHeelLift = sourceBodyBones != null
                ? sourceBodyBones.AppliedHeelLift
                : 0f;

            _clone = Instantiate(source.gameObject, _modelPivot, false);
            _clone.name = $"Character Doll NPC {_npcId}";
            _clone.transform.localPosition = Vector3.zero;
            _clone.transform.localRotation = Quaternion.identity;
            _modelPivot.localRotation = Quaternion.Euler(0f, _yaw, 0f);

            BindSurfaceRenderers(source, _clone.transform);

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
            // The clone is instantiated INACTIVE so the live solvers it carries
            // (FinalIK, cloth, colliders) never reach their first Awake — but an
            // Animator on an inactive hierarchy cannot be evaluated either, so
            // the studio pose silently did nothing and the doll kept the exact
            // bone transforms Instantiate copied from the walking, sitting or
            // lying colonist. Behaviours are dead by now; wake the hierarchy up
            // before posing it.
            _modelPivot.gameObject.SetActive(true);

            _animator = _clone.GetComponentInChildren<Animator>(true);
            if (_animator != null)
            {
                _animator.enabled = true;
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var clonedHips = _animator.GetBoneTransform(HumanBodyBones.Hips);
                if (clonedHips != null && Mathf.Abs(copiedHeelLift) > 0.0001f)
                {
                    clonedHips.position -= _clone.transform.up * copiedHeelLift;
                }
                _animator.Rebind();
                _animator.SetFloat(SpeedParam, 0f);
                _animator.speed = 0f;
                ApplyStudioPose();
                // Nothing may touch the pose again. The clone carries the
                // live colonist's sitting/lying/crawling parameters, and an
                // enabled Animator eventually acts on them — that is how every
                // earlier fix ended up framing a pose it had not measured.
                _animator.enabled = false;
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
            SynchronizeSurfaceState(force: true);
            RepaintHealthMesh();
            _sourceRootId = source.GetInstanceID();
            _sourceVisualSignature = visualSignature;
            _cloneGeneration++;
            _settleFrames = SettleFrames;
            _renderDirty = true;
            ApplyMode();
            FrameClone();
        }

        /// <summary>
        /// Puts the clone into THE studio pose: one evaluated frame of the
        /// fixed "Female Standing Pose" clip, played through a throwaway
        /// playable graph so the actor's own controller — and the sitting,
        /// lying and crawling parameters cloned with it — never take part.
        /// This is what makes both the portrait and the frame measured from it
        /// identical in every session, for every colonist of that actor.
        /// </summary>
        private void ApplyStudioPose()
        {
            if (!_studioPoseTried)
            {
                _studioPoseTried = true;
                _studioPose = HexLive.UnityPresentation.Content.AtomicResources.Load<AnimationClip>(StudioPosePath);
                // A non-humanoid import of the pose (it happened once: the FBX
                // imported before the Poses postprocessor compiled and came out
                // Generic) flattens the figure into a sheet. Refuse it.
                if (_studioPose != null && !_studioPose.humanMotion)
                {
                    Debug.LogWarning(
                        "[CharacterDoll] pose clip is not humanoid — reimport " +
                        "Assets/Resources/HexLive/Poses (delete its .meta); " +
                        "using the controller idle instead.");
                    _studioPose = null;
                }
            }

            if (_studioPose != null && _animator.avatar != null)
            {
                AnimationPlayableUtilities.PlayClip(_animator, _studioPose, out var graph);
                graph.Evaluate(0f);
                graph.Destroy();
                return;
            }

            // Fallback only: the controller's own Idle. It is not deterministic
            // — the copied parameters can still own the transition — so the
            // missing pose asset is the real defect to fix.
            if (_animator.HasState(0, IdleState))
            {
                _animator.Play(IdleState, 0, 0f);
            }
            _animator.Update(0f);
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

        private void BindSurfaceRenderers(Transform source, Transform clone)
        {
            _surfaceBindings.Clear();
            _rendererScratch.Clear();
            _cloneRendererScratch.Clear();
            source.GetComponentsInChildren(true, _rendererScratch);
            clone.GetComponentsInChildren(true, _cloneRendererScratch);

            // Instantiate preserves component traversal order. Validate every
            // pair so a malformed runtime hierarchy cannot copy one actor's
            // skin overrides onto an unrelated renderer.
            var count = Mathf.Min(_rendererScratch.Count, _cloneRendererScratch.Count);
            for (var i = 0; i < count; i++)
            {
                var sourceRenderer = _rendererScratch[i];
                var cloneRenderer = _cloneRendererScratch[i];
                if (sourceRenderer == null || cloneRenderer == null ||
                    sourceRenderer is ParticleSystemRenderer ||
                    sourceRenderer.GetType() != cloneRenderer.GetType() ||
                    !string.Equals(sourceRenderer.gameObject.name,
                        cloneRenderer.gameObject.name, StringComparison.Ordinal))
                {
                    continue;
                }

                sourceRenderer.GetSharedMaterials(_materialScratch);
                cloneRenderer.GetSharedMaterials(_cloneMaterialScratch);
                var slotCount = Mathf.Min(_materialScratch.Count, _cloneMaterialScratch.Count);
                if (slotCount > 0)
                {
                    _surfaceBindings.Add(new SurfaceBinding(
                        sourceRenderer, cloneRenderer, slotCount));
                }
            }
        }

        private void SynchronizeSurfaceState(bool force)
        {
            var signature = SourceSurfaceSignature();
            if (!force && signature == _sourceSurfaceSignature)
            {
                return;
            }

            foreach (var binding in _surfaceBindings)
            {
                if (binding.Source == null || binding.Clone == null)
                {
                    continue;
                }

                for (var slot = 0; slot < binding.SlotCount; slot++)
                {
                    // GetPropertyBlock completely overwrites/clears the block.
                    // Passing null for an empty source block is important: it
                    // also removes an older tan/wet override from the clone.
                    binding.Source.GetPropertyBlock(_surfaceBlock, slot);
                    binding.Clone.SetPropertyBlock(
                        _surfaceBlock.isEmpty ? null : _surfaceBlock, slot);
                }
            }

            _sourceSurfaceSignature = signature;
            _renderDirty = true;
        }

        private int SourceSurfaceSignature()
        {
            unchecked
            {
                var hash = 23;
                foreach (var binding in _surfaceBindings)
                {
                    var renderer = binding.Source;
                    if (renderer == null)
                    {
                        hash *= 31;
                        continue;
                    }

                    renderer.GetSharedMaterials(_materialScratch);
                    var slotCount = Mathf.Min(binding.SlotCount, _materialScratch.Count);
                    for (var slot = 0; slot < slotCount; slot++)
                    {
                        renderer.GetPropertyBlock(_surfaceBlock, slot);
                        hash = hash * 31 + _surfaceBlock.isEmpty.GetHashCode();
                        hash = HashColor(hash, _surfaceBlock.GetColor(BaseColorId));
                        hash = hash * 31 + _surfaceBlock.GetFloat(SmoothnessId).GetHashCode();
                        hash = hash * 31 + _surfaceBlock.GetFloat(TearAmountId).GetHashCode();
                        hash = HashTexture(hash, _surfaceBlock.GetTexture(BaseMapId));
                        hash = HashTexture(hash, _surfaceBlock.GetTexture(BumpMapId));
                        hash = HashTexture(hash, _surfaceBlock.GetTexture(MetallicGlossMapId));
                        hash = HashTexture(hash, _surfaceBlock.GetTexture(TearMaskTexId));

                        var material = _materialScratch[slot];
                        if (material == null)
                        {
                            hash *= 31;
                            continue;
                        }

                        hash = hash * 31 + material.GetInstanceID();
                        hash = hash * 31 + (material.shader != null
                            ? material.shader.GetInstanceID()
                            : 0);
                        hash = hash * 31 + material.renderQueue;
                        hash = HashMaterialColor(hash, material, BaseColorId);
                        hash = HashMaterialColor(hash, material, ColorId);
                        hash = HashMaterialColor(hash, material, EmissionColorId);
                        hash = HashMaterialFloat(hash, material, SmoothnessId);
                        hash = HashMaterialFloat(hash, material, TearAmountId);
                        hash = HashMaterialTexture(hash, material, BaseMapId);
                        hash = HashMaterialTexture(hash, material, BumpMapId);
                        hash = HashMaterialTexture(hash, material, MetallicGlossMapId);
                        hash = HashMaterialTexture(hash, material, TearMaskTexId);
                    }
                }

                return hash;
            }
        }

        private static int HashMaterialColor(int hash, Material material, int propertyId)
        {
            unchecked
            {
                return material.HasProperty(propertyId)
                    ? HashColor(hash * 31 + 1, material.GetColor(propertyId))
                    : hash * 31;
            }
        }

        private static int HashMaterialFloat(int hash, Material material, int propertyId)
        {
            unchecked
            {
                return material.HasProperty(propertyId)
                    ? (hash * 31 + 1) * 31 + material.GetFloat(propertyId).GetHashCode()
                    : hash * 31;
            }
        }

        private static int HashMaterialTexture(int hash, Material material, int propertyId)
        {
            unchecked
            {
                return material.HasProperty(propertyId)
                    ? HashTexture(hash * 31 + 1, material.GetTexture(propertyId))
                    : hash * 31;
            }
        }

        private static int HashColor(int hash, Color color)
        {
            unchecked
            {
                return hash * 31 + color.GetHashCode();
            }
        }

        private static int HashTexture(int hash, Texture texture)
        {
            if (texture == null)
            {
                return unchecked(hash * 31);
            }

            // Texture identity catches a newly assigned paint target;
            // updateCount catches another GPU repaint into the same target.
            unchecked
            {
                return (hash * 31 + texture.GetInstanceID()) * 31 +
                       (int)texture.updateCount;
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

            ApplyWearLayerVisibility();

            _renderDirty = true;
            SetStageEnabled(visible);
        }

        private void ApplyWearLayerVisibility()
        {
            if (_clone == null)
            {
                return;
            }

            // Health is a diagnostic view and always keeps the complete outfit.
            // Inventory alone exposes a cumulative cut: underwear → clothes →
            // outerwear → bags. The Wear enum order is the serialized contract.
            var visibleLayer = _mode == CharacterDollMode.Inventory
                ? _visibleWearLayer
                : VisualWearLayer.Bags;
            foreach (var wear in _cloneWears)
            {
                if (!_equippedCloneWears.Contains(wear))
                {
                    continue;
                }

                if ((int)wear.Layer <= (int)visibleLayer) wear.Show();
                else wear.Hide();
            }

            // Outerwear can explicitly hide a lower Wear garment slot. The
            // mask is empty by default and applies only while Outerwear is in
            // the cumulative layer cut, so browsing the Wear tab still shows
            // the shirt or trousers being authored beneath the jacket.
            foreach (var lowerWear in _cloneWears)
            {
                if (!_equippedCloneWears.Contains(lowerWear) ||
                    lowerWear.Layer != VisualWearLayer.Wear ||
                    (int)VisualWearLayer.Outerwear > (int)visibleLayer)
                {
                    continue;
                }

                var hiddenByOuterwear = false;
                foreach (var slot in lowerWear.Slots)
                {
                    foreach (var outerwear in _cloneWears)
                    {
                        if (!_equippedCloneWears.Contains(outerwear) ||
                            outerwear.Layer != VisualWearLayer.Outerwear ||
                            !outerwear.HidesWearSlot(slot))
                        {
                            continue;
                        }

                        foreach (var outerSlot in outerwear.Slots)
                        {
                            if (outerSlot != slot) continue;
                            hiddenByOuterwear = true;
                            break;
                        }
                        if (hiddenByOuterwear) break;
                    }
                    if (hiddenByOuterwear) break;
                }

                if (hiddenByOuterwear) lowerWear.Hide();
            }

            // Reapply the same whole-garment underwear rule used by BodyBones.
            // Bags deliberately do not participate: they sit over the outfit
            // but do not make bras, briefs or socks disappear.
            foreach (var underwear in _cloneWears)
            {
                if (!_equippedCloneWears.Contains(underwear) ||
                    underwear.Layer != VisualWearLayer.Underwear ||
                    (int)underwear.Layer > (int)visibleLayer)
                {
                    continue;
                }

                var hiddenByOuter = false;
                foreach (var slot in underwear.Slots)
                {
                    foreach (var outer in _cloneWears)
                    {
                        if (!_equippedCloneWears.Contains(outer) ||
                            (int)outer.Layer <= (int)VisualWearLayer.Underwear ||
                            (int)outer.Layer > (int)VisualWearLayer.Outerwear ||
                            (int)outer.Layer > (int)visibleLayer ||
                            !outer.HeedHideUnderwearSlot(slot))
                        {
                            continue;
                        }

                        foreach (var outerSlot in outer.Slots)
                        {
                            if (outerSlot != slot) continue;
                            hiddenByOuter = true;
                            break;
                        }
                        if (hiddenByOuter) break;
                    }
                    if (hiddenByOuter) break;
                }

                if (hiddenByOuter) underwear.Hide();
                else underwear.Show();
            }

            // Hair is also a Wear component, but it has no simulation item id.
            // Reveal it when the layer cut hides the hat that normally covers it.
            var hairCovered = false;
            foreach (var wear in _cloneWears)
            {
                if (_equippedCloneWears.Contains(wear) &&
                    (int)wear.Layer <= (int)visibleLayer && wear.HidesHair)
                {
                    hairCovered = true;
                    break;
                }
            }
            foreach (var hair in _cloneWears)
            {
                if (_equippedCloneWears.Contains(hair) || hair.Slots.Count != 0)
                {
                    continue;
                }

                if (hairCovered) hair.Hide();
                else hair.Show();
            }

            foreach (var pair in _wornPickColliders)
            {
                if (pair.Value != null)
                {
                    pair.Value.enabled = pair.Key != null && pair.Key.enabled &&
                                         pair.Key.gameObject.activeInHierarchy;
                }
            }
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
            _equippedCloneWears.Clear();
            var sourceWears = source.GetComponentsInChildren<Wear>(true);
            var cloneWears = clone.GetComponentsInChildren<Wear>(true);
            _cloneWears = cloneWears;
            var count = Mathf.Min(sourceWears.Length, cloneWears.Length);
            for (var i = 0; i < count; i++)
            {
                var definitionId = sourceWears[i].DefinitionId;
                if (string.IsNullOrEmpty(definitionId) || !_wantedWorn.Contains(definitionId))
                {
                    continue;
                }

                var cloneWear = cloneWears[i];
                _equippedCloneWears.Add(cloneWear);
                foreach (var renderer in cloneWear.GetComponentsInChildren<Renderer>(true))
                {
                    _wornByRenderer[renderer] = definitionId;
                }
            }
        }

        /// <summary>
        /// Bakes the final frozen clothing pose once and gives every garment an
        /// exact, stage-local picking surface. Bounds alone overlap heavily on
        /// layered outfits; triangle raycasts make the foremost actually drawn
        /// garment win without taking another camera render on pointer movement.
        /// </summary>
        private void BuildWornPickColliders()
        {
            ReleaseWornPickMeshes();
            foreach (var pair in _wornByRenderer)
            {
                var renderer = pair.Key;
                if (renderer == null)
                {
                    continue;
                }

                Mesh mesh = null;
                var ownsMesh = false;
                if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                {
                    mesh = new Mesh { name = $"CharacterDollPick_{pair.Value}" };
                    ownsMesh = true;
                    try
                    {
                        skin.BakeMesh(mesh, false);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning(
                            $"[CharacterDoll] cannot bake pick mesh for '{pair.Value}': " +
                            exception.Message, renderer);
                        Destroy(mesh);
                        continue;
                    }
                }
                else if (renderer is MeshRenderer)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null)
                    {
                        mesh = filter.sharedMesh;
                    }
                }

                if (mesh == null || mesh.vertexCount == 0)
                {
                    if (ownsMesh) Destroy(mesh);
                    continue;
                }

                var pickObject = new GameObject($"DollPick_{pair.Value}")
                {
                    hideFlags = HideFlags.DontSave,
                    layer = renderer.gameObject.layer
                };
                pickObject.transform.SetParent(renderer.transform, false);
                var collider = pickObject.AddComponent<MeshCollider>();
                try
                {
                    collider.sharedMesh = mesh;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        $"[CharacterDoll] cannot build pick mesh for '{pair.Value}': " +
                        exception.Message, renderer);
                    Destroy(pickObject);
                    if (ownsMesh) Destroy(mesh);
                    continue;
                }

                if (collider.sharedMesh == null)
                {
                    Destroy(pickObject);
                    if (ownsMesh) Destroy(mesh);
                    continue;
                }

                collider.enabled = renderer.enabled && renderer.gameObject.activeInHierarchy;
                _wornPickColliders[renderer] = collider;
                if (ownsMesh) _wornPickMeshes.Add(mesh);
            }
        }

        private void ReleaseWornPickMeshes()
        {
            foreach (var collider in _wornPickColliders.Values)
            {
                if (collider != null) Destroy(collider.gameObject);
            }
            _wornPickColliders.Clear();
            foreach (var mesh in _wornPickMeshes)
            {
                if (mesh != null) Destroy(mesh);
            }
            _wornPickMeshes.Clear();
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
            // One photo point per actor. The measurement is reproducible
            // because the clone is in the fixed studio pose, so caching it only
            // saves the work — it cannot freeze a lucky or unlucky reading. And
            // because rebuilds reuse it, nothing a colonist puts on, loses or
            // bleeds can re-aim the portrait.
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
        /// Measures the actor's photo point from its own body in the studio
        /// pose — never from clothes, hair or props (they come and go), never
        /// through the player's yaw, and never with an amputation applied (a
        /// lost leg must not zoom the portrait in).
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
            // with large invisible margins. Baking the clone in the studio
            // pose gives the actual visible geometry instead.
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

            // A portrait is fitted on HEIGHT only, with an explicit headroom
            // above the crown and a thin margin under the feet. The width of a
            // standing figure never binds inside a 2:3 frame, and fitting it
            // was the trap: one measurement of a wide silhouette (a lying or
            // T-posed rig) pulled the camera in and cropped every head after.
            var height = bounds.size.y;
            var top = bounds.max.y + height * HeadroomFraction;
            var bottom = bounds.min.y - height * FootroomFraction;
            var focus = new Vector3(bounds.center.x, (top + bottom) * 0.5f, bounds.center.z);
            var halfFov = _camera.fieldOfView * Mathf.Deg2Rad * 0.5f;
            // Perspective fitting must include the half-depth nearest to the
            // camera. Ignoring it made deeper rigs project larger than the fit
            // predicted (female heads/hair clipped while the male doll fit).
            var distance = Mathf.Max(
                1f,
                bounds.extents.z + (top - bottom) * 0.5f / Mathf.Tan(halfFov));
            var eye = focus + Vector3.forward * distance;
            framing = new DollFraming(
                transform.InverseTransformPoint(focus),
                transform.InverseTransformPoint(eye));
            // One line per actor per session: the next time the doll looks
            // wrong this is a measurement instead of another guess.
            Debug.Log($"[CharacterDoll] frame actor={_actorMesh} " +
                      $"bodyHeight={height:0.###} focusY={focus.y:0.###} " +
                      $"distance={distance:0.###} pose=" +
                      (_studioPose != null ? "studio" : "controller-idle"));
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
            ReleaseWornPickMeshes();
            _wornByRenderer.Clear();
            _cloneWears = Array.Empty<Wear>();
            _equippedCloneWears.Clear();
            _surfaceBindings.Clear();
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
            _sourceSurfaceSignature = 0;
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
