Shader "Hidden/HexLive/DropletStamp"
{
    // Spec 40.8 v4: composites a water droplet's ALBEDO effect and its GLOSS
    // mask into the runtime-painted skin maps (SkinTexturePainter). What sells
    // the drop as water is baked right here at stamp time — the skin shader
    // stays plain URP Lit (the shader-swap experiment was reverted):
    //   Pass 0 "DampHalo"  — multiplies the destination by a slight darkening
    //                        ring around the drop (wet skin darkens).
    //   Pass 1 "DropBody"  — replaces the drop interior with the ORIGINAL
    //                        albedo sampled OFFSET by the droplet normal XY
    //                        (fake refraction — the lens look), darkened by
    //                        the wet factor, plus a bright meniscus rim.
    //   Pass 2 "Gloss"     — writes the per-pixel smoothness (map alpha is
    //                        ABSOLUTE: URP Lit multiplies it by the scalar,
    //                        which NpcActorView pins to 1 on mapped slots).
    //                        BlendOp Max so overlapping drops never erase
    //                        each other's shine.
    // The effect stamp encodes: R,G = droplet normal XY (0.5-centered),
    // B = rim mask, A = coverage (0.5..1 drop interior, 0..0.5 damp halo).
    Properties
    {
        _MainTex ("Effect stamp", 2D) = "black" {}
    }

    SubShader
    {
        // ---- shared state: painting into an off-screen RT ----
        ZTest Always Cull Off ZWrite Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
        TEXTURE2D(_UnderTex); SAMPLER(sampler_UnderTex);

        // Stamp footprint in SLOT UV space (x0, y0, width, height) and the
        // atlas cell it samples (u0, v0, width, height in _MainTex UVs).
        float4 _SlotRect;
        float4 _CellRect;
        float _Fade;           // stamp alpha (wetness fade buckets)
        float _RefractStrength; // albedo offset as a fraction of drop size
        float _Darken;          // wet albedo factor under the drop (~0.75)
        float _HaloDarken;      // damp ring factor (~0.9)
        float _RimBoost;        // additive meniscus highlight
        float _BaseGloss;       // smoothness of undropped wet skin
        float _DropGloss;       // smoothness inside the drop (~0.95)

        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
        struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

        Varyings Vert(Attributes IN)
        {
            Varyings OUT;
            OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
            OUT.uv = IN.uv;
            return OUT;
        }

        // Decodes one effect-stamp texel into the droplet terms.
        void SampleStamp(float2 uv, out float2 nxy, out float rim,
                         out float drop, out float halo, out float2 cellLocal)
        {
            half4 s = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
            nxy = s.rg * 2.0 - 1.0;
            rim = s.b;
            drop = saturate(s.a * 2.0 - 1.0);
            halo = saturate(s.a * 2.0) - drop; // ring only, 0 inside the drop
            cellLocal = (uv - _CellRect.xy) / _CellRect.zw;
        }
        ENDHLSL

        // ---- Pass 0: damp halo (multiplies whatever is already painted,
        // wounds included — no destination read needed) ----
        Pass
        {
            Name "DampHalo"
            Blend DstColor Zero
            ColorMask RGB

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings IN) : SV_Target
            {
                float2 nxy; float rim; float drop; float halo; float2 cellLocal;
                SampleStamp(IN.uv, nxy, rim, drop, halo, cellLocal);
                half factor = lerp(1.0, _HaloDarken, halo * _Fade);
                return half4(factor, factor, factor, 1.0);
            }
            ENDHLSL
        }

        // ---- Pass 1: drop body — refracted original albedo + rim ----
        Pass
        {
            Name "DropBody"
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings IN) : SV_Target
            {
                float2 nxy; float rim; float drop; float halo; float2 cellLocal;
                SampleStamp(IN.uv, nxy, rim, drop, halo, cellLocal);

                // The lens: look up the skin BEHIND the drop shifted along the
                // droplet normal, proportional to the drop's own size.
                float2 slotUV = _SlotRect.xy + cellLocal * _SlotRect.zw;
                float2 offset = nxy * _RefractStrength * _SlotRect.zw;
                half3 under = SAMPLE_TEXTURE2D(_UnderTex, sampler_UnderTex, slotUV + offset).rgb;

                // A broad sky wash flattened the drop into a pale smudge —
                // the read comes from CONTRAST: a dark refracted lens body,
                // a thin blue-white CRESCENT hugging the upper rim (fresnel:
                // strong only where the dome normal tilts hard AND upward),
                // and the B channel's crisp specular dot + caustic lower rim.
                half steep = saturate(dot(nxy, nxy) * 1.6);       // 0 center -> 1 rim
                half crescent = steep * saturate(nxy.y * 2.5);    // upper rim only
                half3 skyTint = half3(0.78, 0.88, 1.0);
                half3 color = under * _Darken
                            + skyTint * (crescent * 0.30)
                            + saturate(rim * 1.5) * _RimBoost;
                return half4(color, drop * _Fade);
            }
            ENDHLSL
        }

        // ---- Pass 2: gloss (absolute smoothness in ALPHA; R = metallic 0).
        // Max keeps the brighter of two overlapping drops and leaves the
        // cleared base value everywhere else. ----
        Pass
        {
            Name "Gloss"
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings IN) : SV_Target
            {
                float2 nxy; float rim; float drop; float halo; float2 cellLocal;
                SampleStamp(IN.uv, nxy, rim, drop, halo, cellLocal);
                half gloss = lerp(_BaseGloss, _DropGloss, drop * _Fade);
                return half4(0.0, 0.0, 0.0, gloss);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
