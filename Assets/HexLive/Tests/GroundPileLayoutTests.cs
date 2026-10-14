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
            if(id=="resource.palm_crown")
            {
                Assert.That(ObjectFit.WorldBounds(visual,out var droppedBounds),Is.True);
                Assert.That(droppedBounds.center.x,Is.EqualTo(0f).Within(.00005f),"A single crown must not use the first corner of a20-crown grid.");
                Assert.That(droppedBounds.center.z,Is.EqualTo(0f).Within(.00005f));
                Assert.That(droppedBounds.min.y,Is.EqualTo(0f).Within(.00005f));
            }
            adapter.ApplyScatter(123);
            Assert.That(adapter.SlotTransform.localRotation,Is.EqualTo(first));
            Assert.That(GroundPileLayout.ScatterYaw(123),Is.Not.EqualTo(GroundPileLayout.ScatterYaw(124)));
        }
        finally {Object.DestroyImmediate(root);}
    }

#endif

    [TestCase(8)]
    [TestCase(42)]
    [TestCase(120)]
    public void CompactHarvestLeafLayersStayInsideOneHexWithSmallStableTurns(int count)
    {
#if UNITY_EDITOR
        var root=new GameObject("compact crown harvest");
        root.layer=31;
        var cameraRoot=new GameObject("leaf verification camera");
        RenderTexture target=null;
        Mesh hexMesh=null;Material groundMaterial=null;
        try
        {
            var renderer=root.AddComponent<HexWorldRenderer>();renderer.enabled=false;
            var fit=typeof(HexWorldRenderer).GetMethod("FitObjectPrefab",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var source=UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/palm_frond_native.fbx");
            Assert.That(source,Is.Not.Null);
            Assert.That(GroundPileCatalog.TryGet(ContentIds.PalmLeaf,false,out var profile),Is.True);
            var rotations=new System.Collections.Generic.HashSet<float>();
            for(var rank=0;rank<count;rank++)
            {
                var leaf=new GameObject("Harvest leaf "+rank);leaf.transform.SetParent(root.transform,false);
                var visual=Object.Instantiate(source,leaf.transform);
                fit.Invoke(renderer,new object[]{visual,ContentIds.PalmLeaf,rank+1000,false,true});
                foreach(var child in leaf.GetComponentsInChildren<Transform>(true)) child.gameObject.layer=31;
                var basis=visual.transform.localRotation;
                var pile=leaf.AddComponent<GroundPileLayout>();pile.Initialize(visual.transform,profile);
                pile.ApplyScatter(rank+1000,rank);
                var turn=Mathf.DeltaAngle(0,pile.SlotTransform.localEulerAngles.y);
                Assert.That(Mathf.Abs(turn),Is.LessThanOrEqualTo(12.0001f));rotations.Add(turn);
                Assert.That(pile.SlotTransform.localPosition.x,Is.Zero);Assert.That(pile.SlotTransform.localPosition.z,Is.Zero);
                Assert.That(pile.SlotTransform.localPosition.y,Is.InRange(0f,.118001f));
                Assert.That(visual.transform.localRotation,Is.EqualTo(basis));
                Assert.That(ObjectFit.WorldBounds(visual,out var bounds),Is.True);
                Assert.That(bounds.min.y,Is.EqualTo(pile.SlotTransform.localPosition.y).Within(.00005f));
                foreach(var x in new[]{bounds.min.x,bounds.max.x}) foreach(var z in new[]{bounds.min.z,bounds.max.z})
                    Assert.That(HexLive.Simulation.Spatial.HexSpatialMath.WorldToTile(new HexLive.Simulation.Common.Float2(x,z)),
                        Is.EqualTo(HexLive.Simulation.Common.TileCoord.Zero));
                var pose=pile.SlotTransform.localRotation;
                pile.ApplyScatter(rank+1000,rank);Assert.That(pile.SlotTransform.localRotation,Is.EqualTo(pose));
            }
            Assert.That(rotations.Count,Is.GreaterThan(1));
            if(count!=120)
            {
                var ground=new GameObject("one source hex",typeof(MeshFilter),typeof(MeshRenderer));ground.layer=31;
                ground.transform.SetParent(root.transform,false);ground.transform.localPosition=new Vector3(0,-.002f,0);
                var vertices=new Vector3[7];var triangles=new int[18];
                for(var i=0;i<6;i++)
                {
                    var angle=(30+i*60)*Mathf.Deg2Rad;
                    vertices[i+1]=new Vector3(Mathf.Cos(angle)*1.5f,0,Mathf.Sin(angle)*1.5f);
                    triangles[i*3]=0;triangles[i*3+1]=(i+1)%6+1;triangles[i*3+2]=i+1;
                }
                hexMesh=new Mesh {vertices=vertices,triangles=triangles};hexMesh.RecalculateNormals();
                ground.GetComponent<MeshFilter>().sharedMesh=hexMesh;
                groundMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));groundMaterial.SetColor("_BaseColor",new Color(.25f,.29f,.20f));
                ground.GetComponent<MeshRenderer>().sharedMaterial=groundMaterial;
                var camera=cameraRoot.AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=1.8f;
                camera.cullingMask=1<<31;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.11f,.14f,.12f);
                camera.transform.position=new Vector3(1.6f,3f,-2.4f);camera.transform.LookAt(new Vector3(0,.03f,0));
                target=new RenderTexture(640,640,24);target.Create();camera.targetTexture=target;
                camera.Render();var old=RenderTexture.active;var image=new Texture2D(640,640,TextureFormat.RGBA32,false);
                try
                {
                    RenderTexture.active=target;image.ReadPixels(new Rect(0,0,640,640),0,0);image.Apply();
                    System.IO.Directory.CreateDirectory("Build/MSIRelease/20261014-leaf-scatter/visual");
                    System.IO.File.WriteAllBytes("Build/MSIRelease/20261014-leaf-scatter/visual/leaves-"+count+".png",image.EncodeToPNG());
                }
                finally {RenderTexture.active=old;Object.DestroyImmediate(image);}
            }
        }
        finally {Object.DestroyImmediate(cameraRoot);Object.DestroyImmediate(root);if(hexMesh!=null)Object.DestroyImmediate(hexMesh);
            if(groundMaterial!=null)Object.DestroyImmediate(groundMaterial);if(target!=null){target.Release();Object.DestroyImmediate(target);}}
#endif
    }

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
            // Current compact logs no longer cross a hex when anchored at its
            // center. Put their anchor near an edge so this still tests a real step.
            elevations[center]=2;positions[123]=new HexLive.Simulation.Common.Float2(0,-1f);
            root.transform.position=new Vector3(0,renderer.GroundTopY(center),0);
            var item=new HexLive.Simulation.Debug.ObjectSnapshot {DefinitionId="resource.log",Tile=center};
            item.Junctions.Add(new HexLive.Simulation.Common.JunctionId(123));
            var visual=new GameObject("visual");visual.transform.SetParent(root.transform,false);
            Assert.That(GroundPileCatalog.TryGet(item.DefinitionId,false,out var profile),Is.True);
            var pile=root.AddComponent<GroundPileLayout>();pile.Initialize(visual.transform,profile);
            pile.Apply(0,GroundPileCatalog.Capacity(item.DefinitionId),123);
            var original=pile.SlotTransform.localPosition;
            Assert.That(HexLive.Simulation.Spatial.HexSpatialMath.WorldToTile(
                positions[123]+new HexLive.Simulation.Common.Float2(original.x,original.z)),Is.Not.EqualTo(center));
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
