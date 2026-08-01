#nullable enable
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Bootstrap;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec 20.16: drives the day/night cycle for presentation. Reads the
    /// simulation clock (<c>Environment.TimeOfDayNormalized</c>, 0 = 06:00) and
    /// swings a single directional light through the sky as the sun (warm, bright
    /// by day) and as the moon (cool, dim by night), while updating ambient light
    /// and the stylized skybox material's sun/moon directions and day amount.
    /// </summary>
    public sealed class SkyDayNightController : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour? _runner;

        // A gentle tilt so the arc isn't dead-overhead and shadows read nicely.
        private const float SunAzimuth = -22f;

        private static readonly Color SunLow = new(1f, 0.55f, 0.28f);
        private static readonly Color SunHigh = new(1f, 0.96f, 0.86f);
        private static readonly Color MoonLight = new(0.55f, 0.62f, 0.85f);
        private static readonly Color NightAmbient = new(0.09f, 0.11f, 0.18f);
        private static readonly Color DayAmbient = new(0.52f, 0.54f, 0.56f);

        private Light? _sunLight;
        private Material? _skyMaterial;

        public void SetRunner(SimulationRunnerBehaviour runner)
        {
            _runner = runner;
        }

        private void Awake()
        {
            EnsureLight();
            EnsureSky();
            // Spec 40.8 v4: env specular for the water-droplet glints (flat
            // ambient leaves unity_SpecCube0 gray — smooth pixels had nothing
            // to reflect).
            ProceduralSkyReflection.Apply();
        }

        private void Update()
        {
            _runner ??= FindAnyObjectByType<SimulationRunnerBehaviour>();
            if (_runner == null || _sunLight == null)
            {
                return;
            }

            // Everything the sky needs now comes off the snapshot: the raw time
            // of day and the sim's sun vector. It used to read WorldState
            // directly, which only works while the world is in this process.
            var snapshot = _runner.CreateSnapshot();
            if (snapshot == null)
            {
                return;
            }

            Apply(snapshot, snapshot.TimeOfDayNormalized);
        }

        private void Apply(WorldSnapshot snapshot, float progress)
        {
            // progress: 0 = 06:00 sunrise, 0.25 = noon, 0.5 = 18:00 sunset,
            // 0.75 = midnight. Rotating the light about X by progress*360 puts
            // it on the horizon at 06:00/18:00 and overhead at noon.
            var ang = progress * 360f;
            var sunRot = Quaternion.Euler(ang, SunAzimuth, 0f);
            var sunFwd = sunRot * Vector3.forward;
            var sunHeight = -sunFwd.y; // +1 at noon, -1 at midnight

            var dayAmount = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.10f, 0.30f, sunHeight));

            var sunTint = Color.Lerp(SunLow, SunHigh,
                Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.45f, sunHeight)));

            if (sunHeight >= 0f)
            {
                // Spec 43: aim the light along the SIM's sun vector (snapshot
                // SunDirection/SunElevationDegrees) so the rendered terrain
                // shadows land exactly where the sim says a tile is shaded.
                if (snapshot.SunElevationDegrees > 0f)
                {
                    var toSun = new Vector3(snapshot.SunDirection.X, 0f, snapshot.SunDirection.Y);
                    if (toSun.sqrMagnitude > 0.001f)
                    {
                        var elev = snapshot.SunElevationDegrees * Mathf.Deg2Rad;
                        var dir = (-toSun.normalized * Mathf.Cos(elev) +
                                   Vector3.down * Mathf.Sin(elev)).normalized;
                        sunRot = Quaternion.LookRotation(dir);
                    }
                }

                _sunLight!.transform.rotation = sunRot;
                _sunLight.color = sunTint;
                _sunLight.intensity = Mathf.Lerp(0.35f, 1.15f,
                    Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.4f, sunHeight)));
            }
            else
            {
                // Moon rides the opposite arc.
                _sunLight!.transform.rotation = Quaternion.Euler(ang + 180f, SunAzimuth, 0f);
                _sunLight.color = MoonLight;
                _sunLight.intensity = 0.28f;
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.Lerp(NightAmbient, DayAmbient, dayAmount);
            // Reflections (droplet glints) dim with the sky.
            ProceduralSkyReflection.SetDayAmount(dayAmount);

            if (_skyMaterial != null)
            {
                var toSun = -sunFwd; // direction pointing at the sun
                _skyMaterial.SetVector("_SunDir", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
                _skyMaterial.SetVector("_MoonDir", new Vector4(-toSun.x, -toSun.y, -toSun.z, 0f));
                _skyMaterial.SetFloat("_DayAmount", dayAmount);
                _skyMaterial.SetColor("_SunColor", sunTint);
            }

            ApplyWaterLighting(dayAmount);
        }

        // The imported stylized water shader doesn't read scene lighting — its
        // gradient/foam colours render at authored brightness, so the sea
        // GLOWED at night. Scale the material's colours with the directional
        // light instead: full authored look at noon, deep moonlit blue at
        // night. Base colours are captured once from the runtime material copy
        // (HexWorldRenderer clones the asset), so day always restores exactly.
        private static readonly int[] WaterColorProps =
        {
            Shader.PropertyToID("_DepthGradient1"),
            Shader.PropertyToID("_DepthGradient2"),
            Shader.PropertyToID("_DepthGradient3"),
            Shader.PropertyToID("_FresnelColor"),
            Shader.PropertyToID("_FoamColor"),
            // fallback HexLive/StylizedWater properties
            Shader.PropertyToID("_ShallowColor"),
            Shader.PropertyToID("_DeepColor"),
            Shader.PropertyToID("_GlintColor")
        };

        private Material? _waterMaterial;
        private readonly System.Collections.Generic.Dictionary<int, Color> _waterBaseColors = new();

        private void ApplyWaterLighting(float dayAmount)
        {
            var material = Rendering.HexWorldRenderer.ActiveWaterMaterial;
            if (material == null)
            {
                return;
            }

            if (!ReferenceEquals(material, _waterMaterial))
            {
                _waterMaterial = material;
                _waterBaseColors.Clear();
                foreach (var id in WaterColorProps)
                {
                    if (material.HasProperty(id))
                    {
                        _waterBaseColors[id] = material.GetColor(id);
                    }
                }
            }

            // Moonlit night: dark, blue-shifted; sunlit day: authored colours.
            var tint = Color.Lerp(new Color(0.10f, 0.14f, 0.24f), Color.white, dayAmount);
            foreach (var pair in _waterBaseColors)
            {
                var lit = pair.Value * tint;
                lit.a = pair.Value.a; // never touch transparency
                _waterMaterial.SetColor(pair.Key, lit);
            }
        }

        private void EnsureLight()
        {
            var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (var light in lights)
            {
                if (light.type == LightType.Directional)
                {
                    _sunLight = light;
                    break;
                }
            }

            if (_sunLight == null)
            {
                var go = new GameObject("Sun");
                _sunLight = go.AddComponent<Light>();
                _sunLight.type = LightType.Directional;
            }

            _sunLight.shadows = LightShadows.Soft;
            RenderSettings.sun = _sunLight;
        }

        private void EnsureSky()
        {
            var shader = Shader.Find("HexLive/StylizedSky");
            if (shader == null)
            {
                return; // keep whatever skybox the scene already had
            }

            _skyMaterial = new Material(shader);
            RenderSettings.skybox = _skyMaterial;

            // The skybox only shows if the camera clears to it.
            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.Skybox;
            }

            DynamicGI.UpdateEnvironment();
        }
    }
}
