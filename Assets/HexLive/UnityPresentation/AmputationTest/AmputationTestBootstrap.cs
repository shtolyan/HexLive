using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.AmputationTest
{

// §50 dev scene: a REAL simulation on a staircase of shelves so limb loss can
// be eyeballed in isolation. One girl idles on the low ground; a menu of
// buttons lets you injure any body part (like a bite — a limb tears off "on
// damage when it should") or tear a limb off outright, then watch:
//   • the limb fly off and lie in the world as its real geometry,
//   • the stub bleed and the body drop to the crawl clip (a lost leg),
//   • a lost leg make the higher steps UNREACHABLE (no jumping — press "к еде
//     наверху" and she can't climb; with both legs she jumps the steps).
// No keyboard needed — the on-screen menu drives everything. R reloads the
// scene to start fresh (un-sever).
public sealed class AmputationTestBootstrap : MonoBehaviour
{
    [Tooltip("Задержка старта симуляции после запуска сцены (реальные секунды): Unity успевает прогрузиться, пока мир на паузе.")]
    [Range(0f, 10f)]
    [SerializeField] private float _startDelaySeconds = 2f;

    [Tooltip("Урон за одно нажатие кнопки урона (как крупный укус). ≥ порога §50 — конечность на 0 отлетает сразу.")]
    [Range(0.02f, 0.6f)]
    [SerializeField] private float _biteDamage = 0.34f;

    private SimulationRunnerBehaviour _runner;
    private float _startDelayElapsed;
    private bool _started;

    private void Awake()
    {
        Application.runInBackground = true;

        BuildEnvironment();

        var root = new GameObject("HexLive AmputationTest Sim");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        // Throwaway test world — never overwrite the real hexlive_save.dat
        // (the loader watchdog otherwise enables autosave in paused starts).
        _runner.AutosaveSuppressed = true;
        var worldRenderer = root.AddComponent<HexWorldRenderer>();
        worldRenderer.SetRunner(_runner);
        var sky = root.AddComponent<Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        // Start paused so second 0 isn't lost while shaders/scene load.
        _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: 1f);
    }

    private void Update()
    {
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

        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
        }
    }

    // ---- world: a staircase of shelves (elevation 1 → 2 → 3) ----

    // Rows r -1..1; columns q -5..5. The left half (q ≤ 0) is flat ground
    // (elevation 1) where she spawns and idles; then two up-steps (q=1 → 2,
    // q=2 → 3) lead to the top shelf (q ≥ 3, elevation 3) carrying a coconut.
    // A second coconut sits on the ground so a legless girl still has SOMETHING
    // reachable. Reaching the top needs two hex-step jumps — impossible without
    // legs, so the top shelf goes off-limits once a leg is gone.
    private static WorldBootstrapDefinition BuildWorldDefinition()
    {
        var tiles = new List<TileBootstrap>();
        for (var r = -1; r <= 1; r++)
        {
            for (var q = -5; q <= 5; q++)
            {
                var elevation = q <= 0 ? 1 : q == 1 ? 2 : 3;
                tiles.Add(new TileBootstrap
                {
                    Q = q,
                    R = r,
                    Walkable = true,
                    Water = false,
                    Elevation = elevation
                });
            }
        }

        return new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 777 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 24f },
            Fragments =
            {
                new FragmentBootstrap { Id = 1, Tiles = tiles }
            },
            Objects =
            {
                Food(201, 3, 0, 1),   // on the top shelf (needs two jumps up)
                Food(202, 4, 0, 1),
                Food(203, -3, 0, 1)   // on the ground — always reachable
            },
            Npcs =
            {
                new NpcBootstrap
                {
                    Id = 1,
                    DisplayName = "Marta",
                    ActorMesh = "Marta",
                    FragmentId = 1,
                    TileQ = -4,
                    TileR = 0,
                    // Mostly content so she idles by the spawn and holds still
                    // for inspection; the "проголодаться" button sends her up.
                    Hunger = 0.3f,
                    Thirst = 0.3f,
                    Energy = 0.95f,
                    Comfort = 0.9f,
                    Social = 0.9f,
                    ThermalDiscomfort = 0.1f
                }
            }
        };
    }

    private static ObjectBootstrap Food(int id, int q, int r, int slot)
    {
        return new ObjectBootstrap
        {
            Id = id,
            DefinitionId = "food.coconut",
            FragmentId = 1,
            TileQ = q,
            TileR = r,
            JunctionSlots = { slot }
        };
    }

    // ---- acting on the girl ----

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

    private void Bite(BodyPart part)
    {
        var world = _runner?.Engine?.World;
        var girl = Girl();
        if (world != null && girl != null)
        {
            AmputateSystemHelpers.DebugBite(world, girl, part, _biteDamage);
        }
    }

    private void TearOff(BodyPart part)
    {
        var world = _runner?.Engine?.World;
        var girl = Girl();
        if (world != null && girl != null)
        {
            AmputateSystemHelpers.Sever(world, girl, part);
        }
    }

    private void FitProsthetic(BodyPart part, bool mechanical)
    {
        var world = _runner?.Engine?.World;
        var girl = Girl();
        if (world == null || girl == null ||
            part is not (BodyPart.ArmL or BodyPart.ArmR or BodyPart.LegL or BodyPart.LegR))
        {
            return;
        }

        if (!girl.Body.IsSevered(part))
        {
            AmputateSystemHelpers.Sever(world, girl, part);
        }

        var arm = part is BodyPart.ArmL or BodyPart.ArmR;
        var maxCondition = mechanical
            ? Spec118.MechanicalProstheticDurability
            : Spec118.WoodenProstheticDurability;
        girl.Body.Condition(part).Prosthetic = new ProstheticState
        {
            DefinitionId = mechanical
                ? arm ? ContentIds.MechanicalArm : ContentIds.MechanicalLeg
                : arm ? ContentIds.WoodenArm : ContentIds.WoodenLeg,
            Part = part,
            Condition = maxCondition,
            MaxCondition = maxCondition,
            Function = mechanical
                ? arm ? Spec118.MechanicalArmFunction : Spec118.MechanicalLegFunction
                : arm ? Spec118.WoodenArmFunction : Spec118.WoodenLegFunction,
            Mechanical = mechanical
        };
    }

    private void BreakProsthetic(BodyPart part)
    {
        var girl = Girl();
        if (girl != null)
        {
            girl.Body.Condition(part).Prosthetic = null;
        }
    }

    private void SendUpForFood()
    {
        var girl = Girl();
        if (girl != null)
        {
            girl.Needs.Hunger = 0.97f;
        }
    }

    // ---- menu ----

    private void OnGUI()
    {
        var girl = Girl();
        GUILayout.BeginArea(new Rect(12f, 12f, 360f, 820f), GUI.skin.box);

        GUILayout.Label("<b>ТЕСТ АМПУТАЦИИ</b> — R: перезапуск сцены");
        if (girl != null)
        {
            GUILayout.Label(
                $"Здоровье {girl.Health:0.00}  Кровь {girl.Needs.Blood:0.00}  Голод {girl.Needs.Hunger:0.00}");
            GUILayout.Label($"Оторвано: {DescribeSevered(girl)}");
        }
        else
        {
            GUILayout.Label("(ждём старта симуляции…)");
        }

        GUILayout.Space(8f);
        GUILayout.Label($"— УРОН по части (как укус, {_biteDamage:0.00}) —");
        GUILayout.Label("part→0 крупным ударом = отлетает");
        if (GUILayout.Button("Голова")) Bite(BodyPart.Head);
        if (GUILayout.Button("Туловище")) Bite(BodyPart.Torso);
        if (GUILayout.Button("Таз")) Bite(BodyPart.Pelvis);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Лев. рука")) Bite(BodyPart.ArmL);
        if (GUILayout.Button("Прав. рука")) Bite(BodyPart.ArmR);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Лев. нога")) Bite(BodyPart.LegL);
        if (GUILayout.Button("Прав. нога")) Bite(BodyPart.LegR);
        GUILayout.EndHorizontal();

        GUILayout.Space(10f);
        GUILayout.Label("— ПОСТАВИТЬ ПРОТЕЗ (сразу отсекает часть) —");
        GUILayout.Label("дерево:");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("рука L")) FitProsthetic(BodyPart.ArmL, false);
        if (GUILayout.Button("рука R")) FitProsthetic(BodyPart.ArmR, false);
        if (GUILayout.Button("нога L")) FitProsthetic(BodyPart.LegL, false);
        if (GUILayout.Button("нога R")) FitProsthetic(BodyPart.LegR, false);
        GUILayout.EndHorizontal();
        GUILayout.Label("механика:");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("рука L")) FitProsthetic(BodyPart.ArmL, true);
        if (GUILayout.Button("рука R")) FitProsthetic(BodyPart.ArmR, true);
        if (GUILayout.Button("нога L")) FitProsthetic(BodyPart.LegL, true);
        if (GUILayout.Button("нога R")) FitProsthetic(BodyPart.LegR, true);
        GUILayout.EndHorizontal();
        GUILayout.Label("сломать/снять:");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("рука L")) BreakProsthetic(BodyPart.ArmL);
        if (GUILayout.Button("рука R")) BreakProsthetic(BodyPart.ArmR);
        if (GUILayout.Button("нога L")) BreakProsthetic(BodyPart.LegL);
        if (GUILayout.Button("нога R")) BreakProsthetic(BodyPart.LegR);
        GUILayout.EndHorizontal();

        GUILayout.Space(10f);
        GUILayout.Label("— ОТОРВАТЬ сразу —");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Лев. рука")) TearOff(BodyPart.ArmL);
        if (GUILayout.Button("Прав. рука")) TearOff(BodyPart.ArmR);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Лев. нога")) TearOff(BodyPart.LegL);
        if (GUILayout.Button("Прав. нога")) TearOff(BodyPart.LegR);
        GUILayout.EndHorizontal();

        GUILayout.Space(10f);
        GUILayout.Label("— ПРОВЕРКА «нельзя прыгать» —");
        if (GUILayout.Button("Проголодаться → к еде НАВЕРХУ"))
        {
            SendUpForFood();
        }
        GUILayout.Label("с ногами — прыгает по ступеням;\nбез ноги — верхняя полка недостижима");

        GUILayout.Space(10f);
        GUILayout.Label("— ОТРУБЛЕННЫЕ КОНЕЧНОСТИ в мире —");
        DrawSeveredLimbObjects();
        GUILayout.Label("в Hierarchy ищи \"limb\" (Object body.limb_severed …)");

        GUILayout.EndArea();

        DrawStatusPanel();
    }

    // Live character status, so you can watch how HP and BLOOD drain after a
    // limb comes off (blood bleeds down while a fresh wound sits on a hurt part;
    // a severed zone stays pinned at 0 and never regenerates).
    private static readonly BodyPart[] PartOrder =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    private void DrawStatusPanel()
    {
        GUI.skin.label.richText = true;
        var girl = Girl();
        GUILayout.BeginArea(new Rect(360f, 12f, 300f, 660f), GUI.skin.box);
        GUILayout.Label("<b>СТАТУС</b>");

        if (girl == null)
        {
            GUILayout.Label("(ждём старта симуляции…)");
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label($"Здоровье  {Bar(girl.Health)} {girl.Health:0.00}");
        GUILayout.Label($"Кровь     {Bar(girl.Needs.Blood)} {girl.Needs.Blood:0.00}");
        GUILayout.Label(IsBleeding(girl)
            ? "<color=#ff5a5a><b>КРОВОТЕЧЕНИЕ идёт</b></color>"
            : "кровотечения нет");

        GUILayout.Space(6f);
        GUILayout.Label("— части тела —");
        foreach (var part in PartOrder)
        {
            if (girl.Body.IsSevered(part))
            {
                var prosthetic = girl.Body.Condition(part).Prosthetic;
                if (prosthetic != null)
                {
                    GUILayout.Label($"{PartName(part)}  <color=#7ed0ff>" +
                                    $"{(prosthetic.Mechanical ? "МЕХ" : "ДЕРЕВО")} " +
                                    $"{prosthetic.Condition:0.00}/{prosthetic.MaxCondition:0.00}</color>");
                }
                else
                {
                    GUILayout.Label($"{PartName(part)}  <color=#ff5a5a>ОТРУБЛЕНА</color>");
                }
                continue;
            }

            var hp = girl.Body.Parts.TryGetValue(part, out var v) ? v : 1f;
            GUILayout.Label($"{PartName(part)}  {Bar(hp)} {hp:0.00}");
        }

        GUILayout.Space(6f);
        GUILayout.Label(
            $"Мобильность {girl.Body.MobilityFactor():0.00}   Удар {girl.StrikeFactor():0.00}");

        GUILayout.Space(6f);
        GUILayout.Label($"— раны: {girl.Wounds.Count} (заж = заживление 0→1) —");
        var shown = 0;
        foreach (var wound in girl.Wounds)
        {
            if (shown++ >= 8)
            {
                GUILayout.Label($"…ещё {girl.Wounds.Count - 8}");
                break;
            }

            var fresh = wound.Heal01 < 0.3f ? "  ← свежая" : "";
            GUILayout.Label($"{wound.Zone}  тяж {wound.Severity:0.00}  заж {wound.Heal01:0.00}{fresh}");
        }

        GUILayout.Space(6f);
        GUILayout.Label(
            $"Голод {girl.Needs.Hunger:0.00}  Жажда {girl.Needs.Thirst:0.00}");
        GUILayout.Label(
            $"Энергия {girl.Needs.Energy:0.00}  Стамина {girl.Needs.Stamina:0.00}");
        GUILayout.Label($"Поза (по ногам): {(girl.Body.IsProne ? "ПОЛЗЁТ" : "стоит")}");

        GUILayout.EndArea();
    }

    // 0..1 → a 10-cell text bar.
    private static string Bar(float v)
    {
        v = Mathf.Clamp01(v);
        var n = Mathf.RoundToInt(v * 10f);
        return "[" + new string('#', n) + new string('.', 10 - n) + "]";
    }

    // Mirrors the sim's bleed condition: a fresh wound on a badly-hurt part is
    // what actually drains Blood each tick.
    private static bool IsBleeding(NPCState girl)
    {
        foreach (var wound in girl.Wounds)
        {
            if (wound.Heal01 >= 0.3f)
            {
                continue;
            }

            var hp = girl.Body.Parts.TryGetValue(wound.Zone, out var v) ? v : 1f;
            if (hp < 0.4f)
            {
                return true;
            }
        }

        return false;
    }

    private static string PartName(BodyPart part) => part switch
    {
        BodyPart.Head => "Голова ",
        BodyPart.Torso => "Тулово ",
        BodyPart.Pelvis => "Таз    ",
        BodyPart.ArmL => "Рука L ",
        BodyPart.ArmR => "Рука R ",
        BodyPart.LegL => "Нога L ",
        BodyPart.LegR => "Нога R ",
        _ => part.ToString()
    };

    // Lists the body.limb_severed objects the sim spawned — so you can confirm
    // one was created and where, even if its mesh is hard to spot on scene.
    private void DrawSeveredLimbObjects()
    {
        var world = _runner != null ? _runner.Engine?.World : null;
        if (world == null)
        {
            return;
        }

        var any = false;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId != "body.limb_severed")
            {
                continue;
            }

            any = true;
            GUILayout.Label(
                $"#{obj.Id.Value}  {obj.Variant}  тайл ({obj.Tile.Q},{obj.Tile.R})  распад {obj.ResourceAmount:0}");
        }

        if (!any)
        {
            GUILayout.Label("(пока нет — оторви конечность)");
        }
    }

    private static string DescribeSevered(NPCState girl)
    {
        if (!girl.Body.AnySevered)
        {
            return "—";
        }

        var parts = new List<string>();
        foreach (var zone in girl.Body.Severed)
        {
            parts.Add(zone.ToString());
        }

        return string.Join(", ", parts);
    }

    // ---- environment ----

    private void BuildEnvironment()
    {
        var camGo = new GameObject("AmputationTestCamera")
        {
            tag = "MainCamera"
        };
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 42f;
        cam.nearClipPlane = 0.05f;
        camGo.AddComponent<AmputationTestOrbitCamera>();
    }
}

// Inspection camera glued to the NPC: RMB drag orbits, scroll zooms, MMB drag
// pans (and releases follow), F re-glues to her.
public sealed class AmputationTestOrbitCamera : MonoBehaviour
{
    private Vector3 _focus = new(0f, 1.2f, 0f);
    private float _yaw = 205f;
    private float _pitch = 30f;
    private float _distance = 7f;
    private bool _follow = true;
    private Wearing.NpcActorView _target;

    private void LateUpdate()
    {
        if (_target == null)
        {
            _target = FindAnyObjectByType<Wearing.NpcActorView>();
        }

        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.fKey.wasPressedThisFrame)
        {
            _follow = true;
        }

        if (_follow && _target != null)
        {
            var want = _target.transform.position + Vector3.up * 0.9f;
            _focus = Vector3.Lerp(_focus, want, 1f - Mathf.Exp(-8f * Time.deltaTime));
        }

        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * 0.25f;
                _pitch = Mathf.Clamp(_pitch - delta.y * 0.25f, -10f, 85f);
            }

            if (mouse.middleButton.isPressed)
            {
                _follow = false;
                var delta = mouse.delta.ReadValue();
                var rot = Quaternion.Euler(0f, _yaw, 0f);
                _focus += rot * new Vector3(-delta.x, 0f, -delta.y) * 0.003f * _distance;
            }

            var scroll = Mathf.Clamp(mouse.scroll.ReadValue().y, -3f, 3f);
            if (Mathf.Abs(scroll) > 0.01f)
            {
                _distance = Mathf.Clamp(_distance * (1f - scroll * 0.05f), 1.5f, 30f);
            }
        }

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.position = _focus + rotation * new Vector3(0f, 0f, -_distance);
        transform.rotation = rotation;
    }
}

}
