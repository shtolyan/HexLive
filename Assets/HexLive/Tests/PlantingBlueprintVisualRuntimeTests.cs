#if UNITY_EDITOR
using HexLive.UnityPresentation.Environment;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class PlantingBlueprintVisualRuntimeTests : BugWaveArtRuntimeFixture
{
    [Test]
    public void ProductionMarkerRendersAboveTheSoil()
    {
        for(var i=0;i<3;i++)
        {
            var marker=Keep(PlantingBlueprintMarker.Create());
            marker.transform.position=new Vector3((i-1)*.45f,0,0);
        }
        var plane=Keep(GameObject.CreatePrimitive(PrimitiveType.Plane));plane.transform.position=new Vector3(0,-.001f,0);
        plane.transform.localScale=Vector3.one*.3f;
        var light=Keep(new GameObject("Acceptance sun")).AddComponent<Light>();light.type=LightType.Directional;
        light.intensity=1f;light.transform.rotation=Quaternion.Euler(45,-30,0);
        Capture(new Vector3(0,.03f,0),"planting-mounds",.7f);
    }
}
}
#endif
