using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Регрессия #217: localhost-сервер продолжал тикать, а клиент после короткого
/// разрыва навсегда оставался в «Переподключение». Причиной было состояние
/// старого сокета, переживавшее реконнект.
/// </summary>
public sealed class RemoteReconnectContractTests
{
    private static string Backend() => File.ReadAllText(Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Bootstrap", "Remote",
        "RemoteSocketBackend.cs"));

    private static string Runner() => File.ReadAllText(Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Bootstrap",
        "SimulationRunnerBehaviour.cs"));

    [Test]
    public void RetryBudgetResetsOnlyAfterProtocolHandshake()
    {
        var backend = Backend();
        var connect = backend.IndexOf("await socket.ConnectAsync", StringComparison.Ordinal);
        var pump = backend.IndexOf("await PumpAsync", connect, StringComparison.Ordinal);
        var handshake = backend.IndexOf("case FrameKind.Handshake:", StringComparison.Ordinal);
        var reset = backend.IndexOf("_attempt = 0;", StringComparison.Ordinal);

        Assert.That(connect, Is.GreaterThan(0));
        Assert.That(pump, Is.GreaterThan(connect));
        Assert.That(reset, Is.GreaterThan(handshake),
            "WebSocket upgrade alone must not reset the retry budget");
        Assert.That(reset, Is.GreaterThan(pump),
            "the retry budget must reset in Dispatch(Handshake), not after ConnectAsync");
    }

    [Test]
    public void NewConnectionDropsOldLivenessState()
    {
        var backend = Backend();
        var connect = backend.IndexOf("await socket.ConnectAsync", StringComparison.Ordinal);
        var pump = backend.IndexOf("await PumpAsync", connect, StringComparison.Ordinal);
        var reset = backend.IndexOf("_pongPending = false;", connect, StringComparison.Ordinal);

        Assert.That(reset, Is.InRange(connect + 1, pump - 1),
            "an unanswered pong from the old socket must be cleared before pumping the new one");
    }

    [Test]
    public void SendsAreCancelledWithTheirConnection()
    {
        var backend = Backend();

        Assert.That(backend, Does.Contain("SendSerializedAsync(socket, frame, connectionCancel)"));
        Assert.That(backend, Does.Contain("_sendGate.WaitAsync(connectionCancel)"));
        Assert.That(backend, Does.Contain("connectionLifetime.Cancel();"));
        Assert.That(backend, Does.Not.Contain("_sendGate.WaitAsync(_shutdown.Token)"));
    }

    [Test]
    public void ConnectAndProtocolHandshakeBothHaveDeadlines()
    {
        var backend = Backend();

        Assert.That(backend, Does.Contain("ConnectAttemptTimeoutSeconds"));
        Assert.That(backend, Does.Contain("connectAttempt.CancelAfter"));
        Assert.That(backend, Does.Contain("HandshakeTimeoutSeconds"));
        Assert.That(backend, Does.Contain("No valid handshake after"));
        Assert.That(backend, Does.Contain("Server handshake is invalid"));
    }

    [Test]
    public void RemoteContinueDoesNotBuildAndDiscardALocalFallbackWorld()
    {
        var runner = Runner();
        var remoteFastPath = runner.IndexOf("if (TryBootstrapRemote())", StringComparison.Ordinal);
        var localWorldgen = runner.IndexOf("new WorldStateFactory().Create(definition)", StringComparison.Ordinal);

        Assert.That(remoteFastPath, Is.GreaterThan(0));
        Assert.That(remoteFastPath, Is.LessThan(localWorldgen));
        Assert.That(runner, Does.Contain("RemoteSocketBackend(") );
    }

    [Test]
    public void AuthoritativeTopologyBuildRunsOffTheUnityThread()
    {
        var backend = Backend();

        Assert.That(backend, Does.Contain("Task.Run(() => BuildInitialWorld(handshake))"));
        Assert.That(backend, Does.Contain("TryCompleteInitialWorldBuild()"));
        Assert.That(backend, Does.Contain("ReferenceEquals(_handshake, result.Handshake)"));
    }
}
