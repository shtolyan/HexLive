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
                half mask = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
                // ⭐ Стендовый замер 2026-08-30: линейная альфа заставляла
                // блестеть широкий полупрозрачный ореол брызг — кровь тех
                // пикселей на коже почти невидима (тёмный красный при 0.3-0.5),
                // а глянец от них честные 20-35% на визуально ЧИСТОЙ коже:
                // «блеск сползал с раны» блином рядом с ней. Куб оставляет
                // глянец только плотной луже крови (0.9³≈0.73), а ореол
                // глушит (0.4³≈0.06) — раны блестят, кожа нет.
                half a = mask * mask * mask * _GlossMax * _Fade;
                return half4(0, 0, 0, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
