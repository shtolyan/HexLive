// Spec 40.10: procedural garment tearing. A Voronoi+noise tear mask is
// clipped against _TearAmount (0 pristine .. 1 rags): holes nucleate at cell
// centers and grow with ragged edges; the rim bleaches into pale threadbare
// fuzz (the fabric's own hue, noise-ragged) and worn patches fade between
// the holes as tear rises.
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
        // Carried over from URP Lit on the swap: garments authored with a
        // neutralized bump (scale 0 / tiny) must STAY smooth — sampling at
        // a hard-coded strength 1 made relief pop in the moment damage
        // swapped the shader ("нормали летают").
        _BumpScale ("Normal scale", Float) = 1.0
        // URP Lit convention: metallic in R, smoothness in A; white default
        // keeps constant-driven materials identical.
        _MetallicGlossMap ("Metallic (R) Gloss (A)", 2D) = "white" {}
        _Smoothness ("Smoothness", Range(0, 1)) = 0.35
        _Metallic ("Metallic", Range(0, 1)) = 0.0
        _TearAmount ("Tear (0 none .. 1 rags)", Range(0, 1)) = 0.0
        _TearScale ("Tear cell density (per UV)", Float) = 9.0
        // Artistic dissolve mask (fal.ai): ragged holes/slashes at varied gray
        // depths — darker spots rip first. White default = procedural fallback.
        _TearMaskTex ("Artistic tear mask", 2D) = "white" {}
        _TearMaskTiling ("Tear mask tiling (per UV)", Float) = 1.0
        // 0 = damage spheres do NOT clip holes (painted-wear mode: holes are
        // stamped into the mask in UV space instead — world-space sphere
        // thresholds breathe with the animated bones and flicker). Blood
        // soak keeps using the spheres either way.
        _SphereTearOn ("Spheres rip holes (0/1)", Float) = 1.0
        _TearTexOn ("Use artistic mask (0/1)", Float) = 0.0
        _TearEdgeWidth ("Frayed edge width", Range(0.001, 0.3)) = 0.09
        _TearEdgeTint ("Frayed edge tint", Color) = (0.45, 0.4, 0.38, 1)
        _DirtAmount ("Dirt (0 clean .. 1 filthy)", Range(0, 1)) = 0.0
        _DirtColor ("Dirt tint", Color) = (0.50, 0.42, 0.31, 1)
        // Spec 40.8-C: blood soaks through the cloth over the wound (localized
        // by the damage spheres); sweat = damp darkened patches with a sheen.
        _BloodAmount ("Blood soak (0..1)", Range(0, 1)) = 0.0
        _SweatAmount ("Sweat damp (0..1)", Range(0, 1)) = 0.0
        // 1 = cloth (tear holes clip); 0 = SKIN mode — same paint layers
        // (dirt/blood/sweat/bandage) but the surface never tears open.
        _HolesOn ("Holes enabled (cloth=1, skin=0)", Float) = 1.0
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
            half _BumpScale;
            half _TearAmount;
            float _TearScale;
            float _TearMaskTiling;
            float _SphereTearOn;
            half _TearTexOn;
            half _TearEdgeWidth;
            half4 _TearEdgeTint;
            half _DirtAmount;
            half4 _DirtColor;
            float _DirtScale;
            float _DamageRadius;
            half _BloodAmount;
            half _SweatAmount;
            half _HolesOn;
        CBUFFER_END

        // Spec 40.10-C: world-space damage spheres (xyz = bone anchor,
        // w = strength 0..1) — a hurt zone rips the garment covering it.
        // Set per instance via property block; spatial locality maps the
        // wound to the right garment for free.
        float4 _DamageSpheres[8];
        float _DamageSphereCount;

        // Spec 44: bandaged-zone spheres — a leaf wrap is PAINTED on the skin
        // around these anchors (skin mode only; cloth never gets them).
        float4 _BandageSpheres[8];
        float _BandageSphereCount;

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
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
            // _HolesOn = 0 (skin mode): threshold 0 — nothing ever clips,
            // the paint layers still work. _SphereTearOn = 0 (painted-wear
            // mode): only the UV-stable mask/_TearAmount rip — no breathing
            // holes from bone-anchored world spheres.
            return saturate(max(_TearAmount,
                LocalDamageTear(positionWS) * _SphereTearOn) * 1.08) * _HolesOn;
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

                // Spec 40.10-C: dirt layer — noise-mottled blotches (own scale)
                // creep from sparse smudges to near-full grime as dirt rises.
                // Dust BLENDS toward an earthy tint instead of multiplying —
                // a straight multiply turned dark fabrics (green jacket) into
                // huge near-black blobs that read as broken rendering, not
                // grime; capped cover keeps the cloth readable underneath.
                float dirtNoise = TearValueNoise(input.uv * _DirtScale);
                half dirtCover = smoothstep(1.0 - _DirtAmount * 1.1,
                    1.3 - _DirtAmount * 1.1, dirtNoise);
                // Sandy dust: blend toward the earth tint but never darken
                // below ~80% of the cloth's own brightness — dust LIGHTENS
                // dark fabric and dulls bright fabric, like real dry dirt.
                // Kept gentle (0.45/0.35): the earlier 0.7/0.5 pull painted
                // harsh beige speckle over dark garments.
                half3 dust = lerp(albedo.rgb, _DirtColor.rgb, 0.45);
                dust = max(dust, albedo.rgb * 0.8);
                albedo.rgb = lerp(albedo.rgb, dust,
                    dirtCover * saturate(_DirtAmount * 1.3) * 0.35);

                // Spec 40.8-C: blood soaks THROUGH the garment right over the
                // wound — localized by the same damage spheres that rip the
                // cloth, wicking outward with noise like real fabric. Deep
                // venous red, darker at the core, scaled by the cloth's own
                // brightness so light shirts stain vividly, dark cloth deeply.
                float woundLocal = LocalDamageTear(input.positionWS);
                float soakNoise = TearValueNoise(input.uv * _DirtScale * 0.6 + 57.0);
                half soak = smoothstep(0.12, 0.7, woundLocal * (0.55 + 0.6 * soakNoise))
                    * saturate(_BloodAmount * 1.6);
                half clothLum = dot(albedo.rgb, half3(0.299, 0.587, 0.114));
                half3 bloodCol = lerp(half3(0.42, 0.05, 0.04), half3(0.22, 0.013, 0.011),
                    saturate(woundLocal * 1.2));
                albedo.rgb = lerp(albedo.rgb, bloodCol * (0.45 + 0.55 * clothLum), soak);

                // Sweat: damp patches — the cloth darkens a touch and turns
                // glossy (the sheen below sells the moisture).
                float dampNoise = TearValueNoise(input.uv * _DirtScale * 0.45 + 113.0);
                half damp = smoothstep(0.45, 0.8, dampNoise) * saturate(_SweatAmount * 1.2);
                albedo.rgb *= 1.0 - damp * 0.20;
                half wetGloss = max(soak * 0.55, damp * 0.8);

                // Spec 44: the leaf bandage is PAINTED over the dressed zone —
                // matte leafy pad bound with fiber twine, covering whatever
                // blood/sweat sits beneath (skin mode only; cloth passes 0).
                float bnd = 0.0;
                int bcount = (int)_BandageSphereCount;
                for (int bi = 0; bi < 8; bi++)
                {
                    if (bi >= bcount)
                    {
                        break;
                    }

                    float4 bs = _BandageSpheres[bi];
                    float fall = saturate(1.0 - distance(input.positionWS, bs.xyz) / max(_DamageRadius * 0.9, 1e-3));
                    bnd = max(bnd, bs.w * fall);
                }

                half wrap = smoothstep(0.30, 0.55, bnd);
                if (wrap > 0.001)
                {
                    float leafN = TearValueNoise(input.uv * _DirtScale * 1.3 + 201.0);
                    half3 leaf = lerp(half3(0.24, 0.42, 0.20), half3(0.36, 0.56, 0.27), leafN);
                    float stripe = abs(frac((input.uv.x + input.uv.y) * _DirtScale * 1.6) - 0.5) * 2.0;
                    half twine = smoothstep(0.78, 0.92, stripe);
                    leaf = lerp(leaf, half3(0.70, 0.58, 0.40), twine * 0.85);
                    albedo.rgb = lerp(albedo.rgb, leaf, wrap);
                    wetGloss *= 1.0 - wrap; // the wrap is matte
                }

                // _BumpScale honors the authored strength (0 = neutralized —
                // URP Lit garments without the _NORMALMAP keyword rendered
                // smooth; popping to strength 1 on the damage swap read as
                // "normals flying").
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
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

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo.rgb;
                surfaceData.alpha = 1;
                surfaceData.metallic = _Metallic * metallicGloss.r;
                // Wet patches (blood/sweat) gloss the fabric locally.
                surfaceData.smoothness = saturate(_Smoothness * metallicGloss.a + wetGloss * 0.45);
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
