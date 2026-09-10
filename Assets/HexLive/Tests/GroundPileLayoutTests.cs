using HexLive.Simulation.Content;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class GroundPileLayoutTests
{
    [TestCase("resource.stick",1.05f)]
    [TestCase("resource.palm_leaf",.825f)]
    [TestCase("resource.board",.75f)]
    [TestCase("food.meat_raw",.27f)]
    [TestCase("tool.bottle",.27f)]
    [TestCase("tool.spear",1.35f)]
    [TestCase("tool.lighter",.108f)]
    [TestCase("resource.rope",.15f)]
    [TestCase("med.splint",.18f)]
    [TestCase("item.bandage",.18f)]
    public void SharedFitRetainsExistingGroundAndHandSizes(string id,float expected)
    {
        Assert.That(ObjectFit.TargetWorldSize(id),Is.EqualTo(expected).Within(.00001f));
    }

    [Test]
    public void SlotReflowPreservesSourceSeatingAndSimulationAnchor()
    {
        var root=new GameObject("ground pile fixture");
        try
        {
            root.transform.position=new Vector3(10f,.3f,12f);
            var visual=GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.transform.SetParent(root.transform,false);
            visual.transform.localPosition=new Vector3(0,.04f,0);
            Assert.That(GroundPileCatalog.TryGet(ContentIds.Stick,false,out var profile),Is.True);
            var adapter=root.AddComponent<GroundPileLayout>();adapter.Initialize(visual.transform,profile);
            var sourcePosition=visual.transform.localPosition;
            adapter.Apply(19,20,77);
            Assert.That(adapter.SlotTransform.localPosition.y,Is.GreaterThan(.2f));
            Assert.That(visual.transform.localPosition,Is.EqualTo(sourcePosition));
            Assert.That(root.transform.position,Is.EqualTo(new Vector3(10f,.3f,12f)));
            adapter.Apply(0,20,77); // first neighbour removed/re-ranked, same seated child
            Assert.That(adapter.SlotTransform.localPosition.y,Is.EqualTo(0f));
            Assert.That(visual.transform.localPosition,Is.EqualTo(sourcePosition));
        }
        finally {Object.DestroyImmediate(root);}
    }

    [Test]
    public void LegacyOvercapacityStaysAtBoundedBasePose()
    {
        var root=new GameObject("legacy pile fixture");
        try
        {
            var visual=new GameObject("source");visual.transform.SetParent(root.transform,false);
            Assert.That(GroundPileCatalog.TryGet(ContentIds.Stick,false,out var profile),Is.True);
            var adapter=root.AddComponent<GroundPileLayout>();adapter.Initialize(visual.transform,profile);
            adapter.Apply(19,20,77);adapter.ApplyLegacy(77);
            Assert.That(adapter.SlotIndex,Is.EqualTo(-1));
            Assert.That(adapter.Capacity,Is.EqualTo(0));
            Assert.That(adapter.SlotTransform.localPosition,Is.EqualTo(Vector3.zero));
            Assert.That(adapter.SlotTransform.localRotation,Is.EqualTo(Quaternion.identity));
        }
        finally {Object.DestroyImmediate(root);}
    }

    [Test]
    public void NegativeXSourceCentersActualBoundsWithoutChangingSeatingOrLayerPitch()
    {
        var root=new GameObject("negative X leaf pivot");
        try
        {
            root.transform.position=new Vector3(9,.2f,11);
            var visual=GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.transform.SetParent(root.transform,false);
            // Deliberately asymmetric around the source origin: [-.825,0] X.
            visual.transform.localScale=new Vector3(.825f,.15848f,.42258575f);
            visual.transform.localPosition=new Vector3(-.4125f,.07924f,.06f);
            Assert.That(GroundPileCatalog.TryGet(ContentIds.PalmLeaf,false,out var profile),Is.True);
            var adapter=root.AddComponent<GroundPileLayout>();adapter.Initialize(visual.transform,profile);
            adapter.Apply(59,60,123);
            var bounds=visual.GetComponent<Renderer>().bounds;
            var center=root.transform.InverseTransformPoint(bounds.center);
            Assert.That(center.x,Is.EqualTo(0).Within(.00001f));
            Assert.That(center.z,Is.EqualTo(0).Within(.00001f));
            Assert.That(bounds.size.x,Is.EqualTo(.825f).Within(.00001f));
            Assert.That(center.y-bounds.extents.y,Is.EqualTo(.059f).Within(.00001f));
            Assert.That(root.transform.position,Is.EqualTo(new Vector3(9,.2f,11)));
        }
        finally {Object.DestroyImmediate(root);}
    }
}
}
