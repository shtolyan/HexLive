#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Views;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class PointerTargetCollectorRuntimeTests
{
    [Test]
    public void RealOverlappingStickViewsStaySeparateUntilTheyShareOneContextOwner()
    {
        var root=new GameObject("multi-hit actual stick owners");
        var views=new List<WorldObjectView>();
        try
        {
            root.transform.position=new Vector3(1000,0,1000);
            var renderer=root.AddComponent<HexWorldRenderer>();renderer.enabled=false;
            var fit=typeof(HexWorldRenderer).GetMethod("FitObjectPrefab",BindingFlags.NonPublic|BindingFlags.Instance);
            var source=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/resource.stick.fbx");
            Assert.That(source,Is.Not.Null);
            for(int i=0;i<2;i++)
            {
                var owner=new GameObject("physical stick "+i);owner.transform.SetParent(root.transform,false);
                var visual=Object.Instantiate(source,owner.transform);
                fit.Invoke(renderer,new object[]{visual,ContentIds.Stick,100+i,false,true});
                Assert.That(ObjectFit.WorldBounds(visual,out var bounds),Is.True);
                visual.transform.position-=new Vector3(bounds.center.x-1000,0,bounds.center.z-1000);
                var view=owner.AddComponent<WorldObjectView>();view.Init(100+i,ContentIds.Stick);views.Add(view);
            }
            var hits=new List<WorldObjectPicker.Hit>();var ray=new Ray(new Vector3(1000,3,1000),Vector3.down);
            WorldObjectPicker.Collect(ray,v=>views.Contains(v),v=>false,hits);
            Assert.That(hits.Select(h=>h.View.ObjectId),Is.EquivalentTo(new[]{100,101}));
            Assert.That(hits.All(h=>h.Distance>0),Is.True);
            var choices=new PointerTargetSet();
            choices.Add(PointerTargetKind.Person,100,2);
            foreach(var hit in hits)choices.Add(PointerTargetKind.Object,hit.View.ContextObjectId,hit.Distance);
            Assert.That(choices.Items.Count,Is.EqualTo(3),"a person and same-numbered object must remain distinct");
            views[1].SetContextProxy(100,ContentIds.Stick);choices.Clear();
            foreach(var hit in hits)choices.Add(PointerTargetKind.Object,hit.View.ContextObjectId,hit.Distance);
            Assert.That(choices.Items.Count,Is.EqualTo(1),"multiple construction surfaces have one context owner");
            views[1].gameObject.SetActive(false);
            WorldObjectPicker.Collect(ray,v=>views.Contains(v),v=>false,hits);
            Assert.That(hits.Count,Is.EqualTo(1),"inactive invisible views are absent from the candidate registry");
        }
        finally {Object.DestroyImmediate(root);}
    }
    [Test] public void TargetChooserLabelsComeFromImportedLocalizationInBothLanguages()
    {
        var old=Loc.Current;
        try
        {
            foreach(var language in new[]{Language.Russian,Language.English})
            {
                Loc.Current=language;
                foreach(var key in new[]{"menu.choose_target","menu.walk_here"})
                {Assert.That(Loc.Has(key),Is.True);Assert.That(Loc.Get(key),Is.Not.EqualTo(key).And.Not.Empty);}
            }
        }
        finally {Loc.Current=old;}
    }
}
}
#endif
