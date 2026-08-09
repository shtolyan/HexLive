using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.WolfFightTest
{

// Wolf-fight dev scene: a REAL simulation reduced to one duel. A flat mild
// arena with NOTHING on it — no food, no fire, no clothes on the ground.
// One lightly-dressed girl (top + shorts, exactly — thermally comfortable at
// 18°, so she never wanders off to change) and the duel wolf dropped on an
// ADJACENT tile: contact on the very first ticks. A few more wolves prowl
// the map rim beyond aggro range for ambience. Watch the reactive combat
// loop in isolation — bites landing on body parts, wounds/decals, the
// strike-back, the flee-when-hurt assessment, and (either way it ends) the
// carcass or the death. R reloads the scene for a rematch.
public sealed class WolfFightTestBootstrap : MonoBehaviour
{
    [Tooltip("Задержка старта симуляции после запуска сцены (реальные секунды): Unity успевает прогрузиться, пока мир стоит на паузе.")]
    [Range(0f, 10f)]
    [SerializeField] private float _startDelaySeconds = 2f;

    private SimulationRunnerBehaviour _runner;
    private float _startDelayElapsed;
    private bool _started;

    private void Awake()
    {
        Application.runInBackground = true;

        var camGo = new GameObject("WolfFightTestCamera")
        {
            tag = "MainCamera"
        };
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 42f;
        cam.nearClipPlane = 0.05f;
        camGo.AddComponent<AmputationTest.AmputationTestOrbitCamera>();

        var root = new GameObject("HexLive WolfFightTest Sim");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        // Throwaway test world — never overwrite the real hexlive_save.dat.
        _runner.AutosaveSuppressed = true;
        var worldRenderer = root.AddComponent<HexWorldRenderer>();
        worldRenderer.SetRunner(_runner);
        var sky = root.AddComponent<Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        // Start PAUSED so second 0 isn't lost while shaders/scene load.
        _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: 1f);

        var world = _runner.Engine?.World;
        if (world != null)
        {
            // 15:00 — warm mid-afternoon light; tick 0 is a chilly dawn.
            world.Tick = 900;
        }

        DressForTheFight();
        SpawnWolf();
        SpawnEdgeWolves();
        PushTestOverrides();
        InstallCharacterPanel();
    }

    // The duel is the ONLY mechanic under test, so every rival pressure is
    // switched off: frozen hunger/thirst/energy (there is nothing to eat or
    // drink here anyway), no raids, no ambient dog spawns, warm around the
    // clock. Pushed every frame because HexTuning.LoadAndApply (AfterSceneLoad,
    // i.e. after Awake) rewrites the SimBalance statics from the tuning asset.
    private void PushTestOverrides()
    {
        SimBalance.HungerRate = 0f;
        SimBalance.ThirstRate = 0f;
        SimBalance.EnergyRate = 0f;
        MobCatalog.For(MobIds.Dog).RaidChancePerDay = 0f;
        // 18° ambient: top+shorts (+0.2 warmth → +2°) lands mid-comfort-band
        // [16,22]. At 24° she overheated and walked off to UNDRESS instead of
        // fighting — the outfit must be thermally correct, not just pretty.
        SimBalance.BaseTemperature = 18f;
        SimBalance.TemperatureAmplitude = 2f;

        var world = _runner != null ? _runner.Engine?.World : null;
        if (world != null)
        {
            // Only OUR wolf — the ambient spawner would flood the arena.
            world.NextMobSpawnCheckTick = int.MaxValue;
        }
    }

    private void Update()
    {
        PushTestOverrides();

        if (!_started)
        {
            _startDelayElapsed += Time.deltaTime;
            if (_startDelayElapsed >= _startDelaySeconds && _runner != null)
            {
                _started = true;
                if (_runner.IsPaused)
                {
                    _runner.TogglePause();
                }
            }

            return;
        }

        // Keep the girl selected so the character panel never closes (nothing
        // in this scene sets a selection besides us).
        if (!Input.NpcSelection.HasSelection)
        {
            var girl = Girl();
            if (girl != null)
            {
                Input.NpcSelection.Select(girl.Id.Value);
            }
        }

        var keyboard = Keyboard.current;
        if (keyboard == null || _runner == null)
        {
            return;
        }

        if (keyboard.rKey.wasPressedThisFrame)
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
        }
        else if (keyboard.digit1Key.wasPressedThisFrame)
        {
            _runner.SetSpeed(1f);
        }
        else if (keyboard.digit2Key.wasPressedThisFrame)
        {
            _runner.SetSpeed(3f);
        }
        else if (keyboard.digit3Key.wasPressedThisFrame)
        {
            _runner.SetSpeed(8f);
        }
        else if (keyboard.spaceKey.wasPressedThisFrame)
        {
            _runner.TogglePause();
        }
        else if (keyboard.wKey.wasPressedThisFrame)
        {
            // Ещё один волк вплотную (замена кнопки убранной тест-панели).
            SpawnWolf();
        }
    }

    // ---- world: an empty flat arena ----

    // q -6..6, r -5..5, elevation 1, no water, no objects at all. The girl
    // starts at (-1,0); SpawnWolf drops the duel wolf on an ADJACENT tile so
    // contact is instant, and SpawnEdgeWolves scatters extras along the map
    // rim (≥5 tiles out — beyond the aggro radius, they just roam there).
    private static WorldBootstrapDefinition BuildWorldDefinition()
    {
        var tiles = new List<TileBootstrap>();
        for (var r = -5; r <= 5; r++)
        {
            for (var q = -6; q <= 6; q++)
            {
                tiles.Add(new TileBootstrap
                {
                    Q = q,
                    R = r,
                    Walkable = true,
                    Water = false,
                    Elevation = 1
                });
            }
        }

        return new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 313 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 18f },
            Fragments =
            {
                new FragmentBootstrap { Id = 1, Tiles = tiles }
            },
            Npcs =
            {
                new NpcBootstrap
                {
                    Id = 1,
                    DisplayName = "Jana",
                    ActorMesh = "Jana",
                    FragmentId = 1,
                    TileQ = -1,
                    TileR = 0,
                    // Content on every need — nothing competes with the fight.
                    Hunger = 0.25f,
                    Thirst = 0.25f,
                    Energy = 0.95f,
                    Comfort = 0.9f,
                    Social = 0.9f,
                    ThermalDiscomfort = 0.1f
                }
            }
        };
    }

    // Exactly the requested outfit: a top and shorts, nothing else. The
    // factory seeds a random castaway set — replace it wholesale. Plus a
    // knife in hand: combat picks the best melee weapon from the inventory
    // (spear > axe > knife > fists), so without one she boxes the wolf
    // bare-handed at the ×1.0 baseline; the knife strikes at ×1.25 and also
    // lets her butcher the carcass afterwards.
    private void DressForTheFight()
    {
        var world = _runner?.Engine?.World;
        var girl = Girl();
        if (world != null && girl != null)
        {
            WardrobeDebugHelpers.Redress(world, girl, "clothing.top_tropic", "Shorts 1389");
            girl.Inventory.Items.Add("tool.knife");
        }
    }

    // Drop the duel wolf on the NEAREST junction to the girl that isn't her
    // own (an adjacent tile): it locks on and reaches melee within a tick or
    // two — no walk-up, no window for her to wander off first.
    private void SpawnWolf()
    {
        var world = _runner?.Engine?.World;
        var girl = Girl();
        if (world == null || girl == null)
        {
            return;
        }

        Junction best = null;
        var bestDistance = int.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0)
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(junction.Tiles[0], girl.Tile);
            if (distance >= 1 && distance < bestDistance)
            {
                bestDistance = distance;
                best = junction;
            }
        }

        AddWolf(world, best);
    }

    // Ambience: one wolf near each map corner, all ≥5 tiles from the girl
    // (aggro radius is 2 — they just prowl the rim as a loose ring).
    private void SpawnEdgeWolves()
    {
        var world = _runner?.Engine?.World;
        var girl = Girl();
        if (world == null || girl == null)
        {
            return;
        }

        var corners = new[]
        {
            new HexLive.Simulation.Common.TileCoord(-6, -5),
            new HexLive.Simulation.Common.TileCoord(6, -5),
            new HexLive.Simulation.Common.TileCoord(-6, 5),
            new HexLive.Simulation.Common.TileCoord(6, 5)
        };

        foreach (var corner in corners)
        {
            Junction best = null;
            var bestDistance = int.MaxValue;
            foreach (var junction in world.Junctions.Items.Values)
            {
                if (junction.Blocked || junction.Tiles.Count == 0 ||
                    HexSpatialMath.HexDistance(junction.Tiles[0], girl.Tile) < 5)
                {
                    continue;
                }

                var distance = HexSpatialMath.HexDistance(junction.Tiles[0], corner);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = junction;
                }
            }

            AddWolf(world, best);
        }
    }

    private static void AddWolf(Simulation.Core.WorldState world, Junction junction)
    {
        if (junction == null)
        {
            return;
        }

        world.Mobs.Add(new Simulation.Wildlife.MobState
        {
            Id = world.NextMobId++,
            Junction = junction.Id,
            Position = junction.WorldPosition,
            // Seed the glide target/anchor to the spawn spot — otherwise the
            // dog's rendered Position would glide from the world origin.
            TargetPosition = junction.WorldPosition,
            GlideAnchor = junction.WorldPosition,
            Tile = junction.Tiles[0]
        });
    }

    // The REAL in-game character panel (needs, health, mood, inventory,
    // live portrait) — same wiring as PrototypeRuntimeBootstrap, minus the
    // rest of the game UI. The girl is pre-selected; nothing in this scene
    // ever clears the selection, so the panel stays open.
    private void InstallCharacterPanel()
    {
        var stageRoot = new GameObject("HexLive Portrait Stage");
        var portraitStage = stageRoot.AddComponent<UI.PortraitStage>();

        // Spec §51/§57: the same staged clone serves inventory and health.
        var dollRoot = new GameObject("HexLive Character Doll Stage");
        var characterDollStage = dollRoot.AddComponent<UI.CharacterDollStage>();

        var panelRoot = new GameObject("HexLive Character Panel");
        var document = panelRoot.AddComponent<UIDocument>();
        document.panelSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        var panel = panelRoot.AddComponent<UI.CharacterPanel>();
        panel.SetRunner(_runner);
        panel.SetPortraitStage(portraitStage);
        panel.SetCharacterDollStage(characterDollStage);

        var girl = Girl();
        if (girl != null)
        {
            Input.NpcSelection.Select(girl.Id.Value);
        }
    }

    private NPCState Girl()
    {
        var world = _runner != null ? _runner.Engine?.World : null;
        if (world == null)
        {
            return null;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            return npc;
        }

        return null;
    }

}

}
