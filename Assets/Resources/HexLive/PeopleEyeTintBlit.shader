Shader "Hidden/HexLive/PeopleEyeTintBlit"
{
    Properties { _MainTex ("Source atlas", 2D) = "white" {} _EnableTint ("Iris tint", Float) = 1 }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _Ramp0, _Ramp1, _Ramp2, _Ramp3;
            float _EnableTint;
            struct Attributes {float4 positionOS:POSITION;float2 uv:TEXCOORD0;};
            struct Varyings {float4 positionHCS:SV_POSITION;float2 uv:TEXCOORD0;};
            Varyings vert(Attributes i) {Varyings o;o.positionHCS=TransformObjectToHClip(i.positionOS.xyz);o.uv=i.uv;return o;}
            half4 frag(Varyings i):SV_Target
            {
                // Eye tile occupies u=.875..1, v=0...125 of the primal atlas.
                half4 source=SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.uv*.125+float2(.875,0));
                float distance=min(length(i.uv-float2(506.0/2048,1-1525.0/2048)),
                    length(i.uv-float2(1527.0/2048,1-1530.0/2048)));
                // Preserve the pupil and all sclera/wet rim/veins, not a hue wash.
                float mask=(1-smoothstep(449.0/2048,451.0/2048,distance))*smoothstep(.048,.055,distance);
                mask*=_EnableTint;
                #if defined(UNITY_COLORSPACE_GAMMA)
                float3 gamma=source.rgb;
                #else
                float3 gamma=LinearToSRGB(source.rgb);
                #endif
                float t=saturate(dot(gamma,float3(.2126,.7152,.0722))/.6);
                float3 tint=lerp(_Ramp0.rgb,_Ramp1.rgb,saturate(t/.30));
                tint=lerp(tint,_Ramp2.rgb,saturate((t-.30)/.32));
                tint=lerp(tint,_Ramp3.rgb,saturate((t-.62)/.38));
                float3 result=lerp(gamma,tint,mask);
                #if !defined(UNITY_COLORSPACE_GAMMA)
                result=SRGBToLinear(result);
                #endif
                return half4(result,source.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
