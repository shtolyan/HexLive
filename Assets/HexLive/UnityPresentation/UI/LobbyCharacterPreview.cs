using System;
using System.Collections;
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
    private static LobbyCharacterPreview _active;
    private readonly MonoBehaviour _runner;
    private readonly GameObject _stage;
    private readonly Camera _camera;
    private readonly RenderTexture _texture;
    private readonly Label _status;
    private GameObject _actor;
    private NpcActorView _view;
    private Coroutine _loading;
    private CharacterCreationConfig _character;
    private bool _disposed;
    private float _yaw = 180, _distance = 3.3f;
    private int _generation;
    private readonly System.Collections.Generic.HashSet<string> _bodies = new();
    private readonly System.Collections.Generic.HashSet<string> _hair = new();
    private readonly System.Collections.Generic.HashSet<string> _garments = new();
    public LobbyCharacterPreview(VisualElement parent, MonoBehaviour runner, CharacterCreationConfig character)
    {
        _active = this;
        _runner = runner;
        _stage = new GameObject("LobbyPreview"); _stage.transform.position = new Vector3(0, -10000, 0);
        _texture = new RenderTexture(512, 768, 24); _texture.Create();
        var cameraObject = new GameObject("PreviewCamera"); cameraObject.transform.SetParent(_stage.transform, false);
        _camera = cameraObject.AddComponent<Camera>(); _camera.targetTexture = _texture;
        _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = new Color(.06f,.08f,.09f);
        _camera.cullingMask = 1 << 31; _camera.nearClipPlane = .01f; _camera.farClipPlane = 20;
        var lightObject = new GameObject("PreviewLight"); lightObject.transform.SetParent(_stage.transform, false);
        lightObject.transform.localRotation = Quaternion.Euler(25, -30, 0);
        var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 2; light.cullingMask = 1 << 31;
        var image = new Image { image = _texture, scaleMode = ScaleMode.ScaleToFit }; image.AddToClassList("lobby-doll"); parent.Add(image);
        var orbit = new Slider(Loc.Get("lobby.rotate"), 0, 360) { value = _yaw }; parent.Add(orbit);
        orbit.RegisterValueChangedCallback(e => { _yaw = e.newValue; CameraPose(); });
        var zoom = new Slider(Loc.Get("lobby.zoom"), 1, 6) { value = _distance }; parent.Add(zoom);
        zoom.RegisterValueChangedCallback(e => { _distance = e.newValue; CameraPose(); });
        _status = new Label(); parent.Add(_status);
        image.schedule.Execute(() =>
        {
            if (_disposed || _actor == null) return;
            foreach (var transform in _actor.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = 31;
            _view?.SyncWorn(_character.Clothing);
        }).Every(100);
        CameraPose(); Refresh(character);
    }
    private void CameraPose()
    {
        if (_disposed) return;
        var target = _stage.transform.position + Vector3.up * 1.0f;
        _camera.transform.position = target + Quaternion.Euler(0, _yaw, 0) * new Vector3(0, .1f, -_distance);
        _camera.transform.LookAt(target);
    }
    public void Refresh(CharacterCreationConfig character)
    {
        if (_disposed) return;
        _generation++;
        if (_loading != null) _runner.StopCoroutine(_loading);
        if (_actor != null) UnityEngine.Object.Destroy(_actor);
        _view = null;
        var oldBodies = new System.Collections.Generic.HashSet<string>(_bodies);
        var oldHair = new System.Collections.Generic.HashSet<string>(_hair);
        var oldGarments = new System.Collections.Generic.HashSet<string>(_garments);
        _bodies.Clear(); _hair.Clear(); _garments.Clear();
        _character = character;
        _bodies.Add(character.Body); if (!string.IsNullOrEmpty(character.Skin)) _bodies.Add(character.Skin);
        if (!string.IsNullOrEmpty(character.Hair)) _hair.Add(character.Hair);
        foreach (var garment in character.Clothing) _garments.Add(garment);
        ContentCoroutines.Run(ReleasePayloads(oldBodies, oldHair, oldGarments));
        _loading = _runner.StartCoroutine(Load(_generation));
    }
    private IEnumerator Load(int generation)
    {
        _status.text = Loc.Get("lobby.loadingAppearance");
        var started = Time.realtimeSinceStartup;
        GameObject prefab;
        while ((prefab = ContentPrefabCache.GetOrRequest("actor", _character.Body)) == null ||
            (!string.IsNullOrEmpty(_character.Skin) && ContentPrefabCache.GetOrRequest("actor", _character.Skin) == null))
        {
            if (_disposed || generation != _generation) yield break;
            if (Time.realtimeSinceStartup - started > 60) { _status.text = Loc.Get("lobby.appearanceUnavailable"); yield break; }
            yield return null;
        }
        if (_disposed || generation != _generation) yield break;
        _actor = new GameObject("CharacterPreview"); _actor.transform.SetParent(_stage.transform, false);
        var body = UnityEngine.Object.Instantiate(prefab, _actor.transform);
        var bounds = body.GetComponentsInChildren<SkinnedMeshRenderer>().Select(r => r.bounds).ToArray();
        if (bounds.Length > 0)
        {
            var total = bounds[0]; foreach (var b in bounds) total.Encapsulate(b);
            if (total.size.y > .001f) body.transform.localScale *= 1.8f / total.size.y;
        }
        _view = _actor.AddComponent<NpcActorView>();
        _view.Construct(_character.Body, _character.Id, _character.Skin, _character.Eyes, _character.Hair, _character.Voice, true, _character.HairColour);
        _view.SyncWorn(_character.Clothing);
        foreach (var transform in _actor.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = 31;
        _status.text = string.Empty;
    }
    private static IEnumerator ReleasePayloads(System.Collections.Generic.HashSet<string> bodies,
        System.Collections.Generic.HashSet<string> hair, System.Collections.Generic.HashSet<string> garments)
    {
        yield return null; // Destroy must finish before releasing prefab/material handles.
        // Current preview owns any reused payload. Gameplay's residency manager takes over after connect.
        while (LoadingScreen.IsActive && bodies.Count + hair.Count + garments.Count > 0)
        {
            bodies.RemoveWhere(id => _active?._bodies.Contains(id) == true || ContentPrefabCache.Evict("actor", id));
            hair.RemoveWhere(id => _active?._hair.Contains(id) == true || HairContent.Evict(id));
            garments.RemoveWhere(id => _active?._garments.Contains(id) == true || ActorWardrobe.Evict(id));
            yield return null;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++;
        if (ReferenceEquals(_active, this)) _active = null;
        if (_loading != null) _runner.StopCoroutine(_loading);
        UnityEngine.Object.Destroy(_stage); _texture.Release(); UnityEngine.Object.Destroy(_texture);
        ContentCoroutines.Run(ReleasePayloads(_bodies, _hair, _garments));
    }
}
}
