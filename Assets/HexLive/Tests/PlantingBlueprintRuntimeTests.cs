#if UNITY_EDITOR
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Wearing.Garments;
using System.Reflection;
using UnityEditor;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class PlantingBlueprintRuntimeTests
{
    [TestCase(ContentIds.Coconut,ContentIds.Palm)]
    [TestCase(ContentIds.YuccaSeed,ContentIds.Yucca)]
    [TestCase(ContentIds.HerbSeed,ContentIds.HerbBush)]
    public void PlantingCardLoadsTheMaturePlantAtomicIcon(string seed,string plant)
    {
        var select = typeof(BuildModePanel).GetMethod("CatalogIconId",BindingFlags.Static|BindingFlags.NonPublic);
        Assert.That(select,Is.Not.Null);
        Assert.That(select.Invoke(null,new object[]{seed}),Is.EqualTo(plant));
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/HexLiveContent/Icons/"+plant+".png");
        Assert.That(sprite,Is.Not.Null,plant);
        Assert.That(sprite.rect.size,Is.EqualTo(new Vector2(512,512)));
        Assert.That(ItemIcons.Address(plant),Is.EqualTo("object/"+plant+"#icon"));
    }

    [TestCase(ContentIds.Coconut)]
    [TestCase(ContentIds.YuccaSeed)]
    [TestCase(ContentIds.HerbSeed)]
    public void CatalogPlantingNameAndDescriptionResolveThroughImportedLoc(string seed)
    {
        Assert.That(BuildCatalogDefinition.TryGet(seed, out var card), Is.True);
        var original = Loc.Current;
        try
        {
            foreach(var language in new[]{Language.English,Language.Russian})
            {
                Loc.Current=language;
                foreach(var key in new[]{card.NameTerm,card.DescriptionTerm})
                {
                    Assert.That(Loc.Has(key), Is.True,key);
                    Assert.That(Loc.Get(key), Is.Not.Empty.And.Not.EqualTo(key));
                    if(language==Language.Russian) Assert.That(Loc.Get(key), Does.Match("[А-Яа-яЁё]"));
                }
            }
        }
        finally {Loc.Current=original;}
    }

    [Test]
    public void ActualAndGhostMarkerFactoryMakesASmallGroundMound()
    {
        var marker=PlantingBlueprintMarker.Create();
        try
        {
            var mesh=marker.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(mesh,Is.Not.Null);
            Assert.That(mesh.triangles.Length/3,Is.EqualTo(8));
            Assert.That(mesh.bounds.size.x,Is.EqualTo(PlantingBlueprintMarker.Radius*2).Within(0.0001f));
            Assert.That(mesh.bounds.size.z,Is.EqualTo(PlantingBlueprintMarker.Radius*2).Within(0.0001f));
            Assert.That(mesh.bounds.min.y,Is.GreaterThanOrEqualTo(0));
            Assert.That(mesh.bounds.max.y,Is.EqualTo(PlantingBlueprintMarker.Height).Within(0.0001f));
            Assert.That(marker.transform.localScale,Is.EqualTo(Vector3.one));
            Assert.That(marker.GetComponent<MeshRenderer>().sharedMaterial,Is.Not.Null);
        }
        finally {Object.DestroyImmediate(marker);}
    }
}
}
#endif
