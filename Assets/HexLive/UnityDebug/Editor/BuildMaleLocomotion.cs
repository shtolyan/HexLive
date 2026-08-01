using UnityEditor;
using UnityEngine;
using HexLive.UnityPresentation.Wearing;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// §78 — wires the MALE locomotion takes into
    /// <c>Resources/HexLive/NpcAnimSet.asset</c> (<see cref="NpcAnimSet.male"/>).
    ///
    /// The animator is ONE controller for everyone and its locomotion clips are
    /// the colonists': a neutral idle, a woman's walk, a woman's sit. On the
    /// male body (§72 Kshishtof) they read as somebody else's gait. The men get
    /// their own five takes — idle, шаг, трусца, бег, сидение — and the view
    /// swaps them into the same slots through the override controller, so no
    /// second animator and no second prefab exist.
    ///
    /// The clips are Mixamo takes on the X Bot rig, imported into AnimLibrary
    /// (the postprocessor makes them Humanoid + in-place and renames each clip
    /// after its file):
    ///   Male@Idle    ← Breathing Idle
    ///   Male@Walk    ← Walking
    ///   Male@SlowRun ← Running slow
    ///   Male@Run     ← Running
    ///   Male@Sit     ← Sitting
    ///
    /// Idempotent — safe to re-run after re-importing any of them.
    /// Menu: <b>HexLive ▸ Actors ▸ Build Male Locomotion</b>.
    /// </summary>
    public static class BuildMaleLocomotion
    {
        const string AssetPath = "Assets/Resources/HexLive/NpcAnimSet.asset";
        const string AnimDir = "Assets/ImportedActors/AnimLibrary/";

        [MenuItem("HexLive/Actors/Build Male Locomotion")]
        public static void Build()
        {
            if (Application.productName != "HexLive")
            {
                Debug.LogWarning("[MaleLocomotion] Не тот проект — выходим.");
                return;
            }

            var set = AssetDatabase.LoadAssetAtPath<NpcAnimSet>(AssetPath);
            if (set == null)
            {
                Debug.LogError($"[MaleLocomotion] Не найден NpcAnimSet: {AssetPath}");
                return;
            }

            set.male ??= new NpcAnimSet.LocomotionSet();
            set.male.idle = Clip("Male@Idle");
            set.male.walk = Clip("Male@Walk");
            set.male.slowRun = Clip("Male@SlowRun");
            set.male.run = Clip("Male@Run");
            set.male.sit = Clip("Male@Sit");

            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();

            var missing = 0;
            foreach (var clip in new[]
                     { set.male.idle, set.male.walk, set.male.slowRun, set.male.run, set.male.sit })
            {
                if (clip == null) missing++;
            }

            Debug.Log($"[MaleLocomotion] {AssetPath}: idle={Name(set.male.idle)} " +
                $"walk={Name(set.male.walk)} slowRun={Name(set.male.slowRun)} " +
                $"run={Name(set.male.run)} sit={Name(set.male.sit)}" +
                (missing > 0
                    ? $"\nНЕ НАЙДЕНО клипов: {missing} — положи FBX в {AnimDir} и запусти снова."
                    : string.Empty));
        }

        static string Name(AnimationClip clip) => clip != null ? clip.name : "—";

        static AnimationClip Clip(string fileName)
        {
            var fbx = AnimDir + fileName + ".fbx";
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(fbx))
            {
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview"))
                {
                    return clip;
                }
            }

            Debug.LogWarning($"[MaleLocomotion] Clip not found: {fbx}");
            return null;
        }
    }
}
