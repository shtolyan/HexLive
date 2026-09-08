using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
// §162: real game assets, one serial render stage, requests from visible tiles only.
internal sealed class LobbyAppearanceThumbnails : IDisposable
{
    private readonly MonoBehaviour _runner;
    private readonly Dictionary<string, Texture2D> _cache = new();
    private readonly Dictionary<string, float> _seen = new();
    private readonly HashSet<string> _pending = new(), _failed = new();
    private readonly Queue<(string key, CharacterCreationConfig look, int framing)> _queue = new();
    private Coroutine _work;
    private LobbyCharacterPreview _stage;
    private bool _disposed;
    public LobbyAppearanceThumbnails(MonoBehaviour runner) => _runner = runner;
    public Texture2D GetOrRequest(CharacterCreationConfig look, int framing)
    {
        if (_disposed) return null;
        var key = string.Join("|", look.Body, look.Skin, look.Eyes, look.Hair, look.HairColour, framing);
        _seen[key] = Time.realtimeSinceStartup;
        if (_cache.TryGetValue(key, out var image)) return image;
        if (_failed.Contains(key) || !_pending.Add(key)) return null;
        _queue.Enqueue((key, look, framing));
        if (_work == null) _work = _runner.StartCoroutine(Render());
        return null;
    }
    private IEnumerator Render()
    {
        yield return null;
        while (!_disposed && _queue.Count > 0)
        {
            var (key, look, framing) = _queue.Dequeue();
            if (Time.realtimeSinceStartup - _seen[key] > 1) { _pending.Remove(key); continue; }
            if (_stage == null) _stage = new LobbyCharacterPreview(new VisualElement(), _runner, look, true);
            else _stage.Refresh(look);
            _stage.FrameThumbnail(framing);
            var deadline = Time.realtimeSinceStartup + 65;
            yield return null;
            while (!_disposed && !_stage.IsReady && !_stage.Failed && Time.realtimeSinceStartup < deadline) yield return null;
            if (_disposed) yield break;
            if (_stage.IsReady)
            {
                yield return null; yield return null;
                _cache[key] = _stage.Capture();
                foreach (var stale in _cache.Keys.OrderBy(k => _seen[k]).ToArray())
                {
                    if (_cache.Count <= 96) break;
                    if (Time.realtimeSinceStartup - _seen[stale] <= 1) continue;
                    UnityEngine.Object.Destroy(_cache[stale]); _cache.Remove(stale);
                }
            }
            else _failed.Add(key);
            _pending.Remove(key);
        }
        _stage?.Dispose(); _stage = null; _work = null;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_work != null) _runner.StopCoroutine(_work);
        _stage?.Dispose();
        foreach (var texture in _cache.Values) UnityEngine.Object.Destroy(texture);
        _cache.Clear(); _pending.Clear(); _queue.Clear();
    }
}
}
