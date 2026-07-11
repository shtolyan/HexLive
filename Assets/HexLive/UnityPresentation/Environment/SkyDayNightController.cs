#nullable enable
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
        }

        private void Update()
        {
            _runner ??= FindAnyObjectByType<SimulationRunnerBehaviour>();
            if (_runner?.Engine is null || _sunLight == null)
            {
                return;
            }

            Apply(_runner.Engine.World.Environment.TimeOfDayNormalized);
        }

        private void Apply(float progress)
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

            if (_skyMaterial != null)
            {
                var toSun = -sunFwd; // direction pointing at the sun
                _skyMaterial.SetVector("_SunDir", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
                _skyMaterial.SetVector("_MoonDir", new Vector4(-toSun.x, -toSun.y, -toSun.z, 0f));
                _skyMaterial.SetFloat("_DayAmount", dayAmount);
                _skyMaterial.SetColor("_SunColor", sunTint);
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
