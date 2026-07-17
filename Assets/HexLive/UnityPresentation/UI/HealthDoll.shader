Shader "HexLive/HealthDoll"
{
    // The limb-health "body doll" (spec §57): the character's skin mesh is
    // re-rendered on the hidden Portrait layer with every vertex tinted by the
    // HP of the body zone that owns it (StarCraft wireframe style — green →
    // yellow → red, dark stump when severed). All colour arrives as per-vertex
    // Color32 written by HealthDollStage; the shader only shades it with a
    // fixed studio light + a rim so the figure reads as a 3D hologram, never
    // touching scene lights (the stage sits far outside the world).
    Properties
    {
        _Ambient ("Ambient", Range(0, 1)) = 0.55
        _RimStrength ("Rim strength", Range(0, 1)) = 0.28
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _Ambient;
            float _RimStrength;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
                float4 color : COLOR;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.viewDirWS = GetWorldSpaceViewDir(positionWS);
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half3 n = normalize(IN.normalWS);
                half3 v = normalize(IN.viewDirWS);

                // Fixed key light from the camera's upper-left — the doll
                // rotates, the light doesn't, so the shading sweeps the form.
                half3 l = normalize(half3(-0.35, 0.75, -0.55));
                half lambert = saturate(dot(n, l));
                half3 lit = IN.color.rgb * (_Ambient + (1.0 - _Ambient) * lambert);

                // Rim lighten: hologram edge so limbs separate from the backdrop.
                half rim = pow(saturate(1.0 - saturate(dot(n, v))), 3.0);
                lit += rim * _RimStrength;

                return half4(lit, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
