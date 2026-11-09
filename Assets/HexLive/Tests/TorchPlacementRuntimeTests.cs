#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class TorchPlacementRuntimeTests : BugWaveArtRuntimeFixture
{
    [TestCase(false)] [TestCase(true)]
    public void RealWorldFactoryDistinguishesDropAndUprightPlacement(bool planted)
    {
        const BindingFlags fields=BindingFlags.NonPublic|BindingFlags.Instance;
        var service=ContentAssetService.Instance;
        var ready=typeof(ContentAssetService).GetField("_registryReady",fields);
        var old=ready.GetValue(service);ready.SetValue(service,true);
        var wear=(IDictionary)typeof(HexLive.UnityPresentation.Wearing.ActorWardrobe).GetField("Cache",Static).GetValue(null);
        wear[ContentIds.Torch]=new List<HexLive.UnityPresentation.Wearing.Wear>();
        try
        {
            var renderer=Keep(new GameObject("Production renderer fixture")).AddComponent<HexWorldRenderer>();renderer.enabled=false;
            var root=Keep(new GameObject("Production objects"));
            typeof(HexWorldRenderer).GetField("_objectsRoot",fields).SetValue(renderer,root.transform);
            var item=new ObjectSnapshot {Id=new ObjectId(627),DefinitionId=ContentIds.Torch,
                Variant=planted?InventoryPlacementMath.PlantedTorchVariant:string.Empty};
            item.Junctions.Add(new JunctionId(0));
            var view=Keep((GameObject)typeof(HexWorldRenderer).GetMethod("CreateObjectView",fields)
                .Invoke(renderer,new object[]{item,new Dictionary<int,Float2>{{0,new Float2(0,0)}},0}));
            var tip=view.GetComponentsInChildren<Transform>().Single(t=>t.name=="fire_point");
            var direction=(tip.position-tip.parent.position).normalized;
            if(planted) Assert.That(Vector3.Dot(direction,Vector3.up),Is.GreaterThan(.99f));
            else Assert.That(System.Math.Abs(Vector3.Dot(direction,Vector3.up)),Is.LessThan(.2f));
            ObjectFit.WorldBounds(view,out var bounds);
            Assert.That(bounds.min.y,Is.EqualTo(renderer.ObjectGroundTopY(item)).Within(.001f));
            var light=Keep(new GameObject("Acceptance sun")).AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(40,-25,0);
            Capture(bounds.center,"torch-world-"+(planted?"placed":"dropped"));
            foreach(var language in new[]{Language.Russian,Language.English})
            {
                var original=Loc.Current;
                try{Loc.Current=language;Assert.That(Loc.Get("inv.action.place"),Is.EqualTo(language==Language.Russian?"Поставить":"Place"));}
                finally{Loc.Current=original;}
            }
        }
        finally{ready.SetValue(service,old);}
    }
}
}
#endif
