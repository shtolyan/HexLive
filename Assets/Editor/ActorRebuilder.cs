#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Rebuilds a girl's actor prefab on a NEW body export, keeping the materials
/// the old one was wearing.
/// </summary>
/// <remarks>
/// The 2026-08 re-export finally gives all four girls ONE topology (17 418
/// vertices each, against 20571/20628/20276 before) and 109 blendshapes —
/// emotions plus the full viseme set the §67.7 lipsync needs. What it does not
/// carry is the skin: tone, eyes, lashes and nails were tuned by hand in Unity
/// over a long time and exist only in the old prefab's materials.
///
/// So the rebuild is: new mesh and skeleton, old materials, matched BY NAME.
/// Both sides carry the same seventeen Genesis surface names (Torso, Face,
/// Lips, Cornea, …), which is what makes this mechanical rather than a
/// seventeen-way guess per girl.
///
/// It writes a NEW prefab next to the old one rather than overwriting it: the
/// old body is the only fallback until someone has looked at the new one in
/// play, and the whole wardrobe is fitted to the old topology.
/// </remarks>
public static class ActorRebuilder
{
    private const string ActorRoot = "Assets/ImportedActors/Actors";
    private const string PrefabRoot = "Assets/Resources/HexLive/Actors";

    private static readonly string[] Girls = { "Jolly", "Jana", "Marta", "Molly" };

    /// <summary>
    /// Point every bone reference in a pasted component at the NEW skeleton.
    /// </summary>
    /// <remarks>
    /// `PasteComponentAsNew` copies the values but leaves object references
    /// aimed at the hierarchy they were copied FROM. On a rig that means the IK
    /// still lists the old prefab's bones — a component that inspects as fully
    /// configured and drives nothing.
    ///
    /// Rebinding by NAME is safe here precisely because both bodies are
    /// Genesis 3: the bone names are identical, and a name that has no match in
    /// the new skeleton is reported rather than quietly nulled, because that
    /// would be the one case where the two rigs really differ.
    /// </remarks>
    private static int Rebind(Component component, Dictionary<string, Transform> byName,
                             HashSet<Object> oldHierarchy, List<string> lost)
    {
        var so = new SerializedObject(component);
        var p = so.GetIterator();
        var rebound = 0;
        while (p.NextVisible(true))
        {
            if (p.propertyType != SerializedPropertyType.ObjectReference ||
                p.objectReferenceValue == null)
            {
                continue;
            }

            // Remap ONLY what points into the old prefab's own hierarchy.
            //
            // Asking "is this an asset?" is the wrong question in both
            // directions: the old prefab's bones live inside a prefab asset and
            // would be skipped, while hair is a separate prefab asset that must
            // be left alone (spec §31B.4B — hair is an ordinary Wear prefab,
            // instantiated at runtime, and looking for a bone of that name
            // reported a loss that never existed). Membership is the question.
            var value = p.objectReferenceValue;
            if (!oldHierarchy.Contains(value))
            {
                continue;
            }

            var wantedName = value.name;
            if (!byName.TryGetValue(wantedName, out var target))
            {
                lost.Add(wantedName);
                continue;
            }

            Object replacement = value switch
            {
                Transform => target,
                GameObject => target.gameObject,
                Component c => target.GetComponent(c.GetType()),
                _ => null,
            };

            if (replacement != null && replacement != value)
            {
                p.objectReferenceValue = replacement;
                rebound++;
            }
            else if (replacement == null)
            {
                lost.Add($"{wantedName} ({value.GetType().Name})");
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        return rebound;
    }

    [MenuItem("HexLive/Actors/Rebuild Girls From New Exports")]
    private static void Rebuild()
    {
        var log = new System.Text.StringBuilder();
        foreach (var girl in Girls)
        {
            var fbx = $"{ActorRoot}/{girl}/{girl}.new.fbx";
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (source == null)
            {
                log.AppendLine($"  {girl}: нет {fbx} — пропущен");
                continue;
            }

            var oldPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabRoot}/{girl}.prefab");
            if (oldPrefab == null)
            {
                log.AppendLine($"  {girl}: нет старого префаба — не с чего снять материалы");
                continue;
            }

            var oldSkin = oldPrefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (oldSkin == null)
            {
                log.AppendLine($"  {girl}: у старого префаба нет рендерера");
                continue;
            }

            // Old materials by surface name — the new export names them the same.
            var byName = new Dictionary<string, Material>();
            foreach (var m in oldSkin.sharedMaterials)
            {
                if (m != null)
                {
                    byName[m.name.Replace(" (Instance)", "")] = m;
                }
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            try
            {
                instance.name = girl;
                var skin = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .OrderByDescending(r => r.sharedMesh != null ? r.sharedMesh.vertexCount : 0)
                    .FirstOrDefault();
                if (skin == null)
                {
                    log.AppendLine($"  {girl}: в новом экспорте нет скиннед-меша");
                    continue;
                }

                var mats = skin.sharedMaterials;
                var missed = new List<string>();
                for (var i = 0; i < mats.Length; i++)
                {
                    var name = mats[i] != null ? mats[i].name.Replace(" (Instance)", "") : "";
                    if (byName.TryGetValue(name, out var kept))
                    {
                        mats[i] = kept;
                    }
                    else
                    {
                        missed.Add(name);
                    }
                }

                skin.sharedMaterials = mats;

                // Carry over every component the old root wore (BodyBones, the
                // IK rigs, MagicaCloth) — they hold hand-tuned settings, and a
                // fresh FBX has none of them.
                //
                // Pasting alone is NOT enough and looks like it is: the values
                // come across, but every reference to a BONE still points into
                // the old prefab's hierarchy. Measured on the first attempt —
                // 27 to 29 dangling references per girl, with FullBodyBipedIK's
                // 87 bone slots among them, which would have shipped a rig that
                // silently drives nothing. So each reference is re-bound BY
                // NAME afterwards; both skeletons are Genesis 3 and their bone
                // names match one-for-one.
                // Carry the root children the FBX does not have. The export is
                // the body and nothing else, but the prefab grew things by hand
                // that the game depends on: the "Wear" transform every garment
                // is parented under, and her hair. Copied before the components,
                // so the rebinding below can find them by name.
                var already = new Dictionary<string, Transform>();
                foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                {
                    already[t.name] = t;
                }

                // Anything in the old hierarchy whose name the new skeleton does
                // NOT have is a Unity-side addition, and it has to come across
                // under the same parent. Not just at the root: hair hangs off
                // the head BONE, so a root-only sweep left it behind.
                var brought = new List<string>();
                foreach (var child in oldPrefab.GetComponentsInChildren<Transform>(true))
                {
                    if (child.parent == null || already.ContainsKey(child.name))
                    {
                        continue;   // a bone — the new export brought its own
                    }

                    if (!already.TryGetValue(child.parent.name, out var anchor))
                    {
                        continue;   // its parent came across already; it rides along
                    }

                    var copy = Object.Instantiate(child.gameObject, anchor);
                    copy.name = child.name;   // Instantiate appends "(Clone)"
                    copy.transform.localPosition = child.localPosition;
                    copy.transform.localRotation = child.localRotation;
                    copy.transform.localScale = child.localScale;
                    brought.Add($"{child.name}→{child.parent.name}");
                    foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                    {
                        already[t.name] = t;
                    }
                }

                var newByName = new Dictionary<string, Transform>();
                foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                {
                    newByName[t.name] = t;
                }

                // Everything that belongs to the OLD prefab — the only things a
                // pasted component may still be pointing at.
                var oldHierarchy = new HashSet<Object>();
                foreach (var t in oldPrefab.GetComponentsInChildren<Transform>(true))
                {
                    oldHierarchy.Add(t);
                    oldHierarchy.Add(t.gameObject);
                    foreach (var c in t.GetComponents<Component>())
                    {
                        if (c != null)
                        {
                            oldHierarchy.Add(c);
                        }
                    }
                }

                var carried = new List<string>();
                var rebound = 0;
                var lost = new List<string>();
                foreach (var component in oldPrefab.GetComponents<Component>())
                {
                    if (component is Transform)
                    {
                        continue;
                    }

                    if (!UnityEditorInternal.ComponentUtility.CopyComponent(component) ||
                        !UnityEditorInternal.ComponentUtility.PasteComponentAsNew(instance))
                    {
                        continue;
                    }

                    carried.Add(component.GetType().Name);
                    var pasted = instance.GetComponents<Component>().LastOrDefault(
                        c => c != null && c.GetType() == component.GetType());
                    if (pasted != null)
                    {
                        rebound += Rebind(pasted, newByName, oldHierarchy, lost);
                    }
                }

                // The avatar belongs to the NEW rig; the old one describes a
                // skeleton that is no longer here.
                var animator = instance.GetComponent<Animator>();
                var sourceAnimator = source.GetComponent<Animator>();
                if (animator != null && sourceAnimator != null && sourceAnimator.avatar != null)
                {
                    animator.avatar = sourceAnimator.avatar;
                }

                var path = $"{PrefabRoot}/{girl}.new.prefab";
                PrefabUtility.SaveAsPrefabAsset(instance, path);
                log.AppendLine($"  {girl}: {skin.sharedMesh.vertexCount} вершин, " +
                               $"{skin.sharedMesh.blendShapeCount} блендшейпов, " +
                               $"материалов {mats.Length - missed.Count}/{mats.Length}" +
                               (missed.Count > 0 ? $" (не нашлись: {string.Join(", ", missed)})" : "") +
                               $", компоненты: {string.Join(", ", carried)}" +
                               (brought.Count > 0 ? $", принесено с собой: {string.Join(", ", brought)}" : "") +
                               $", перепривязано ссылок: {rebound}" +
                               (lost.Count > 0
                                   ? $", НЕ НАШЛОСЬ {lost.Count}: {string.Join(", ", lost.Distinct().Take(8))}"
                                   : "") +
                               $"\n      -> {path}");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        var report = "[Actors] пересборка девушек:\n" + log;
        // (report is written below)
        Debug.Log(report);
        File.WriteAllText("Temp/actors-rebuild.txt", report);
    }
}
#endif
