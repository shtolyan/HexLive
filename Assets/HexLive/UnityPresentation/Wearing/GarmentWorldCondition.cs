using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    // Applies the same item condition to a bind-pose garment lying in the
    // world. Updates are bucketed inside GarmentWearPainter; this component
    // only forwards snapshot state and wet-cloth material properties.
    public sealed class GarmentWorldCondition : MonoBehaviour
    {
        private const float TearBiteDurability = 0.72f;
        private const float TearProgressGamma = 1.35f;

        private sealed class Piece
        {
            public MeshRenderer Renderer;
            public GarmentWearPainter Painter;
            public float[] DrySmoothness;
            public Color[] DryColors;
        }

        private readonly List<Piece> _pieces = new();
        private static readonly int TearAmountId = Shader.PropertyToID("_TearAmount");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private MaterialPropertyBlock _block;

        public void Construct(GameObject visual)
        {
            var shader = Shader.Find("HexLive/GarmentTear");
            var tearMask = Resources.Load<Texture2D>("HexLive/Decals/tear_mask");
            foreach (var renderer in visual.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    continue;
                }

                var materials = renderer.materials;
                var piece = new Piece
                {
                    Renderer = renderer,
                    DrySmoothness = new float[materials.Length],
                    DryColors = new Color[materials.Length]
                };

                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    var cutoff = material.HasProperty("_Cutoff")
                        ? material.GetFloat("_Cutoff")
                        : material.HasProperty("_AlphaCutoff")
                            ? material.GetFloat("_AlphaCutoff")
                            : 0.5f;
                    var alphaClip =
                        material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f ||
                        material.HasProperty("_AlphaCutoffEnable") &&
                        material.GetFloat("_AlphaCutoffEnable") > 0.5f;
                    piece.DrySmoothness[i] = material.HasProperty(SmoothnessId)
                        ? material.GetFloat(SmoothnessId) : 0.3f;
                    piece.DryColors[i] = material.HasProperty(BaseColorId)
                        ? material.GetColor(BaseColorId) : Color.white;
                    if (shader == null || Wear.IsTransparentMaterial(material))
                    {
                        continue;
                    }

                    material.shader = shader;
                    material.SetFloat("_Cutoff", cutoff);
                    material.SetFloat("_AlphaClipOn", alphaClip ? 1f : 0f);
                    material.SetTexture("_TearMaskTex", tearMask);
                    material.SetFloat("_TearTexOn", tearMask != null ? 1f : 0f);
                    material.SetFloat("_MaskMapOn",
                        material.GetTexture("_MaskMap") != null ? 1f : 0f);
                    material.SetFloat("_OcclusionMapOn",
                        material.GetTexture("_OcclusionMap") != null ? 1f : 0f);
                    material.SetFloat("_DetailAlbedoMapOn",
                        material.GetTexture("_DetailAlbedoMap") != null ? 1f : 0f);
                    material.SetFloat("_DetailNormalMapOn",
                        material.GetTexture("_DetailNormalMap") != null ? 1f : 0f);
                }

                piece.Painter = renderer.gameObject.AddComponent<GarmentWearPainter>();
                piece.Painter.Construct(renderer, filter.sharedMesh);
                _pieces.Add(piece);
            }
        }

        public void Sync(float durability, float dirtiness, float bloodiness, float wetness)
        {
            var rawTear = Mathf.InverseLerp(TearBiteDurability, 0f, Mathf.Clamp01(durability));
            var tear = Mathf.Pow(rawTear, TearProgressGamma);
            var wet = Mathf.Clamp01(wetness);
            _block ??= new MaterialPropertyBlock();
            foreach (var piece in _pieces)
            {
                piece.Painter.SetDroppedState(tear, dirtiness, bloodiness);
                for (var i = 0; i < piece.DrySmoothness.Length; i++)
                {
                    piece.Renderer.GetPropertyBlock(_block, i);
                    _block.SetFloat(TearAmountId, tear);
                    _block.SetFloat(SmoothnessId, Mathf.Lerp(piece.DrySmoothness[i], 0.72f, wet));
                    var color = Color.Lerp(piece.DryColors[i], piece.DryColors[i] * 0.6f, wet);
                    color.a = piece.DryColors[i].a;
                    _block.SetColor(BaseColorId, color);
                    piece.Renderer.SetPropertyBlock(_block, i);
                }
            }
        }
    }
}
