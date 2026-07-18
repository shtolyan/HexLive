// Spec 40.10: procedural garment tearing. A Voronoi+noise tear mask is
// clipped against _TearAmount (0 pristine .. 1 rags): holes nucleate at cell
// centers and grow with ragged edges; the rim bleaches into pale threadbare
// fuzz (the fabric's own hue, noise-ragged) and worn patches fade between
// the holes as tear rises.
// Property names match URP Lit so a runtime shader swap keeps the garment's
// authored maps. Cull Off shows the cloth inside through holes; the
// ShadowCaster pass clips identically.
Shader "HexLive/GarmentTear"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        _AlphaClipOn ("Alpha clip assigned", Float) = 0.0
        [Normal] _BumpMap ("Normal", 2D) = "bump" {}
        // Carried over from URP Lit on the swap: garments authored with a
        // neutralized bump (scale 0 / tiny) must STAY smooth — sampling at
        // a hard-coded strength 1 made relief pop in the moment damage
        // swapped the shader ("нормали летают").
        _BumpScale ("Normal scale", Float) = 1.0
        // URP Lit convention: metallic in R, smoothness in A; white default
        // keeps constant-driven materials identical.
        _MetallicGlossMap ("Metallic (R) Gloss (A)", 2D) = "white" {}
        _MaskMap ("Mask Map (R metal, G AO, A smooth)", 2D) = "white" {}
        _MaskMapOn ("Mask map assigned", Float) = 0.0
        _OcclusionMap ("Occlusion", 2D) = "white" {}
        _OcclusionMapOn ("Occlusion map assigned", Float) = 0.0
        _OcclusionStrength ("Occlusion Strength", Range(0, 1)) = 1.0
        _DetailMask ("Detail Mask", 2D) = "white" {}
        _DetailAlbedoMap ("Detail Albedo", 2D) = "grey" {}
        _DetailAlbedoMapOn ("Detail albedo assigned", Float) = 0.0
        _DetailAlbedoMapScale ("Detail albedo scale", Float) = 1.0
        [Normal] _DetailNormalMap ("Detail Normal", 2D) = "bump" {}
        _DetailNormalMapOn ("Detail normal assigned", Float) = 0.0
        _DetailNormalMapScale ("Detail normal scale", Float) = 1.0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.35
        _Metallic ("Metallic", Range(0, 1)) = 0.0
        _TearAmount ("Tear (0 none .. 1 rags)", Range(0, 1)) = 0.0
        _TearScale ("Tear cell density (per UV)", Float) = 9.0
        // Artistic dissolve mask (fal.ai): ragged holes/slashes at varied gray
        // depths — darker spots rip first. White default = procedural fallback.
        _TearMaskTex ("Artistic tear mask", 2D) = "white" {}
        _TearMaskTiling ("Tear mask tiling (per UV)", Float) = 1.0
        _TearTexOn ("Use artistic mask (0/1)", Float) = 0.0
        _TearEdgeWidth ("Frayed edge width", Range(0.001, 0.3)) = 0.09
        _TearEdgeTint ("Frayed edge tint", Color) = (0.45, 0.4, 0.38, 1)
        _HolesOn ("Holes enabled (cloth=1, skin=0)", Float) = 1.0
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
            half _Cutoff;
            half _AlphaClipOn;
            half _Smoothness;
            half _Metallic;
            half _BumpScale;
            float4 _DetailAlbedoMap_ST;
            half _MaskMapOn;
            half _OcclusionMapOn;
            half _OcclusionStrength;
            half _DetailAlbedoMapOn;
            half _DetailAlbedoMapScale;
            half _DetailNormalMapOn;
            half _DetailNormalMapScale;
            half _TearAmount;
            float _TearScale;
            float _TearMaskTiling;
            half _TearTexOn;
            half _TearEdgeWidth;
            half4 _TearEdgeTint;
            half _HolesOn;
        CBUFFER_END

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_MaskMap); SAMPLER(sampler_MaskMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
        TEXTURE2D(_DetailMask); SAMPLER(sampler_DetailMask);
        TEXTURE2D(_DetailAlbedoMap); SAMPLER(sampler_DetailAlbedoMap);
        TEXTURE2D(_DetailNormalMap); SAMPLER(sampler_DetailNormalMap);
        TEXTURE2D(_TearMaskTex); SAMPLER(sampler_TearMaskTex);

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

        half3 ScaleDetailAlbedo(half3 detailAlbedo, half scale)
        {
            return half(2.0) * detailAlbedo * scale - scale + half(1.0);
        }

        half DetailMaskAt(float2 uv)
        {
            half texMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, uv).r;
            return saturate(max(_DetailAlbedoMapOn, _DetailNormalMapOn) * texMask);
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
            float proc = saturate(cells * 0.8 + ragged * 0.35);

            // Artistic mask (fal.ai): hand-drawn ragged holes/slashes at many
            // gray depths — darker rips first, so a rising TearAmount plays a
            // natural destruction sequence. Value noise keeps rims lively;
            // _TearTexOn gates it so a missing texture falls back to Voronoi.
            half art = SAMPLE_TEXTURE2D_LOD(_TearMaskTex, sampler_TearMaskTex,
                uv * _TearMaskTiling, 0).r;
            float artistic = saturate(art * 0.92 + ragged * 0.12 - 0.02);
            return lerp(proc, artistic, saturate(_TearTexOn));
        }

        float TearThresholdAt()
        {
            return saturate(_TearAmount * 1.08) * _HolesOn;
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
                float threshold = TearThresholdAt();
                clip(mask - threshold);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                clip(lerp(1.0, albedo.a - _Cutoff, _AlphaClipOn));

                // Frayed hem: real frayed cloth goes PALE at the edge — loose
                // threads catch light. Push the fabric's own colour toward a
                // bleached, desaturated fuzz (keeps the hue family: grey shirt
                // → pale grey, jeans → pale blue) instead of tinting it brown,
                // and rag the band with noise so it never reads as a sticker.
                half rim = threshold > 0.001
                    ? saturate(1.0 - (mask - threshold) / max(_TearEdgeWidth, 1e-4))
                    : 0.0;
                half rimNoise = 0.45 + 0.55 * TearValueNoise(input.uv * _TearScale * 6.1);
                half lum = dot(albedo.rgb, half3(0.299, 0.587, 0.114));
                half3 fuzz = lerp(albedo.rgb, saturate(half3(lum, lum, lum) * 1.5 + 0.18), 0.85);
                albedo.rgb = lerp(albedo.rgb, fuzz, rim * rim * rimNoise);

                // Threadbare thinning: as wear rises the cloth also fades in
                // patches BETWEEN the holes (worn elbows/seams feel) — the
                // garment reads as old fabric, not pristine cloth with holes.
                half wearNoise = TearValueNoise(input.uv * _TearScale * 1.7 + 31.0);
                half thinning = smoothstep(0.55, 0.95, wearNoise) * saturate(_TearAmount * 1.6);
                albedo.rgb = lerp(albedo.rgb, fuzz, thinning * 0.5);

                float2 detailUv = TRANSFORM_TEX(input.uv, _DetailAlbedoMap);
                half detailMask = DetailMaskAt(input.uv);
                half3 detailAlbedo = SAMPLE_TEXTURE2D(_DetailAlbedoMap,
                    sampler_DetailAlbedoMap, detailUv).rgb;
                detailAlbedo = ScaleDetailAlbedo(detailAlbedo, _DetailAlbedoMapScale);
                albedo.rgb *= lerp(half3(1, 1, 1), detailAlbedo, detailMask * _DetailAlbedoMapOn);

                // _BumpScale honors the authored strength (0 = neutralized —
                // URP Lit garments without the _NORMALMAP keyword rendered
                // smooth; popping to strength 1 on the damage swap read as
                // "normals flying").
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                half3 detailNormalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, detailUv),
                    _DetailNormalMapScale);
                detailNormalTS = normalize(detailNormalTS);
                half detailNormalMask = detailMask * _DetailNormalMapOn;
                normalTS = normalize(lerp(normalTS,
                    half3(normalTS.xy + detailNormalTS.xy, normalTS.z * detailNormalTS.z),
                    detailNormalMask));
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

                // Per-pixel metallic/smoothness from the authored map (white
                // default = the old constants): losing the gloss map on the
                // damage swap flattened worn leather/satin to uniform plastic.
                half4 metallicGloss = SAMPLE_TEXTURE2D(_MetallicGlossMap,
                    sampler_MetallicGlossMap, input.uv);
                half4 maskMap = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, input.uv);
                half occlusionMap = SAMPLE_TEXTURE2D(_OcclusionMap,
                    sampler_OcclusionMap, input.uv).g;
                half occlusion = lerp(1.0, occlusionMap, _OcclusionMapOn * _OcclusionStrength);
                occlusion = lerp(occlusion, lerp(1.0, maskMap.g, _OcclusionStrength), _MaskMapOn);
                half metallicFactor = lerp(metallicGloss.r, maskMap.r, _MaskMapOn);
                half smoothnessFactor = lerp(metallicGloss.a, maskMap.a, _MaskMapOn);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo.rgb;
                surfaceData.alpha = 1;
                surfaceData.metallic = _Metallic * metallicFactor;
                surfaceData.smoothness = saturate(_Smoothness * smoothnessFactor);
                surfaceData.occlusion = occlusion;
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
                clip(TearMask(input.uv) - TearThresholdAt());
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(lerp(1.0, alpha - _Cutoff, _AlphaClipOn));
                return 0;
            }
            ENDHLSL
        }
    }
}
