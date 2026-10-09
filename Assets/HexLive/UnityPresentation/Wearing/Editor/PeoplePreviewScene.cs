#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Editor
{
    public static class PeoplePreviewScene
    {
        public static void Prepare()
        {
            const string root = PeopleAssetPreparation.Root;
            const string folder = root + "/Preview";
            Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            string controllerPath = folder + "/Run.controller";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null)
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
                var clip = AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/Male@Run.fbx")
                    .OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));
                controller.AddMotion(clip);
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (string actor in new[] { "Marta", "Kshishtof" })
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Prefabs/Actors/" + actor + ".prefab"));
                go.transform.position = new Vector3(actor == "Marta" ? -.6f : .6f, 0, 0);
                var animator = go.GetComponent<Animator>();
                animator.runtimeAnimatorController = controller; animator.applyRootMotion = false;
                var preview = go.AddComponent<PeoplePreviewOutfit>();
                preview.actor = actor == "Marta" ? ActorName.Marta : ActorName.Kshishtof;
                var ids = new[] { "PrimalBriefs", "PrimalSkirt", "PrimalArmWraps", "PrimalWrapBoots", "PrimalHeadband", "PrimalGathererPack" }.ToList();
                if (actor == "Marta") ids.Add("PrimalTop");
                preview.equipment = ids.Select(id => AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Prefabs/Wear/" + actor + "_" + id + ".prefab").GetComponent<Wear>()).ToArray();
                if (actor == "Marta") preview.hair = AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Prefabs/Hair/LoonaHair.prefab").GetComponent<Wear>();
            }
            var camera = new GameObject("Review Camera").AddComponent<Camera>();
            camera.tag = "MainCamera"; camera.transform.position = new Vector3(0, 1.15f, 4.7f);
            camera.transform.LookAt(new Vector3(0, 1f, 0)); camera.fieldOfView = 35f;
            camera.backgroundColor = new Color(.13f,.15f,.17f); camera.clearFlags = CameraClearFlags.SolidColor;
            var light = new GameObject("Key Light").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.4f;
            light.transform.rotation = Quaternion.Euler(35, 180, 0);
            RenderSettings.ambientLight = new Color(.4f,.4f,.4f);
            EditorSceneManager.SaveScene(scene, folder + "/PrimalRunning.unity");
            AssetDatabase.SaveAssets();
            Debug.Log("PEOPLE_PREVIEW_SCENE: " + folder + "/PrimalRunning.unity");
        }
    }
}
#endif
