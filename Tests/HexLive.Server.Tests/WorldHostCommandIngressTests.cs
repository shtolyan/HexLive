using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Common;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Server.Tests
{

[NonParallelizable]
public sealed class WorldHostCommandIngressTests
{
    [Test]
    public void SubmitManualCommand_ReturnsAdmissionAndRefreshesSameTickSnapshot()
    {
        using var host = CreateHost();
        var before = DecodeSnapshot(host);
        var npcId = before.Npcs[0].Id;

        var accepted = host.SubmitManualCommand(
            new SetManualControlCommand(npcId, enabled: true));
        var after = DecodeSnapshot(host);
        var rejected = host.SubmitManualCommand(
            new StopCommand(new EntityId(int.MaxValue)));

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
            Assert.That(accepted.Actor, Is.EqualTo(npcId));
            Assert.That(after.Tick, Is.EqualTo(before.Tick),
                "Host ingress applies between ticks; cache invalidation must not rely on a tick change.");
            Assert.That(after.Npcs.Find(npc => npc.Id.Equals(npcId))!.IsManualControl,
                Is.True,
                "A snapshot cached before the command must be rebuilt at the same tick.");
            Assert.That(rejected.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(rejected.Reason, Is.EqualTo("NoSuchNpc"));
        });
    }

    [Test]
    public void SubmitManualCommand_HoldsWorldGateForWholeAdmission()
    {
        using var host = CreateHost();
        using var commandEntered = new ManualResetEventSlim();
        using var releaseCommand = new ManualResetEventSlim();
        using var readStarted = new ManualResetEventSlim();
        var command = new BlockingUnsupportedCommand(commandEntered, releaseCommand);

        var submission = Task.Run(() => host.SubmitManualCommand(command));
        Assert.That(commandEntered.Wait(TimeSpan.FromSeconds(3)), Is.True,
            "The command did not reach the admission boundary.");

        var concurrentRead = Task.Run(() =>
        {
            readStarted.Set();
            return host.Tick;
        });
        Assert.That(readStarted.Wait(TimeSpan.FromSeconds(3)), Is.True);

        try
        {
            Assert.That(concurrentRead.Wait(TimeSpan.FromMilliseconds(100)), Is.False,
                "World readers must not enter while a host command is being admitted.");
        }
        finally
        {
            releaseCommand.Set();
        }

        Assert.Multiple(() =>
        {
            Assert.That(submission.Result.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(submission.Result.Reason, Is.EqualTo("UnsupportedCommand"));
            Assert.That(concurrentRead.Wait(TimeSpan.FromSeconds(3)), Is.True,
                "The world gate was not released after command admission.");
        });
    }

    private static WorldHost CreateHost()
    {
        var savePath = Path.Combine(
            Path.GetTempPath(), $"hexlive-worldhost-command-{Guid.NewGuid():N}.sav");
        return new WorldHost(
            seed: 12345,
            mode: GameMode.Feud,
            savePath,
            FindRepoFile("SimData", "simdata.json"),
            verboseTrace: false);
    }

    private static WorldSnapshot DecodeSnapshot(WorldHost host)
    {
        using var stream = new MemoryStream(host.EncodeSnapshot(includeDebugDetails: false));
        using var reader = new BinaryReader(stream);
        var snapshot = new WorldSnapshot();
        WorldSnapshotCodec.Read(reader, snapshot);
        return snapshot;
    }

    private static string FindRepoFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var parts = new string[relativePath.Length + 1];
            parts[0] = directory.FullName;
            Array.Copy(relativePath, 0, parts, 1, relativePath.Length);
            var candidate = Path.Combine(parts);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Repository file not found: {Path.Combine(relativePath)}");
    }

    private sealed class BlockingUnsupportedCommand : ISimulationCommand
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        public BlockingUnsupportedCommand(
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public EntityId? TargetEntity
        {
            get
            {
                _entered.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(3)))
                {
                    throw new TimeoutException("Test did not release the blocking command.");
                }

                return null;
            }
        }
    }
}

}
