using System.Collections.Generic;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.BedBuildTest
{

// Bed build test scene (§54.12 dev tool): a REAL simulation reduced to the
// staged bed build-site problem. One flat warm island, a burning hearth, and
// EXACTLY the bills of BOTH beds scattered loose on the ground (96 leaves +
// 13 sticks + 18 fiber + 4 logs + the hammer; ready rope has no pickup goal —
// the sim only crafts rope from fiber at the fire, so fiber is the ground
// form of the rope bills). Marta wakes fed, watered, rested and warm but
// with ZERO comfort, so the staked bed is the only project worth doing:
// watch her lash the leaf mat stage by stage (frame → slats → lashing →
// mattress), then BedSiteSystem stakes the premium bedroll upgrade and she
// builds that too (logs → slats → lashing → mattress, hammer-raised).
public sealed class BedBuildTestBootstrap : MonoBehaviour
{
    [Tooltip("Задержка старта симуляции после запуска сцены (реальные секунды): Unity успевает прогрузиться, пока мир стоит на паузе.")]
    [Range(0f, 10f)]
    [SerializeField] private float _startDelaySeconds = 3f;

    private SimulationRunnerBehaviour _runner;
    private float _startDelayElapsed;
    private bool _started;

    private void Awake()
    {
        Application.runInBackground = true;

        var camGo = new GameObject("BedBuildTestCamera")
        {
            tag = "MainCamera"
        };
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 42f;
        cam.nearClipPlane = 0.05f;
        camGo.AddComponent<SwimTest.SwimTestOrbitCamera>();

        var root = new GameObject("HexLive BedBuildTest Sim");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        // Throwaway world: never let it touch the real hexlive_save.dat (the
        // loader watchdog otherwise turns autosave on ~2s into the paused
        // start and every run overwrote the main game's save).
        _runner.AutosaveSuppressed = true;
        var worldRenderer = root.AddComponent<HexWorldRenderer>();
        worldRenderer.SetRunner(_runner);
        var sky = root.AddComponent<Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        // Start PAUSED: the sim otherwise begins ticking while Unity is still
        // compiling shaders / building the scene, and the first seconds of
        // action play out unseen. Update() unpauses after _startDelaySeconds.
        _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: 1f);

        var world = _runner.Engine?.World;
        if (world != null)
        {
            // 15:00 — the warm mid-afternoon (tick 0 is a 9° dawn: a naked
            // Marta immediately chases warmth instead of the bed).
            world.Tick = 900;
        }

        PushTestOverrides();
    }

    // The test isolates ONE mechanic — the bed build — so every rival
    // pressure is switched off: no dog packs, no night raids, and frozen
    // hunger/thirst/energy (there is no food or water on this map). Pushed
    // every frame because HexTuning.LoadAndApply (AfterSceneLoad, i.e. after
    // Awake) rewrites the SimBalance statics from the tuning asset.
    private void PushTestOverrides()
    {
        SimBalance.HungerRate = 0f;
        SimBalance.ThirstRate = 0f;
        SimBalance.EnergyRate = 0f;
        MobCatalog.For(MobIds.Dog).RaidChancePerDay = 0f;
        // Warm around the clock — night on this map would otherwise freeze a
        // naked builder (there are no clothes here either). Kept mild so the
        // +8° fireside warmth doesn't overheat her while she builds.
        SimBalance.BaseTemperature = 22f;
        SimBalance.TemperatureAmplitude = 2f;
        // The scattered fiber is budgeted for the 8-rope lashing stage EXACTLY;
        // an unreachable cloth cost keeps the §54 cloth/tent chain from eating
        // it (bedDeficit wants one cloth for a sun-shelter otherwise).
        SimBalance.ClothFiberCost = 999;

        var world = _runner != null ? _runner.Engine?.World : null;
        if (world != null)
        {
            world.NextMobSpawnCheckTick = int.MaxValue;
            if (world.Mobs.Count > 0)
            {
                world.Mobs.Clear();
            }

            // Keep the hearth BURNING: an unlit pit dead-ends every fireside
            // interaction ("fire went out" aborts HaulToFire stashes into an
            // endless replan loop) — and a lit fire is the §54 camp anyway.
            foreach (var obj in world.Entities.Objects.Values)
            {
                if (obj.DefinitionId == "campfire.spot" && obj.ResourceAmount < 5000f)
                {
                    obj.ResourceAmount = 10000f;
                }
            }
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

        // Speed hotkeys — a bed is a long project, watching at 1x is optional.
        var keyboard = Keyboard.current;
        if (keyboard == null || _runner == null)
        {
            return;
        }

        if (keyboard.digit1Key.wasPressedThisFrame)
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
    }

    // ---- world ----

    // One flat island (q -4..4, r -3..3, elevation 1), warm (24°) so no
    // thermal pressure. A cold campfire.spot anchors the colony — the
    // BedSiteSystem stakes the bed.leaf site beside it (tag check only, the
    // fire needn't burn). The exact bed bill lies scattered around the map:
    // SimBalance.BedLeafBill* = 46 leaves + 8 sticks + 8 rope (as 8 fiber),
    // demanded stage by stage (§54.12: frame → slats → lashing → mattress).
    private static WorldBootstrapDefinition BuildWorldDefinition()
    {
        var tiles = new List<TileBootstrap>();
        for (var r = -3; r <= 3; r++)
        {
            for (var q = -4; q <= 4; q++)
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

        var def = new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 424242 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 24f },
            Fragments =
            {
                new FragmentBootstrap { Id = 1, Tiles = tiles }
            },
            Objects =
            {
                // The hearth: the bed site gets staked beside this.
                new ObjectBootstrap
                {
                    Id = 100,
                    DefinitionId = "campfire.spot",
                    FragmentId = 1,
                    TileQ = -2,
                    TileR = 0,
                    JunctionSlots = { 0 }
                }
            },
            Npcs =
            {
                new NpcBootstrap
                {
                    Id = 1,
                    DisplayName = "Marta",
                    ActorMesh = "Marta",
                    FragmentId = 1,
                    TileQ = 3,
                    TileR = 1,
                    // Fed, watered, rested, warm and social — the ONLY thing
                    // missing is comfort, so the bed project has no rival.
                    Hunger = 0.05f,
                    Thirst = 0.05f,
                    Energy = 0.95f,
                    Comfort = 0f,
                    Social = 0.95f,
                    ThermalDiscomfort = 0f
                }
            }
        };

        // The bills for BOTH beds lie scattered on the ground: after the leaf
        // mat is lashed, BedSiteSystem stakes the premium bedroll upgrade
        // (§54.12) and the build continues — logs → slats → lashing → mattress.
        // Leaves live on the even (q+r) tiles, everything else on the odd
        // ones; slot = 1 + (i % 6) never repeats a (tile, slot) pair because
        // the tile counts are coprime-ish with 6.
        var leafTiles = new List<(int q, int r)>();
        var otherTiles = new List<(int q, int r)>();
        for (var r = -3; r <= 3; r++)
        {
            for (var q = -4; q <= 4; q++)
            {
                if ((q == -2 && r == 0) || (q == -1 && r == 0))
                {
                    continue; // the hearth tile + the fireside site spot
                }

                if ((q + r) % 2 == 0)
                {
                    leafTiles.Add((q, r));
                }
                else
                {
                    otherTiles.Add((q, r));
                }
            }
        }

        // 96 palm leaves — both mattress stages (46 leaf mat + 50 bedroll).
        var leafCount = SimBalance.BedLeafBillLeaves + SimBalance.BedBasicBillLeaves;
        for (var i = 0; i < leafCount; i++)
        {
            var (q, r) = leafTiles[i % leafTiles.Count];
            def.Objects.Add(Loose(201 + i, "resource.palm_leaf", q, r, 1 + (i % 6)));
        }

        // 17 sticks — the leaf mat's frame+slats (4+4), the bedroll's slats (5)
        // and a +4 margin: normal camp life nibbles sticks too (the drying rack
        // costs 2, a spear 1) and an exact count deadlocks the last stage.
        var stickCount = SimBalance.BedLeafBillSticks + SimBalance.BedBasicBillSticks + 4;
        for (var i = 0; i < stickCount; i++)
        {
            var (q, r) = otherTiles[(i * 5) % otherTiles.Count];
            def.Objects.Add(Loose(401 + i, "resource.stick", q, r, 1 + (i % 6)));
        }

        // 18 fiber — the two rope lashings (8 + 10), twisted at the fire
        // (RopeFiberCost = 1). Loose READY rope is never picked up: no gather
        // goal targets the Rope tag, only the fiber→craft chain.
        var fiberCount = SimBalance.BedLeafBillRope + SimBalance.BedBasicBillRope;
        for (var i = 0; i < fiberCount; i++)
        {
            var (q, r) = otherTiles[(i * 5 + 2) % otherTiles.Count];
            def.Objects.Add(Loose(501 + i, "resource.fiber", q, r, 1 + ((i + 3) % 6)));
        }

        // 4 logs — the bedroll's side rails (stage 1 of bed.basic).
        for (var i = 0; i < SimBalance.BedBasicBillLogs; i++)
        {
            var (q, r) = otherTiles[(i * 7 + 4) % otherTiles.Count];
            def.Objects.Add(Loose(601 + i, "resource.log", q, r, 1 + ((i + 1) % 6)));
        }

        // The builder's hammer — raising the premium bedroll needs it in hand
        // (the leaf mat is hand-lashed and needs none).
        def.Objects.Add(Loose(700, "tool.hammer", -3, 0, 3));

        return def;
    }

    private static ObjectBootstrap Loose(int id, string definitionId, int q, int r, int slot)
    {
        return new ObjectBootstrap
        {
            Id = id,
            DefinitionId = definitionId,
            FragmentId = 1,
            TileQ = q,
            TileR = r,
            JunctionSlots = { slot }
        };
    }

    // ---- readout ----

    private void OnGUI()
    {
        GUI.Label(new Rect(12f, 8f, 1300f, 22f),
            "BED BUILD TEST §54.12 — лежанка (2×4 палки → 8 верёвок → 46 листьев), затем апгрейд: премиум (4 бревна → 5 палок → 10 верёвок → 50 листьев, молоток). " +
            "1/2/3 — скорость, пробел — пауза, ПКМ — орбита, F — прилипнуть к NPC");

        var world = _runner != null ? _runner.Engine?.World : null;
        if (world == null)
        {
            return;
        }

        var y = 32f;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.BuildProduct is "bed.leaf" or "bed.basic")
            {
                int leaves = 0, sticks = 0, ropes = 0, logs = 0;
                foreach (var item in obj.Contents)
                {
                    switch (item.DefinitionId)
                    {
                        case "resource.palm_leaf": leaves++; break;
                        case "resource.stick": sticks++; break;
                        case "resource.rope": ropes++; break;
                        case "resource.log": logs++; break;
                    }
                }

                var kind = obj.BuildProduct == "bed.leaf" ? "лежанки" : "ПРЕМИУМ кровати";
                var logsPart = obj.BillLogs > 0 ? $"брёвна {logs}/{obj.BillLogs}, " : string.Empty;
                GUI.Label(new Rect(12f, y, 1000f, 22f),
                    $"Сайт {kind}: {logsPart}палки {sticks}/{obj.BillSticks}, верёвка {ropes}/{obj.BillRope}, листья {leaves}/{obj.BillLeaves}");
                y += 22f;
            }
            else if (obj.DefinitionId == "bed.leaf")
            {
                GUI.Label(new Rect(12f, y, 900f, 22f), "ЛЕЖАНКА ГОТОВА ✔");
                y += 22f;
            }
            else if (obj.DefinitionId == "bed.basic")
            {
                GUI.Label(new Rect(12f, y, 900f, 22f), "ПРЕМИУМ КРОВАТЬ ГОТОВА ✔");
                y += 22f;
            }
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            var carried = new List<string>();
            foreach (var item in npc.Inventory.Items)
            {
                carried.Add(item.DefinitionId);
            }

            GUI.Label(new Rect(12f, y, 1200f, 22f),
                $"{npc.DisplayName}: цель {npc.Mind.CurrentGoal}, комфорт {npc.Needs.Comfort:0.00}, в руках: " +
                (carried.Count == 0 ? "пусто" : string.Join(", ", carried)));
            y += 22f;
        }
    }
}

}
