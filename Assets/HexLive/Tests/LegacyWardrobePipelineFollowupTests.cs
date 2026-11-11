#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class LegacyWardrobePipelineFollowupTests
{
    static Type Preparation => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation"))
        .First(t => t != null);
    static object Call(string name, params object[] args) => Preparation.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);

    [TestCase(8000L, 43966L, 64000L)]
    [TestCase(16000L, 43966L, 64000L)]
    [TestCase(32000L, 43966L, 64000L)]
    [TestCase(8000L, 8000L, 16000L)]
    [TestCase(8000L, 16000L, 32000L)]
    [TestCase(32000L, 0L, 64000L)]
    public void NextBudgetSkipsOnlyBudgetsAtOrBelowExhaustedActual(long requested, long actual, long expected)
    {
        Assert.That((long)Call("NextTriangleBudget", requested, actual), Is.EqualTo(expected));
    }

    [Test]
    public void SetFitsPreservesBothHeightsThroughTemplateFitAndOutput()
    {
        var source = new GameObject("height-source");
        GameObject template = null, fit = null, output = null;
        var oldMesh = new Mesh(); var female = new Mesh(); var male = new Mesh();
        try
        {
            var original = source.AddComponent<Wear>();
            // Reverse actor order: preserving index 0/1 rather than actor identity is wrong.
            original.SetConfigHeight(ActorName.Kshishtof, -.027f);
            original.SetConfigHeight(ActorName.Marta, .013f);
            original.SetConfigScale(ActorName.Kshishtof, 1.08f);
            original.SetConfigScale(ActorName.Marta, 1.04f);
            template = Object.Instantiate(source);
            Call("SetFits", template.GetComponent<Wear>(), oldMesh, oldMesh, 1.04f, 1.08f, false);
            fit = Object.Instantiate(template);
            Call("SetFits", fit.GetComponent<Wear>(), female, male, 1.04f, 1.08f, false);
            output = Object.Instantiate(template);
            Call("SetFits", output.GetComponent<Wear>(), female, male, 1.04f, 1.08f, false);
            foreach (var go in new[] { source, template, fit, output })
            {
                var wear = go.GetComponent<Wear>();
                Assert.That(wear.GetConfigHeight(ActorName.Marta), Is.EqualTo(.013f), go.name);
                Assert.That(wear.GetConfigHeight(ActorName.Kshishtof), Is.EqualTo(-.027f), go.name);
                Assert.That(wear.GetConfigScale(ActorName.Marta), Is.EqualTo(1.04f), go.name);
                Assert.That(wear.GetConfigScale(ActorName.Kshishtof), Is.EqualTo(1.08f), go.name);
            }
            var configs = new SerializedObject(output.GetComponent<Wear>()).FindProperty("configs");
            Assert.That(configs.arraySize, Is.EqualTo(2));
            Assert.That(configs.GetArrayElementAtIndex(0).FindPropertyRelative("mesh").objectReferenceValue, Is.SameAs(female));
            Assert.That(configs.GetArrayElementAtIndex(1).FindPropertyRelative("mesh").objectReferenceValue, Is.SameAs(male));
        }
        finally
        {
            foreach (var go in new[] { output, fit, template, source }) if (go != null) Object.DestroyImmediate(go);
            Object.DestroyImmediate(oldMesh); Object.DestroyImmediate(female); Object.DestroyImmediate(male);
        }
    }

    [Test]
    public void UnderlayerDependenciesIncludeBothBodiesAndBothVisibleSlots()
    {
        var paths = (string[])Preparation.GetField("FittingUnderlayerPaths", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        Assert.That(paths.Length, Is.EqualTo(4));
        Assert.That(paths.Distinct().Count(), Is.EqualTo(4));
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        foreach (var slot in new[] { "PrimalBriefs", "PrimalTop" })
        {
            string path = "Assets/HexLiveContent/People/Prefabs/Wear/" + actor + "_" + slot + ".prefab";
            Assert.That(paths, Does.Contain(path));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(path), Is.Not.Null, path);
        }
        Assert.That((string)Call("DependencyFingerprint", (object)paths), Is.Not.Null.And.Not.Empty);
        Assert.That((string)Call("SimplifierInputFingerprint"), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void DependencyFingerprintChangesWhenActualImportedInputChanges()
    {
        string directory = "Assets/__LegacyInputFingerprint_" + Guid.NewGuid().ToString("N");
        string path = directory + "/source.txt";
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "first");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            string first = (string)Call("DependencyFingerprint", (object)new[] { path });
            string guid = AssetDatabase.AssetPathToGUID(path);
            File.WriteAllText(path, "changed-input");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Assert.That((string)Call("DependencyFingerprint", (object)new[] { path }), Is.Not.EqualTo(first));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
        }
        finally
        {
            string full = Path.GetFullPath(directory);
            string assets = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            Assert.That(full.StartsWith(assets, StringComparison.OrdinalIgnoreCase), Is.True, "Owned fixture must remain inside this project's Assets.");
            if (AssetDatabase.IsValidFolder(directory)) Assert.That(AssetDatabase.DeleteAsset(directory), Is.True);
            else
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
            if (File.Exists(directory + ".meta")) File.Delete(directory + ".meta");
        }
    }
}
}
#endif
