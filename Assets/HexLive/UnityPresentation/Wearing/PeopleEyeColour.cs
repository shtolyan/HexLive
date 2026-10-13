using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
// §85 / #460: the primal eye submesh uses the bottom-right 1/8 atlas tile.
// Recolour that tile only, on the GPU; never mutate skin/shared source assets.
public sealed class PeopleEyeColour : IDisposable
{
    private static readonly Dictionary<string,string[]> Ramps=new()
    {
        ["blue"]=new[]{"05080e","193b5c","4478a0","c2d8e8"},
        ["blue_green"]=new[]{"040a0b","12403d","3a827a","bcd8cf"},
        ["green"]=new[]{"050803","20381a","5c7f3f","c5d5a6"},
        ["grey"]=new[]{"06080a","1e262b","6a7681","d8dee2"},
        ["hazel"]=new[]{"0a0703","3a2a10","8a6a2c","e3d09b"},
        ["amber"]=new[]{"0d0602","4a2c07","a86e18","f0d190"},
        ["brown"]=new[]{"080402","2d1a0a","6d4522","c8a273"},
        ["dark_brown"]=new[]{"050301","1c1006","452a12","9b7448"}
    };
    private sealed class Binding
    {
        public SkinnedMeshRenderer Renderer; public int Slot; public Material Source, Painted;
        public RenderTexture Texture;
    }
    private readonly List<Binding> _bindings=new();
    private Material _blit;
    private string _current;
    public static Color Swatch(string id)
    {
        ColorUtility.TryParseHtmlString("#"+(Ramps.TryGetValue(id ?? "",out var ramp)?ramp[2]:"89968a"),out var color);
        return color;
    }
    public PeopleEyeColour(SkinnedMeshRenderer[] renderers)
    {
        foreach(var renderer in renderers)
        {
            var materials=renderer.sharedMaterials;
            for(var slot=0;slot<materials.Length;slot++)
                if(materials[slot]!=null && materials[slot].name.Replace(" (Instance)","").EndsWith("_Eyes",StringComparison.Ordinal))
                    _bindings.Add(new Binding {Renderer=renderer,Slot=slot,Source=materials[slot]});
        }
    }
    public bool Apply(string id)
    {
        id ??= "";
        if(id.Length>0 && !Ramps.ContainsKey(id)) return false;
        if(_bindings.Count==0) return id.Length==0;
        if(_current==id) return true;
        if(id.Length>0 && _blit==null)
        {
            var shader=Resources.Load<Shader>("HexLive/PeopleEyeTintBlit");
            if(shader==null || !shader.isSupported) return false;
            _blit=new Material(shader);
        }
        foreach(var binding in _bindings)
        {
            if(binding.Renderer==null) continue;
            if(id.Length>0)
            {
                var source=binding.Source.GetTexture("_BaseMap");
                if(source==null) return false;
                if(binding.Painted==null)
                {
                    binding.Texture=new RenderTexture(256,256,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB)
                        {name="Primal iris",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
                    binding.Texture.Create();
                    binding.Painted=new Material(binding.Source) {name=binding.Source.name};
                    foreach(var property in new[]{"_BaseMap","_MainTex"})
                        if(binding.Painted.HasProperty(property))
                        {
                            binding.Painted.SetTexture(property,binding.Texture);
                            binding.Painted.SetTextureScale(property,new Vector2(8,8));
                            binding.Painted.SetTextureOffset(property,new Vector2(-7,0));
                        }
                }
                for(var i=0;i<4;i++)
                {
                    ColorUtility.TryParseHtmlString("#"+Ramps[id][i],out var color);
                    _blit.SetVector("_Ramp"+i,new Vector4(color.r,color.g,color.b,1));
                }
                var previous=GL.sRGBWrite;
                var active=RenderTexture.active;
                try {GL.sRGBWrite=QualitySettings.activeColorSpace==ColorSpace.Linear;Graphics.Blit(source,binding.Texture,_blit);}
                finally {GL.sRGBWrite=previous;RenderTexture.active=active;}
            }
            var materials=binding.Renderer.sharedMaterials;
            materials[binding.Slot]=id.Length==0?binding.Source:binding.Painted;
            binding.Renderer.sharedMaterials=materials;
        }
        _current=id;return true;
    }
    public void Dispose()
    {
        foreach(var b in _bindings)
        {
            if(b.Painted!=null) UnityEngine.Object.Destroy(b.Painted);
            if(b.Texture!=null) {b.Texture.Release();UnityEngine.Object.Destroy(b.Texture);}
        }
        if(_blit!=null) UnityEngine.Object.Destroy(_blit);
        _bindings.Clear();
    }
}
}
