using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using HexLive.Server;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests
{

/// <summary>§149: assignment is durable ownership; ControlLeases only protect
/// the currently active command stream.</summary>
[NonParallelizable]
public sealed class PlayerCharacterAssignmentsTests
{
    private string _directory = null!;
    private string _path = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "hexlive-player-assignments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "hexlive-players.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void FirstFreeIsStableAcrossReconnectAndDistinctForAnotherPlayer()
    {
        var assignments = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);
        var firstPlayer = Id(1);
        var secondPlayer = Id(2);

        Assert.Multiple(() =>
        {
            Assert.That(assignments.Reconcile(firstPlayer, new[] { 1, 2 }, new[] { 1, 2 }),
                Is.EqualTo(new[] { 1 }));
            Assert.That(assignments.Reconcile(secondPlayer, new[] { 1, 2 }, new[] { 1, 2 }),
                Is.EqualTo(new[] { 2 }));
            Assert.That(assignments.Reconcile(firstPlayer, new[] { 1, 2 }, new[] { 1, 2 }),
                Is.EqualTo(new[] { 1 }),
                "Reconnect must restore the old character, not take the next free one.");
        });
    }

    [Test]
    public void SimultaneousFirstConnectionsNeverReceiveTheSameCharacter()
    {
        var assignments = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);

        var first = Task.Run(() => assignments.Reconcile(
            Id(21), new[] { 1, 2 }, new[] { 1, 2 }));
        var second = Task.Run(() => assignments.Reconcile(
            Id(22), new[] { 1, 2 }, new[] { 1, 2 }));
        Task.WaitAll(first, second);

        Assert.That(new[] { first.Result[0], second.Result[0] },
            Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void DeadCharacterIsReplacedByTheFirstFreeOneOnNextConnect()
    {
        var assignments = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);
        var returningPlayer = Id(1);
        var otherPlayer = Id(2);
        assignments.Reconcile(returningPlayer, new[] { 1, 2, 3 }, new[] { 1, 2, 3 });
        assignments.Reconcile(otherPlayer, new[] { 1, 2, 3 }, new[] { 1, 2, 3 });

        var replacement = assignments.Reconcile(
            returningPlayer,
            retainableNpcIds: new[] { 2, 3 },
            assignableNpcIds: new[] { 2, 3 });

        Assert.That(replacement, Is.EqualTo(new[] { 3 }),
            "NPC1 died, NPC2 is reserved by another player, so NPC3 is first free.");
    }

    [Test]
    public void AssignmentSurvivesServerRestart()
    {
        var player = Id(7);
        var firstProcess = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);
        firstProcess.Reconcile(player, new[] { 11, 12 }, new[] { 11, 12 });

        var restarted = PlayerCharacterAssignments.Load(_path, continueExistingWorld: true);

        Assert.That(restarted.Reconcile(player, new[] { 11, 12 }, new[] { 11, 12 }),
            Is.EqualTo(new[] { 11 }));
    }

    [Test]
    public void FreshWorldClearsAStaleSidecar()
    {
        var player = Id(9);
        var oldWorld = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);
        oldWorld.Reconcile(player, new[] { 41 }, new[] { 41 });

        var freshWorld = PlayerCharacterAssignments.Load(_path, continueExistingWorld: false);

        Assert.That(freshWorld.Reconcile(player, new[] { 51 }, new[] { 51 }),
            Is.EqualTo(new[] { 51 }));
    }

    [Test]
    public void ListContractSupportsTwoCharactersAndCurrentSingleCharacterLimit()
    {
        var twoPerCamp = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "big-island.json"), continueExistingWorld: false);
        var onePerCamp = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "huge-island.json"), continueExistingWorld: false);

        Assert.Multiple(() =>
        {
            Assert.That(twoPerCamp.Reconcile(
                    Id(1),
                    retainableNpcIds: new[] { 1, 2 },
                    assignableNpcIds: new[] { 1, 2 },
                    characterLimit: 2),
                Is.EqualTo(new[] { 1, 2 }),
                "BigIsland's player camp has NPC1/NPC2; the future squad limit owns both.");
            Assert.That(onePerCamp.Reconcile(
                    Id(2),
                    retainableNpcIds: new[] { 1 },
                    assignableNpcIds: new[] { 1 },
                    characterLimit: 1),
                Is.EqualTo(new[] { 1 }),
                "HugeIsland/Maniac start with one NPC in the player camp.");
        });
    }

    [Test]
    public void NonCanonicalClientIdCannotBecomeAPersistentPlayer()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PlayerCharacterAssignments.TryNormalizePlayerId("not-a-guid", out _), Is.False);
            Assert.That(PlayerCharacterAssignments.TryNormalizePlayerId(Id(5), out var canonical), Is.True);
            Assert.That(canonical, Is.EqualTo(Id(5)));
        });
    }

    [Test]
    public void ServerRejectsAnUnassignedActorAndTheWholeMixedGroup()
    {
        var assigned = new HashSet<int> { 1, 2 };

        Assert.Multiple(() =>
        {
            Assert.That(PlayerCommandAssignment.Allows(
                new SetManualControlCommand(new EntityId(1), true), assigned), Is.True);
            Assert.That(PlayerCommandAssignment.Allows(
                new SetManualControlCommand(new EntityId(3), true), assigned), Is.False);
            Assert.That(PlayerCommandAssignment.Allows(
                new SetGroupManualControlCommand(
                    new[] { new EntityId(1), new EntityId(3) }, true), assigned), Is.False,
                "A mixed group must be rejected atomically, not partially executed.");
        });
    }

    [Test]
    public void RealModeRostersMatchTheOneAndTwoCharacterArchitecture()
    {
        using var bigIsland = CreateHost(GameMode.BigIsland, "big.sav");
        using var hugeIsland = CreateHost(GameMode.HugeIsland, "huge.sav");
        var bigAssignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "big-players.json"), continueExistingWorld: false);
        var hugeAssignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "huge-players.json"), continueExistingWorld: false);

        Assert.Multiple(() =>
        {
            Assert.That(bigAssignments.Reconcile(bigIsland, Id(11), characterLimit: 2),
                Is.EqualTo(new[] { 1, 2 }));
            Assert.That(hugeAssignments.Reconcile(hugeIsland, Id(12), characterLimit: 1),
                Is.EqualTo(new[] { 1 }));
        });
    }

    [Test]
    public void LateWorldSwapNotificationDoesNotEraseAnEarlyNewWorldAssignment()
    {
        using var host = CreateHost(GameMode.BigIsland, "generation.sav");
        var assignments = PlayerCharacterAssignments.Load(
            Path.Combine(_directory, "generation-players.json"),
            continueExistingWorld: false);

        Assert.That(assignments.Reconcile(
            host, Id(31), worldGeneration: 0), Is.EqualTo(new[] { 1 }));

        // A viewer can enter generation 1 immediately after the supervisor
        // releases its swap lock, before Program receives WorldSwapped.
        Assert.That(assignments.Reconcile(
            host, Id(32), worldGeneration: 1), Is.EqualTo(new[] { 1 }));
        assignments.SwitchWorld(1);

        Assert.Multiple(() =>
        {
            Assert.That(assignments.Reconcile(
                host, Id(31), worldGeneration: 0), Is.Empty,
                "A late old-world viewer must not write its roster back.");
            Assert.That(assignments.Reconcile(
                host, Id(33), worldGeneration: 1), Is.EqualTo(new[] { 2 }),
                "The late notification must not free NPC1 from the early viewer.");
        });
    }

    [Test]
    public void ServerCraftingReadModelContainsOnlyAssignedManualCharacters()
    {
        using var host = CreateHost(GameMode.BigIsland, "crafting.sav");
        var admission = host.SubmitManualCommand(
            new SetManualControlCommand(new EntityId(1), true));

        var snapshot = host.Read(world => PlayerCraftingOptions.Capture(
            world, new[] { 1, 1, 999 }));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Accepted, Is.True);
            Assert.That(snapshot.Npcs.Count, Is.EqualTo(1));
            Assert.That(snapshot.Npcs[0].NpcId, Is.EqualTo(1));
            Assert.That(snapshot.Npcs[0].Options, Is.Not.Empty);
            Assert.That(snapshot.Npcs[0].Options,
                Has.None.Matches<CraftRecipeOption>(option =>
                    option.BlockReason == CraftBlockReason.NotManual),
                "The server read model must observe the same manual-control state as admission.");
        });
    }

    private WorldHost CreateHost(GameMode mode, string saveName) => new(
        seed: 12345,
        mode,
        savePath: Path.Combine(_directory, saveName),
        simDataPath: FindRepoFile("SimData", "simdata.json"),
        verboseTrace: false);

    private static string Id(int value) => new Guid(
        value, 0, 0, new byte[8]).ToString("N");

    private static string FindRepoFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = directory.FullName;
            for (var i = 0; i < relativePath.Length; i++)
            {
                candidate = Path.Combine(candidate, relativePath[i]);
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(string.Join('/', relativePath));
    }
}

}
