Shader "Hidden/HexLive/WoundGlossStamp"
{
    // Stamps a wound's WET GLOSS into the painted _MetallicGlossMap.
    // Alpha-only (smoothness lives in the map's alpha; RGB = metallic
    // stays untouched at 0) with BlendOp Max: overlapping wounds and
    // droplets keep the shiniest value, and the wet-skin base gloss is
    // never darkened — a healing wound simply sinks below the base and
    // vanishes.
    Properties
    {
        _MainTex ("Gloss stamp", 2D) = "black" {}
        _GlossMax ("Wet smoothness", Range(0, 1)) = 0.75
        _Fade ("Fade", Range(0, 1)) = 1
    }

    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            BlendOp Max
            Blend One One
            ColorMask A

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float _GlossMax;
            float _Fade;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a * _GlossMax * _Fade;
                return half4(0, 0, 0, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
