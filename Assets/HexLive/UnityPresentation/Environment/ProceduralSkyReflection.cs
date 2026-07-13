#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec 40.8 v4: the glint source for water droplets. The scenes run
    /// flat ambient with no reflection probes, so unity_SpecCube0 falls back
    /// to a flat gray — and at smoothness ~1 a GGX highlight from the single
    /// directional light collapses to a sub-pixel point: droplets looked
    /// FLAT no matter how correct their relief was. This tiny code-generated
    /// cubemap (sky gradient + a bright HDR sun blob + dark ground
    /// hemisphere) becomes the CUSTOM default reflection, so every glossy
    /// pixel — droplet domes above all — has environment specular to catch.
    /// Low-smoothness surfaces (ground 0, dry skin 0.32) sample the blurry
    /// top mips and barely change. Cheap for mobile: 64 px faces, built once.
    /// The sun blob is static (high in the east-ish sky) — the env glint
    /// reads as "sky", it doesn't need to track the day cycle; day/night is
    /// handled by RenderSettings.reflectionIntensity instead.
    /// </summary>
    public static class ProceduralSkyReflection
    {
        private const int FaceSize = 64;
        // HDR sun: this is what pings off a droplet as a hot sparkle.
        private const float SunIntensity = 5f;
        private static readonly Vector3 SunDir = new Vector3(0.4f, 0.75f, 0.5f).normalized;
        private static readonly Color ZenithColor = new(0.36f, 0.54f, 0.86f);
        private static readonly Color HorizonColor = new(0.78f, 0.86f, 0.95f);
        private static readonly Color GroundColor = new(0.16f, 0.17f, 0.15f);
        private static readonly Color SunColor = new(1f, 0.95f, 0.82f);

        private static Cubemap? _cubemap;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _cubemap = null;
        }

        /// <summary>Installs the cubemap as the scene's default reflection.
        /// Idempotent — call from every bootstrap that sets up lighting.</summary>
        public static void Apply()
        {
            if (_cubemap == null)
            {
                _cubemap = Build();
            }

            RenderSettings.defaultReflectionMode =
                UnityEngine.Rendering.DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = _cubemap;
        }

        /// <summary>Day/night dimming: reflections (droplet glints included)
        /// fade toward the moonlit level at night.</summary>
        public static void SetDayAmount(float dayAmount)
        {
            RenderSettings.reflectionIntensity = Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(dayAmount));
        }

        private static Cubemap Build()
        {
            // RGBAHalf keeps the >1 sun values (an LDR map would clamp the
            // glint to paper white).
            var cube = new Cubemap(FaceSize, TextureFormat.RGBAHalf, mipChain: true)
            {
                name = "ProceduralSkyReflection",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear
            };

            var pixels = new Color[FaceSize * FaceSize];
            for (var face = 0; face < 6; face++)
            {
                for (var y = 0; y < FaceSize; y++)
                {
                    for (var x = 0; x < FaceSize; x++)
                    {
                        var dir = FaceDirection((CubemapFace)face,
                            (x + 0.5f) / FaceSize * 2f - 1f,
                            (y + 0.5f) / FaceSize * 2f - 1f);
                        pixels[y * FaceSize + x] = Shade(dir);
                    }
                }

                cube.SetPixels(pixels, (CubemapFace)face);
            }

            cube.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return cube;
        }

        private static Color Shade(Vector3 dir)
        {
            // Below the horizon: dark ground with a soft horizon blend.
            var horizon = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.05f, dir.y));
            var sky = Color.Lerp(HorizonColor, ZenithColor,
                Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(dir.y)));
            var color = Color.Lerp(GroundColor, sky, horizon);

            // The sun: a tight hot core inside a wide soft glow.
            var toSun = Mathf.Clamp01(Vector3.Dot(dir, SunDir));
            var glow = Mathf.Pow(toSun, 32f) * 0.6f;
            var core = Mathf.Pow(toSun, 400f) * SunIntensity;
            color += SunColor * ((glow + core) * horizon);
            color.a = 1f;
            return color;
        }

        // Standard cubemap face layout: u right, v DOWN on each face.
        private static Vector3 FaceDirection(CubemapFace face, float u, float v)
        {
            return face switch
            {
                CubemapFace.PositiveX => new Vector3(1f, -v, -u).normalized,
                CubemapFace.NegativeX => new Vector3(-1f, -v, u).normalized,
                CubemapFace.PositiveY => new Vector3(u, 1f, v).normalized,
                CubemapFace.NegativeY => new Vector3(u, -1f, -v).normalized,
                CubemapFace.PositiveZ => new Vector3(u, -v, 1f).normalized,
                _ => new Vector3(-u, -v, -1f).normalized,
            };
        }
    }
}
