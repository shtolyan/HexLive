#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Environment;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HexLive.Tests
{
public sealed class DryingRackFeetRuntimeTests : BugWaveArtRuntimeFixture
{
    static Bounds BoundsIn(Transform root, MeshFilter filter)
    {
        var vertices=filter.sharedMesh.vertices;
        Assert.That(vertices,Is.Not.Empty);
        var matrix=(root!=null?root.worldToLocalMatrix:Matrix4x4.identity)*filter.transform.localToWorldMatrix;
        var bounds=new Bounds(matrix.MultiplyPoint3x4(vertices[0]),Vector3.zero);
        foreach(var vertex in vertices) bounds.Encapsulate(matrix.MultiplyPoint3x4(vertex));
        return bounds;
    }
    [UnityTest]
    public IEnumerator AuthoredFeetRemainGroundedThroughFactoryConstructionAndAllSixYaws()
    {
        var rack=Keep(BedAssembly.BuildFinished(ContentIds.DryingRack));
        Assert.That(rack.transform.position,Is.EqualTo(Vector3.zero));
        Assert.That(Quaternion.Angle(rack.transform.rotation,Quaternion.identity),Is.LessThan(.0001f));
        Assert.That(rack.transform.localScale,Is.EqualTo(Vector3.one));
        var feet=rack.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.name.StartsWith("Rack_corner_stay_")).OrderBy(f=>f.name).ToArray();
        Assert.That(feet.Length,Is.EqualTo(2),"Use the two original stays as feet; no duplicate runtime geometry.");
        foreach(var foot in feet)
        {
            var bounds=BoundsIn(rack.transform,foot);
            Assert.That(bounds.min.y,Is.EqualTo(0f).Within(.00005f),foot.name);
            Assert.That(bounds.size.y,Is.LessThan(.03f),foot.name);
            Assert.That(bounds.size.x,Is.InRange(.32f,.34f),foot.name);
            Assert.That(bounds.size.x,Is.GreaterThan(bounds.size.z*8),"The foot must cross the rack's longitudinal Z axis.");
            // A seven-sided cross section has an asymmetric AABB. Measure the
            // authored axis from both cap rings, as the Blender verifier does.
            var matrix=rack.transform.worldToLocalMatrix*foot.transform.localToWorldMatrix;
            var points=foot.sharedMesh.vertices.Select(matrix.MultiplyPoint3x4).Distinct().ToArray();
            float minX=points.Min(p=>p.x),maxX=points.Max(p=>p.x);
            // The authored end rings can be slightly oblique; selecting only
            // absolute min/max X would keep two corners rather than a cap.
            var first=points.Where(p=>p.x<minX+(maxX-minX)*.25f).ToArray();
            var last=points.Where(p=>p.x>maxX-(maxX-minX)*.25f).ToArray();
            Assert.That(first.Length,Is.EqualTo(7),foot.name+" first cap");
            Assert.That(last.Length,Is.EqualTo(7),foot.name+" last cap");
            var axisCenter=(first.Aggregate(Vector3.zero,(a,b)=>a+b)/first.Length+last.Aggregate(Vector3.zero,(a,b)=>a+b)/last.Length)*.5f;
            Assert.That(Mathf.Abs(axisCenter.z),Is.EqualTo(.5f).Within(.00005f),foot.name);
            var renderer=foot.GetComponent<MeshRenderer>();
            Assert.That(renderer,Is.Not.Null);
            Assert.That(renderer.sharedMaterials,Is.Not.Empty);
            foreach(var material in renderer.sharedMaterials)
            {
                Assert.That(material,Is.Not.Null);
                Assert.That(material.shader,Is.Not.Null);
                Assert.That(material.shader.name,Is.EqualTo("Universal Render Pipeline/Lit"));
            }
        }
        var stages=rack.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("BuildStage_")).OrderBy(t=>t.name).ToArray();
        Assert.That(stages.Length,Is.EqualTo(3));
        Assert.That(stages.Select(t=>t.childCount).ToArray(),Is.EqualTo(new[]{2,2,4}),"The original four sticks and four ropes still pay for all units.");
        var units=stages.SelectMany(stage=>stage.Cast<Transform>()).ToArray();
        Assert.That(units.Length,Is.EqualTo(8));
        foreach(var foot in feet)
        {
            var unit=foot.transform;
            while(unit.parent!=null && unit.parent!=stages[0]) unit=unit.parent;
            Assert.That(unit.parent,Is.EqualTo(stages[0]),foot.name+" must arrive with a supporting first-stage post.");
        }
        var sun=Keep(new GameObject("Rack639 acceptance sun")).AddComponent<Light>();
        sun.type=LightType.Directional;sun.intensity=1.4f;sun.transform.rotation=Quaternion.Euler(40,-25,0);
        var assembly=rack.GetComponent<BedAssembly>();
        for(int delivered=0;delivered<=8;delivered++)
        {
            assembly.Apply(0,Mathf.Min(delivered,4),Mathf.Max(0,delivered-4),0);
            yield return null;
            Assert.That(units.Count(t=>t.gameObject.activeInHierarchy),Is.EqualTo(delivered),"Paid units "+delivered);
            Assert.That(feet.Count(f=>f.gameObject.activeInHierarchy),Is.EqualTo(Mathf.Min(delivered,2)),"Feet arrive with their posts.");
            if(delivered==2 || delivered==8) Capture(new Vector3(0,.65f,0),"rack639-delivered"+delivered);
        }
        // Out-of-order rope delivery must not reveal a floating later stage.
        assembly.Apply(0,1,4,0);yield return null;
        Assert.That(units.Count(t=>t.gameObject.activeInHierarchy),Is.EqualTo(1));
        Assert.That(feet.Count(f=>f.gameObject.activeInHierarchy),Is.EqualTo(1));
        assembly.ApplyAll();
        for(int yaw=0;yaw<6;yaw++)
        {
            rack.transform.rotation=Quaternion.Euler(0,yaw*60,0);yield return null;
            foreach(var foot in feet)
            {
                var world=BoundsIn(null,foot);
                Assert.That(world.min.y,Is.EqualTo(0f).Within(.00005f),"Ground contact at yaw "+yaw);
            }
        }
    }
}
}
#endif
