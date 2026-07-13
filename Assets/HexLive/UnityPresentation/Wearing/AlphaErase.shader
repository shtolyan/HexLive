Shader "Hidden/HexLive/AlphaErase"
{
    // Punches holes into a texture copy's ALPHA channel (dst.a *= 1 - src.a)
    // without touching RGB. Used for wear holes on garments that were
    // authored TRANSPARENT: their own shader keeps blending, so erased
    // alpha = a real see-through hole — swapping them to the opaque tear
    // shader turned sheer fabric solid black.
    Properties
    {
        _MainTex ("Hole stamp", 2D) = "white" {}
        _Strength ("Erase strength", Range(0, 1)) = 1
    }

    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            Blend Zero OneMinusSrcAlpha
            ColorMask A

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float _Strength;

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
                half a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a * _Strength;
                return half4(0, 0, 0, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
