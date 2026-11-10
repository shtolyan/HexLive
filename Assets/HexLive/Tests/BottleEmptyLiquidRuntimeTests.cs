#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Environment;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;

namespace HexLive.Tests;
public sealed class BottleEmptyLiquidRuntimeTests
{
    [UnityTest]
    public IEnumerator ActualGroundBottleEmptySnapshotHidesLiquidImmediatelyAndKeepsFractionalWater()
    {
        Assert.That(Application.isPlaying,Is.True,"Exercise the real runtime easing path.");
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/tool_bottle_native.prefab");
        Assert.That(prefab,Is.Not.Null);
        var cache=(IDictionary)typeof(ContentPrefabCache).GetField("Handles",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        string key="object/"+ContentIds.Bottle;bool had=cache.Contains(key);object old=cache[key];GameObject bottle=null;
        try
        {
            cache[key]=Activator.CreateInstance(typeof(ContentAssetHandle<GameObject>),BindingFlags.Instance|BindingFlags.NonPublic,
                null,new object[]{prefab,null},null);
            bottle=WorldPropResources.Build(ContentIds.Bottle);
            Assert.That(bottle,Is.Not.Null);
            BottleLiquidVisual.Sync(bottle,0f,WaterKind.None);
            var liquid=bottle.transform.Find("Bottle liquid").GetComponent<Renderer>();
            Assert.That(liquid.enabled,Is.False,"The first empty snapshot has no water.");
            BottleLiquidVisual.Sync(bottle,1f,WaterKind.Raw);
            yield return null;
            Assert.That(liquid.enabled,Is.True);
            BottleLiquidVisual.Sync(bottle,0f,WaterKind.None);
            Assert.That(liquid.enabled,Is.False,"A later empty snapshot must not ease through stale drops.");
            Assert.That(liquid.transform.localScale.y,Is.Zero);
            yield return null;yield return null;
            Assert.That(liquid.enabled,Is.False);
            BottleLiquidVisual.Sync(bottle,.0001f,WaterKind.Rain);
            yield return null;
            Assert.That(liquid.enabled,Is.True,"A real fractional remainder must remain distinguishable from zero.");
            BottleLiquidVisual.Sync(bottle,0f,WaterKind.Raw);
            Assert.That(liquid.enabled,Is.False,"Even an obsolete appearance cannot paint water into an empty vessel.");
        }
        finally
        {
            if(bottle!=null)Object.DestroyImmediate(bottle);
            if(had)cache[key]=old;else cache.Remove(key);
        }
    }
}
#endif
