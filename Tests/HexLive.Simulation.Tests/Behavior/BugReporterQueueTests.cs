using System;
using System.IO;
using System.Linq;
using System.Text;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class BugReporterQueueTests
{
    [Test]
    public void FailedHeadDoesNotBlockAnotherReportAndRetryRetainsItsFrozenPayload()
    {
        var queue = new BugReportQueue();
        var first = new BugReportQueue.Job("tick=10", new byte[] { 1, 2 }, new byte[] { 3 }, "first");
        var second = new BugReportQueue.Job("tick=11", new byte[] { 4 }, null, "second");
        Assert.That(queue.Enqueue(first) && queue.Enqueue(second), Is.True);
        var uuid = first.RequestId;
        Assert.That(queue.BeginNext(0), Is.SameAs(first));
        Assert.That(queue.BeginNext(0), Is.Null, "Only one delivery owns the transport.");
        queue.RetryNetwork(first, 1, "network");
        Assert.That(queue.BeginNext(2), Is.SameAs(second), "A backoff must not freeze the queue.");
        queue.Finish(second);
        Assert.That(queue.BeginNext(5), Is.SameAs(first));
        queue.RetryNetwork(first, 5, "network");
        Assert.That(queue.BeginNext(19), Is.Null);
        Assert.That(queue.BeginNext(20), Is.SameAs(first));
        queue.RetryNetwork(first, 20, "network");
        Assert.That(first.State, Is.EqualTo(BugReportQueue.Delivery.Failed));
        Assert.That(queue.Resubmit(first, "corrected"), Is.True);
        Assert.That(first.RequestId, Is.EqualTo(uuid), "Uncertain acknowledgements must not create duplicates.");
        Assert.That(first.Context, Is.EqualTo("tick=10"));
        Assert.That(first.Screenshot, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(queue.BeginNext(21), Is.SameAs(first));
        queue.Fail(first, "recognition failed"); first.DraftId = "expired-draft";
        queue.Resubmit(first, "");
        Assert.That(first.DraftId, Is.Empty, "Manual retry must create a fresh recognition draft, not repoll the failed one.");
    }
    [Test]
    public void QueueLimitsRejectNewCaptureWithoutDroppingExistingReports()
    {
        var queue = new BugReportQueue();
        for (int i = 0; i < BugReportQueue.MaxEntries; i++)
            Assert.That(queue.Enqueue(new BugReportQueue.Job("", null, null, i.ToString())), Is.True);
        var ids = queue.Jobs.Select(j => j.RequestId).ToArray();
        Assert.That(queue.Enqueue(new BugReportQueue.Job("", null, null, "overflow")), Is.False);
        Assert.That(queue.Jobs.Select(j => j.RequestId), Is.EqualTo(ids));
        var oversized = new BugReportQueue.Job("", new byte[BugReportQueue.MaxBytes + 1], null, "");
        Assert.That(new BugReportQueue().Enqueue(oversized), Is.False);
    }
    [TestCase(0, true)] [TestCase(408, true)] [TestCase(429, true)] [TestCase(500, true)]
    [TestCase(401, false)] [TestCase(403, false)] [TestCase(413, false)]
    public void RetryPolicyDistinguishesTransientErrors(long code, bool expected) =>
        Assert.That(BugReportQueue.Retryable(code), Is.EqualTo(expected));

    [Test]
    public void CaptureCopiesTheSelectedNpcAndBoundsNeighborsWithoutIncludingSpeech()
    {
        var snapshot = new WorldSnapshot { Seed = 616, Tick = 42 };
        var selected = new NpcSnapshot { Id = new(1), Position = new(0, 0), CurrentGoal = "Build", KnownObjectCount = 100 };
        selected.InventoryItems.Add("tool.hammer");
        for (int i = 0; i < 200; i++) selected.KnownObjects.Add("object" + i);
        snapshot.Npcs.Add(selected);
        for (int i = 2; i < 40; i++) snapshot.Npcs.Add(new NpcSnapshot { Id = new(i), Position = new(i, 0) });
        snapshot.TraceEvents.Add(new TraceEventSnapshot { Tick = 42, EntityId = 1, Type = "PlayerSpoke", Message = "private words" });
        snapshot.TraceEvents.Add(new TraceEventSnapshot { Tick = 42, EntityId = 1, Type = "PlanFailed", Message = "token=private" });
        var captured = BugReportContextData.Capture(snapshot, 1);
        selected.InventoryItems.Clear(); selected.CurrentGoal = "Eat"; snapshot.Tick = 43;
        Assert.Multiple(() =>
        {
            Assert.That(captured.tick, Is.EqualTo(42));
            Assert.That(captured.selected.goal, Is.EqualTo("Build"));
            Assert.That(captured.selected.inventory, Is.EqualTo(new[] { "tool.hammer" }));
            Assert.That(captured.selected.knownObjects, Has.Length.EqualTo(48));
            Assert.That(captured.nearbyNpcs, Has.Length.EqualTo(12));
            Assert.That(captured.nearbyNpcs[0].id, Is.EqualTo(2));
            Assert.That(captured.events, Has.Length.EqualTo(1));
            Assert.That(captured.events[0].detail, Is.Empty);
        });
    }
    [Test]
    public void DeviceAndAdminKeysCannotPassTheGameBuildPolicy()
    {
        Assert.That(BugReportConnectionPolicy.IsWriteOnlyReporter("hexlive", false, new[] { "created" }), Is.True);
        Assert.That(BugReportConnectionPolicy.IsWriteOnlyReporter("hexlive", true, new[] { "created" }), Is.False);
        Assert.That(BugReportConnectionPolicy.IsWriteOnlyReporter(null, false, new[] { "created", "in_progress" }), Is.False);
        Assert.That(BugReportConnectionPolicy.IsWriteOnlyReporter("first-signal", false, new[] { "created" }), Is.False);
        Assert.That(BugReportConnectionPolicy.IsApi("https://example.com/api/bugs/v1"), Is.True);
        Assert.That(BugReportConnectionPolicy.IsApi("https://secret@example.com/api/bugs/v1?token=x"), Is.False);
    }
    [Test]
    public void RecordingIsResampledToMonoPcm16WithoutChangingItsDuration()
    {
        var input = Pcm(48000, 2, Enumerable.Repeat(.5f, 48000 * 2).ToArray());
        var result = BugReportWav.Prepare(input);
        Assert.That(Encoding.ASCII.GetString(result, 0, 4), Is.EqualTo("RIFF"));
        Assert.That(BitConverter.ToInt16(result, 22), Is.EqualTo(1));
        Assert.That(BitConverter.ToInt32(result, 24), Is.EqualTo(16000));
        Assert.That(result.Length, Is.EqualTo(44 + 16000 * 2));
        Assert.That(BitConverter.ToInt16(result, 44), Is.InRange(16380, 16385));
        Assert.Throws<ArgumentException>(() => BugReportWav.Prepare(new byte[44]));
    }
    [Test]
    public void RecognitionUploadShortensOnlyQuietPausesAndPreservesUnclearTakes()
    {
        var input = new float[16000 * 7];
        for (int i = 0; i < 16000; i++) input[16000 + i] = input[16000 * 5 + i] = .3f;
        var result = BugReportWav.Prepare(Pcm(16000, 1, input));
        Assert.That(result.Length, Is.InRange(44 + 16000 * 2 * 2, 44 + 16000 * 2 * 4));
        var unclear = BugReportWav.Prepare(Pcm(16000, 1, new float[16000]));
        Assert.That(unclear.Length, Is.EqualTo(44 + 16000 * 2));
    }
    private static byte[] Pcm(int rate, int channels, float[] samples)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
        writer.Write((short)channels); writer.Write(rate); writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2)); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write((short)(sample * 32767));
        return stream.ToArray();
    }
}
