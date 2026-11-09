#nullable enable
using HexLive.Simulation.Spatial;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>Derived planting intent, shared by the actual site and build ghost.</summary>
    public sealed class PlantingBlueprintMarker : MonoBehaviour
    {
        public const float Radius = HexSpatialMath.HexRadius * 0.12f;
        public const float Height = HexSpatialMath.HexRadius * 0.03f;
        private Mesh? _mesh;
        private Material? _material;

        public static GameObject Create()
        {
            var root = new GameObject("Planting mound");
            root.AddComponent<PlantingBlueprintMarker>();
            return root;
        }

        private void Awake()
        {
            const int sides = 8;
            var vertices = new Vector3[sides * 3];
            var triangles = new int[vertices.Length];
            for (var i=0; i<sides; i++)
            {
                var a = 2f * Mathf.PI * i / sides;
                var b = 2f * Mathf.PI * (i+1) / sides;
                var index = i*3;
                vertices[index] = new Vector3(0, Height, 0);
                vertices[index+1] = new Vector3(Mathf.Cos(b)*Radius, 0.008f, Mathf.Sin(b)*Radius);
                vertices[index+2] = new Vector3(Mathf.Cos(a)*Radius, 0.008f, Mathf.Sin(a)*Radius);
                triangles[index]=index; triangles[index+1]=index+1; triangles[index+2]=index+2;
            }
            _mesh = new Mesh { name="Planting mound (8 facets)", vertices=vertices, triangles=triangles };
            _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var shader=Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            _material = new Material(shader) { name="Planting soil", color=new Color(0.33f,0.22f,0.12f) };
            if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", _material.color);
            if (_material.HasProperty("_Smoothness")) _material.SetFloat("_Smoothness",0f);
            gameObject.AddComponent<MeshRenderer>().sharedMaterial=_material;
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }
    }
}
