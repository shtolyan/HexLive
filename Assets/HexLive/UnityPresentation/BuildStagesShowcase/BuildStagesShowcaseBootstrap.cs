#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Environment;
using UnityEngine;

namespace HexLive.UnityPresentation.BuildStagesShowcase
{

/// <summary>
/// Bug #327 (просьба игрока): одна сцена со ВСЕМ, что строится, — мебель по
/// стадиям и архитектурные модули, — чтобы стадии строительства настраивались
/// глядя на них, а не по памяти. Всё собирается в Play из тех же фабрик, что
/// рисуют настоящую игру (BedAssembly / BlueprintArchitectureFactory), поэтому
/// увиденное здесь равно увиденному в мире по построению.
///
/// Раскладка: каждая постройка — свой ряд по Z; вдоль X — её стадии от пустой
/// площадки до готовой, подписанные TextMesh'ем количеством материалов.
/// </summary>
public sealed class BuildStagesShowcaseBootstrap : MonoBehaviour
{
    private const float RowStep = 6f;
    private const float ColumnStep = 4f;

    // (product, максимумы материалов: logs, sticks, rope, leaves, stones,
    // boards) — прогрессия строится добавлением материалов по одному в
    // порядке их стадий, как их выкладывает BuildSiteMath.
    private static readonly (string Product, int Logs, int Sticks, int Rope, int Leaves, int Stones, int Boards)[]
        Furniture =
        {
            (ContentIds.BedBasic, 4, 5, 4, 96, 0, 0),
            ("campfire.spot", 0, 12, 2, 0, 18, 0),
            ("station.drying_rack", 0, 4, 4, 0, 0, 0),
            ("station.water_collector", 0, 8, 8, 11, 5, 0),
            (ContentIds.Workbench, 0, 6, 2, 0, 0, 6),
        };

    private static readonly string[] ArchitectureModules =
    {
        "architecture.support.wood", "architecture.wall.wood",
        "architecture.window.wood", "architecture.door.wood",
        "architecture.floor.board", "architecture.roof.palm",
    };

    private readonly List<GameObject> _spawned = new();
    private float _retryAt;

    private void Awake()
    {
        var camGo = new GameObject("ShowcaseCamera") { tag = "MainCamera" };
        var cam = camGo.AddComponent<Camera>();
        cam.transform.position = new Vector3(8f, 10f, -10f);
        cam.transform.rotation = Quaternion.Euler(38f, -20f, 0f);
        camGo.AddComponent<SwimTest.SwimTestOrbitCamera>();

        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(55f, 35f, 0f);
        light.intensity = 1.1f;

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(12f, 1f, 12f);
    }

    private void Update()
    {
        // Атомарные бандлы асинхронны: первые кадры фабрики отдают null.
        // Перестраиваем витрину раз в секунду, пока все ряды не соберутся.
        if (_spawned.Count > 0 || Time.time < _retryAt)
        {
            return;
        }

        _retryAt = Time.time + 1f;
        BuildShowcase();
    }

    private void BuildShowcase()
    {
        var row = 0;
        var complete = true;
        foreach (var f in Furniture)
        {
            var stages = StageProgression(f);
            for (var column = 0; column < stages.Count; column++)
            {
                var (logs, sticks, rope, leaves, stones, boards) = stages[column];
                var built = column == stages.Count - 1
                    ? BedAssembly.BuildFinished(f.Product)
                    : BedAssembly.BuildPartial(
                        f.Product, logs, sticks, rope, leaves, stones, boards);
                if (built == null)
                {
                    complete = false;
                    continue;
                }

                Place(built, row, column,
                    $"{f.Product}\nL{logs} S{sticks} R{rope} Lf{leaves} St{stones} B{boards}");
            }

            row++;
        }

        foreach (var module in ArchitectureModules)
        {
            var built = BlueprintArchitectureFactory.InstantiateModel(
                module, Vector3.zero, Quaternion.identity);
            if (built == null)
            {
                complete = false;
                continue;
            }

            Place(built, row, 0, module);
            row++;
        }

        if (!complete)
        {
            // Что-то ещё едет из бандлов — снести и собрать заново целиком.
            foreach (var go in _spawned) Destroy(go);
            _spawned.Clear();
        }
    }

    private static List<(int, int, int, int, int, int)> StageProgression(
        (string Product, int Logs, int Sticks, int Rope, int Leaves, int Stones, int Boards) f)
    {
        // Пустая площадка → +каждый материал в порядке стадий → готовая.
        var stages = new List<(int, int, int, int, int, int)>
        {
            (0, 0, 0, 0, 0, 0),
            (f.Logs, 0, 0, 0, 0, 0),
            (f.Logs, f.Sticks, 0, 0, 0, 0),
            (f.Logs, f.Sticks, f.Rope, 0, 0, 0),
            (f.Logs, f.Sticks, f.Rope, f.Leaves, 0, 0),
            (f.Logs, f.Sticks, f.Rope, f.Leaves, f.Stones, 0),
            (f.Logs, f.Sticks, f.Rope, f.Leaves, f.Stones, f.Boards),
        };
        // Схлопнуть подряд идущие одинаковые (материал с нулевым максимумом).
        for (var i = stages.Count - 1; i > 0; i--)
        {
            if (stages[i].Equals(stages[i - 1]))
            {
                stages.RemoveAt(i);
            }
        }

        return stages;
    }

    private void Place(GameObject built, int row, int column, string caption)
    {
        built.transform.position = new Vector3(column * ColumnStep, 0f, -row * RowStep);
        _spawned.Add(built);

        var label = new GameObject("label");
        label.transform.position = built.transform.position + new Vector3(0f, 2.6f, 0f);
        var text = label.AddComponent<TextMesh>();
        text.text = caption;
        text.characterSize = 0.12f;
        text.fontSize = 32;
        text.anchor = TextAnchor.LowerCenter;
        text.color = Color.white;
        _spawned.Add(label);
    }
}

}
