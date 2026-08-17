using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §144.8: описание мира ужимается — и обязано доказать, что ничего не выдумало.
/// <para>
/// Замер, из которого выросло сжатие: живая сводка колонистки весила 30–88 КБ
/// одной строкой, до 350 объектов, из них 176 пальмовых листьев поодиночке.
/// Платили оба читателя — и внешний агент, и LLM-контур §32.15.
/// </para>
/// </summary>
public sealed class LlmContextBudgetGateTests
{
    [Test]
    public void UnboundedReproducesTheOldTextByteForByte()
    {
        var (world, npc) = Scene();

        var unbounded = LlmDecisionContextBuilder.Build(world, npc, ContextBudget.Unbounded);

        // Прежняя форма: каждый объект отдельной строкой со своим id, никаких
        // count= и никаких omitted=. Это опора всей правки — пока она держит,
        // видно, что сжатие только СКРЫВАЕТ повторы, а не переписывает мир.
        Assert.Multiple(() =>
        {
            Assert.That(unbounded.PerceptionSummary, Does.Not.Contain("count="));
            Assert.That(unbounded.PerceptionSummary, Does.Not.Contain("omitted="));
            Assert.That(unbounded.MemorySummary, Does.Not.Contain("omitted="));
            for (var i = 0; i < 40; i++)
            {
                Assert.That(unbounded.PerceptionSummary, Does.Contain($"{{id={100 + i},"),
                    "безлимитный режим обязан показать КАЖДЫЙ экземпляр");
            }
        });
    }

    [Test]
    public void DefaultCollapsesRepeatsAndStaysSmall()
    {
        var (world, npc) = Scene();

        var unbounded = LlmDecisionContextBuilder.Build(world, npc, ContextBudget.Unbounded);
        var packed = LlmDecisionContextBuilder.Build(world, npc, ContextBudget.Default);

        Assert.Multiple(() =>
        {
            Assert.That(packed.PerceptionSummary.Length,
                Is.LessThan(unbounded.PerceptionSummary.Length / 2));
            Assert.That(packed.PerceptionSummary, Does.Contain("definition=resource.palm_leaf"));
            Assert.That(packed.PerceptionSummary, Does.Contain("count=40"),
                "сорок листьев обязаны стать одной строкой с честным счётом");
        });
    }

    [Test]
    public void CollapsedRowStillOffersIdsToActOn()
    {
        var (world, npc) = Scene();

        var packed = LlmDecisionContextBuilder.Build(world, npc, ContextBudget.Default);

        // Схлопнуть до «их 40» и не дать ни одного id значило бы сказать
        // «вокруг полно листьев, но взять нельзя ни один»: действовать агент
        // умеет только по id.
        Assert.That(packed.PerceptionSummary, Does.Contain("nearest=[{id="));
        Assert.That(packed.PerceptionSummary, Does.Contain("{id=100,"),
            "ближайший экземпляр обязан быть назван поимённо");
    }

    [Test]
    public void TruncationIsAnElementNotSilence()
    {
        var (world, npc) = Scene();

        // Узкий бюджет: два класса объектов из многих.
        var tight = new ContextBudget(
            maxObjectRows: 2, maxAgentRows: 2, maxKnownObjectRows: 2,
            maxKnownAgentRows: 2, maxDangerRows: 1, nearestPerDefinition: 1,
            aggregate: true);

        var packed = LlmDecisionContextBuilder.Build(world, npc, tight);

        // Молча короткий список читается как «этого рядом нет» — и агент уходит
        // искать то, что лежит у него под ногами.
        Assert.That(packed.PerceptionSummary, Does.Contain("{omitted="));
        Assert.That(packed.MemorySummary, Does.Contain("{omitted="));
    }

    [Test]
    public void EmptyListsKeepTheirLabels()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Perception.Objects.Clear();
        npc.Perception.Agents.Clear();
        npc.Perception.Hostiles.Clear();
        npc.Memory.Dangers.Clear();

        var packed = LlmDecisionContextBuilder.Build(world, npc, ContextBudget.Default);

        // ⭐ Связь, которую легко «прибрать» и тем сломать: MockLlmControlProvider
        // ищет в этих строках подстроку "danger", и её сегодня даёт сам ключ
        // пустого списка. Спрячь ключ при пустоте — и тестовый двойник §32.15
        // молча начнёт отвечать противоположное.
        Assert.Multiple(() =>
        {
            Assert.That(packed.MemorySummary, Does.Contain("dangers=["));
            Assert.That(packed.PerceptionSummary, Does.Contain("objects=["));
            Assert.That(packed.PerceptionSummary, Does.Contain("allies=["));
            Assert.That(packed.PerceptionSummary, Does.Contain("hostiles=["));
            Assert.That(packed.PerceptionSummary, Does.Contain("mobs=["));
        });
    }

    [Test]
    public void BuildingAContextNeverReordersTheLiveLists()
    {
        var (world, npc) = Scene();
        var firstBefore = npc.Perception.Objects[0].Id;

        LlmDecisionContextBuilder.Build(world, npc, ContextBudget.Default);

        Assert.That(npc.Perception.Objects[0].Id, Is.EqualTo(firstBefore),
            "сборка описания — чтение; тронув живой список, она изменила бы мир");
    }

    /// <summary>Сцена с повторами: сорок листьев и по одному прочему.</summary>
    private static (WorldState World, NPCState Npc) Scene()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

        npc.Perception.Objects.Clear();
        for (var i = 0; i < 40; i++)
        {
            npc.Perception.Objects.Add(new PerceivedObject
            {
                Id = new ObjectId(100 + i),
                DefinitionId = "resource.palm_leaf",
                Distance = 1f + i,
                IsReachable = true,
            });
        }

        foreach (var definition in new[] { "food.coconut", "campfire.spot", "bed.basic" })
        {
            npc.Perception.Objects.Add(new PerceivedObject
            {
                Id = new ObjectId(900 + npc.Perception.Objects.Count),
                DefinitionId = definition,
                Distance = 2f,
                IsReachable = true,
            });
        }

        npc.Memory.KnownObjects.Clear();
        for (var i = 0; i < 40; i++)
        {
            var id = new ObjectId(500 + i);
            npc.Memory.KnownObjects[id] = new ObjectMemory
            {
                Id = id,
                DefinitionId = "resource.stone",
                Tile = new TileCoord(i, 0),
                LastSeenTick = i,
            };
        }

        npc.Memory.Dangers.Clear();
        for (var i = 0; i < 12; i++)
        {
            npc.Memory.Dangers.Add(new DangerMemory
            {
                Tile = new TileCoord(-i, i),
                Tick = i * 10,
            });
        }

        return (world, npc);
    }
}

}
