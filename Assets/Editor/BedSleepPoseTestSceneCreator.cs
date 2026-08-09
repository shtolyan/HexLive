using HexLive.UnityPresentation.BedSleepPoseTest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BedSleepPoseTestSceneCreator
{
    private const string ScenePath = "Assets/Scenes/BedSleepPoseTest.unity";

    [MenuItem("HexLive/Tests/Create Bed Sleep Pose Test Scene")]
    public static void Create()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var root = new GameObject("BedSleepPoseTest");
        root.AddComponent<BedSleepPoseTestBootstrap>();

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
        {
            Debug.LogError($"[BedSleepPoseTest] Failed to save {ScenePath}");
            return;
        }

        Selection.activeGameObject = root;
        Debug.Log($"[BedSleepPoseTest] Scene created: {ScenePath}. Press Play and tune Sleep Y Offset.");
    }
}
