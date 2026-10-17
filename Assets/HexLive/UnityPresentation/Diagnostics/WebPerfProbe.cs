using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace HexLive.UnityPresentation.Diagnostics
{
    /// <summary>
    /// §168.11: questions WebPerf's timings cannot answer — WHAT is being drawn
    /// and which switch moves the frame. Lives only beside an enabled WebPerf
    /// (<c>?perf=1</c>); every knob is a page-address switch for A/B runs on
    /// the measurement stand, never a player setting:
    /// <list type="bullet">
    /// <item><c>?census=1</c> — once, after the world settles, logs
    /// <c>[WebPerf] census</c>: renderers and triangles grouped by object name
    /// (digits folded), visible versus total, shadow casters, and every UI
    /// Toolkit document with its element count;</item>
    /// <item><c>?hide=Grass,Palm</c> — renderers whose GameObject name contains
    /// a token are switched off (re-applied every 2 s for late spawns);</item>
    /// <item><c>?shadows=0</c> — URP shadow distance 0 (no shadow pass);</item>
    /// <item><c>?uitk=0</c> — every UI Toolkit document hidden, to price the
    /// UI Toolkit repaint;</item>
    /// <item><c>?perfgui=0</c> — no on-screen WebPerf table (log only).</item>
    /// </list>
    /// </summary>
    public sealed class WebPerfProbe : MonoBehaviour
    {
        private const float CensusDelaySeconds = 25f;
        private const float ReapplySeconds = 2f;
        private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

        private string[] _hide = System.Array.Empty<string>();
        private bool _census;
        private bool _hideUi;
        private float _nextApply;
        private float _censusAt;

        public static bool ShowGui { get; private set; } = true;

        private void Start()
        {
            ShowGui = Platform.WebPage.QueryFloat("perfgui") != 0f;
            _census = Platform.WebPage.QueryFloat("census") == 1f;
            _hideUi = Platform.WebPage.QueryFloat("uitk") == 0f;
            _hide = QueryText("hide")?.Split(',').Where(token => token.Length > 0).ToArray() ??
                    System.Array.Empty<string>();
            _censusAt = Time.realtimeSinceStartup + CensusDelaySeconds;
            if (Platform.WebPage.QueryFloat("shadows") == 0f &&
                GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline)
            {
                pipeline.shadowDistance = 0f;
                Debug.Log("[WebPerf] shadows off (?shadows=0)");
            }

            Debug.Log($"[WebPerf] probe: census={_census} hide=[{string.Join(",", _hide)}] uitk={(_hideUi ? "hidden" : "on")} gui={ShowGui}");
        }

        private void Update()
        {
            var now = Time.realtimeSinceStartup;
            if ((_hide.Length > 0 || _hideUi) && now >= _nextApply)
            {
                _nextApply = now + ReapplySeconds;
                Apply();
            }

            // The census waits until the world has been on screen for a while;
            // before that the scene is the menu and the numbers mean nothing.
            if (_census && now >= _censusAt && !UI.LoadingScreen.IsActive &&
                FindAnyObjectByType<Rendering.HexWorldRenderer>() != null)
            {
                _census = false;
                Debug.Log(Census());
            }
        }

        private void Apply()
        {
            if (_hide.Length > 0)
            {
                foreach (var renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    var name = renderer.gameObject.name;
                    foreach (var token in _hide)
                    {
                        if (name.Contains(token))
                        {
                            renderer.enabled = false;
                            break;
                        }
                    }
                }
            }

            if (_hideUi)
            {
                foreach (var document in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                {
                    if (document.rootVisualElement != null)
                    {
                        document.rootVisualElement.style.display = DisplayStyle.None;
                    }
                }
            }
        }

        private static string Census()
        {
            var groups = new Dictionary<string, (int count, int visible, long tris, long visibleTris, int shadow)>();
            long allTris = 0, visibleAll = 0;
            foreach (var renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var mesh = renderer switch
                {
                    SkinnedMeshRenderer skinned => skinned.sharedMesh,
                    MeshRenderer => renderer.GetComponent<MeshFilter>()?.sharedMesh,
                    _ => null,
                };
                long tris = 0;
                if (mesh != null)
                {
                    for (var sub = 0; sub < mesh.subMeshCount; sub++) tris += mesh.GetIndexCount(sub) / 3;
                }

                var kind = renderer switch
                {
                    SkinnedMeshRenderer => "skinned ",
                    ParticleSystemRenderer => "particles ",
                    _ => string.Empty,
                };
                var key = kind + Digits.Replace(renderer.gameObject.name, "#");
                groups.TryGetValue(key, out var entry);
                entry.count++;
                entry.tris += tris;
                if (renderer.isVisible)
                {
                    entry.visible++;
                    entry.visibleTris += tris;
                    visibleAll += tris;
                }

                if (renderer.shadowCastingMode != ShadowCastingMode.Off) entry.shadow++;
                allTris += tris;
                groups[key] = entry;
            }

            var text = new StringBuilder(4096);
            text.Append("[WebPerf] census\n")
                .Append($"renderers {groups.Values.Sum(g => g.count)}, mesh triangles {allTris / 1000} k, visible {visibleAll / 1000} k\n")
                .Append("  visible/total   vis k-tris   all k-tris  shadow  name\n");
            foreach (var (name, g) in groups.OrderByDescending(pair => pair.Value.visibleTris).Take(30))
            {
                text.Append($"  {g.visible,6}/{g.count,-6} {g.visibleTris / 1000,10} {g.tris / 1000,12} {g.shadow,7}  {name}\n");
            }

            text.Append("ui toolkit documents:\n");
            foreach (var document in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                var root = document.rootVisualElement;
                var shown = document.isActiveAndEnabled && root != null && root.resolvedStyle.display != DisplayStyle.None;
                var elements = root == null ? 0 : root.Query<VisualElement>().ToList().Count;
                text.Append($"  {(shown ? "shown " : "hidden")} {elements,6} elements  {document.gameObject.name}\n");
            }

            return text.ToString();
        }

        private static string QueryText(string name)
        {
            if (!System.Uri.TryCreate(Application.absoluteURL, System.UriKind.Absolute, out var page)) return null;
            foreach (var pair in page.Query.TrimStart('?').Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == name) return System.Uri.UnescapeDataString(pair.Substring(eq + 1));
            }

            return null;
        }
    }
}
