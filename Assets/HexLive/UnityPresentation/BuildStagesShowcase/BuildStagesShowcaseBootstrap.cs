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

    private sealed class FurnitureStages
    {
        public readonly string Product;
        public readonly (int Logs, int Sticks, int Rope, int Leaves, int Stones, int Boards)[] Stages;

        public FurnitureStages(string product,
            params (int Logs, int Sticks, int Rope, int Leaves, int Stones, int Boards)[] stages)
        {
            Product = product;
            Stages = stages;
        }
    }

    // Same ordered stage contract as Tools/construction_stage_manifest.json and
    // BuildSiteMath. Repeated materials deliberately stay separate: the water
    // collector builds four posts, then stones, then another four sticks.
    private static readonly FurnitureStages[] Furniture =
    {
        new(ContentIds.BedBasic,
            Step(logs: 4), Step(sticks: 5), Step(rope: 10), Step(leaves: 50)),
        new(ContentIds.Campfire,
            Step(sticks: 9), Step(sticks: 2), Step(sticks: 1), Step(rope: 2), Step(stones: 18)),
        new(ContentIds.DryingRack,
            Step(sticks: 2), Step(sticks: 2), Step(rope: 4)),
        new(ContentIds.WaterCollector,
            Step(sticks: 4), Step(stones: 5), Step(sticks: 4), Step(rope: 8), Step(leaves: 11)),
        new(ContentIds.Workbench,
            Step(sticks: 4), Step(sticks: 2), Step(boards: 2), Step(rope: 2), Step(boards: 4)),
    };

    private static readonly FurnitureStages WardrobeStages = new(
        ContentIds.Wardrobe, Step(sticks: 4), Step(boards: 8), Step(rope: 1));

    private static readonly string[] ArchitectureModules =
    {
        "architecture.support.wood", "architecture.wall.wood",
        "architecture.window.wood", "architecture.door.wood",
        "architecture.floor.board", "architecture.roof.palm",
        "architecture.roof.palm.flat",
        ContentIds.FurnitureHearth,
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
            complete &= BuildFurnitureRow(f, row);
            row++;
        }

        foreach (var module in ArchitectureModules)
        {
            var probe = BlueprintArchitectureFactory.InstantiateModel(
                module, Vector3.zero, Quaternion.identity);
            if (probe == null)
            {
                complete = false;
                continue;
            }
            var counts = (
                BlueprintArchitectureFactory.ResourceCount(probe, 1),
                BlueprintArchitectureFactory.ResourceCount(probe, 2),
                BlueprintArchitectureFactory.ResourceCount(probe, 3));
            Destroy(probe);
            for (var column = 0; column <= 3; column++)
            {
                var built = BlueprintArchitectureFactory.InstantiateModel(
                    module, Vector3.zero, Quaternion.identity);
                if (built == null)
                {
                    complete = false;
                    continue;
                }
                var sticks = column >= 1 ? counts.Item1 : 0;
                var middle = column >= 2 ? counts.Item2 : 0;
                var rope = column >= 3 ? counts.Item3 : 0;
                BlueprintArchitectureFactory.ApplyStageProgress(built, sticks, middle, rope);
                Place(built, row, column,
                    $"{module}\nstage {column}/3: {sticks} / {middle} / {rope}");
            }
            row++;
        }

        // New stage assets show empty/frame/boards/rope. Keep a conspicuous
        // legacy column if an old published content bundle is still loaded.
        var wardrobeProbe = BedAssembly.BuildPartial(ContentIds.Wardrobe, 0, 0, 0, 0, 0, 0);
        if (wardrobeProbe == null)
        {
            complete = false;
        }
        else
        {
            var staged = HasStageParent(wardrobeProbe.transform);
            Destroy(wardrobeProbe);
            if (staged)
            {
                complete &= BuildFurnitureRow(WardrobeStages, row);
            }
            else
            {
                var wardrobe = WardrobeAssembly.BuildFinished();
                if (wardrobe == null) complete = false;
                else Place(wardrobe, row, 0,
                    "furniture.wardrobe\nНЕ РАЗЛОЖЕН ПО СТАДИЯМ");
            }
        }
        row++;

        if (!complete)
        {
            // Что-то ещё едет из бандлов — снести и собрать заново целиком.
            foreach (var go in _spawned) Destroy(go);
            _spawned.Clear();
        }
    }

    private static (int Logs, int Sticks, int Rope, int Leaves, int Stones, int Boards) Step(
        int logs = 0, int sticks = 0, int rope = 0, int leaves = 0, int stones = 0,
        int boards = 0) => (logs, sticks, rope, leaves, stones, boards);

    private static List<(int, int, int, int, int, int)> StageProgression(FurnitureStages furniture)
    {
        var stages = new List<(int, int, int, int, int, int)>
        {
            (0, 0, 0, 0, 0, 0)
        };
        var total = (Logs: 0, Sticks: 0, Rope: 0, Leaves: 0, Stones: 0, Boards: 0);
        foreach (var step in furniture.Stages)
        {
            total.Logs += step.Logs;
            total.Sticks += step.Sticks;
            total.Rope += step.Rope;
            total.Leaves += step.Leaves;
            total.Stones += step.Stones;
            total.Boards += step.Boards;
            stages.Add(total);
        }
        return stages;
    }

    private bool BuildFurnitureRow(FurnitureStages furniture, int row)
    {
        var complete = true;
        var stages = StageProgression(furniture);
        for (var column = 0; column < stages.Count; column++)
        {
            var (logs, sticks, rope, leaves, stones, boards) = stages[column];
            var built = column == stages.Count - 1
                ? BedAssembly.BuildFinished(furniture.Product)
                : BedAssembly.BuildPartial(
                    furniture.Product, logs, sticks, rope, leaves, stones, boards);
            if (built == null)
            {
                complete = false;
                continue;
            }
            Place(built, row, column,
                $"{furniture.Product}\nL{logs} S{sticks} R{rope} " +
                $"Lf{leaves} St{stones} B{boards}");
        }
        return complete;
    }

    private static bool HasStageParent(Transform root)
    {
        if (root.name.StartsWith("BuildStage_", System.StringComparison.Ordinal)) return true;
        for (var index = 0; index < root.childCount; index++)
            if (HasStageParent(root.GetChild(index))) return true;
        return false;
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
