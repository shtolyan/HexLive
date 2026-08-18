using System.IO;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §147: виртуальные мобы. Превью — чистая функция, слоты — сейв-состояние,
/// материализация — ровно в позе превью с зарезервированным id.
/// NonParallelizable: AmbientSpawner-тест временно гасит глобальный
/// WildlifeBalance.WolfSlots, а параллельная фикстура с реплеем реального
/// сида увидела бы мир без слотов и разъехалась (пойман флейком жилетки).
/// </summary>
[NonParallelizable]
public sealed class MobSlotTests
{
    private static WorldState BigIsland(int seed = 12345) =>
        new WorldStateFactory().Create(
            PrototypeWorldDefinitionFactory.Create(seed, GameMode.BigIsland));

    private static void RunMedium(WorldState world, int mediumPasses)
    {
        var mob = new MobSystem();
        var rabbit = new RabbitSystem();
        for (var i = 0; i < mediumPasses; i++)
        {
            world.Tick += 4;
            mob.Run(world);
            rabbit.Run(world);
        }
    }

    [Test]
    public void PreviewPose_IsDeterministic_AndWalksTheRing()
    {
        var world = BigIsland();
        RunMedium(world, 1);
        var slot = world.MobSpawnSlots.First(s => s.MobId == MobIds.Dog);
        Assert.That(slot.Ring, Has.Count.GreaterThanOrEqualTo(2));

        for (var tick = 0; tick < 200; tick += 7)
        {
            MobPreview.PreviewPose(world.Seed, slot, tick,
                WildlifeBalance.MobPreviewSegmentTicks,
                WildlifeBalance.MobPreviewPauseChance,
                out var p1, out var f1, out var n1);
            MobPreview.PreviewPose(world.Seed, slot, tick,
                WildlifeBalance.MobPreviewSegmentTicks,
                WildlifeBalance.MobPreviewPauseChance,
                out var p2, out var f2, out var n2);
            Assert.Multiple(() =>
            {
                Assert.That(p1.X, Is.EqualTo(p2.X), "детерминизм позы");
                Assert.That(p1.Y, Is.EqualTo(p2.Y));
                Assert.That(f1.X, Is.EqualTo(f2.X), "детерминизм поворота");
                Assert.That(n1, Is.EqualTo(n2));
            });
            Assert.That(n1, Is.InRange(0, slot.Ring.Count - 1));
        }
    }

    [Test]
    public void SlotsGenerateLazily_AndReplaceTheAmbientSpawner()
    {
        var world = BigIsland();
        Assert.That(world.Mobs, Is.Empty, "мир рождается без живых волков");

        RunMedium(world, 1);
        Assert.Multiple(() =>
        {
            Assert.That(world.MobSpawnSlots.Count(s => s.MobId == MobIds.Dog),
                Is.EqualTo(WildlifeBalance.BigIslandWolfSlots));
            Assert.That(world.MobSpawnSlots.Count(s => s.MobId == MobIds.Crab),
                Is.EqualTo(WildlifeBalance.BigIslandCrabSlots));
            Assert.That(world.MobSpawnSlots.Count(s => s.MobId == MobIds.Shark),
                Is.EqualTo(WildlifeBalance.BigIslandSharkSlots),
                "акульи слоты — превью навсегда (§147.6)");
            // Слоты рождаются вдали от людей (радиус материализации 8 <
            // дистанция спавна от лагеря 9), поэтому на первом тике никто
            // не материализован и амбиентного спавна нет.
            Assert.That(world.Mobs, Is.Empty,
                "слотовый режим не создаёт MobState, пока никто не подошёл");
        });
    }

    [Test]
    public void ApproachMaterializes_AtThePreviewPose_WithTheReservedId()
    {
        var world = BigIsland();
        RunMedium(world, 1);
        var slot = world.MobSpawnSlots.First(s => s.MobId == MobIds.Dog);

        // Телепортируем девушку прямо к дому слота — на следующем medium-тике
        // волк обязан стать настоящим, ровно с зарезервированным id.
        var girl = world.Entities.Npcs.Values.First();
        girl.Tile = slot.Ring[0].Tile;

        RunMedium(world, 1);
        Assert.Multiple(() =>
        {
            Assert.That(slot.State, Is.EqualTo(MobSlotState.Live));
            Assert.That(world.Mobs.Any(m => m.Id == slot.ReservedMobId),
                "живой волк с ReservedMobId — вью превью подхватывается без пересоздания");
        });

        var dog = world.Mobs.First(m => m.Id == slot.ReservedMobId);
        MobPreview.PreviewPose(world.Seed, slot, world.Tick,
            WildlifeBalance.MobPreviewSegmentTicks,
            WildlifeBalance.MobPreviewPauseChance,
            out _, out _, out var nearest);
        Assert.That(slot.Ring.Any(w => w.Junction.Equals(dog.Junction)),
            "материализация — на вэйпоинте собственного кольца");

        // Смерть → кулдаун → слот больше не Live.
        dog.Health = 0f;
        RunMedium(world, 1);
        Assert.Multiple(() =>
        {
            Assert.That(world.Mobs.Any(m => m.Id == slot.ReservedMobId), Is.False);
            Assert.That(slot.State, Is.EqualTo(MobSlotState.Cooldown));
            Assert.That(slot.CooldownUntilTick, Is.GreaterThan(world.Tick));
            Assert.That(slot.CycleIndex, Is.EqualTo(1));
        });
    }

    [Test]
    public void SlotsSurviveSaveLoad_RingsIncluded()
    {
        var world = BigIsland();
        RunMedium(world, 1);
        var wolves = world.MobSpawnSlots.Where(s => s.MobId == MobIds.Dog).ToList();
        Assert.That(wolves, Is.Not.Empty);

        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        blob.Position = 0;
        var loaded = BigIsland();
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        Assert.That(loaded.MobSpawnSlots.Count, Is.EqualTo(world.MobSpawnSlots.Count));
        for (var i = 0; i < world.MobSpawnSlots.Count; i++)
        {
            var a = world.MobSpawnSlots[i];
            var b = loaded.MobSpawnSlots[i];
            Assert.Multiple(() =>
            {
                Assert.That(b.SlotId, Is.EqualTo(a.SlotId));
                Assert.That(b.MobId, Is.EqualTo(a.MobId));
                Assert.That(b.State, Is.EqualTo(a.State));
                Assert.That(b.ReservedMobId, Is.EqualTo(a.ReservedMobId));
                Assert.That(b.HomeJunction, Is.EqualTo(a.HomeJunction));
                Assert.That(b.CycleIndex, Is.EqualTo(a.CycleIndex));
                Assert.That(b.Ring.Count, Is.EqualTo(a.Ring.Count),
                    "кольцо сохраняется, не перепекается (§147.1)");
            });
            for (var j = 0; j < a.Ring.Count; j++)
            {
                Assert.That(b.Ring[j].Junction, Is.EqualTo(a.Ring[j].Junction));
                Assert.That(b.Ring[j].Position.X, Is.EqualTo(a.Ring[j].Position.X));
            }
        }
    }

    [Test]
    public void FeudDefaultsRunTheSlotSpawnerToo()
    {
        // ⭐ §147.6: включено в ОБОИХ режимах (решение игрока) — прежние
        // популяции Feud стали слотами; эталон трейсов принят заново.
        Assert.Multiple(() =>
        {
            Assert.That(WildlifeBalance.WolfSlots, Is.EqualTo(2));
            Assert.That(WildlifeBalance.CrabSlots, Is.EqualTo(4));
            Assert.That(WildlifeBalance.SharkSlots, Is.EqualTo(2));
        });

        var world = TestWorld.CreateWorld();
        world.Tick = WildlifeBalance.DogRespawnCheckTicks + 4;
        new MobSystem().Run(world);
        new RabbitSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(world.MobSpawnSlots.Count(s => s.MobId == MobIds.Dog),
                Is.EqualTo(WildlifeBalance.WolfSlots), "волчьи слоты в Feud");
            Assert.That(world.Mobs, Is.Empty,
                "амбиентный спавнер заменён: волк существует только у людей");
        });
    }

    [Test]
    public void AmbientSpawnerStillWorksWhenSlotsAreOff()
    {
        var oldWolves = WildlifeBalance.WolfSlots;
        try
        {
            WildlifeBalance.WolfSlots = 0;
            var world = TestWorld.CreateWorld();
            world.Tick = WildlifeBalance.DogRespawnCheckTicks + 4;
            new MobSystem().Run(world);
            Assert.Multiple(() =>
            {
                Assert.That(world.MobSpawnSlots.Count(s => s.MobId == MobIds.Dog),
                    Is.EqualTo(0), "0 = механика выключена целиком");
                Assert.That(world.Mobs, Is.Not.Empty, "амбиентный спавнер жив");
            });
        }
        finally
        {
            WildlifeBalance.WolfSlots = oldWolves;
        }
    }
}

}
