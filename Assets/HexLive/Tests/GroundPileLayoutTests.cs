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

#if UNITY_EDITOR
    [TestCase("resource.log", "resource.log")]
    [TestCase("resource.palm_leaf", "palm_frond_native")]
    [TestCase("resource.palm_crown", "resource.palm_crown")]
    public void HarvestSourceRestsAtGroundWithStableYawAndNoPileOffset(string id,string filename)
    {
        var root=new GameObject("harvest ground fixture");
        try
        {
            var source=UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/HexLiveContent/RuntimeSource/Objects/"+filename+".fbx");
            Assert.That(source,Is.Not.Null);
            var visual=Object.Instantiate(source,root.transform);
            var renderer=root.AddComponent<HexWorldRenderer>();renderer.enabled=false;
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            if(id=="resource.palm_crown")
                typeof(HexWorldRenderer).GetMethod("GroundVisual",System.Reflection.BindingFlags.NonPublic|
                    System.Reflection.BindingFlags.Static).Invoke(null,new object[]{visual,0f});
            else
                typeof(HexWorldRenderer).GetMethod("FitObjectPrefab",flags)
                    .Invoke(renderer,new object[]{visual,id,123,false,true});
            Assert.That(GroundPileCatalog.TryGet(id,false,out var profile),Is.True);
            var adapter=root.AddComponent<GroundPileLayout>();adapter.Initialize(visual.transform,profile);
            var basis=visual.transform.localRotation;
            for(var i=1;i<=16;i++)
            {
                adapter.ApplyScatter(i);
                Assert.That(ObjectFit.WorldBounds(visual,out var bounds),Is.True);
                Assert.That(bounds.min.y,Is.EqualTo(0).Within(.00005f),id+" floats");
                Assert.That(adapter.SlotTransform.localPosition,Is.EqualTo(Vector3.zero));
                Assert.That(visual.transform.localRotation,Is.EqualTo(basis));
                Assert.That(bounds.min.x,Is.GreaterThanOrEqualTo(-profile.Single.RadiusXZ-.00005f));
                Assert.That(bounds.max.x,Is.LessThanOrEqualTo(profile.Single.RadiusXZ+.00005f));
                Assert.That(bounds.max.y,Is.LessThanOrEqualTo(profile.Single.MaxY+.00005f));
            }
            adapter.ApplyScatter(123);var first=adapter.SlotTransform.localRotation;
            adapter.Apply(0,GroundPileCatalog.Capacity(id),123);
            adapter.ApplyScatter(123);
            Assert.That(adapter.SlotTransform.localRotation,Is.EqualTo(first));
            Assert.That(GroundPileLayout.ScatterYaw(123),Is.Not.EqualTo(GroundPileLayout.ScatterYaw(124)));
        }
        finally {Object.DestroyImmediate(root);}
    }

#endif

    [Test]
    public void LooseObjectSupportUsesTileUnderAnchorInsteadOfRecordedTile()
    {
        var root=new GameObject("support ground fixture");
        try
        {
            var renderer=root.AddComponent<HexWorldRenderer>();renderer.enabled=false;
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            var elevations=(System.Collections.Generic.Dictionary<HexLive.Simulation.Common.TileCoord,int>)
                typeof(HexWorldRenderer).GetField("_tileElevations",flags).GetValue(renderer);
            var positions=(System.Collections.Generic.Dictionary<int,HexLive.Simulation.Common.Float2>)
                typeof(HexWorldRenderer).GetField("_junctionPositions",flags).GetValue(renderer);
            var high=new HexLive.Simulation.Common.TileCoord(0,0);
            var low=new HexLive.Simulation.Common.TileCoord(1,0);
            elevations[high]=2;elevations[low]=1;
            positions[123]=HexLive.Simulation.Spatial.HexSpatialMath.TileToWorld(low);
            var item=new HexLive.Simulation.Debug.ObjectSnapshot {DefinitionId="resource.log",Tile=high,IsHarvestScatter=true};
            item.Junctions.Add(new HexLive.Simulation.Common.JunctionId(123));
            Assert.That(renderer.ObjectGroundTopY(item),Is.EqualTo(renderer.GroundTopY(low)));
        }
        finally {Object.DestroyImmediate(root);}
    }

    [Test]
    public void OrderlyLogPileSeatsEachColumnOnItsOwnTerrainStep()
    {
        var root=new GameObject("pile terrain fixture");
        try
        {
            var renderer=root.AddComponent<HexWorldRenderer>();renderer.enabled=false;
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            var elevations=(System.Collections.Generic.Dictionary<HexLive.Simulation.Common.TileCoord,int>)
                typeof(HexWorldRenderer).GetField("_tileElevations",flags).GetValue(renderer);
            var positions=(System.Collections.Generic.Dictionary<int,HexLive.Simulation.Common.Float2>)
                typeof(HexWorldRenderer).GetField("_junctionPositions",flags).GetValue(renderer);
            var center=HexLive.Simulation.Common.TileCoord.Zero;
            for(var q=-3;q<=3;q++) for(var r=-3;r<=3;r++)
                elevations[new HexLive.Simulation.Common.TileCoord(q,r)]=1;
            elevations[center]=2;positions[123]=new HexLive.Simulation.Common.Float2(0,0);
            root.transform.position=new Vector3(0,renderer.GroundTopY(center),0);
            var item=new HexLive.Simulation.Debug.ObjectSnapshot {DefinitionId="resource.log",Tile=center};
            item.Junctions.Add(new HexLive.Simulation.Common.JunctionId(123));
            var visual=new GameObject("visual");visual.transform.SetParent(root.transform,false);
            Assert.That(GroundPileCatalog.TryGet(item.DefinitionId,false,out var profile),Is.True);
            var pile=root.AddComponent<GroundPileLayout>();pile.Initialize(visual.transform,profile);
            pile.Apply(0,GroundPileCatalog.Capacity(item.DefinitionId),123);
            var original=pile.SlotTransform.localPosition;
            typeof(HexWorldRenderer).GetMethod("AdjustGroundPileSupport",flags).Invoke(renderer,new object[]{pile,item});
            var offset=pile.SlotTransform.localPosition;
            Assert.That(offset.x,Is.EqualTo(original.x));Assert.That(offset.z,Is.EqualTo(original.z));
            Assert.That(offset.y,Is.EqualTo(-.55f).Within(.00001f));
            Assert.That(pile.SlotTransform.localRotation,Is.EqualTo(Quaternion.identity));
            Assert.That(root.transform.position.y,Is.EqualTo(renderer.GroundTopY(center)));
        }
        finally {Object.DestroyImmediate(root);}
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
