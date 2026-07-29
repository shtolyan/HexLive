Shader "Hidden/HexLive/SkinTintBlit"
{
    // Copies the authored skin albedo into the paint-target RenderTexture while
    // MULTIPLYING it by _TintColor (the current tan/sunburn/grime tone). Laying
    // the base this way bakes the tan UNDER the wound/bandage/droplet stamps, so
    // the marks that stamp on top keep their true colour instead of being tinted
    // by the tan (a bandage on a tanned body was coming out brown). White tint =
    // an ordinary copy, identical to a plain Graphics.Blit.
    Properties
    {
        _MainTex ("Source albedo", 2D) = "white" {}
        _TintColor ("Skin tone multiply", Color) = (1,1,1,1)
    }

    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _TintColor;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                return half4(c.rgb * _TintColor.rgb, c.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
