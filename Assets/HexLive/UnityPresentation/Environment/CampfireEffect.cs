#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec 20.16: a cartoon campfire flame — rising particle tongues plus a
    /// warm, flickering point light so the fire actually lights its surroundings.
    /// Built entirely in code; attach to a campfire object view via Construct.
    /// </summary>
    public sealed class CampfireEffect : MonoBehaviour
    {
        private Light? _light;
        private ParticleSystem? _flame;
        private ParticleSystem? _embers;
        private float _baseIntensity;
        private Vector3 _lightBasePos;
        private float _seed;
        private bool _lit;

        public void Construct(float hexRadius)
        {
            _seed = (GetInstanceID() % 997) * 0.131f;
            BuildLight(hexRadius);
            _flame = BuildFlame(hexRadius);
            _embers = BuildEmbers(hexRadius);
            // Campfires start cold (spec 29E.3); RenderSnapshot lights them
            // whenever the simulation reports fuel.
            SetLit(false);
        }

        /// <summary>
        /// Turns the visible fire on/off. Driven from the simulation: a campfire
        /// burns only while it has fuel (<c>ObjectSnapshot.ResourceAmount &gt; 0</c>).
        /// </summary>
        public void SetLit(bool lit)
        {
            _lit = lit;

            if (_light != null)
            {
                _light.enabled = lit;
            }

            ToggleParticles(_flame, lit);
            ToggleParticles(_embers, lit);
        }

        private static void ToggleParticles(ParticleSystem? ps, bool play)
        {
            if (ps == null)
            {
                return;
            }

            if (play)
            {
                if (!ps.isPlaying)
                {
                    ps.Play();
                }
            }
            else
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private void Update()
        {
            if (_light == null || !_lit)
            {
                return;
            }

            // Two Perlin octaves give a lively but non-jittery flicker.
            var n = Mathf.PerlinNoise(Time.time * 9f, _seed) * 0.7f
                    + Mathf.PerlinNoise(Time.time * 21f, _seed + 5f) * 0.3f;
            _light.intensity = _baseIntensity * (0.72f + n * 0.55f);
            _light.transform.localPosition = _lightBasePos + new Vector3(
                (Mathf.PerlinNoise(Time.time * 6f, _seed) - 0.5f) * 0.08f,
                (Mathf.PerlinNoise(Time.time * 7f, _seed + 2f) - 0.5f) * 0.05f,
                (Mathf.PerlinNoise(Time.time * 6f, _seed + 9f) - 0.5f) * 0.08f);
        }

        private void BuildLight(float r)
        {
            var go = new GameObject("FireLight");
            go.transform.SetParent(transform, false);
            _lightBasePos = new Vector3(0f, r * 0.3f, 0f);
            go.transform.localPosition = _lightBasePos;

            _light = go.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = new Color(1f, 0.6f, 0.25f);
            _light.range = r * 5f;
            _baseIntensity = 3.2f;
            _light.intensity = _baseIntensity;
            _light.shadows = LightShadows.None;
        }

        private ParticleSystem BuildFlame(float r)
        {
            var go = new GameObject("Flame");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, r * 0.06f, 0f);
            // Rotate so the cone emits straight up (local +Z -> world +Y).
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.startLifetime = 0.55f;
            main.startSpeed = r * 0.7f;
            main.startSize = r * 0.4f;
            main.startColor = new Color(1f, 0.78f, 0.28f);
            main.gravityModifier = -0.04f; // slight upward buoyancy
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 80;
            main.playOnAwake = false;

            var emission = ps.emission;
            emission.rateOverTime = 28f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 11f;
            shape.radius = r * 0.13f;

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.92f, 0.45f), 0f),
                    new GradientColorKey(new Color(1f, 0.55f, 0.12f), 0.5f),
                    new GradientColorKey(new Color(0.6f, 0.12f, 0.05f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.2f),
                    new GradientAlphaKey(0.7f, 0.6f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = gradient;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            var sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.35f), new Keyframe(0.25f, 1f), new Keyframe(1f, 0f));
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = GetFireParticleMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return ps;
        }

        private ParticleSystem BuildEmbers(float r)
        {
            var go = new GameObject("Embers");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, r * 0.1f, 0f);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.startLifetime = 1.1f;
            main.startSpeed = r * 0.9f;
            main.startSize = r * 0.05f;
            main.startColor = new Color(1f, 0.7f, 0.3f);
            main.gravityModifier = -0.12f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 24;
            main.playOnAwake = false;

            var emission = ps.emission;
            emission.rateOverTime = 7f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 22f;
            shape.radius = r * 0.1f;

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.8f, 0.35f), 0f),
                    new GradientColorKey(new Color(1f, 0.4f, 0.1f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.15f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = gradient;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = GetFireParticleMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return ps;
        }

        private static Material? _fireParticleMaterial;

        private static Material GetFireParticleMaterial()
        {
            if (_fireParticleMaterial != null)
            {
                return _fireParticleMaterial;
            }

            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            var material = new Material(shader);
            material.SetTexture("_BaseMap", MakeSoftDotTexture(64));
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Surface", 1f);      // transparent
            material.SetFloat("_Blend", 2f);        // additive (URP particles)
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_ZWrite", 0);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _fireParticleMaterial = material;
            return material;
        }

        // Soft radial dot: white core fading to transparent — a clean flame puff.
        private static Texture2D MakeSoftDotTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color[size * size];
            var c = (size - 1) * 0.5f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (x - c) / c;
                    var dy = (y - c) / c;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var a = Mathf.Clamp01(1f - d);
                    a = a * a; // soft falloff
                    pixels[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(true);
            return tex;
        }
    }
}
