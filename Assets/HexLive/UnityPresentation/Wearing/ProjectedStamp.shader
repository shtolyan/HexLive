Shader "Hidden/HexLive/ProjectedStamp"
{
    // Spec 40.8-J: paints ONE decal (wound art, blood underlay, leaf/gauze
    // wrap) into a skin paint target WITHOUT a UV rectangle.
    //
    // The legacy stamp is a Graphics.DrawTexture rect in one slot's [0,1] UV
    // space, so it dies at the edge of its UV island and cannot continue onto
    // the neighbouring body part — that part lives in another texture on
    // another material. Here the quad covers the WHOLE target and each texel
    // asks a different question: the baked position map (SkinPositionMapSet)
    // says which 3D point of the body this texel covers, and the texel is
    // painted iff that point falls inside the decal's box. Islands, UDIM
    // tiles and separate textures all stop mattering — a hip wrap simply
    // lands on every texel near the hip, half of them on the Legs texture and
    // half on the Torso one.
    //
    // Rejection is threefold: no coverage (texel maps to no geometry), outside
    // the decal's XY footprint, or outside the depth band / facing away — the
    // last two keep a wrap on a thin forearm from also printing mirrored on
    // the far side.
    Properties
    {
        _MainTex ("Decal art", 2D) = "black" {}
    }

    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);  SAMPLER(sampler_MainTex);
        TEXTURE2D(_PosMap);   SAMPLER(sampler_PosMap);
        TEXTURE2D(_NrmMap);   SAMPLER(sampler_NrmMap);

        // Quad UV -> paint-target UV (DropletStamp's convention). The caller
        // draws only the window the baked cell grid says the decal can reach,
        // so this is rarely the whole target.
        float4 _SlotRect;
        // Bind/mesh space -> decal space: xy in [-0.5,0.5] across the art,
        // z in mesh units along the projection axis.
        float4x4 _ObjectToDecal;
        float3 _DecalNormal;   // outward surface normal at the decal anchor
        float _Fade;           // stamp alpha (heal fade / underlay halving)
        float _Depth;          // half-thickness of the accepted slab
        float _DepthFeather;   // soft edge of that slab
        float _GlossMax;       // gloss pass: absolute smoothness of a wet core

        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
        struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

        Varyings Vert(Attributes IN)
        {
            Varyings OUT;
            OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
            OUT.uv = IN.uv;
            return OUT;
        }

        // Resolves this texel against the decal: where it lands in the art and
        // how strongly it is covered (0 = reject). Deliberately CLIP-FREE —
        // the caller must take the art's derivatives before discarding
        // anything, or the mip gradients of the surviving lanes are undefined.
        float2 ResolveDecal(float2 quadUv, out float weight)
        {
            float2 rtUV = _SlotRect.xy + quadUv * _SlotRect.zw;

            float4 pm = SAMPLE_TEXTURE2D(_PosMap, sampler_PosMap, rtUV);
            float3 dp = mul(_ObjectToDecal, float4(pm.xyz, 1.0)).xyz;
            float2 duv = dp.xy + 0.5;

            // Facing: the far side of a limb maps into the same box, and on a
            // thin forearm it also falls inside the depth slab.
            float3 n = SAMPLE_TEXTURE2D(_NrmMap, sampler_NrmMap, rtUV).xyz * 2.0 - 1.0;
            float facing = smoothstep(0.02, 0.35, dot(normalize(n), _DecalNormal));
            float depth = 1.0 - smoothstep(_Depth - _DepthFeather, _Depth, abs(dp.z));

            float covered = step(0.5, pm.a);        // texel maps to geometry
            float inside = step(0.0, duv.x) * step(duv.x, 1.0) *
                           step(0.0, duv.y) * step(duv.y, 1.0);

            weight = covered * inside * facing * depth;
            return duv;
        }

        // Derivative-limited sample. Across an island boundary the decal UV
        // jumps, and raw ddx/ddy would drop the sampler to the lowest mip —
        // a grey halo along exactly the seams this shader exists to cross.
        half4 SampleArt(float2 duv)
        {
            const float lim = 0.02;
            float2 dx = clamp(ddx(duv), -lim, lim);
            float2 dy = clamp(ddy(duv), -lim, lim);
            return SAMPLE_TEXTURE2D_GRAD(_MainTex, sampler_MainTex, duv, dx, dy);
        }
        ENDHLSL

        // ---- Pass 0: albedo. Straight alpha (a custom material bypasses the
        // GUI blit's colour doubling that StampTint compensates for). ----
        Pass
        {
            Name "Albedo"
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings IN) : SV_Target
            {
                float weight;
                float2 duv = ResolveDecal(IN.uv, weight);
                half4 art = SampleArt(duv);   // derivatives BEFORE any clip
                clip(weight - 0.002);
                return half4(art.rgb, art.a * _Fade * weight);
            }
            ENDHLSL
        }

        // ---- Pass 1: wet-core gloss. Same semantics as WoundGlossStamp —
        // absolute smoothness in ALPHA, BlendOp Max so overlapping wounds and
        // droplets keep the shiniest value and the wet-skin base is never
        // darkened. ----
        Pass
        {
            Name "Gloss"
            BlendOp Max
            Blend One One
            ColorMask A

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings IN) : SV_Target
            {
                float weight;
                float2 duv = ResolveDecal(IN.uv, weight);
                half art = SampleArt(duv).a;  // derivatives BEFORE any clip
                clip(weight - 0.002);
                return half4(0.0, 0.0, 0.0, art * _GlossMax * _Fade * weight);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
