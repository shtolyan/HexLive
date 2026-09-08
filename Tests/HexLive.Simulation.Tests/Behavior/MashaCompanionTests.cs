using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§159: fixed identity, durable narrative state and idempotent voice bond.</summary>
public sealed class MashaCompanionTests
{
    [Test]
    public void SpawnIsIdempotentAndKeepsJanaMeshMartaSkinAndJanaAuthoredDetails()
    {
        var world = TestWorld.CreateWorld(15901);

        Assert.That(MashaCompanionProfile.EnsureSpawned(world), Is.True);
        Assert.That(MashaCompanionProfile.EnsureSpawned(world), Is.False,
            "Повторный запуск опции не должен создавать вторую Машу.");

        var masha = world.Entities.Npcs[new EntityId(MashaCompanionProfile.ReservedNpcId)];
        Assert.Multiple(() =>
        {
            Assert.That(masha.ProfileId, Is.EqualTo("masha"));
            Assert.That(masha.ActorMesh, Is.EqualTo("Jana"));
            Assert.That(masha.SkinSet, Is.EqualTo("Marta"));
            Assert.That(masha.EyeColor, Is.Empty,
                "Пустое значение сохраняет глаза авторского префаба Яны.");
            Assert.That(masha.Hairstyle, Is.Empty,
                "Пустое значение сохраняет причёску авторского префаба Яны.");
            Assert.That(masha.VoiceBank, Is.EqualTo("masha"));
            Assert.That(masha.UseAuthoredAppearance, Is.True);
            Assert.That(masha.CharacterPresetVersion,
                Is.EqualTo(MashaCompanionProfile.AuthoredStarterOutfitVersion));
            Assert.That(masha.Health, Is.GreaterThan(0f));
            Assert.That(masha.Inventory.Items, Is.Not.Empty);
            Assert.That(masha.WornItems.Select(x => x.DefinitionId),
                Is.EqualTo(MashaCompanionProfile.AuthoredStarterOutfit),
                "Маша должна получить ровно утверждённый в WardrobeTest образ, без §76-ролла.");
            Assert.That(masha.WornItems.All(x => x.OwnerId == masha.Id.Value), Is.True,
                "Каждая вещь стартового образа принадлежит Маше.");
            Assert.That(masha.WornItems.Count(x => x.DefinitionId == "gear.backpack_riot"),
                Is.EqualTo(1), "Стартовый рюкзак надет ровно один раз.");
            Assert.That(masha.Inventory.Items.Any(x => x.DefinitionId == "gear.backpack_riot"),
                Is.False, "Дубликат рюкзака в карманах не создаётся.");
            Assert.That(world.Entities.Npcs.Values.Count(x => x.ProfileId == "masha"), Is.EqualTo(1));
        });
    }

    [Test]
    public void ExistingMashaReceivesApprovedOutfitOnceButLaterWardrobeChangesPersist()
    {
        const int seed = 15907;
        var world = TestWorld.CreateWorld(seed);
        MashaCompanionProfile.EnsureSpawned(world);
        var masha = world.Entities.Npcs[new EntityId(MashaCompanionProfile.ReservedNpcId)];

        // Reproduce the already-running v69 production world: Masha exists,
        // but the approved authored outfit has not been stamped yet.
        masha.CharacterPresetVersion = 0;
        masha.WornItems.Clear();
        masha.WornItems.Add(new ItemInstance("underwear.bra_riot")
        {
            OwnerId = masha.Id.Value,
            Dirtiness = 0.4f,
        });

        var legacyBlob = new MemoryStream();
        using (var writer = new BinaryWriter(
                   legacyBlob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.WriteAtVersion(world, writer, 69);

        legacyBlob.Position = 0;
        var migratedWorld = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(
                   legacyBlob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Read(migratedWorld, reader);

        masha = migratedWorld.Entities.Npcs[masha.Id];
        Assert.That(masha.CharacterPresetVersion, Is.Zero);
        Assert.That(MashaCompanionProfile.EnsureSpawned(migratedWorld), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(masha.WornItems.Select(x => x.DefinitionId),
                Is.EqualTo(MashaCompanionProfile.AuthoredStarterOutfit));
            Assert.That(masha.CharacterPresetVersion,
                Is.EqualTo(MashaCompanionProfile.AuthoredStarterOutfitVersion));
        });

        // Once stamped, normal game actions own the outfit. A restart/load may
        // not force the blouse back onto her.
        var removed = masha.WornItems[0];
        masha.WornItems.RemoveAt(0);
        masha.Inventory.Items.Add(removed);

        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(migratedWorld, writer);

        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Read(loaded, reader);

        Assert.That(MashaCompanionProfile.EnsureSpawned(loaded), Is.False);
        var restored = loaded.Entities.Npcs[masha.Id];
        Assert.Multiple(() =>
        {
            Assert.That(restored.CharacterPresetVersion,
                Is.EqualTo(MashaCompanionProfile.AuthoredStarterOutfitVersion));
            Assert.That(restored.WornItems.Any(x => x.DefinitionId == removed.DefinitionId), Is.False,
                "После одноразовой миграции обычная смена одежды не откатывается на рестарте.");
        });
    }

    [Test]
    public void PermanentSpawnMarkerSurvivesSaveAfterMashaHasLeftTheWorld()
    {
        const int seed = 15905;
        var world = TestWorld.CreateWorld(seed);
        world.MashaCompanionHasSpawned = true;

        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(world, writer);

        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Read(loaded, reader);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.MashaCompanionHasSpawned, Is.True);
            Assert.That(MashaCompanionProfile.EnsureSpawned(loaded), Is.False);
            Assert.That(loaded.Entities.Npcs.ContainsKey(
                new EntityId(MashaCompanionProfile.ReservedNpcId)), Is.False,
                "Постоянный маркер не позволяет воскресить Машу после удаления тела.");
        });
    }

    [Test]
    public void VoiceTurnChangesBondOnceAndAutonomousTurnDoesNotChangeIt()
    {
        var world = TestWorld.CreateWorld(15902);
        MashaCompanionProfile.EnsureSpawned(world);
        var masha = world.Entities.Npcs[new EntityId(MashaCompanionProfile.ReservedNpcId)];
        masha.Needs.Social = 0.40f;

        var voice = new RecordCompanionTurnCommand(
            masha.Id, "voice-1", "voice", CompanionReaction.Warm, "Проверяет совет.",
            new List<CompanionMemoryUpsert>
            {
                new("voice.prefers_honesty", "Голос предпочёл честный ответ.", 0.8f)
            },
            "Сегодня знакомый голос помог мне решиться.");

        Assert.That(ManualCommandExecutor.Apply(world, voice).Accepted, Is.True);
        Assert.That(ManualCommandExecutor.Apply(world, voice).Accepted, Is.True,
            "Повтор turnId является успешным no-op для безопасного HTTP retry.");
        Assert.Multiple(() =>
        {
            Assert.That(masha.Needs.Social, Is.EqualTo(0.58f).Within(0.0001f));
            Assert.That(masha.Companion.PlayerVoiceBond.Familiarity,
                Is.EqualTo(0.03f).Within(0.0001f));
            Assert.That(masha.Companion.PlayerVoiceBond.Trust,
                Is.EqualTo(0.03f).Within(0.0001f));
            Assert.That(masha.Companion.PlayerVoiceBond.Affinity,
                Is.EqualTo(0.02f).Within(0.0001f));
            Assert.That(masha.Companion.Memories.Any(x => x.Key == "voice.prefers_honesty"), Is.True);
            Assert.That(masha.Companion.NarrativeJournal.Count, Is.EqualTo(1));
        });

        var voiceInteractionTick = masha.Companion.PlayerVoiceBond.LastInteractionTick;
        world.Tick += 10;
        var heartbeat = new RecordCompanionTurnCommand(
            masha.Id, "heartbeat-1", "heartbeat", CompanionReaction.Hostile,
            "Осматривает берег.");
        Assert.That(ManualCommandExecutor.Apply(world, heartbeat).Accepted, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(masha.Needs.Social, Is.EqualTo(0.58f).Within(0.0001f),
                "Реакция меняет отношения только у завершённого голосового хода.");
            Assert.That(masha.Companion.PlayerVoiceBond.LastInteractionTick,
                Is.EqualTo(voiceInteractionTick),
                "Автономный heartbeat не является взаимодействием с голосом игрока.");
        });
    }

    [Test]
    public void VoiceBodyEffectChangesOnlyPhysicalSocialNeed()
    {
        var world = TestWorld.CreateWorld(15908);
        MashaCompanionProfile.EnsureSpawned(world);
        var masha = world.Entities.Npcs[new EntityId(MashaCompanionProfile.ReservedNpcId)];
        masha.Needs.Social = 0.4f;

        var bodyEffect = new RecordCompanionTurnCommand(
            masha.Id, "voice-body-1", "voice_body", CompanionReaction.Warm, string.Empty);

        Assert.That(ManualCommandExecutor.Apply(world, bodyEffect).Accepted, Is.True);
        Assert.That(ManualCommandExecutor.Apply(world, bodyEffect).Accepted, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(masha.Needs.Social, Is.EqualTo(0.58f).Within(0.0001f));
            Assert.That(masha.Companion.PlayerVoiceBond.Familiarity, Is.Zero);
            Assert.That(masha.Companion.Memories, Is.Empty);
            Assert.That(masha.Companion.NarrativeJournal, Is.Empty);
            Assert.That(masha.Companion.LastIntentSummary, Is.Empty);
        });
    }

    [Test]
    public void AppearanceMemoryJournalLanguageAndBondSurviveSaveLoad()
    {
        const int seed = 15903;
        var world = TestWorld.CreateWorld(seed);
        MashaCompanionProfile.EnsureSpawned(world);
        var original = world.Entities.Npcs[new EntityId(MashaCompanionProfile.ReservedNpcId)];
        original.Companion.HexkufaExposure = 17;
        original.Needs.Social = 0.5f;
        Assert.That(ManualCommandExecutor.Apply(world, new RecordCompanionTurnCommand(
            original.Id, "save-voice", "voice", CompanionReaction.Tense,
            "Не доверяет поспешному совету.",
            new[] { new CompanionMemoryUpsert("island.cave", "У скал есть пещера.", 0.7f) },
            "Я нашла пещеру, но голос сегодня торопил меня." )).Accepted, Is.True);

        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(world, writer);

        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Read(loaded, reader);

        var masha = loaded.Entities.Npcs[original.Id];
        Assert.Multiple(() =>
        {
            Assert.That(masha.ProfileId, Is.EqualTo("masha"));
            Assert.That(masha.ActorMesh, Is.EqualTo("Jana"));
            Assert.That(masha.SkinSet, Is.EqualTo("Marta"));
            Assert.That(masha.EyeColor, Is.Empty);
            Assert.That(masha.Hairstyle, Is.Empty);
            Assert.That(masha.VoiceBank, Is.EqualTo("masha"));
            Assert.That(masha.UseAuthoredAppearance, Is.True);
            Assert.That(masha.CharacterPresetVersion,
                Is.EqualTo(MashaCompanionProfile.AuthoredStarterOutfitVersion));
            Assert.That(masha.WornItems.Select(x => x.DefinitionId),
                Is.EqualTo(MashaCompanionProfile.AuthoredStarterOutfit));
            Assert.That(masha.WornItems.All(x => x.OwnerId == masha.Id.Value), Is.True);
            Assert.That(masha.Companion.HexkufaExposure, Is.EqualTo(17));
            Assert.That(masha.Companion.LastIntentSummary,
                Is.EqualTo("Не доверяет поспешному совету."));
            Assert.That(masha.Companion.Memories.Any(x => x.Key == "island.cave"), Is.True);
            Assert.That(masha.Companion.NarrativeJournal.Count, Is.EqualTo(1));
            Assert.That(masha.Companion.AppliedTurnIds, Does.Contain("save-voice"));
            Assert.That(masha.Companion.PlayerVoiceBond.Familiarity,
                Is.EqualTo(0.01f).Within(0.0001f));
            Assert.That(masha.Companion.PlayerVoiceBond.Trust,
                Is.EqualTo(0f).Within(0.0001f));
        });
    }

    [Test]
    public void InvalidCompanionTurnIsRejectedBeforeItMutatesState()
    {
        var world = TestWorld.CreateWorld(15904);
        MashaCompanionProfile.EnsureSpawned(world);
        var masha = world.Entities.Npcs[new EntityId(MashaCompanionProfile.ReservedNpcId)];
        var invalid = new RecordCompanionTurnCommand(
            masha.Id, "bad", "voice", CompanionReaction.Warm, "ok",
            new[] { new CompanionMemoryUpsert("", "value", 1f) });

        var admission = ManualCommandExecutor.Apply(world, invalid);

        Assert.Multiple(() =>
        {
            Assert.That(admission.Accepted, Is.False);
            Assert.That(admission.Reason, Is.EqualTo("InvalidCompanionMemory"));
            Assert.That(masha.Companion.AppliedTurnIds, Is.Empty);
            Assert.That(masha.Companion.PlayerVoiceBond.Familiarity, Is.Zero);
        });
    }

    [Test]
    public void UnknownTriggerAndOutOfRangeMemoryAreRejectedWithoutClamping()
    {
        var world = TestWorld.CreateWorld(15906);
        MashaCompanionProfile.EnsureSpawned(world);
        var masha = world.Entities.Npcs[new EntityId(MashaCompanionProfile.ReservedNpcId)];

        var unknownTrigger = ManualCommandExecutor.Apply(world, new RecordCompanionTurnCommand(
            masha.Id, "unknown-trigger", "timer", CompanionReaction.None, "Неверный источник."));
        var badImportance = ManualCommandExecutor.Apply(world, new RecordCompanionTurnCommand(
            masha.Id, "bad-importance", "heartbeat", CompanionReaction.None, "Неверный факт.",
            new[] { new CompanionMemoryUpsert("fact", "value", 1.01f) }));

        Assert.Multiple(() =>
        {
            Assert.That(unknownTrigger.Accepted, Is.False);
            Assert.That(unknownTrigger.Reason, Is.EqualTo("InvalidCompanionTurn"));
            Assert.That(badImportance.Accepted, Is.False);
            Assert.That(badImportance.Reason, Is.EqualTo("InvalidCompanionMemory"));
            Assert.That(masha.Companion.AppliedTurnIds, Is.Empty);
            Assert.That(masha.Companion.Memories, Is.Empty,
                "Личная стартовая память живёт в portable MashaCore, а не в world state.");
        });
    }
}

}
