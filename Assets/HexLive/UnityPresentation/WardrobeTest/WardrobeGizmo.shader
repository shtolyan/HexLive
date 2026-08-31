Shader "HexLive/WardrobeGizmo"
{
    // Ручки runtime-гизмо WardrobeTest (§31B.4F): unlit-цвет, рисуется ПОВЕРХ
    // всего (ZTest Always + очередь Overlay) — кость head сидит ВНУТРИ шлема,
    // и с обычной глубиной половина ручек была бы спрятана его же мешем.
    // Дев-сцена: в билд не попадает, Shader.Find здесь допустим.
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionHCS : SV_POSITION; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                return half4(_Color.rgb, _Color.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
