#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
// Integration coverage for the actual offline pipeline. Never invokes a bundle build.
public sealed class LegacyWardrobePreparationRegressionTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private Type _pipeline;
    private string _preparedRoot, _tag, _ownedTexture;
    private readonly List<string> _ownedAssets = new();
    private readonly List<Object> _transient = new();
    private FieldInfo _textureCacheField;
    private object _savedTextureCache;
    private IDictionary _preparedTextures;
    private List<DictionaryEntry> _savedPreparedTextures;
    private readonly List<FileSnapshot> _cacheFiles = new();

    [SetUp]
    public void LocateActualPipeline()
    {
        _pipeline = EditorType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation");
        _preparedRoot = (string)_pipeline.GetField("PreparedRoot", Hidden).GetRawConstantValue();
        _tag = "__Tests649_" + Guid.NewGuid().ToString("N");
        Assert.That(AssetDatabase.IsValidFolder(_preparedRoot), Is.True, "Prepare the wardrobe before running this integration fixture.");
    }

    [TearDown]
    public void RestoreCachesAndDeleteOnlyOwnedFixtures()
    {
        try
        {
            foreach (var go in _transient.OfType<GameObject>())
                if (go != null) Object.DestroyImmediate(go);
            foreach (var value in _transient.Where(value => !(value is GameObject)))
                if (value != null) Object.DestroyImmediate(value);
            _transient.Clear();
            foreach (var path in _ownedAssets.OrderByDescending(p => p.Length))
            {
                Assert.That(path.StartsWith(_preparedRoot + "/", StringComparison.Ordinal), Is.True, path);
                Assert.That(path.Contains(_tag) || path == _ownedTexture, Is.True, "Refuse to delete another asset: " + path);
                if (AssetDatabase.LoadMainAssetAtPath(path) != null || AssetDatabase.IsValidFolder(path) || File.Exists(path))
                    Assert.That(AssetDatabase.DeleteAsset(path), Is.True, path);
            }
            _ownedAssets.Clear();
        }
        finally
        {
            if (_savedPreparedTextures != null)
            {
                _preparedTextures.Clear();
                foreach (var entry in _savedPreparedTextures) _preparedTextures.Add(entry.Key, entry.Value);
                _textureCacheField.SetValue(null, _savedTextureCache);
            }
            foreach (var file in _cacheFiles) file.Restore();
            _cacheFiles.Clear();
            _savedPreparedTextures = null;
        }
    }

    [Test]
    public void PreparedTextureReloadsChangedSourceBytesWithoutReplacingItsOwnedGuid()
    {
        var cachePath = _preparedRoot + "/texture-fingerprints.json";
        _cacheFiles.Add(new FileSnapshot(cachePath));
        _cacheFiles.Add(new FileSnapshot(cachePath + ".meta"));
        _textureCacheField = _pipeline.GetField("TextureCache", Hidden);
        Assert.That(_textureCacheField, Is.Not.Null);
        _savedTextureCache = _textureCacheField.GetValue(null);
        _preparedTextures = (IDictionary)_pipeline.GetField("PreparedTextures", Hidden).GetValue(null);
        _savedPreparedTextures = new List<DictionaryEntry>();
        foreach (DictionaryEntry entry in _preparedTextures) _savedPreparedTextures.Add(entry);
        _preparedTextures.Clear();
        // Exercise real lazy initialization. Test code must not mask a null-cache failure.
        _textureCacheField.SetValue(null, null);
        var folder = Folder(_preparedRoot);
        var sourcePath = folder + "/source.png";

        WritePng(sourcePath, Color.red);
        var source = AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
        Assert.That(source, Is.Not.Null);
        var identity = (string)Invoke(_pipeline, "AssetKey", source);
        _ownedTexture = _preparedRoot + "/Textures/" + identity + ".png";
        Assert.That(File.Exists(_ownedTexture) || File.Exists(_ownedTexture + ".meta"), Is.False,
            "The new source GUID must not collide with an existing prepared texture.");
        _ownedAssets.Add(_ownedTexture);
        var first = (Texture)Invoke(_pipeline, "PreparedTexture", source);
        Assert.That(AssetDatabase.GetAssetPath(first), Is.EqualTo(_ownedTexture));
        var firstGuid = AssetDatabase.AssetPathToGUID(_ownedTexture);
        var firstBytes = File.ReadAllBytes(_ownedTexture);
        Assert.That(firstGuid, Is.Not.Empty);
        Assert.That(firstBytes, Is.EqualTo(File.ReadAllBytes(sourcePath)));
        Assert.That(_textureCacheField.GetValue(null), Is.Not.Null, "The helper itself initializes persistent fingerprints.");

        WritePng(sourcePath, Color.blue);
        var changed = File.ReadAllBytes(sourcePath);
        Assert.That(changed, Is.Not.EqualTo(firstBytes));
        _preparedTextures.Clear(); // A new preparation pass; retain the persistent fingerprint cache.
        var refreshed = (Texture)Invoke(_pipeline, "PreparedTexture", AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath));
        Assert.That(AssetDatabase.GetAssetPath(refreshed), Is.EqualTo(_ownedTexture));
        Assert.That(File.ReadAllBytes(_ownedTexture), Is.EqualTo(changed));
        Assert.That(AssetDatabase.AssetPathToGUID(_ownedTexture), Is.EqualTo(firstGuid));
        Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(changed), "Only the owned copy is rewritten.");
    }

    [Test]
    public void CachedPrototypeRejectsTheLossOfOneOwnedBodyMesh()
    {
        var originals = AssetDatabase.FindAssets("t:Prefab", new[] { _preparedRoot + "/Prefabs" })
            .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.That(originals, Is.Not.Empty, "A current prepared prefab is required.");
        var original = originals[0];
        var originalBytes = File.ReadAllBytes(original);
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(original);
        Assert.That((bool)Invoke(_pipeline, "CachedPrototypeValid", source), Is.True, original);
        var folder = Folder(_preparedRoot);
        var meshFolder = Folder(_preparedRoot + "/Meshes");
        var copyPath = folder + "/fixture.prefab";
        Assert.That(AssetDatabase.CopyAsset(original, copyPath), Is.True);
        var contents = PrefabUtility.LoadPrefabContents(copyPath);
        string secondMeshPath = null;
        try
        {
            var wear = contents.GetComponent<Wear>();
            var serialized = new SerializedObject(wear);
            var configs = serialized.FindProperty("configs");
            Assert.That(configs.arraySize, Is.EqualTo(2));
            Mesh first = null;
            for (var i = 0; i < 2; i++)
            {
                var reference = configs.GetArrayElementAtIndex(i).FindPropertyRelative("mesh");
                var mesh = Object.Instantiate((Mesh)reference.objectReferenceValue);
                mesh.name = _tag + "_body" + i;
                var path = meshFolder + "/body" + i + ".asset";
                AssetDatabase.CreateAsset(mesh, path);
                reference.objectReferenceValue = mesh;
                if (i == 0) first = mesh; else secondMeshPath = path;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            contents.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh = first;
            Assert.That(PrefabUtility.SaveAsPrefabAsset(contents, copyPath), Is.Not.Null);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
        Assert.That((bool)Invoke(_pipeline, "CachedPrototypeValid", AssetDatabase.LoadAssetAtPath<GameObject>(copyPath)), Is.True);
        Assert.That(AssetDatabase.DeleteAsset(secondMeshPath), Is.True);
        AssetDatabase.ImportAsset(copyPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        Assert.That((bool)Invoke(_pipeline, "CachedPrototypeValid", AssetDatabase.LoadAssetAtPath<GameObject>(copyPath)), Is.False);
        Assert.That(File.ReadAllBytes(original), Is.EqualTo(originalBytes), "The original prepared asset was read only.");
    }

    [Test]
    public void RestorePreservesAuthoredFitAndHidingButKeepsTheNewGeometry()
    {
        var sourceObject = new GameObject("source settings " + _tag); _transient.Add(sourceObject);
        var targetObject = new GameObject("target settings " + _tag); _transient.Add(targetObject);
        var oldMesh = Triangle("old " + _tag); var newMesh = Triangle("new " + _tag);
        var source = sourceObject.AddComponent<Wear>(); var target = targetObject.AddComponent<Wear>();
        sourceObject.AddComponent<SkinnedMeshRenderer>().sharedMesh = oldMesh;
        targetObject.AddComponent<SkinnedMeshRenderer>().sharedMesh = newMesh;
        var serialized = new SerializedObject(source);
        serialized.FindProperty("layer").intValue = (int)VisualWearLayer.Outerwear;
        serialized.FindProperty("gender").intValue = (int)VisualGender.Male;
        serialized.FindProperty("hidesHair").boolValue = true;
        Slots(serialized.FindProperty("slots"), VisualWearSlot.Chest, VisualWearSlot.Pelvis);
        Slots(serialized.FindProperty("noHideUnderwearSlots"), VisualWearSlot.Chest);
        Slots(serialized.FindProperty("hideWearSlots"), VisualWearSlot.Pelvis, VisualWearSlot.ThighL);
        serialized.FindProperty("heel").boxedValue = new HeelPose { footDegrees = 17f, toeDegrees = -9f, lift = .04f, axis = new Vector3(.1f, .9f, .2f) };
        serialized.FindProperty("headwearFit").boxedValue = new HeadwearFit { position = new Vector3(.01f, .02f, -.03f), rotation = new Vector3(2f, -3f, 4f), scale = new Vector3(.97f, 1.03f, 1.01f) };
        var configs = serialized.FindProperty("configs"); configs.arraySize = 2;
        for (var i = 0; i < 2; i++)
        {
            var row = configs.GetArrayElementAtIndex(i);
            row.FindPropertyRelative("actorName").intValue = (int)(i == 0 ? ActorName.Marta : ActorName.Kshishtof);
            row.FindPropertyRelative("scale").floatValue = i == 0 ? 1.07f : .93f;
            row.FindPropertyRelative("heightOffset").floatValue = i == 0 ? .023f : -.012f;
            row.FindPropertyRelative("mesh").objectReferenceValue = oldMesh;
            var lods = row.FindPropertyRelative("hairLodMeshes"); lods.arraySize = 1;
            lods.GetArrayElementAtIndex(0).objectReferenceValue = oldMesh;
        }
        serialized.ApplyModifiedPropertiesWithoutUndo();
        var sourceBefore = EditorJsonUtility.ToJson(source);
        var settings = EditorType("HexLive.UnityPresentation.Wearing.Editor.WearPreparationSettings");
        Invoke(settings, "Restore", target, source, ActorName.Marta, 1.5f);
        Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(sourceBefore));
        Assert.That(target.Layer, Is.EqualTo(source.Layer)); Assert.That(target.Gender, Is.EqualTo(source.Gender));
        Assert.That(target.HidesHair, Is.True); Assert.That(target.Slots, Is.EqualTo(source.Slots));
        Assert.That(target.NoHideUnderwearSlots, Is.EqualTo(source.NoHideUnderwearSlots));
        Assert.That(target.HideWearSlots, Is.EqualTo(source.HideWearSlots));
        Assert.That(target.Heel, Is.EqualTo(source.Heel)); Assert.That(target.GetHeadwearFit(), Is.EqualTo(source.GetHeadwearFit()));
        Assert.That(target.GetConfigScale(ActorName.Marta), Is.EqualTo(1.07f));
        Assert.That(target.GetConfigScale(ActorName.Kshishtof), Is.EqualTo(.93f));
        Assert.That(target.GetConfigHeight(ActorName.Marta), Is.EqualTo(.023f));
        Assert.That(target.GetConfigHeight(ActorName.Kshishtof), Is.EqualTo(-.012f));
        Assert.That(targetObject.GetComponent<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(newMesh));
        Assert.That(sourceObject.GetComponent<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(oldMesh));
        var actual = new SerializedObject(target).FindProperty("configs");
        Assert.That(actual.arraySize, Is.EqualTo(2));
        for (var i = 0; i < actual.arraySize; i++)
        {
            Assert.That(actual.GetArrayElementAtIndex(i).FindPropertyRelative("mesh").objectReferenceValue, Is.SameAs(newMesh));
            Assert.That(actual.GetArrayElementAtIndex(i).FindPropertyRelative("hairLodMeshes").arraySize, Is.Zero);
        }
    }

    private string Folder(string parent)
    {
        Assert.That(AssetDatabase.IsValidFolder(parent), Is.True, parent);
        var path = parent + "/" + _tag;
        Assert.That(AssetDatabase.IsValidFolder(path) || Directory.Exists(path), Is.False);
        Assert.That(AssetDatabase.CreateFolder(parent, _tag), Is.Not.Empty);
        _ownedAssets.Add(path);
        return path;
    }

    private static Type EditorType(string name)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        Assert.That(type, Is.Not.Null, "Missing actual editor implementation: " + name);
        return type;
    }

    private static object Invoke(Type type, string name, params object[] args)
    {
        var method = type.GetMethod(name, Hidden | BindingFlags.Public);
        Assert.That(method, Is.Not.Null, name);
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static void WritePng(string path, Color color)
    {
        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        try { texture.SetPixels(Enumerable.Repeat(color, 16).ToArray()); texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG()); }
        finally { Object.DestroyImmediate(texture); }
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
    }

    private Mesh Triangle(string name)
    {
        var mesh = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        mesh.RecalculateNormals(); _transient.Add(mesh); return mesh;
    }

    private static void Slots(SerializedProperty array, params VisualWearSlot[] slots)
    {
        array.arraySize = slots.Length;
        for (var i = 0; i < slots.Length; i++) array.GetArrayElementAtIndex(i).intValue = (int)slots[i];
    }

    private sealed class FileSnapshot
    {
        private readonly string _path;
        private readonly byte[] _bytes;
        private readonly DateTime _lastWrite;
        internal FileSnapshot(string path)
        {
            _path = path;
            if (File.Exists(path)) { _bytes = File.ReadAllBytes(path); _lastWrite = File.GetLastWriteTimeUtc(path); }
        }
        internal void Restore()
        {
            if (_bytes == null) { if (File.Exists(_path)) File.Delete(_path); }
            else { File.WriteAllBytes(_path, _bytes); File.SetLastWriteTimeUtc(_path, _lastWrite); }
        }
    }
}
}
#endif
