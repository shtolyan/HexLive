Shader "Hidden/MSI/SourceNormalDecode"
{
    Properties { _MainTex ("Normal", 2D) = "bump" {} _Scale ("Legacy scale", Float) = 1 }
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
            float _Scale;
            struct A { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct V { float4 position:SV_POSITION; float2 uv:TEXCOORD0; };
            V vert(A input) { V o; o.position=TransformObjectToHClip(input.vertex.xyz); o.uv=input.uv; return o; }
            float4 frag(V input):SV_Target
            {
                float3 n=normalize(UnpackNormalScale(SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,input.uv),_Scale));
                return float4(n*.5+.5,1);
            }
            ENDHLSL
        }
    }
}
