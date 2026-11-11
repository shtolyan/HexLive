#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class SparseGarmentPaintMapTests
{
    static PaintPointMap Bake(Mesh mesh, SkinnedMeshRenderer renderer, float radius)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HexLive.UnityDebug.Editor.PaintPointMapGenerator")).First(t => t != null);
        return (PaintPointMap)type.GetMethod("BuildGarmentMap",BindingFlags.Static|BindingFlags.NonPublic)
            .Invoke(null,new object[]{"sparse-regression",mesh,renderer,radius});
    }
    static bool Valid(PaintPointMap map,string zone) => map.Zones.Single(z => z.Zone == zone).Points.Any(p => p.Valid);

    [TestCase("clothing.bowtie_nerd","Head")]
    [TestCase("clothing.pendant_amy","Torso")]
    [TestCase("clothing.gloves_fit","ArmL")]
    [TestCase("clothing.ankleboots_amy","LegL")]
    public void RealPreparedAccessoryBothBodyMeshesHaveAnatomicalPoints(string id,string expectedZone)
    {
        string path = "Assets/HexLiveContent/People/Wardrobe/Prefabs/" + Hash128.Compute(id) + ".prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Assert.That(prefab,Is.Not.Null,path);
        var wear = prefab.GetComponent<Wear>(); var renderer = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
        var configs = new SerializedObject(wear).FindProperty("configs");
        foreach (var actor in new[]{ActorName.Marta,ActorName.Kshishtof})
        {
            var row = Enumerable.Range(0,configs.arraySize).Select(i => configs.GetArrayElementAtIndex(i))
                .Single(c => c.FindPropertyRelative("actorName").intValue == (int)actor);
            var mesh = row.FindPropertyRelative("mesh").objectReferenceValue as Mesh;
            Assert.That(mesh,Is.Not.Null,actor.ToString());
            var map = Bake(mesh,renderer,.9f/Mathf.Max(.00001f,Mathf.Abs(renderer.transform.lossyScale.x)));
            try
            {
                Assert.That(map.Zones.Length,Is.EqualTo(7));
                Assert.That(Valid(map,expectedZone),Is.True,id+"/"+actor);
                Assert.That(map.MeshName,Is.EqualTo(mesh.name)); Assert.That(map.VertexCount,Is.EqualTo(mesh.vertexCount));
                if (id == "clothing.bowtie_nerd")
                    foreach(string zone in new[]{"Torso","Pelvis","ArmL","ArmR","LegL","LegR"}) Assert.That(Valid(map,zone),Is.False,zone);
            }
            finally { Object.DestroyImmediate(map); }
        }
    }

    [TestCase("neckLower","Head")]
    [TestCase("chestUpper","Torso")]
    [TestCase("hip","Pelvis")]
    [TestCase("lHand","ArmL")]
    [TestCase("rIndex3","ArmR")]
    [TestCase("lFoot","LegL")]
    [TestCase("rSmallToe4_2","LegR")]
    public void SparseBoneMapsOnlyItsAnatomicalZoneWithinExistingRadius(string bone,string zone)
    {
        WithRig(new[]{bone},new[]{Vector3.zero},(mesh,renderer) =>
        {
            var map = Bake(mesh,renderer,.05f);
            try
            {
                Assert.That(map.Zones.Length,Is.EqualTo(7)); Assert.That(Valid(map,zone),Is.True);
                Assert.That(map.Zones.Count(z => z.Points.Any(p => p.Valid)),Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(map); }
        });
    }
    [Test] public void CanonicalAnchorWinsEvenWhenAliasWouldBeCloser()
    {
        WithRig(new[]{"head","neckLower"},new[]{Vector3.up*2,Vector3.zero},(mesh,renderer) =>
        {
            var map = Bake(mesh,renderer,.05f);
            try { Assert.That(map.Zones.All(z => z.Points.Length == 0),Is.True); }
            finally { Object.DestroyImmediate(map); }
        });
    }
    [Test] public void AnatomicalAliasOutsideRadiusDoesNotFabricateValidPoint()
    {
        WithRig(new[]{"neckLower"},new[]{Vector3.up*2},(mesh,renderer) =>
        {
            var map = Bake(mesh,renderer,.05f);
            try { Assert.That(map.Zones.All(z => z.Points.Length == 0),Is.True); }
            finally { Object.DestroyImmediate(map); }
        });
    }
    static void WithRig(string[] names,Vector3[] positions,Action<Mesh,SkinnedMeshRenderer> action)
    {
        var go = new GameObject("sparse-paint-rig"); var mesh = new Mesh();
        try
        {
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.bones = names.Select(name => { var bone = new GameObject(name); bone.transform.SetParent(go.transform,false); return bone.transform; }).ToArray();
            mesh.vertices = new[]{new Vector3(-.01f,0,-.01f),new Vector3(.01f,0,-.01f),new Vector3(0,0,.02f)};
            mesh.uv = new[]{Vector2.zero,Vector2.right,Vector2.up}; mesh.triangles = new[]{0,1,2};
            mesh.bindposes = positions.Select(p => Matrix4x4.Translate(-p)).ToArray();
            mesh.RecalculateNormals(); renderer.sharedMesh=mesh; action(mesh,renderer);
        }
        finally { Object.DestroyImmediate(go); Object.DestroyImmediate(mesh); }
    }
}
}
#endif
