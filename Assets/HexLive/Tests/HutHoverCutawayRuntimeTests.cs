#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{

// Offline: actual imported hut/roof geometry and production cutaway dispatcher.
// Controlled private cache injection avoids registry requests and world mutation.
public sealed class HutHoverCutawayRuntimeTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    static FieldInfo Field(string name)=>typeof(HexWorldRenderer).GetField(name,Private);
    [TestCase(false)] [TestCase(true)]
    public void SelectedAiColonistCanHoverOnlyOwnBuildingAndMovingAwayRestoresIt(bool modular)
    {
        var roots=new List<GameObject>();var selection=NpcSelection.SelectedIds.ToArray();
        var handles=(IDictionary)typeof(AtomicResources).GetField("Handles",Static).GetValue(null);
        string contentKey="building/"+ContentIds.Hut1Hex+"#main";
        bool had=handles.Contains(contentKey);object previous=handles[contentKey];
        var serviceField=typeof(ContentAssetService).GetField("_instance",Static);var oldService=serviceField.GetValue(null);
        var sourceField=typeof(AtomicResources).GetField("_registrySource",Static);var oldSource=sourceField.GetValue(null);
        try
        {
            var service=FormatterServices.GetUninitializedObject(typeof(ContentAssetService));
            typeof(ContentAssetService).GetField("_pinned",Private).SetValue(service,new Dictionary<string,ContentRecord>());
            serviceField.SetValue(null,service);sourceField.SetValue(null,service);
            var source=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/"+ContentIds.Hut1Hex+".fbx");
            Assert.That(source,Is.Not.Null);
            handles[contentKey]=Activator.CreateInstance(typeof(ContentAssetHandle<Object>),Private,null,new object[]{source,null},null);
            var host=new GameObject("Hover-cutaway production renderer");roots.Add(host);
            var renderer=host.AddComponent<HexWorldRenderer>();renderer.enabled=false;
            var cameraRoot=new GameObject("Hover-cutaway camera");roots.Add(cameraRoot);var camera=cameraRoot.AddComponent<Camera>();camera.enabled=false;
            camera.transform.position=new Vector3(0,5,-8);Field("_cutawayCamera").SetValue(renderer,camera);
            var snapshot=new WorldSnapshot();snapshot.Npcs.Add(new NpcSnapshot{Id=new HexLive.Simulation.Common.EntityId(661),
                Faction=Faction.Colony,Health=1,Position=new HexLive.Simulation.Common.Float2(100,100)});
            var house=new ObjectSnapshot{Id=new ObjectId(662),DefinitionId=modular?ContentIds.HutPlan:ContentIds.Hut1Hex,
                Tile=TileCoord.Zero,PropertyOwnerFaction=Faction.Colony};snapshot.Objects.Add(house);
            Field("_lastSnapshot").SetValue(renderer,snapshot);
            ((IDictionary)Field("_cutawayOwners").GetValue(renderer))[662]=house;
            Renderer[] visible;
            if(modular)
            {
                var roof=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/architecture.roof.palm.fbx");
                Assert.That(roof,Is.Not.Null);var art=Object.Instantiate(roof);roots.Add(art);visible=art.GetComponentsInChildren<Renderer>(true);
                Assert.That(visible,Is.Not.Empty);
                var module=art.AddComponent<ArchitectureModuleView>();typeof(ArchitectureModuleView).GetField("_renderers",Private).SetValue(module,visible);
                var row=typeof(HexWorldRenderer).GetNestedType("PlanCutawayModule",BindingFlags.NonPublic);
                ((IList)Field("_planCutawayModules").GetValue(renderer)).Add(Activator.CreateInstance(row,new object[]{662,true,Vector2.zero,module}));
                ((HashSet<int>)Field("_planRaisedOwners").GetValue(renderer)).Add(662);
                ((IDictionary)Field("_planFootprints").GetValue(renderer))[662]=new[]{TileCoord.Zero};
            }
            else
            {
                var art=HutAssembly.BuildFinished();Assert.That(art,Is.Not.Null);roots.Add(art);visible=art.GetComponentsInChildren<Renderer>(true);
                var row=typeof(HexWorldRenderer).GetNestedType("HutCutawayView",BindingFlags.NonPublic);
                ((IList)Field("_hutCutawayViews").GetValue(renderer)).Add(Activator.CreateInstance(row,new object[]{662,TileCoord.Zero,art.GetComponent<HutAssembly>()}));
            }
            NpcSelection.Replace(661);
            void Sync()=>typeof(HexWorldRenderer).GetMethod("UpdateHutCutaways",Private).Invoke(renderer,new object[]{snapshot});
            renderer.SetHutHover(662,TileCoord.Zero);Sync();
            Assert.That(visible.Any(v=>v.shadowCastingMode==ShadowCastingMode.ShadowsOnly),Is.True,"AI selection outside must reveal own house.");
            renderer.SetHutHover(-1,TileCoord.Zero);Sync();
            Assert.That(visible.Any(v=>v.shadowCastingMode==ShadowCastingMode.ShadowsOnly),Is.True,"Ground footprint sustains hover after roof disappears.");
            house.PropertyOwnerFaction=Faction.Colony2;renderer.SetHutHover(662,TileCoord.Zero);Sync();
            Assert.That(visible.All(v=>v.shadowCastingMode!=ShadowCastingMode.ShadowsOnly),Is.True,"Foreign house cannot be inspected by hover.");
            house.PropertyOwnerFaction=Faction.Colony;renderer.SetHutHover(-1,null);Sync();
            Assert.That(visible.All(v=>v.shadowCastingMode!=ShadowCastingMode.ShadowsOnly),Is.True);
            renderer.SetHutHover(662,TileCoord.Zero);NpcSelection.Clear();Sync();
            Assert.That(visible.All(v=>v.shadowCastingMode!=ShadowCastingMode.ShadowsOnly),Is.True,"Selection is mandatory.");
        }
        finally
        {
            foreach(var root in roots)if(root!=null)Object.DestroyImmediate(root);
            if(had)handles[contentKey]=previous;else handles.Remove(contentKey);
            serviceField.SetValue(null,oldService);sourceField.SetValue(null,oldSource);
            NpcSelection.ActivateMany(selection);
        }
    }
}
}
#endif
