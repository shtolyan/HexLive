using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
public sealed class LobbyCharacterPreview : IDisposable
{
    private static readonly HashSet<LobbyCharacterPreview> Owners = new();
    private static int _nextId = -100000;
    private readonly MonoBehaviour _runner;
    private readonly GameObject _stage;
    private readonly Camera _camera;
    private readonly RenderTexture _texture;
    private readonly Label _status;
    private readonly int _layer;
    private readonly HashSet<string> _bodies = new(), _hair = new(), _garments = new();
    private readonly List<NpcActorView> _retiredViews = new();
    private GameObject _actor;
    private NpcActorView _view;
    private Coroutine _loading;
    private CharacterCreationConfig _character;
    private string _body;
    private bool _disposed;
    private float _yaw = 180, _pitch = 8, _distance = 3.8f;
    private Vector3 _focus = new(0, .95f, 0);
    private int _generation;
    private int _thumbnailFraming;
    public bool IsReady => !Failed && _view != null && _view.CreationHairReady && _view.CreationClothesReady(_character.Clothing) && _loading == null;
    public bool Failed { get; private set; }
    internal string LoadingDependency { get; private set; } = "";
    private readonly bool _creator;
    private bool _frameBody;
    private bool _bodyFraming = true;
    public Animator Animator => _actor != null ? _actor.GetComponentInChildren<Animator>() : null;

    public LobbyCharacterPreview(VisualElement parent, MonoBehaviour runner, CharacterCreationConfig character, bool thumbnail = false, bool creator = false)
    {
        Owners.Add(this); _runner = runner; _layer = thumbnail ? 30 : 31; _creator = creator; _frameBody = creator;
        _stage = new GameObject(thumbnail ? "LobbyThumbnail" : "LobbyPreview");
        _stage.transform.position = new Vector3(0, thumbnail ? -11000 : -10000, 0);
        _texture = new RenderTexture(thumbnail ? 192 : 640, thumbnail ? 192 : 960, 24); _texture.Create();
        var cameraObject = new GameObject("PreviewCamera"); cameraObject.transform.SetParent(_stage.transform, false);
        _camera = cameraObject.AddComponent<Camera>(); _camera.targetTexture = _texture;
        _camera.enabled = !thumbnail;
        _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = new Color(.035f, .047f, .055f, 1);
        _camera.cullingMask = 1 << _layer; _camera.nearClipPlane = .03f; _camera.farClipPlane = 30; _camera.fieldOfView = 34;
        AddLight("Key", new Vector3(-3, 4, 4), new Color(1, .91f, .82f), 3.0f, creator || thumbnail);
        AddLight("Fill", new Vector3(3, 2.8f, 3), new Color(.77f, .86f, 1), 1.8f);
        if (!thumbnail)
        {
            var image = new Image { image = _texture, scaleMode = ScaleMode.ScaleToFit, name = "characterOrbit" };
            image.AddToClassList(creator ? "creator-viewport" : "lobby-doll"); parent.Add(image); image.AddManipulator(new Orbit(this, creator));
            if (creator) image.RegisterCallback<GeometryChangedEvent>(e => {
                if (_disposed || e.newRect.width < 1 || e.newRect.height < 1) return;
                var ratio = Mathf.Min(1, 1024f / Mathf.Max(e.newRect.width, e.newRect.height));
                var width = Mathf.Max(32, Mathf.RoundToInt(e.newRect.width * ratio));
                var height = Mathf.Max(32, Mathf.RoundToInt(e.newRect.height * ratio));
                if (_texture.width == width && _texture.height == height) return;
                _texture.Release(); _texture.width = width; _texture.height = height; _texture.Create();
                _camera.aspect = (float)width / height;
                if (_bodyFraming) FocusBody(); else FocusFace();
            });
            if (!creator)
            {
                var reset = new Button(ResetCamera) { text = Loc.Get("lobby.resetCamera") }; parent.Add(reset);
                var help = new Label(Loc.Get("lobby.orbitHelp")) { pickingMode = PickingMode.Ignore };
                help.AddToClassList("lobby-camera-help"); parent.Add(help);
            }
        }
        _status = new Label(); parent.Add(_status);
        CameraPose(); Refresh(character);
    }
    private void AddLight(string name, Vector3 position, Color colour, float intensity, bool directional = false)
    {
        var go = new GameObject(name); go.transform.SetParent(_stage.transform, false); go.transform.localPosition = position;
        go.transform.LookAt(_stage.transform.position + Vector3.up);
        var light = go.AddComponent<Light>(); light.type = directional ? LightType.Directional : LightType.Spot; light.spotAngle = 65; light.range = 18;
        light.color = colour; light.intensity = intensity; light.cullingMask = 1 << _layer; light.shadows = LightShadows.None;
    }
    public void ResetCamera() { _yaw = 180; _pitch = 8; _distance = 3.8f; _focus = new Vector3(0, .95f, 0); CameraPose(); }
    public void Rotate(float degrees) { _yaw += degrees; CameraPose(); }

    /// <summary>
    /// §174.7: the web page's creator leaves a transparent hole for the model;
    /// the camera renders straight into that part of the screen instead of
    /// into a texture shown by UI Toolkit. <paramref name="viewport"/> is
    /// normalized, origin bottom-left, as Camera.rect.
    /// </summary>
    public void ShowOnScreen(Rect viewport)
    {
        if (_disposed) return;
        var resize = _camera.targetTexture != null || _camera.rect != viewport;
        _camera.targetTexture = null;
        _camera.rect = viewport;
        _camera.depth = 50; // after the world camera, inside its own rectangle only
        _camera.ResetAspect();
        if (resize) { if (_bodyFraming) FocusBody(); else FocusFace(); }
    }

    /// <summary>The page's drag on the preview: the same orbit as the UI Toolkit manipulator.</summary>
    public void OrbitBy(float dx, float dy)
    {
        _yaw += dx * .25f; _pitch = Mathf.Clamp(_pitch + dy * .25f, -15, 85); CameraPose();
    }

    /// <summary>The page's wheel on the preview, one notch per step.</summary>
    public void Zoom(float steps)
    {
        _distance = Mathf.Clamp(_distance * (1 + Mathf.Clamp(steps, -3, 3) * .05f), .6f, 10); CameraPose();
    }
    public void FocusBody()
    {
        _bodyFraming = true;
        if (_actor == null) return;
        Bounds? total = null; var baked = new Mesh();
        try
        {
            foreach (var skin in _actor.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!skin.enabled || skin.sharedMesh == null) continue;
                skin.BakeMesh(baked, true);
                foreach (var vertex in baked.vertices)
                {
                    var point = skin.transform.TransformPoint(vertex);
                    if (total == null) total = new Bounds(point, Vector3.zero);
                    else { var bounds = total.Value; bounds.Encapsulate(point); total = bounds; }
                }
            }
        }
        finally { UnityEngine.Object.Destroy(baked); }
        if (total == null) return;
        _focus = total.Value.center - _stage.transform.position;
        _distance = Mathf.Max(total.Value.extents.y, total.Value.extents.x / _camera.aspect) * 1.16f /
            Mathf.Tan(_camera.fieldOfView * .5f * Mathf.Deg2Rad);
        _pitch = 4; CameraPose(); _frameBody = false;
    }
    public void FocusFace()
    {
        _bodyFraming = false;
        if (_view == null || !_view.TryGetFace(out var face, out var forward, out var up, out var scale)) return;
        _view.GetPortraitHeadExtents(face, Vector3.Cross(up, forward).normalized, up, 1, out var above, out var below, out var halfWidth);
        _focus = face - _stage.transform.position + up * ((above - below) * .5f);
        _distance = Mathf.Clamp(Mathf.Max((above + below) * .5f, halfWidth) * 1.35f /
            Mathf.Tan(_camera.fieldOfView * .5f * Mathf.Deg2Rad), .6f, 2.5f);
        _pitch = 0; CameraPose();
    }
    public void FrameThumbnail(int framing)
    {
        _thumbnailFraming = framing;
        _focus = new Vector3(0, framing == 2 ? 1.66f : framing == 1 ? 1.6f : .95f, 0);
        _distance = framing == 2 ? .48f : framing == 1 ? .92f : 3.6f;
        _pitch = framing == 2 ? 0 : 8; CameraPose();
    }
    private void CameraPose()
    {
        if (_disposed) return;
        var target = _stage.transform.position + _focus;
        _camera.transform.position = target + Quaternion.Euler(_pitch, _yaw, 0) * new Vector3(0, 0, -_distance);
        _camera.transform.LookAt(target);
    }
    public Texture2D Capture()
    {
        if (_thumbnailFraming > 0 && _view != null && _view.TryGetFace(out var face, out var forward, out var up, out var scale))
        {
            var aim = face; var distance = .42f;
            if (_thumbnailFraming == 2) aim += up * (.02f * scale);
            else
            {
                _view.GetPortraitHeadExtents(face, Vector3.Cross(up, forward).normalized, up, 1,
                    out var above, out var below, out var halfWidth);
                aim += up * ((above - below) * .5f);
                distance = Mathf.Max((above + below) * .5f, halfWidth) * 1.15f / Mathf.Tan(_camera.fieldOfView * .5f * Mathf.Deg2Rad);
            }
            _camera.transform.position = aim + (_thumbnailFraming == 3 ? Quaternion.AngleAxis(25, up) * forward : forward) * distance;
            _camera.transform.LookAt(aim, Vector3.up);
        }
        var previous = RenderTexture.active;
        try
        {
            _camera.Render(); RenderTexture.active = _texture;
            var copy = new Texture2D(_texture.width, _texture.height, TextureFormat.RGB24, false);
            copy.ReadPixels(new Rect(0, 0, _texture.width, _texture.height), 0, 0); copy.Apply(); return copy;
        }
        finally { RenderTexture.active = previous; }
    }
    public void Refresh(CharacterCreationConfig character)
    {
        if (_disposed) return;
        _character = character; Failed = false;
        _bodies.Add(character.Body); if (!string.IsNullOrEmpty(character.Skin)) _bodies.Add(character.Skin);
        if (!string.IsNullOrEmpty(character.Hair)) _hair.Add(character.Hair);
        foreach (var garment in character.Clothing) _garments.Add(garment);
        var generation = ++_generation;
        if (_loading != null) _runner.StopCoroutine(_loading);
        if (_body != character.Body)
        {
            if (_view != null) _retiredViews.Add(_view);
            if (_actor != null) UnityEngine.Object.Destroy(_actor);
            _actor = null; _view = null; _body = character.Body;
            _frameBody = _creator;
        }
        _loading = _runner.StartCoroutine(Apply(generation));
    }
    private IEnumerator Apply(int generation)
    {
        // Always yield once so a synchronously cached request cannot leave a stale Coroutine handle.
        yield return null;
        _status.text = Loc.Get("lobby.loadingAppearance");
        var started = Time.realtimeSinceStartup;
        GameObject prefab;
        while ((prefab = ContentPrefabCache.GetOrRequest("actor", _character.Body)) == null ||
            AtomicResources.Load<NpcAnimSet>("HexLive/NpcAnimSet") == null ||
            (!string.IsNullOrEmpty(_character.Skin) && prefab.GetComponentInChildren<PeopleAppearance>(true)==null &&
             ContentPrefabCache.GetOrRequest("actor", _character.Skin) == null) ||
            (!string.IsNullOrEmpty(_character.Eyes) && prefab.GetComponentInChildren<PeopleAppearance>(true)==null &&
             (AtomicResources.LoadAll<Material>("HexLive/Eyes/Common").Length < 3 ||
              AtomicResources.LoadAll<Material>("HexLive/Eyes/" + _character.Eyes).Length < 2)))
        {
            LoadingDependency=prefab==null?"actor:"+_character.Body:
                AtomicResources.Load<NpcAnimSet>("HexLive/NpcAnimSet")==null?"config:npcanimset":
                ContentPrefabCache.GetOrRequest("actor",_character.Skin)==null?"skin:"+_character.Skin:
                "legacyEyes:"+_character.Eyes;
            if (_disposed || generation != _generation) yield break;
            if (Time.realtimeSinceStartup - started > 60)
            { Failed = true; _loading = null; _status.text = Loc.Get("lobby.appearanceUnavailable"); yield break; }
            yield return null;
        }
        LoadingDependency="";
        if (_disposed || generation != _generation) yield break;
        if (_actor == null)
        {
            _actor = new GameObject("CharacterPreview"); _actor.transform.SetParent(_stage.transform, false);
            var body = UnityEngine.Object.Instantiate(prefab, _actor.transform);
            var bounds = body.GetComponentsInChildren<SkinnedMeshRenderer>().Select(r => r.bounds).ToArray();
            if (bounds.Length > 0)
            {
                var total = bounds[0]; foreach (var b in bounds) total.Encapsulate(b);
                if (total.size.y > .001f) body.transform.localScale *= 1.8f / total.size.y;
                var scaled = body.GetComponentsInChildren<SkinnedMeshRenderer>().Select(r => r.bounds).ToArray();
                body.transform.position -= Vector3.up * (scaled.Min(b => b.min.y) - _stage.transform.position.y);
            }
            _view = _actor.AddComponent<NpcActorView>(); _view.IsCreationPreview = true;
            _view.CreationAppearanceSeed = _character.Id;
            _view.Construct(_character.Body, --_nextId, _character.Skin, _character.Eyes, _character.Hair, _character.Voice, true, _character.HairColour);
            var animator = Animator;
            if (animator != null) { animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.updateMode = AnimatorUpdateMode.UnscaledTime; }
        }
        _view.ApplyCreationAppearance(_character.Skin, _character.Eyes, _character.Hair, _character.HairColour);
        _status.text = string.Empty; _loading = null;
        var retired = false;
        while (!_disposed && generation == _generation)
        {
            _view.SyncWorn(_character.Clothing);
            foreach (var t in _actor.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = _layer;
            if (_view.CreationAppearanceFailed || (!IsReady && Time.realtimeSinceStartup - started > 60))
            { Failed = true; _status.text = Loc.Get("lobby.appearanceUnavailable"); }
            if (!retired && IsReady)
            { if (_frameBody) FocusBody(); RetireUnused(); retired = true; }
            yield return null;
        }
    }
    private void RetireUnused()
    {
        var bodies = _bodies.Where(id => id != _character.Body && id != _character.Skin).ToHashSet();
        var hair = _hair.Where(id => id != _character.Hair).ToHashSet();
        var garments = _garments.Where(id => !_character.Clothing.Contains(id)).ToHashSet();
        if (bodies.Count + hair.Count + garments.Count == 0) return;
        _bodies.ExceptWith(bodies); _hair.ExceptWith(hair); _garments.ExceptWith(garments);
        ContentCoroutines.Run(ReleasePayloads(bodies, hair, garments, _retiredViews.ToArray()));
        _retiredViews.RemoveAll(v => ReferenceEquals(v, null) || v.CreationHairReady);
    }
    private static IEnumerator ReleasePayloads(HashSet<string> bodies, HashSet<string> hair, HashSet<string> garments,
        NpcActorView[] pending = null)
    {
        yield return null;
        while (pending != null && pending.Any(v => !ReferenceEquals(v, null) && !v.CreationHairReady)) yield return null;
        while (LoadingScreen.IsActive && bodies.Count + hair.Count + garments.Count > 0)
        {
            // Primal skins are aliases of the same actor payload. Retiring Jana
            // must not evict Marta while another preview still owns that body.
            bodies.RemoveWhere(id => Owners.Any(o => o._bodies.Any(owned =>
                PeopleIdMap.ContentId("actor",owned)==PeopleIdMap.ContentId("actor",id))) || ReleaseBody(id));
            hair.RemoveWhere(id => Owners.Any(o => o._hair.Contains(id)) || HairContent.Evict(id));
            garments.RemoveWhere(id => Owners.Any(o => o._garments.Contains(id)) || ActorWardrobe.Evict(id));
            yield return null;
        }
    }
    private static bool ReleaseBody(string id)
    {
        NpcActorView.ForgetCreationSkin(id);
        return ContentPrefabCache.Evict("actor", id);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; Owners.Remove(this);
        if (_loading != null) _runner.StopCoroutine(_loading);
        UnityEngine.Object.Destroy(_stage); _texture.Release(); UnityEngine.Object.Destroy(_texture);
        if (!ReferenceEquals(_view, null)) _retiredViews.Add(_view);
        ContentCoroutines.Run(ReleasePayloads(_bodies, _hair, _garments, _retiredViews.ToArray()));
    }
    private sealed class Orbit : PointerManipulator
    {
        private readonly LobbyCharacterPreview _owner;
        private readonly bool _direct;
        private int _pointer = -1, _button;
        public Orbit(LobbyCharacterPreview owner, bool direct = false) { _owner = owner; _direct = direct; }
        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<PointerDownEvent>(Down); target.RegisterCallback<PointerMoveEvent>(Move);
            target.RegisterCallback<PointerUpEvent>(Up); target.RegisterCallback<PointerCaptureOutEvent>(Lost);
            target.RegisterCallback<WheelEvent>(Wheel);
        }
        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<PointerDownEvent>(Down); target.UnregisterCallback<PointerMoveEvent>(Move);
            target.UnregisterCallback<PointerUpEvent>(Up); target.UnregisterCallback<PointerCaptureOutEvent>(Lost);
            target.UnregisterCallback<WheelEvent>(Wheel);
        }
        private void Down(PointerDownEvent e)
        {
            if (_pointer >= 0 || (e.button != 1 && e.button != 2 && !(_direct && e.button == 0))) return;
            _pointer = e.pointerId; _button = e.button; target.CapturePointer(_pointer); e.StopPropagation();
        }
        private void Move(PointerMoveEvent e)
        {
            if (_pointer != e.pointerId || !target.HasPointerCapture(_pointer)) return;
            if (!target.worldBound.Contains(e.position)) { e.StopPropagation(); return; }
            if (_button == 1 || (_direct && _button == 0)) { _owner._yaw += e.deltaPosition.x * .25f; _owner._pitch = Mathf.Clamp(_owner._pitch + e.deltaPosition.y * .25f, -15, 85); }
            else
            {
                var delta = new Vector3(-e.deltaPosition.x, e.deltaPosition.y, 0) * (.002f * _owner._distance);
                _owner._focus += Quaternion.Euler(0, _owner._yaw, 0) * delta;
                _owner._focus.y = Mathf.Clamp(_owner._focus.y, .1f, 2.2f);
            }
            _owner.CameraPose(); e.StopPropagation();
        }
        private void Up(PointerUpEvent e)
        {
            if (e.pointerId != _pointer || e.button != _button) return;
            target.ReleasePointer(_pointer); _pointer = -1; e.StopPropagation();
        }
        private void Lost(PointerCaptureOutEvent e) => _pointer = -1;
        private void Wheel(WheelEvent e)
        {
            _owner._distance = Mathf.Clamp(_owner._distance * (1 + Mathf.Clamp(e.delta.y, -3, 3) * .05f), .6f, 10);
            _owner.CameraPose(); e.StopPropagation();
        }
    }
}
}
