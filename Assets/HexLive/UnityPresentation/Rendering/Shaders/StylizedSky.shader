Shader "HexLive/StylizedSky"
{
    // Spec 20.16: stylized day/night skybox — vertical gradient that shifts
    // from day to night, a sun disc + glow, a moon disc + glow, a warm sunset
    // band near the horizon, and a twinkling star field at night. Sun/moon
    // directions and the day amount are driven per-frame by SkyDayNightController.
    Properties
    {
        _DayZenith    ("Day Zenith",    Color) = (0.23, 0.52, 0.86, 1)
        _DayHorizon   ("Day Horizon",   Color) = (0.68, 0.85, 0.95, 1)
        _NightZenith  ("Night Zenith",  Color) = (0.02, 0.03, 0.09, 1)
        _NightHorizon ("Night Horizon", Color) = (0.06, 0.09, 0.18, 1)
        _SunsetColor  ("Sunset Color",  Color) = (1.0, 0.55, 0.25, 1)
        _SunColor     ("Sun Color",     Color) = (1.0, 0.95, 0.8, 1)
        _MoonColor    ("Moon Color",    Color) = (0.85, 0.9, 1.0, 1)
        _StarColor    ("Star Color",    Color) = (1.0, 1.0, 1.0, 1)
        _SunDir       ("Sun Dir",       Vector) = (0, 1, 0, 0)
        _MoonDir      ("Moon Dir",      Vector) = (0, -1, 0, 0)
        _DayAmount    ("Day Amount",    Range(0,1)) = 1
        _SunSize      ("Sun Size",      Range(0.99, 1.0)) = 0.9975
        _MoonSize     ("Moon Size",     Range(0.99, 1.0)) = 0.9985
        _SunGlow      ("Sun Glow Power", Float) = 300
    }

    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 viewDir : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _DayZenith, _DayHorizon, _NightZenith, _NightHorizon;
                float4 _SunsetColor, _SunColor, _MoonColor, _StarColor;
                float4 _SunDir, _MoonDir;
                float _DayAmount, _SunSize, _MoonSize, _SunGlow;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.viewDir = IN.positionOS.xyz; // skybox vertex == view direction
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.viewDir);
                float up = saturate(dir.y);
                float g = pow(up, 0.55);

                float3 dayCol   = lerp(_DayHorizon.rgb,   _DayZenith.rgb,   g);
                float3 nightCol = lerp(_NightHorizon.rgb, _NightZenith.rgb, g);
                float3 col = lerp(nightCol, dayCol, _DayAmount);

                // Warm sunset/sunrise band: strongest when the sun sits near the
                // horizon and we look toward it, low in the sky.
                float towardSun = saturate(dot(dir, _SunDir.xyz));
                float horizonBand = pow(1.0 - up, 3.0);
                float sunLow = saturate(1.0 - abs(_SunDir.y) * 2.5);
                col += _SunsetColor.rgb * horizonBand * pow(towardSun, 2.0) * sunLow * _DayAmount * 1.2;

                // Sun disc + glow, faded out as it drops below the horizon.
                float sunUp = saturate(_SunDir.y * 6.0);
                float sd = dot(dir, _SunDir.xyz);
                float sunDisc = smoothstep(_SunSize, _SunSize + 0.0008, sd);
                float sunGlow = pow(saturate(sd), _SunGlow) * 0.6;
                col += _SunColor.rgb * (sunDisc + sunGlow) * sunUp;

                // Moon disc + soft glow at night.
                float moonUp = saturate(_MoonDir.y * 6.0);
                float md = dot(dir, _MoonDir.xyz);
                float moonDisc = smoothstep(_MoonSize, _MoonSize + 0.0006, md);
                float moonGlow = pow(saturate(md), 400.0) * 0.35;
                col += _MoonColor.rgb * (moonDisc + moonGlow) * moonUp;

                // Blocky twinkling stars, only at night and above the horizon.
                float night = saturate(1.0 - _DayAmount);
                if (night > 0.01 && dir.y > 0.03)
                {
                    float3 cell = floor(dir * 90.0);
                    float h = frac(sin(dot(cell, float3(12.9898, 78.233, 37.719))) * 43758.5453);
                    float star = step(0.992, h);
                    star *= 0.5 + 0.5 * sin(_Time.y * 3.0 + h * 100.0);
                    col += _StarColor.rgb * star * night * saturate(dir.y * 2.0);
                }

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
