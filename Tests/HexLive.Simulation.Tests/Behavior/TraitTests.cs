using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §126: черты характера — ролл, взаимное исключение полюсов, сейв и провод.
///
/// <para>
/// Ради чего файл существует: до §126 «гнобит» (§81) и «не моется» (§89) были
/// свойствами бита <c>Faction</c>. Черта, которая переезжает вместе с фракцией,
/// это мина: колонистка не могла быть неряхой в принципе, а любой будущий плен
/// или вербовка перенесли бы поведение не туда. Здесь стерегут три вещи, каждая
/// из которых один раз уже ломалась в соседних системах:
/// </para>
/// <list type="bullet">
/// <item>ролл ДЕТЕРМИНИРОВАН по сиду — иначе реплей расходится;</item>
/// <item>полюса пары не выпадают ВМЕСТЕ — «неряха-чистюля» это не характер;</item>
/// <item>старый сейв возвращает чужаку его характер, а не разоружает его молча.</item>
/// </list>
/// </summary>
public sealed class TraitTests
{
    private sealed class Rarities
    {
        public bool Enabled;
        public float Neat, Lazy, Diligent, Coward, Brave, Sleepy;
    }

    private Rarities _saved;

    [SetUp]
    public void SaveRarities() => _saved = new Rarities
    {
        Enabled = SpecTraits.Enabled,
        Neat = SpecTraits.NeatChance,
        Lazy = SpecTraits.LazyChance,
        Diligent = SpecTraits.DiligentChance,
        Coward = SpecTraits.CowardChance,
        Brave = SpecTraits.BraveChance,
        Sleepy = SpecTraits.SleepyChance
    };

    [TearDown]
    public void RestoreRarities()
    {
        SpecTraits.Enabled = _saved.Enabled;
        SpecTraits.NeatChance = _saved.Neat;
        SpecTraits.LazyChance = _saved.Lazy;
        SpecTraits.DiligentChance = _saved.Diligent;
        SpecTraits.CowardChance = _saved.Coward;
        SpecTraits.BraveChance = _saved.Brave;
        SpecTraits.SleepyChance = _saved.Sleepy;
    }

    private static NPCState RollOne(int seed, int npcId)
    {
        var npc = new NPCState { Id = new EntityId(npcId) };
        TraitMath.Roll(npc, seed, npcId);
        return npc;
    }

    [Test]
    public void RollIsDeterministicOnSeedAndId()
    {
        SpecTraits.Enabled = true;
        SpecTraits.NeatChance = 0.3f;
        SpecTraits.LazyChance = 0.3f;
        SpecTraits.SleepyChance = 0.4f;

        for (var id = 1; id <= 200; id++)
        {
            Assert.That(RollOne(4242, id).Traits.Bits,
                Is.EqualTo(RollOne(4242, id).Traits.Bits),
                $"Ролл черт разошёлся на одном и том же сиде и id={id} — реплей " +
                "и сервер разъедутся с клиентом на первой же перезагрузке.");
        }
    }

    [Test]
    public void OppositePolesNeverLandTogether()
    {
        SpecTraits.Enabled = true;
        // Полосы вплотную и почти на всю единицу: если бы полюса катились
        // ДВУМЯ бросками, пересечение здесь было бы почти неизбежным.
        SpecTraits.NeatChance = 0.5f;
        SpecTraits.LazyChance = 0.49f;
        SpecTraits.DiligentChance = 0.49f;
        SpecTraits.CowardChance = 0.45f;
        SpecTraits.BraveChance = 0.45f;

        for (var id = 1; id <= 10000; id++)
        {
            var traits = RollOne(770077, id).Traits;
            Assert.That(traits.Has(TraitKind.Lazy) && traits.Has(TraitKind.Diligent),
                Is.False, $"id={id}: лентяйка И трудяга разом.");
            Assert.That(traits.Has(TraitKind.Coward) && traits.Has(TraitKind.Brave),
                Is.False, $"id={id}: трусиха И храбрая разом.");
        }
    }

    [Test]
    public void DisabledMeansNobodyHasATrait()
    {
        SpecTraits.Enabled = false;
        SpecTraits.NeatChance = 1f;
        SpecTraits.SleepyChance = 1f;

        for (var id = 1; id <= 50; id++)
        {
            Assert.That(RollOne(1, id).Traits.Bits, Is.EqualTo(0UL),
                "Выключатель §126 обязан давать пустой набор: пустой набор и " +
                "есть игра до §126 (каждый гейт спрашивает Has()).");
        }
    }

    [Test]
    public void EveryTraitChangesSomething()
    {
        // ⭐ Черта, которая ничего не меняет, — это бейдж на листе персонажа и
        // ничего больше. Каждый хелпер обязан отвечать на «есть черта» иначе,
        // чем на «нет черты»; иначе однажды выкатится «Трудяга», которая
        // работает ровно как все, и разбираться в этом будет некому.
        var plain = new NPCState { Id = new EntityId(1) };

        var neat = new NPCState { Id = new EntityId(2) };
        neat.Traits.Add(TraitKind.Neat);
        Assert.That(TraitMath.GroomingThresholdMult(neat),
            Is.LessThan(TraitMath.GroomingThresholdMult(plain)),
            "Чистюле планка грязи не снижена — она моется как все.");

        var diligent = new NPCState { Id = new EntityId(3) };
        diligent.Traits.Add(TraitKind.Diligent);
        var lazy = new NPCState { Id = new EntityId(4) };
        lazy.Traits.Add(TraitKind.Lazy);
        Assert.That(TraitMath.IndustryMult(diligent),
            Is.GreaterThan(TraitMath.IndustryMult(plain)),
            "Трудяга берётся за дело не охотнее обычной.");
        Assert.That(TraitMath.IndustryMult(lazy),
            Is.LessThan(TraitMath.IndustryMult(plain)),
            "Лентяйка берётся за дело не реже обычной.");
        Assert.That(TraitMath.LeisureBonus(lazy),
            Is.GreaterThan(TraitMath.LeisureBonus(plain)),
            "Лентяйке некуда деваться вместо дела — «меньше работает» без " +
            "«больше отдыхает» это просто медленнее живущий человек.");

        var brave = new NPCState { Id = new EntityId(5) };
        brave.Traits.Add(TraitKind.Brave);
        Assert.That(TraitMath.FitBoneHealth(brave),
            Is.LessThan(TraitMath.FitBoneHealth(plain)),
            "Храбрая принимает бой не раньше обычной.");
        // ⭐ Про размер стаи здесь НЕТ проверки, и это намеренно: у Храброй нет
        // и не должно быть надбавки к нему. §62 «первой бьём ТОЛЬКО одиночку»
        // это правило выживания, а не порог храбрости; черта, которая его
        // отменяла, стоила колонии всех четверых на сиде 424242.

        var coward = new NPCState { Id = new EntityId(6) };
        coward.Traits.Add(TraitKind.Coward);
        Assert.That(TraitMath.DangerStepCost(coward),
            Is.GreaterThan(TraitMath.DangerStepCost(plain)),
            "Трусиха даёт такой же крюк, как все, — значит черта пустая.");

        var sleepy = new NPCState { Id = new EntityId(7) };
        sleepy.Traits.Add(TraitKind.Sleepyhead);
        Assert.That(TraitMath.EffectiveSleepThreshold(sleepy),
            Is.GreaterThan(TraitMath.EffectiveSleepThreshold(plain)),
            "Соня ложится не раньше обычной.");
    }

    [Test]
    public void ZeroRarityRollsNothingButAuthoredTraitsSurvive()
    {
        // Нули на всех осях: колония выходит без единой черты, а чужак всё
        // равно остаётся собой — его характер АВТОРСКИЙ, а не выпавший.
        SpecTraits.Enabled = true;
        SpecTraits.NeatChance = 0f;
        SpecTraits.LazyChance = 0f;
        SpecTraits.DiligentChance = 0f;
        SpecTraits.CowardChance = 0f;
        SpecTraits.BraveChance = 0f;
        SpecTraits.SleepyChance = 0f;

        var world = TestWorld.CreateWorld();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Faction == Faction.Colony)
            {
                Assert.That(npc.Traits.Bits, Is.EqualTo(0UL),
                    $"NPC{npc.Id.Value}: черта выпала колонистке при нулевых " +
                    "рарностях — значит ролл читает не те ручки.");
            }
            else
            {
                Assert.That(npc.Traits.Has(TraitKind.Abuser), Is.True,
                    "Чужак потерял черту абьюзера — §81 замолчит целиком.");
                Assert.That(npc.Traits.Has(TraitKind.Slob), Is.True,
                    "Чужак потерял черту неряхи — пойдёт полоскать рубаху (§89).");
            }
        }
    }

    [Test]
    public void AuthoredTraitsReplaceTheRollEntirely()
    {
        SpecTraits.Enabled = true;
        SpecTraits.NeatChance = 1f;

        var definition = new WorldBootstrapDefinition();
        var fragment = new FragmentBootstrap { Id = 1 };
        fragment.Tiles.Add(new TileBootstrap { Q = 0, R = 0, Walkable = true });
        definition.Fragments.Add(fragment);
        definition.Npcs.Add(new NpcBootstrap
        {
            Id = 1, FragmentId = 1, TileQ = 0, TileR = 0,
            // Пустой список — это «автор сказал: черт нет», а не «роллить».
            Traits = new List<string>()
        });
        definition.Npcs.Add(new NpcBootstrap
        {
            Id = 2, FragmentId = 1, TileQ = 0, TileR = 0,
            Traits = new List<string> { "Brave", "нетаканойчерты" }
        });

        var world = new WorldStateFactory().Create(definition);

        Assert.That(world.Entities.Npcs[new EntityId(1)].Traits.Bits, Is.EqualTo(0UL),
            "Авторский ПУСТОЙ список обязан отменять ролл — иначе «дайте мне " +
            "заведомо безликую для контроля» невыразимо.");

        var second = world.Entities.Npcs[new EntityId(2)].Traits;
        Assert.That(second.Has(TraitKind.Brave), Is.True);
        Assert.That(second.Has(TraitKind.Neat), Is.False,
            "Авторский список ЗАМЕНЯЕТ ролл, а не дополняет его.");
        Assert.That(second.Count, Is.EqualTo(1),
            "Незнакомое имя черты обязано быть отброшено, а не угадано.");
    }

    [Test]
    public void SaveRoundTripKeepsEveryTrait()
    {
        var world = TestWorld.CreateWorld();
        var subject = world.Entities.Npcs.Values.First();
        subject.Traits.Clear();
        subject.Traits.Add(TraitKind.Neat);
        subject.Traits.Add(TraitKind.Sleepyhead);
        subject.Traits.Add(TraitKind.Brave);

        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        blob.Position = 0;
        var loaded = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        var restored = loaded.Entities.Npcs[subject.Id].Traits;
        Assert.That(restored.Bits, Is.EqualTo(subject.Traits.Bits),
            "Черты не пережили сейв — характер обязан быть состоянием мира, а " +
            "не свойством запущенной сессии.");
    }

    [Test]
    public void TraitsReachThePanelThroughTheWire()
    {
        var world = TestWorld.CreateWorld();
        var subject = world.Entities.Npcs.Values.First();
        subject.Traits.Clear();
        subject.Traits.Add(TraitKind.Abuser);
        subject.Traits.Add(TraitKind.Lazy);

        var snapshot = WorldSnapshotExporter.Export(world);
        var exported = snapshot.Npcs.First(n => n.Id.Value == subject.Id.Value);
        Assert.That(exported.Traits,
            Is.EquivalentTo(new[] { "trait.abuser", "trait.lazy" }),
            "Экспортёр обязан отдавать ГОТОВЫЕ ключи локализации: вид, который " +
            "вывел бы черты сам (например из фракции, как было до §126), " +
            "разошёлся бы с симуляцией на первой же правке ролла.");

        var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSnapshotCodec.Write(snapshot, writer, includeDebugDetails: false);
        }

        bytes.Position = 0;
        var decoded = new WorldSnapshot();
        using (var reader = new BinaryReader(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSnapshotCodec.Read(reader, decoded);
        }

        Assert.That(decoded.Npcs.First(n => n.Id.Value == subject.Id.Value).Traits,
            Is.EquivalentTo(exported.Traits),
            "Черты не доехали по проводу — на сервере вкладка «Характер» пуста.");
    }

    [Test]
    public void PreTraitSaveGivesTheOutsiderHisCharacterBack()
    {
        // Блоб v35 черт не знает, и загрузка идёт через ЭТОТ шов. Игра, которая
        // его записала, гнобила и не мылась по ФРАКЦИИ — значит вернуть надо
        // ровно это, а не мирного чужака, который ходит стирать рубаху.
        var stranger = new NPCState
        {
            Id = new EntityId(101), Faction = Faction.Outsiders
        };
        TraitMath.ApplyPreTraitDefaults(stranger);

        Assert.That(stranger.Traits.Has(TraitKind.Abuser), Is.True,
            "Старый сейв разоружил чужака: §81 читает черту, а её никто не выдал.");
        Assert.That(stranger.Traits.Has(TraitKind.Slob), Is.True,
            "Старый сейв отправил чужака стираться (§89).");

        var girl = new NPCState { Id = new EntityId(1), Faction = Faction.Colony };
        TraitMath.ApplyPreTraitDefaults(girl);
        Assert.That(girl.Traits.Bits, Is.EqualTo(0UL),
            "Миграция раздала черты СВОИМ — старый мир вдруг получил бы " +
            "колонистку, которая гнобит и не моется.");
    }

    [Test]
    public void AbuserAndSlob_AreNeverRolled_AtAnySetting()
    {
        // ⭐ «В нашем лагере грязнуль и абьюзеров не бывает» — правило, а не
        // выставленный ноль. Ролла для этих двух черт нет вовсе: они достаются
        // ТОЛЬКО авторски, дикарям чужого клана. Ручки «шанс абьюзера» нет и
        // быть не должно — она была бы не настройкой, а дверью, за которой
        // колония однажды начнёт грызть себя без единой строки кода.
        SpecTraits.Enabled = true;
        SpecTraits.NeatChance = 1f;
        SpecTraits.LazyChance = 0.5f;
        SpecTraits.DiligentChance = 0.5f;
        SpecTraits.CowardChance = 0.5f;
        SpecTraits.BraveChance = 0.5f;
        SpecTraits.SleepyChance = 1f;

        for (var id = 1; id <= 5000; id++)
        {
            var traits = RollOne(31337, id).Traits;
            Assert.That(traits.Has(TraitKind.Abuser), Is.False,
                $"id={id}: абьюзер ВЫКАТИЛСЯ. Черта обязана быть авторской.");
            Assert.That(traits.Has(TraitKind.Slob), Is.False,
                $"id={id}: неряха ВЫКАТИЛАСЬ. Черта обязана быть авторской.");
        }
    }

    [Test]
    public void Sleepyhead_GoesToBedEarlierThanEverybodyElse()
    {
        var ordinary = new NPCState { Id = new EntityId(1) };
        var sleepy = new NPCState { Id = new EntityId(2) };
        sleepy.Traits.Add(TraitKind.Sleepyhead);

        var plain = TraitMath.EffectiveSleepThreshold(ordinary);
        var early = TraitMath.EffectiveSleepThreshold(sleepy);

        Assert.That(plain, Is.EqualTo(SimBalance.SleepEnergyThreshold).Within(0.0001f),
            "Человек без черты обязан читать ровно общий порог — иначе черты " +
            "стали бы налогом на всех остальных.");
        Assert.That(early, Is.GreaterThan(plain),
            "Соня ложится не раньше обычной — черта ничего не делает.");
        Assert.That(early, Is.EqualTo(
                SimBalance.SleepEnergyThreshold + SpecTraits.SleepyThresholdBonus).Within(0.0001f));
    }

    [Test]
    public void TiredEnoughIsTheWholeCondition()
    {
        // §49 r2 «хочешь спать — спи»: порог сна щедрый настолько, чтобы лечь
        // можно было ЗАДОЛГО до обморока. Раньше он стоял на 0.203 — лечь
        // нельзя было до 80% истощения, и весь ночной затвор с часами
        // существовал только затем, чтобы обойти этот запрет.
        Assert.That(SimBalance.SleepEnergyThreshold,
            Is.GreaterThan(Spec49.DeadTiredEnergy * 2f),
            "Порог сна снова прижат к обмороку — значит «хочешь спать — спи» " +
            "отменили, и ночной костыль вернётся следующим коммитом.");

        // И сон кончается по ЭНЕРГИИ, а не по часам — одной чертой для всех.
        Assert.That(Spec49.NightSleepWakeEnergy, Is.GreaterThan(0.9f),
            "Сон обязан идти до полного бара: просыпаться на дневной черте " +
            "значило вставать всё ещё уставшей.");
    }

    [Test]
    public void SaveWritesTheCurrentBlobVersionForTraits()
    {
        // Хвостовое поле §126 читается за `version >= 36`. Если версию забыли
        // поднять, запись и чтение расходятся молча — и мир грузится с
        // мусорными чертами вместо ошибки.
        Assert.That(WorldSaveSerializer.BlobVersion, Is.GreaterThanOrEqualTo(36),
            "Черты пишутся в хвост NPC-записи, а версия блоба ниже 36.");
    }
}

}
