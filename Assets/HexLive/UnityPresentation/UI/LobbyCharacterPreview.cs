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
    public Animator Animator => _actor != null ? _actor.GetComponentInChildren<Animator>() : null;

    public LobbyCharacterPreview(VisualElement parent, MonoBehaviour runner, CharacterCreationConfig character, bool thumbnail = false)
    {
        Owners.Add(this); _runner = runner; _layer = thumbnail ? 30 : 31;
        _stage = new GameObject(thumbnail ? "LobbyThumbnail" : "LobbyPreview");
        _stage.transform.position = new Vector3(0, thumbnail ? -11000 : -10000, 0);
        _texture = new RenderTexture(thumbnail ? 192 : 640, thumbnail ? 192 : 960, 24); _texture.Create();
        var cameraObject = new GameObject("PreviewCamera"); cameraObject.transform.SetParent(_stage.transform, false);
        _camera = cameraObject.AddComponent<Camera>(); _camera.targetTexture = _texture;
        _camera.enabled = !thumbnail;
        _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = new Color(.035f, .047f, .055f, 1);
        _camera.cullingMask = 1 << _layer; _camera.nearClipPlane = .03f; _camera.farClipPlane = 30; _camera.fieldOfView = 34;
        AddLight("Key", new Vector3(-3, 4, 4), new Color(1, .91f, .82f), 3.0f);
        AddLight("Fill", new Vector3(3, 2.8f, 3), new Color(.77f, .86f, 1), 1.8f);
        if (!thumbnail)
        {
            var image = new Image { image = _texture, scaleMode = ScaleMode.ScaleToFit, name = "characterOrbit" };
            image.AddToClassList("lobby-doll"); parent.Add(image); image.AddManipulator(new Orbit(this));
            var reset = new Button(ResetCamera) { text = Loc.Get("lobby.resetCamera") }; parent.Add(reset);
            var help = new Label(Loc.Get("lobby.orbitHelp")) { pickingMode = PickingMode.Ignore };
            help.AddToClassList("lobby-camera-help"); parent.Add(help);
        }
        _status = new Label(); parent.Add(_status);
        CameraPose(); Refresh(character);
    }
    private void AddLight(string name, Vector3 position, Color colour, float intensity)
    {
        var go = new GameObject(name); go.transform.SetParent(_stage.transform, false); go.transform.localPosition = position;
        go.transform.LookAt(_stage.transform.position + Vector3.up);
        var light = go.AddComponent<Light>(); light.type = LightType.Spot; light.spotAngle = 65; light.range = 18;
        light.color = colour; light.intensity = intensity; light.cullingMask = 1 << _layer; light.shadows = LightShadows.None;
    }
    public void ResetCamera() { _yaw = 180; _pitch = 8; _distance = 3.8f; _focus = new Vector3(0, .95f, 0); CameraPose(); }
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
            _camera.transform.position = aim + forward * distance;
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
            (!string.IsNullOrEmpty(_character.Skin) && ContentPrefabCache.GetOrRequest("actor", _character.Skin) == null) ||
            (!string.IsNullOrEmpty(_character.Eyes) &&
             (AtomicResources.LoadAll<Material>("HexLive/Eyes/Common").Length < 3 ||
              AtomicResources.LoadAll<Material>("HexLive/Eyes/" + _character.Eyes).Length < 2)))
        {
            if (_disposed || generation != _generation) yield break;
            if (Time.realtimeSinceStartup - started > 60)
            { Failed = true; _loading = null; _status.text = Loc.Get("lobby.appearanceUnavailable"); yield break; }
            yield return null;
        }
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
            if (!retired && IsReady) { RetireUnused(); retired = true; }
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
            bodies.RemoveWhere(id => Owners.Any(o => o._bodies.Contains(id)) || ReleaseBody(id));
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
        private int _pointer = -1, _button;
        public Orbit(LobbyCharacterPreview owner) => _owner = owner;
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
            if (_pointer >= 0 || (e.button != 1 && e.button != 2)) return;
            _pointer = e.pointerId; _button = e.button; target.CapturePointer(_pointer); e.StopPropagation();
        }
        private void Move(PointerMoveEvent e)
        {
            if (_pointer != e.pointerId || !target.HasPointerCapture(_pointer)) return;
            if (!target.worldBound.Contains(e.position)) { e.StopPropagation(); return; }
            if (_button == 1) { _owner._yaw += e.deltaPosition.x * .25f; _owner._pitch = Mathf.Clamp(_owner._pitch + e.deltaPosition.y * .25f, -15, 85); }
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
