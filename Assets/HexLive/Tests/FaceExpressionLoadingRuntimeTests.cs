using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class FaceExpressionLoadingRuntimeTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Path = "HexLive/FaceExpressions/RecoveryTest";
    private const string Key = "config/faceexpressions.recoverytest#main";
    private object _previousService, _previousCatalog, _previousSubscription;
    private HashSet<string> _loading;
    private IDictionary _handles;
    private TextAsset _asset;
    private Mesh _mesh;
    private GameObject _actor;

    [SetUp]
    public void SetUp()
    {
        // Isolate the registry so this test never opens a production connection.
        var service = FormatterServices.GetUninitializedObject(typeof(ContentAssetService));
        typeof(ContentAssetService).GetField("_pinned", Instance).SetValue(service,
            new Dictionary<string, ContentRecord>());
        _previousService = Swap(typeof(ContentAssetService), "_instance", service);
        _previousSubscription = Swap(typeof(AtomicResources), "_registrySource", service);
        _previousCatalog = Swap(typeof(NpcFaceAnimator), "_shared", null);
        _loading = (HashSet<string>)typeof(AtomicResources).GetField("Loading", Static).GetValue(null);
        _handles = (IDictionary)typeof(AtomicResources).GetField("Handles", Static).GetValue(null);
        _loading.Add(Key);
        _asset = new TextAsset("{\"expressions\":[{\"name\":\"Pack 05\",\"shapes\":[{\"shape\":\"eCTRLMouthSmile\",\"weight\":80}]}]}");
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        // Complete a pending coroutine even when an assertion failed.
        Publish();
        yield return null;
        if (_actor != null) Object.DestroyImmediate(_actor);
        if (_mesh != null) Object.DestroyImmediate(_mesh);
        Swap(typeof(NpcFaceAnimator), "_shared", _previousCatalog);
        Swap(typeof(AtomicResources), "_registrySource", _previousSubscription);
        Swap(typeof(ContentAssetService), "_instance", _previousService);
        _handles.Remove(Key);
        _loading.Remove(Key);
        if (_asset != null) Object.DestroyImmediate(_asset);
    }

    [UnityTest]
    public IEnumerator LatePackAnimatesExistingActorAndPreservesCustomBindings()
    {
        var catalog = FaceExpressionCatalog.Load(Path);
        Assert.That(catalog.IsReady, Is.False);
        catalog.AddCustom("x_sad", "Sad", new[] { ("eCTRLMouthFrown", 65f) });
        var sadIndex = catalog.FindIndex("x_sad");
        Swap(typeof(NpcFaceAnimator), "_shared", catalog);
        _actor = new GameObject("Late face pack regression");
        var skin = _actor.AddComponent<SkinnedMeshRenderer>();
        _mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } };
        var delta = new[] { Vector3.one * .01f, Vector3.one * .01f, Vector3.one * .01f };
        _mesh.AddBlendShapeFrame("eCTRLMouthFrown", 100, delta, null, null);
        _mesh.AddBlendShapeFrame("eCTRLMouthSmile", 100, delta, null, null);
        skin.sharedMesh = _mesh;
        var rig = new FaceExpressionRig(new[] { skin }, catalog);
        rig.Apply(sadIndex, 1);
        Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(65));
        var face = _actor.AddComponent<NpcFaceAnimator>();
        face.Construct(new[] { skin });
        face.SetMood(1, false);
        face.SetTalkTopic("SmallTalk");
        Assert.That(Index(face), Is.EqualTo(-1));
        yield return null;
        LogAssert.NoUnexpectedReceived();

        Publish();
        for (var i = 0; i < 30 && (!catalog.IsReady || skin.GetBlendShapeWeight(1) <= 0); i++)
            yield return null;
        Assert.That(catalog.IsReady, Is.True);
        Assert.That(catalog.FindIndex("x_sad"), Is.EqualTo(sadIndex));
        Assert.That(Index(face), Is.EqualTo(catalog.FindIndex("05")));
        Assert.That((int)typeof(NpcFaceAnimator).GetField("_topicIndex", Instance).GetValue(face),
            Is.EqualTo(catalog.FindIndex("05")), "A conversation started before arrival must acquire its expression too.");
        Assert.That(skin.GetBlendShapeWeight(1), Is.GreaterThan(0), "An already constructed actor must acquire its smile without respawning.");
        rig.Apply(sadIndex, 1);
        Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(65), "Appending the pack must preserve previously resolved custom recipe bindings.");
    }

    [Test]
    public void AlreadyCachedPackIsImmediatelyReady()
    {
        Publish();
        var catalog = FaceExpressionCatalog.Load(Path);
        Assert.That(catalog.IsReady, Is.True);
        Assert.That(catalog.FindIndex("05"), Is.EqualTo(0));
        Assert.That(catalog.Expressions[0].Shapes[0].weight, Is.EqualTo(80));
    }

    private void Publish()
    {
        _handles[Key] = Activator.CreateInstance(typeof(ContentAssetHandle<Object>),
            Instance, null, new object[] { _asset, null }, null);
        _loading.Remove(Key);
    }

    private static int Index(NpcFaceAnimator face) =>
        (int)typeof(NpcFaceAnimator).GetField("_idxMoodSmile", Instance).GetValue(face);

    private static object Swap(Type type, string name, object value)
    {
        var field = type.GetField(name, Static);
        var previous = field.GetValue(null);
        field.SetValue(null, value);
        return previous;
    }
}
}
