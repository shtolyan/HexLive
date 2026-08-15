using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Live sliders for skin relief strength, one per actor.
/// </summary>
/// <remarks>
/// The point of this window is that it costs NOTHING to drag. Relief has two
/// knobs and they are not equal:
///
///   * `heightScale` on the importer — converts the DAZ height field into a
///     normal map. Changing it reimports a 4K texture, several seconds each,
///     twenty seconds for a cast. It is set once (ActorSkinNormals.HeightRelief)
///     and then left alone.
///   * `_BumpScale` on the material — scales the sampled normal in the shader.
///     Free, immediate, visible in the Scene view while the mouse is still
///     down. This is what the sliders below write.
///
/// So the tuning loop is a drag, not a rebuild. Values are saved into the
/// materials, so they survive a domain reload and go into git like any other
/// material edit.
///
/// Marta is here on the same footing as everyone else. Her product shipped a
/// real normal map instead of a height field, which changes how the texture is
/// IMPORTED but not how it is scaled — `_BumpScale` drives her exactly the
/// same way, and she starts higher because an encoded normal at 1.0 read as
/// flat next to the converted ones.
/// </remarks>
public sealed class ActorSkinReliefWindow : EditorWindow
{
    private static readonly int BumpScaleId = Shader.PropertyToID("_BumpScale");

    private readonly Dictionary<string, float> _values = new();
    private List<(string actor, string dir, List<Material> mats)> _actors;
    private float _all = ActorSkinNormals.DefaultBumpScale;

    [MenuItem("HexLive/Actors/Skin Relief")]
    private static void Open()
    {
        GetWindow<ActorSkinReliefWindow>("Skin Relief").Refresh();
    }

    private void OnFocus() => Refresh();

    private void Refresh()
    {
        _actors = ActorSkinNormals.SkinMaterials().ToList();
        _values.Clear();
        foreach (var (actor, _, mats) in _actors)
        {
            // Read back the BODY strength: the face carries the ratio, so
            // seeding from a face material would show double and the first
            // repaint would silently double it again.
            var body = mats.FirstOrDefault(m => m.name != "Face" && m.name != "Lips" &&
                                                m.name != "Ears" && m.name != "EyeSocket");
            var probe = body ?? mats[0];
            var v = probe.HasProperty(BumpScaleId) ? probe.GetFloat(BumpScaleId) : 1f;
            _values[actor] = body != null ? v : v / ActorSkinNormals.FaceReliefRatio;
        }
    }

    private void OnGUI()
    {
        if (_actors == null || _actors.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "Кожа ещё не подключена. Сначала: HexLive ▸ Actors ▸ Wire Skin Normal Maps.",
                MessageType.Info);
            if (GUILayout.Button("Обновить"))
            {
                Refresh();
            }

            return;
        }

        EditorGUILayout.HelpBox(
            "Сила рельефа кожи. Меняется сразу, без переимпорта.\n" +
            $"Лицо всегда в {ActorSkinNormals.FaceReliefRatio:0.#}x рельефнее тела — так это задано в DAZ.",
            MessageType.None);

        EditorGUILayout.Space();
        EditorGUI.BeginChangeCheck();
        _all = EditorGUILayout.Slider("Всем сразу", _all, 0f, 3f);
        if (EditorGUI.EndChangeCheck())
        {
            foreach (var (actor, _, mats) in _actors)
            {
                _values[actor] = _all;
                Apply(actor, mats, _all);
            }
        }

        EditorGUILayout.Space();
        foreach (var (actor, _, mats) in _actors)
        {
            EditorGUI.BeginChangeCheck();
            var v = EditorGUILayout.Slider($"{actor}  ({mats.Count})", _values[actor], 0f, 3f);
            if (EditorGUI.EndChangeCheck())
            {
                _values[actor] = v;
                Apply(actor, mats, v);
            }
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Сохранить"))
            {
                AssetDatabase.SaveAssets();
            }

            if (GUILayout.Button("Сбросить"))
            {
                _all = ActorSkinNormals.DefaultBumpScale;
                foreach (var (actor, _, mats) in _actors)
                {
                    _values[actor] = _all;
                    Apply(actor, mats, _all);
                }
            }
        }

        EditorGUILayout.LabelField(
            $"Импортный масштаб: {ActorSkinNormals.HeightRelief:0.###} " +
            "(меняется только в коде — требует переимпорта)",
            EditorStyles.miniLabel);
    }

    private static void Apply(string actor, List<Material> mats, float body)
    {
        foreach (var m in mats)
        {
            ActorSkinNormals.SetRelief(m, m.name, body);
        }

        SceneView.RepaintAll();
    }
}
