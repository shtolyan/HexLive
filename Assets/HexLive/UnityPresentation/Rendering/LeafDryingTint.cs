using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

/// <summary>§54 / #469: only authored leaf slots age; never tint wood or lashings.</summary>
public sealed class LeafDryingTint : MonoBehaviour
{
    private readonly List<(Renderer Renderer,int Slot,Color Fresh)> _slots=new();
    private MaterialPropertyBlock _block;
    private HexLive.UnityPresentation.Views.WorldObjectView _view;
    private static readonly int BaseColor=Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId=Shader.PropertyToID("_Color");
    private float _applied=-1f;
    public int LeafSlotCount => _slots.Count;

    public void Initialize()
    {
        _block ??= new MaterialPropertyBlock();
        _slots.Clear(); _applied=-1f;
        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
        {
            var materials=renderer.sharedMaterials;
            for (var i=0;i<materials.Length;i++)
            {
                var material=materials[i];
                if (material==null || !IsLeaf(renderer.transform,material)) continue;
                var color=material.HasProperty(BaseColor) ? material.GetColor(BaseColor) : material.color;
                _slots.Add((renderer,i,color));
            }
        }
    }

    private bool IsLeaf(Transform node,Material material)
    {
        if (material.name.StartsWith("Leaf",System.StringComparison.OrdinalIgnoreCase) ||
            material.name.StartsWith("HL_Roof_PalmMat_",System.StringComparison.Ordinal) ||
            material.name.StartsWith("resource.palm_leaf",System.StringComparison.Ordinal)) return true;
        // Assembled beds/collectors use logical leaf_* pieces; siblings log_*,
        // stick_* and rope_* remain untouched even if they share a palette.
        for (var t=node; t!=null; t=t.parent)
        {
            if (t.name.StartsWith("leaf_",System.StringComparison.OrdinalIgnoreCase)) return true;
            if (t==transform) break;
        }
        return false;
    }

    public void Sync(float dryness)
    {
        // An independent roof bundle may arrive after its world view exists.
        if (_slots.Count==0) Initialize();
        if (_view==null) _view=GetComponent<HexLive.UnityPresentation.Views.WorldObjectView>();
        // Hover owns its saved/restored blocks until Clear. Do not erase its
        // highlight or snapshot a lifted tint as a new drying base colour.
        if (_view!=null && _view.Highlighted) return;
        dryness=Mathf.Clamp01(dryness);
        if (Mathf.Abs(dryness-_applied)<.002f) return;
        _applied=dryness;
        foreach (var slot in _slots)
        {
            if (slot.Renderer==null) continue;
            // Retain variation between flat-shaded fronds and the authored
            // alpha. Native leaf slots use solid albedo; no texture is replaced.
            var value=Mathf.Max(slot.Fresh.r,Mathf.Max(slot.Fresh.g,slot.Fresh.b));
            var dry=new Color(value*.94f,value*.77f,value*.31f,slot.Fresh.a);
            var color=Color.Lerp(slot.Fresh,dry,dryness);
            slot.Renderer.GetPropertyBlock(_block,slot.Slot);
            _block.SetColor(BaseColor,color); _block.SetColor(ColorId,color);
            slot.Renderer.SetPropertyBlock(_block,slot.Slot);
        }
    }
}
}
