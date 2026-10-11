#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>§118.2: authored wraps transferred to a browser body's single UV atlas.</summary>
    public sealed class SkinBandageAtlas : ScriptableObject
    {
        public const string ResourceFolder = "HexLive/Decals/BodyAtlases";

        [System.Serializable]
        public sealed class ZoneWrap
        {
            public string Zone = string.Empty;
            public Texture2D? Albedo;
            // Plain tangent RGB plus coverage alpha, imported as linear Default,
            // not NormalMap: SkinTexturePainter blends these bytes into its RT.
            public Texture2D? Normal;
        }

        public string MeshName = string.Empty;
        public int VertexCount;
        public string SourceBlendSha256 = string.Empty;
        public ZoneWrap[] Zones = System.Array.Empty<ZoneWrap>();

        public static bool UsesAtlasLayout(Mesh? mesh) => mesh != null &&
            mesh.name.EndsWith("_LOD0", System.StringComparison.Ordinal);

        public static SkinBandageAtlas? Load(Mesh? mesh)
        {
            if (mesh == null) return null;
            var atlas = Resources.Load<SkinBandageAtlas>(ResourceFolder + "/" + mesh.name);
            // Match the same topology identity used by PaintPointMap. A stale
            // atlas must not silently fall back to the old Genesis rectangles.
            return atlas != null && atlas.MeshName == mesh.name &&
                atlas.VertexCount == mesh.vertexCount ? atlas : null;
        }

        public ZoneWrap? ForZone(string zone)
        {
            foreach (var entry in Zones)
                if (entry.Zone == zone && entry.Albedo != null && entry.Normal != null)
                    return entry;
            return null;
        }
    }
}
