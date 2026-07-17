using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// One-click creator for the bed build test scene: an empty scene holding only
// the BedBuildTestBootstrap (it builds the camera, sim runner, renderer and
// the hand-authored world itself in Awake, like the SwimTest scene does).
public static class BedBuildTestSceneCreator
{
    private const string ScenePath = "Assets/Scenes/BedBuildTest.unity";

    [MenuItem("HexLive/Tests/Create Bed Build Test Scene")]
    public static void Create()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var go = new GameObject("BedBuildTest");
        go.AddComponent<HexLive.UnityPresentation.BedBuildTest.BedBuildTestBootstrap>();

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
        {
            Debug.LogError($"BedBuildTest: failed to save scene at {ScenePath}");
            return;
        }

        AssetDatabase.Refresh();
        Debug.Log($"BedBuildTest: scene created at {ScenePath} — press Play to run.");
    }
}
