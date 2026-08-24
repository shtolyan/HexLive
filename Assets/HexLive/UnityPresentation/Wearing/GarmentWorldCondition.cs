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

            // The instances `renderer.materials` handed us, kept so OnDestroy
            // can free them without asking the renderer again while it is
            // itself being torn down.
            public Material[] Materials;
        }

        private readonly List<Piece> _pieces = new();
        private Views.WorldObjectView _view;
        private static readonly int TearAmountId = Shader.PropertyToID("_TearAmount");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private MaterialPropertyBlock _block;

        public void Construct(GameObject visual)
        {
            var shader = Shader.Find("HexLive/GarmentTear");
            var tearMask = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/tear_mask");
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
                    Materials = materials,
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
            // §121.4: пока вид под курсором, per-index блоки принадлежат
            // подсветке — Sync каждый тик перезаписывал их и «hover» на
            // одежде не проявлялся никогда. Состояние тряпки за время
            // наведения не убежит; снятие подсветки восстановит блоки, и
            // следующий Sync перепишет их заново.
            _view ??= GetComponentInParent<Views.WorldObjectView>();
            if (_view != null && _view.Highlighted)
            {
                return;
            }

            if (Mathf.Clamp01(dirtiness + bloodiness) < 0.10f)
            {
                dirtiness = 0f;
                bloodiness = 0f;
            }

            var rawTear = Mathf.InverseLerp(TearBiteDurability, 0f, Mathf.Clamp01(durability));
            var tear = Mathf.Pow(rawTear, TearProgressGamma);
            var wet = Mathf.Clamp01(wetness);
            _block ??= new MaterialPropertyBlock();
            foreach (var piece in _pieces)
            {
                var dust = Mathf.Max(0f, dirtiness - bloodiness);
                piece.Painter.SetDroppedState(tear, dust, bloodiness);
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

        // `renderer.materials` above handed us INSTANCES, and Unity does not
        // free those with the renderer. A ground garment is created and
        // destroyed every time one is dropped or picked up — during a fight
        // that is constant — so without this the orphaned materials (and the
        // textures they hold) pile up for the whole session.
        private void OnDestroy()
        {
            foreach (var piece in _pieces)
            {
                if (piece.Materials == null)
                {
                    continue;
                }

                foreach (var material in piece.Materials)
                {
                    if (material != null)
                    {
                        Destroy(material);
                    }
                }
            }

            _pieces.Clear();
        }
    }
}
