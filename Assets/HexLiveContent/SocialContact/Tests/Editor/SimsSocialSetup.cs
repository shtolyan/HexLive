using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class SimsSocialSetup : AssetPostprocessor
{
    private const string Root = "Assets/HexLiveContent/SocialContact";
    public const string ScenePath = Root + "/Tests/SimsSocialTest.unity";

    [MenuItem("HexLive/Local Tests/Create Sims Social Scene")]
    public static void CreateScene()
    {
        var paths = Directory.GetFiles(Root + "/Clips", "*.fbx");
        if (paths.Length < 6)
            throw new InvalidOperationException($"Expected at least six Sims tracks, found {paths.Length}");
        foreach (var path in paths)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(path.Replace('\\', '/'));
            var avatar = assets.OfType<Avatar>().SingleOrDefault();
            var name = Path.GetFileNameWithoutExtension(path);
            var clip = assets.OfType<AnimationClip>().SingleOrDefault(a => a.name == name && a.humanMotion);
            if (avatar == null || !avatar.isValid || !avatar.isHuman || clip == null || clip.length <= 0f)
                throw new InvalidOperationException($"Invalid Sims humanoid import: {path}");
            Debug.Log($"[SimsSocial] {clip.name}: {clip.length:0.000} s @ {clip.frameRate} fps");
        }
        if (!File.Exists(ScenePath))
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var root = new GameObject("Sims Social Test");
            SceneManager.MoveGameObjectToScene(root, scene);
            root.AddComponent<SimsSocialTest>();
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException($"Could not save {ScenePath}");
            EditorSceneManager.CloseScene(scene, true);
        }
        Debug.Log($"[SimsSocial] Scene ready: {ScenePath}");
    }

    private bool SimsTrack => assetPath.StartsWith(Root + "/Clips/", StringComparison.Ordinal);

    private void OnPreprocessModel()
    {
        if (!SimsTrack) return;
        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.importCameras = false;
        importer.importLights = false;
        importer.optimizeGameObjects = false;
        importer.animationCompression = ModelImporterAnimationCompression.Off;
        var description = importer.humanDescription;
        description.human = new[]
        {
            Bone("Hips", "ROOT_bind"), Bone("Spine", "Spine0"), Bone("Chest", "Spine1"),
            Bone("UpperChest", "Spine2"), Bone("Neck", "Neck"), Bone("Head", "Head"),
        }.Concat(LimbBones("Left", "L")).Concat(LimbBones("Right", "R")).ToArray();
        description.armStretch = 0.05f;
        description.legStretch = 0.05f;
        description.upperArmTwist = description.lowerArmTwist = 0.5f;
        description.upperLegTwist = description.lowerLegTwist = 0.5f;
        importer.humanDescription = description;
    }

    private void OnPreprocessAnimation()
    {
        if (!SimsTrack) return;
        var importer = (ModelImporter)assetImporter;
        var clips = importer.defaultClipAnimations;
        foreach (var clip in clips)
        {
            clip.name = Path.GetFileNameWithoutExtension(assetPath);
            clip.loopTime = clip.name == "a2o_sleepingBag_sleep_idle_breathe_x";
            clip.lockRootRotation = true;
            clip.lockRootHeightY = true;
            clip.lockRootPositionXZ = true;
            clip.keepOriginalOrientation = true;
            clip.keepOriginalPositionY = true;
            clip.keepOriginalPositionXZ = true;
        }
        importer.clipAnimations = clips;
    }

    private static HumanBone Bone(string human, string source) => new HumanBone
    {
        humanName = human,
        boneName = "b__" + source + "__",
        limit = new HumanLimit { useDefaultValues = true }
    };

    private static HumanBone[] LimbBones(string side, string prefix)
    {
        return new[]
        {
            Bone(side + "Shoulder", prefix + "_Clavicle"), Bone(side + "UpperArm", prefix + "_UpperArm"),
            Bone(side + "LowerArm", prefix + "_Forearm"), Bone(side + "Hand", prefix + "_Hand"),
            Bone(side + "UpperLeg", prefix + "_Thigh"), Bone(side + "LowerLeg", prefix + "_Calf"),
            Bone(side + "Foot", prefix + "_Foot"), Bone(side + "Toes", prefix + "_Toe"),
        };
    }
}
