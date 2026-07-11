// Spec 40.10: procedural garment tearing. A Voronoi+noise tear mask is
// clipped against _TearAmount (0 pristine .. 1 rags): holes nucleate at cell
// centers and grow with ragged edges; the rim is tinted into a frayed hem.
// Property names match URP Lit so a runtime shader swap keeps the garment's
// textures (_BaseMap/_BaseColor/_BumpMap). Cull Off shows the cloth inside
// through holes; the ShadowCaster pass clips identically.
Shader "HexLive/GarmentTear"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap ("Normal", 2D) = "bump" {}
        _Smoothness ("Smoothness", Range(0, 1)) = 0.35
        _Metallic ("Metallic", Range(0, 1)) = 0.0
        _TearAmount ("Tear (0 none .. 1 rags)", Range(0, 1)) = 0.0
        _TearScale ("Tear cell density (per UV)", Float) = 9.0
        _TearEdgeWidth ("Frayed edge width", Range(0.001, 0.3)) = 0.09
        _TearEdgeTint ("Frayed edge tint", Color) = (0.45, 0.4, 0.38, 1)
        _DirtAmount ("Dirt (0 clean .. 1 filthy)", Range(0, 1)) = 0.0
        _DirtColor ("Dirt tint", Color) = (0.42, 0.36, 0.28, 1)
        _DirtScale ("Dirt blotch density (per UV)", Float) = 14.0
        _DamageRadius ("Zone damage rip radius (world)", Float) = 0.3
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Smoothness;
            half _Metallic;
            half _TearAmount;
            float _TearScale;
            half _TearEdgeWidth;
            half4 _TearEdgeTint;
            half _DirtAmount;
            half4 _DirtColor;
            float _DirtScale;
            float _DamageRadius;
        CBUFFER_END

        // Spec 40.10-C: world-space damage spheres (xyz = bone anchor,
        // w = strength 0..1) — a hurt zone rips the garment covering it.
        // Set per instance via property block; spatial locality maps the
        // wound to the right garment for free.
        float4 _DamageSpheres[8];
        float _DamageSphereCount;

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

        float2 TearHash(float2 p)
        {
            p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
            return frac(sin(p) * 43758.5453123);
        }

        float TearValueNoise(float2 p)
        {
            float2 i = floor(p);
            float2 f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            float a = TearHash(i).x;
            float b = TearHash(i + float2(1, 0)).x;
            float c = TearHash(i + float2(0, 1)).x;
            float d = TearHash(i + float2(1, 1)).x;
            return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
        }

        float TearVoronoi(float2 p)
        {
            float2 n = floor(p);
            float2 f = frac(p);
            float md = 8.0;
            [unroll]
            for (int y = -1; y <= 1; y++)
            {
                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    float2 g = float2(x, y);
                    float2 o = TearHash(n + g);
                    float2 r = g + o - f;
                    md = min(md, dot(r, r));
                }
            }

            return sqrt(md);
        }

        // 0 at hole seeds .. ~1 in solid cloth; value noise roughens the rims.
        float TearMask(float2 uv)
        {
            float2 p = uv * _TearScale;
            float cells = TearVoronoi(p);
            float ragged = TearValueNoise(p * 2.7);
            return saturate(cells * 0.8 + ragged * 0.35);
        }

        // Spec 40.10-C: local rip from the nearest damage sphere.
        float LocalDamageTear(float3 positionWS)
        {
            float local = 0.0;
            int count = (int)_DamageSphereCount;
            for (int i = 0; i < 8; i++)
            {
                if (i >= count)
                {
                    break;
                }

                float4 s = _DamageSpheres[i];
                float falloff = saturate(1.0 - distance(positionWS, s.xyz) / max(_DamageRadius, 1e-3));
                local = max(local, s.w * falloff * falloff);
            }

            return local;
        }

        // Rags at amount 1 must clear ~the whole garment. Zone damage rips
        // locally as if that spot were fully worn (max, not additive).
        float TearThresholdAt(float3 positionWS)
        {
            return saturate(max(_TearAmount, LocalDamageTear(positionWS)) * 1.08);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half4 tangentWS : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = normals.normalWS;
                output.tangentWS = half4(normals.tangentWS, input.tangentOS.w);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 Frag(Varyings input, bool isFront : SV_IsFrontFace) : SV_Target
            {
                float mask = TearMask(input.uv);
                float threshold = TearThresholdAt(input.positionWS);
                clip(mask - threshold);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                // Frayed hem: tint the band just outside the hole edge.
                half rim = threshold > 0.001
                    ? saturate(1.0 - (mask - threshold) / max(_TearEdgeWidth, 1e-4))
                    : 0.0;
                albedo.rgb = lerp(albedo.rgb, albedo.rgb * _TearEdgeTint.rgb, rim);

                // Spec 40.10-C: dirt layer — noise-mottled blotches (own scale)
                // creep from sparse smudges to near-full grime as dirt rises.
                float dirtNoise = TearValueNoise(input.uv * _DirtScale);
                half dirtCover = smoothstep(1.0 - _DirtAmount * 1.1,
                    1.3 - _DirtAmount * 1.1, dirtNoise);
                albedo.rgb = lerp(albedo.rgb, albedo.rgb * _DirtColor.rgb,
                    dirtCover * saturate(_DirtAmount * 2.0));

                half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv));
                half sgn = input.tangentWS.w;
                half3 bitangent = sgn * cross(input.normalWS, input.tangentWS.xyz);
                half3 normalWS = normalize(TransformTangentToWorld(
                    normalTS, half3x3(input.tangentWS.xyz, bitangent, input.normalWS)));
                normalWS *= isFront ? 1 : -1; // inside of the cloth via Cull Off

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = normalize(GetWorldSpaceViewDir(input.positionWS));
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.bakedGI = SampleSH(normalWS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo.rgb;
                surfaceData.alpha = 1;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion = 1;
                surfaceData.normalTS = normalTS;

                return UniversalFragmentPBR(inputData, surfaceData);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, _LightDirection));
#if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif
                output.positionCS = positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.positionWS = positionWS;
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                clip(TearMask(input.uv) - TearThresholdAt(input.positionWS));
                return 0;
            }
            ENDHLSL
        }
    }
}
