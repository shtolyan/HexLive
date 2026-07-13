Shader "HexLive/StylizedWater"
{
    // Spec 20.16: cartoony low-poly water — gentle vertex waves, fresnel
    // two-tone (deep -> shallow), drifting glints and a soft specular sheen.
    Properties
    {
        _ShallowColor ("Shallow Color", Color) = (0.36, 0.74, 0.86, 0.72)
        _DeepColor    ("Deep Color",    Color) = (0.11, 0.38, 0.68, 0.92)
        _GlintColor   ("Glint Color",   Color) = (0.92, 0.98, 1.00, 1.0)
        _WaveAmp      ("Wave Amplitude", Float) = 0.06
        _WaveFreq     ("Wave Frequency", Float) = 0.9
        _WaveSpeed    ("Wave Speed",     Float) = 1.0
        _FresnelPower ("Fresnel Power",  Float) = 3.0
        _Glints       ("Glint Strength", Range(0,1)) = 0.35
        _GlintScale   ("Glint Scale",    Float) = 2.6
        _Smoothness   ("Smoothness",     Range(0,1)) = 0.75
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100

        Pass
        {
            Name "ForwardWater"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;
                float4 _GlintColor;
                float _WaveAmp;
                float _WaveFreq;
                float _WaveSpeed;
                float _FresnelPower;
                float _Glints;
                float _GlintScale;
                float _Smoothness;
            CBUFFER_END

            // The wave surface Y = base + A*sin(ax)*cos(az), where ax,az are
            // functions of world x,z only. Shared by vert (displacement) and
            // frag (analytic normal) so both read the exact same wave.
            float WaveHeight(float x, float z, float t)
            {
                float ax = x * _WaveFreq + t;
                float az = z * _WaveFreq * 0.8 + t * 1.3;
                return sin(ax) * cos(az);
            }

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);

                float t = _Time.y * _WaveSpeed;
                posWS.y += WaveHeight(posWS.x, posWS.z, t) * _WaveAmp;

                OUT.positionWS = posWS;
                OUT.positionHCS = TransformWorldToHClip(posWS);
                // Normal is derived per-pixel from the wave gradient in frag,
                // so it no longer matters how coarsely the mesh is tessellated.
                OUT.normalWS = float3(0, 1, 0);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 viewDir = normalize(GetWorldSpaceViewDir(IN.positionWS));

                // Per-pixel wave normal from the analytic gradient of
                // Y = A*sin(ax)*cos(az). posWS.xz is the true world position
                // (the wave only moved Y), so this is exact at any tessellation
                // — the merged water sheet and the coarse sea plane both shade
                // as one continuous, seam-free wave.
                float tN = _Time.y * _WaveSpeed;
                float ax = IN.positionWS.x * _WaveFreq + tN;
                float az = IN.positionWS.z * _WaveFreq * 0.8 + tN * 1.3;
                float dYdx = _WaveAmp * _WaveFreq * cos(ax) * cos(az);
                float dYdz = -_WaveAmp * _WaveFreq * 0.8 * sin(ax) * sin(az);
                float3 n = normalize(float3(-dYdx, 1.0, -dYdz));

                // Fresnel: steep view -> deep colour, grazing view -> shallow.
                float fres = pow(1.0 - saturate(dot(n, viewDir)), _FresnelPower);
                float3 col = lerp(_DeepColor.rgb, _ShallowColor.rgb, fres);

                // Drifting cartoon glints.
                float t = _Time.y;
                float g = sin(IN.positionWS.x * _GlintScale + t * 2.0)
                        * sin(IN.positionWS.z * _GlintScale * 0.85 - t * 1.5);
                g = smoothstep(0.82, 1.0, g) * _Glints;
                col += _GlintColor.rgb * g;

                // Soft main-light sheen.
                Light mainLight = GetMainLight();
                float3 h = normalize(mainLight.direction + viewDir);
                float spec = pow(saturate(dot(n, h)), lerp(8.0, 96.0, _Smoothness)) * _Smoothness;
                col += mainLight.color * spec;

                float alpha = saturate(lerp(_DeepColor.a, _ShallowColor.a, fres) + g);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
