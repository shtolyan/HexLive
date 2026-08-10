// Spec §112: the existing GarmentTear idea, specialized for upright palm
// crowns. World-space character spheres clip only the LeafGreen material slot;
// the same clip runs in Forward, ShadowCaster and DepthOnly passes.
Shader "HexLive/StandingPalmCrownCutout"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        _AlphaClipOn ("Alpha clip assigned", Float) = 0.0
        _MetallicGlossMap ("Metallic (R) Gloss (A)", 2D) = "white" {}
        _Metallic ("Metallic", Range(0, 1)) = 0.0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.35
        _OcclusionMap ("Occlusion", 2D) = "white" {}
        _OcclusionStrength ("Occlusion Strength", Range(0, 1)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Cutoff;
            half _AlphaClipOn;
            half _Metallic;
            half _Smoothness;
            half _OcclusionStrength;
        CBUFFER_END

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);

        // Global, camera-independent world-space spheres. xyz = rendered body
        // centre, w = radius. The fixed capacity keeps the shader layout stable;
        // the loop exits at the live count (normally four colonists).
        float4 _CharacterPalmCutoutSpheres[64];
        float _CharacterPalmCutoutSphereCount;

        float CharacterSphereOutside(float3 positionWS)
        {
            float outside = 1.0;
            int count = (int)_CharacterPalmCutoutSphereCount;
            [loop]
            for (int i = 0; i < 64; i++)
            {
                if (i >= count)
                {
                    break;
                }

                float4 sphere = _CharacterPalmCutoutSpheres[i];
                float3 delta = positionWS - sphere.xyz;
                outside = min(outside, dot(delta, delta) - sphere.w * sphere.w);
            }

            return outside;
        }

        void ClipCharacterSpheres(float3 positionWS)
        {
            clip(CharacterSphereOutside(positionWS));
        }

        void ClipAuthoredAlpha(float2 uv)
        {
            half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a * _BaseColor.a;
            clip(lerp(1.0, alpha - _Cutoff, _AlphaClipOn));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
                half3 normalWS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                half3 vertexLighting : TEXCOORD4;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = normals.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(positions.positionCS.z);
                output.vertexLighting = VertexLighting(positions.positionWS, normals.normalWS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                ClipCharacterSpheres(input.positionWS);
                ClipAuthoredAlpha(input.uv);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                half4 metallicGloss = SAMPLE_TEXTURE2D(
                    _MetallicGlossMap, sampler_MetallicGlossMap, input.uv);
                half occlusion = lerp(1.0,
                    SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, input.uv).g,
                    _OcclusionStrength);
                half3 normalWS = normalize(input.normalWS);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = normalize(GetWorldSpaceViewDir(input.positionWS));
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.vertexLighting = input.vertexLighting;
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo.rgb;
                surfaceData.alpha = 1.0;
                surfaceData.metallic = _Metallic * metallicGloss.r;
                surfaceData.smoothness = saturate(_Smoothness * metallicGloss.a);
                surfaceData.occlusion = occlusion;
                surfaceData.normalTS = half3(0, 0, 1);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                return color;
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
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

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
                float3 lightDirectionWS = _LightDirection;
#if _CASTING_PUNCTUAL_LIGHT_SHADOW
                lightDirectionWS = normalize(_LightPosition - positionWS);
#endif
                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
#if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif
                output.positionCS = positionCS;
                output.positionWS = positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                ClipCharacterSpheres(input.positionWS);
                ClipAuthoredAlpha(input.uv);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                ClipCharacterSpheres(input.positionWS);
                ClipAuthoredAlpha(input.uv);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
