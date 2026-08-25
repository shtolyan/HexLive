Shader "HexLive/PortraitNeonGrid"
{
    Properties
    {
        _BackgroundLow ("Background low", Color) = (0.006, 0.015, 0.028, 1)
        _BackgroundHigh ("Background high", Color) = (0.018, 0.047, 0.070, 1)
        _GridColor ("Grid", Color) = (0.04, 0.92, 0.88, 1)
        _AccentColor ("Accent", Color) = (0.78, 0.12, 1.0, 1)
        _UnscaledTime ("Unscaled time", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "PortraitNeonGrid"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BackgroundLow;
                float4 _BackgroundHigh;
                float4 _GridColor;
                float4 _AccentColor;
                float _UnscaledTime;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            float GridLine(float2 coordinates)
            {
                float2 width = max(fwidth(coordinates), 0.001);
                float2 distanceToLine = abs(frac(coordinates - 0.5) - 0.5) / width;
                return 1.0 - saturate(min(distanceToLine.x, distanceToLine.y));
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float3 color = lerp(_BackgroundLow.rgb, _BackgroundHigh.rgb,
                    smoothstep(0.0, 1.0, uv.y));

                // The face remains at the old portrait anchor (x≈0.27). A soft
                // cyan halo separates it from the set without drawing a ring.
                float2 haloDelta = (uv - float2(0.27, 0.52)) * float2(1.0, 1.35);
                float halo = exp(-dot(haloDelta, haloDelta) * 8.0);
                color += _GridColor.rgb * halo * 0.075;

                // TRON-style floor. Its lines converge toward the portrait
                // anchor instead of the panel centre, so the set reinforces the
                // deliberately off-centre composition.
                const float horizon = 0.58;
                float depth = max(horizon - uv.y, 0.018);
                float floorMask = 1.0 - smoothstep(horizon - 0.025, horizon + 0.01, uv.y);
                float2 floorCoordinates;
                floorCoordinates.x = (uv.x - 0.27) * 1.55 / depth;
                floorCoordinates.y = 0.13 / depth + _UnscaledTime * 0.42;
                float floorGrid = GridLine(floorCoordinates);
                float horizonFade = smoothstep(0.02, 0.22, depth);
                color += _GridColor.rgb * floorGrid * floorMask * horizonFade * 0.58;

                // A restrained wall grid keeps the upper half alive while the
                // slower vertical drift prevents the portrait from feeling like
                // a static wallpaper when the simulation is paused.
                float2 wallCoordinates = float2(
                    uv.x * 9.0,
                    (uv.y - horizon) * 11.0 + _UnscaledTime * 0.09);
                float wallGrid = GridLine(wallCoordinates);
                float wallMask = smoothstep(horizon - 0.015, horizon + 0.06, uv.y);
                color += _GridColor.rgb * wallGrid * wallMask * 0.09;

                // A travelling magenta scanline gives the set a second rhythm
                // without competing with status colours in the foreground UI.
                float scanY = frac(_UnscaledTime * 0.075);
                float scan = exp(-abs(uv.y - scanY) * 95.0);
                color += _AccentColor.rgb * scan * 0.16;

                // Fine CRT bands are deliberately sub-pixel soft at the target
                // 512x368 texture; they read as energy, not aliasing.
                float band = 0.5 + 0.5 * sin((uv.y * 368.0 + _UnscaledTime * 24.0) * 3.14159);
                color += _GridColor.rgb * band * 0.012;

                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
