Shader "Hidden/HexLive/NormalDecodeBlit"
{
    // Copies a (possibly DXT5nm-compressed) normal map into a plain RGB-encoded
    // RenderTexture so droplet normal stamps can be alpha-blended on top and
    // URP Lit still unpacks it correctly (RGorAG path with A = 1).
    Properties
    {
        _MainTex ("Source normal map", 2D) = "bump" {}
    }

    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

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
                half4 packed = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half3 n = UnpackNormal(packed);
                return half4(n * 0.5 + 0.5, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
