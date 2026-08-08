using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace HexLive.UnityPresentation.CarryPoseTest
{
    /// <summary>
    /// Isolated, paused carry-pose showcase. The real renderer and real
    /// simulation snapshot are used, and the two rescue links are authored
    /// immediately after bootstrap so the art can be judged without AI noise.
    /// The POSES come from the SHIPPED path — NpcActorView.ApplyCarryPose
    /// (carrier arms from Carrying.fbx) and CarriedPoseFollower (patient body
    /// from BeingCarried.fbx, hips anchored to the carrier's hands), both
    /// bound by HexWorldRenderer off the carry link. This scene renders what
    /// the game renders; the tuning constants live in CarryPoseVisuals.
    /// </summary>
    public sealed class CarryPoseTestBootstrap : MonoBehaviour
    {
        [Header("Camera framing")]
        [SerializeField] private Vector3 _cameraPosition = new Vector3(4.6f, 3.1f, -6.4f);
        [SerializeField] private Vector3 _cameraLookAt = new Vector3(0f, 1.05f, 0f);
        [SerializeField, Range(25f, 60f)] private float _fieldOfView = 38f;

        private SimulationRunnerBehaviour _runner;
        private NPCState _carrier;
        private NPCState _patient;
        private bool _linksAuthored;

        private void Awake()
        {
            Application.runInBackground = true;
            BuildStage();

            var root = new GameObject("HexLive CarryPoseTest Sim");
            _runner = root.AddComponent<SimulationRunnerBehaviour>();
            _runner.AutosaveSuppressed = true;
            _runner.AutosaveEnabled = true;

            var renderer = root.AddComponent<HexWorldRenderer>();
            renderer.SetRunner(_runner);
            var sky = root.AddComponent<Environment.SkyDayNightController>();
            sky.SetRunner(_runner);

            _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: 1f);
            AuthorCarryLinks();
        }

        private void BuildStage()
        {
            var cameraObject = new GameObject("CarryPoseTest Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = _fieldOfView;
            camera.nearClipPlane = 0.05f;
            camera.transform.position = _cameraPosition;
            camera.transform.LookAt(_cameraLookAt);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "CarryPoseTest Ground";
            ground.transform.position = new Vector3(0f, -0.02f, 0f);
            ground.transform.localScale = new Vector3(2.2f, 1f, 2.2f);

            var lightObject = new GameObject("CarryPoseTest Key Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.transform.rotation = Quaternion.Euler(42f, -28f, 0f);
        }

        private void Update()
        {
            if (!_linksAuthored)
            {
                AuthorCarryLinks();
            }

            SetUnscaledAnimatorMode();
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
            {
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            }
        }

        private static void SetUnscaledAnimatorMode()
        {
            var animators = UnityEngine.Object.FindObjectsOfType<Animator>();
            foreach (var animator in animators)
            {
                if (animator != null && animator.transform.root.name == "HexLive CarryPoseTest Sim")
                {
                    animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                }
            }
        }

        private void AuthorCarryLinks()
        {
            var world = _runner != null && _runner.Engine != null
                ? _runner.Engine.World
                : null;
            if (world == null || world.Entities.Npcs.Count < 2)
            {
                return;
            }

            world.Tick = 600;
            _carrier = world.Entities.Npcs[new HexLive.Simulation.Common.EntityId(1)];
            _patient = world.Entities.Npcs[new HexLive.Simulation.Common.EntityId(2)];

            _carrier.Tile = new TileCoord(0, 0);
            _carrier.Position = HexSpatialMath.TileToWorld(_carrier.Tile);
            _carrier.RotationDegrees = 0f;
            _carrier.CarriedNpcId = _patient.Id;
            _carrier.CarriedByNpcId = null;
            _carrier.Health = 1f;

            _patient.Tile = _carrier.Tile;
            _patient.Position = _carrier.Position;
            _patient.RotationDegrees = 90f;
            _patient.CarriedByNpcId = _carrier.Id;
            _patient.CarriedNpcId = null;
            _patient.Health = 0.65f;
            _patient.Mind.FaintedUntilTick = int.MaxValue;
            _linksAuthored = true;
        }

        private static WorldBootstrapDefinition BuildWorldDefinition()
        {
            var tiles = new List<TileBootstrap>();
            for (var r = -3; r <= 3; r++)
            {
                for (var q = -3; q <= 3; q++)
                {
                    tiles.Add(new TileBootstrap
                    {
                        Q = q,
                        R = r,
                        Walkable = true,
                        Elevation = 1
                    });
                }
            }

            return new WorldBootstrapDefinition
            {
                Simulation = new SimulationBootstrapSettings
                {
                    Seed = 424264,
                    TickDeltaTime = 0.25f
                },
                Environment = new EnvironmentBootstrap { GlobalTemperature = 24f },
                Fragments =
                {
                    new FragmentBootstrap { Id = 1, Tiles = tiles }
                },
                Npcs =
                {
                    new NpcBootstrap
                    {
                        Id = 1,
                        DisplayName = "Несущая",
                        ActorMesh = "Marta",
                        FragmentId = 1,
                        TileQ = 0,
                        TileR = 0,
                        Hunger = 0.25f,
                        Thirst = 0.25f,
                        Energy = 0.9f,
                        Comfort = 0.9f,
                        Social = 0.9f,
                        ThermalDiscomfort = 0.05f
                    },
                    new NpcBootstrap
                    {
                        Id = 2,
                        DisplayName = "Несомая",
                        ActorMesh = "Jana",
                        FragmentId = 1,
                        TileQ = 0,
                        TileR = 0,
                        Hunger = 0.25f,
                        Thirst = 0.25f,
                        Energy = 0.1f,
                        Comfort = 0.8f,
                        Social = 0.8f,
                        ThermalDiscomfort = 0.05f
                    }
                }
            };
        }

        private void OnGUI()
        {
            GUI.Box(new Rect(12f, 12f, 620f, 84f), string.Empty);
            GUI.Label(new Rect(24f, 20f, 590f, 22f),
                "CARRY POSE TEST — несущая Marta / несомая Jana");
            GUI.Label(new Rect(24f, 44f, 590f, 22f),
                "Позы — из игрового пути (Carrying / Being Carried). R — пересоздать постановку.");
            GUI.Label(new Rect(24f, 68f, 590f, 22f),
                "Константы кадра и офсетов — CarryPoseVisuals (Wearing).");
        }
    }
}
