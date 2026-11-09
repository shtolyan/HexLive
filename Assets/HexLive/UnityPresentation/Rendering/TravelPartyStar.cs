#nullable enable
using HexLive.UnityPresentation.Wearing;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.Rendering
{
/// <summary>#653: one blue world-space star above a consenting travel companion.</summary>
public sealed class TravelPartyStar : MonoBehaviour
{
    private NpcActorView _actor = null!;
    private MeshRenderer _view = null!;
    private bool _shown;
    private static Mesh? _mesh;
    private static Material? _material;

    public static void Sync(NpcActorView actor,bool shown)
    {
        var star = actor.GetComponent<TravelPartyStar>();
        if (star == null && !shown) return;
        if (star == null)
        {
            star = actor.gameObject.AddComponent<TravelPartyStar>();
            star._actor = actor;
            var visual = new GameObject("Travel companion star");
            visual.transform.SetParent(actor.transform,false);
            visual.AddComponent<MeshFilter>().sharedMesh = Mesh();
            star._view = visual.AddComponent<MeshRenderer>();
            if (_material == null)
            {
                _material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                _material.color = new Color(0.22f,0.58f,1f,1f);
                _material.SetInt("_Cull",0);
            }
            star._view.sharedMaterial = _material;
            star._view.shadowCastingMode = ShadowCastingMode.Off;
            star._view.receiveShadows = false;
            visual.layer = actor.gameObject.layer;
        }
        star._shown = shown;
        star._view.enabled = shown;
    }

    private static Mesh Mesh()
    {
        if (_mesh != null) return _mesh;
        var vertices = new Vector3[11];
        var triangles = new int[30];
        for (var i = 0; i < 10; i++)
        {
            var angle = (90f+i*36f)*Mathf.Deg2Rad;
            var radius = (i&1)==0 ? 0.12f : 0.052f;
            vertices[i+1] = new Vector3(Mathf.Cos(angle)*radius,Mathf.Sin(angle)*radius,0);
            triangles[i*3]=0; triangles[i*3+1]=i+1; triangles[i*3+2]=(i+1)%10+1;
        }
        _mesh = new Mesh { name="TravelPartyStar", vertices=vertices,triangles=triangles };
        _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
        return _mesh;
    }

    private void LateUpdate()
    {
        if (!_shown || !_actor.TryGetFace(out var face,out _,out _,out var scale)) return;
        _view.transform.position = face+Vector3.up*(0.38f*scale);
        _view.transform.localScale = Vector3.one*scale;
        var camera = Camera.main;
        if (camera != null) _view.transform.rotation = camera.transform.rotation;
    }
}
}
