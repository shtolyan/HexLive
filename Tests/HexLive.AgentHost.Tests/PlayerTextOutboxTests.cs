using HexLive.UnityPresentation.Audio;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class PlayerTextOutboxTests
{
    [Test]
    public void LostAcceptanceRetriesSameIdentityAndDoesNotReleaseNextText()
    {
        var queue = new PlayerTextOutbox(16, 4096);
        var first = new PlayerTextOutbox.Message(901, "attachment-a", "message-a", new string('я', 3000), 5);
        queue.TryEnqueue(first, out _);
        queue.TryEnqueue(new(902, "attachment-b", "message-b", "далее", 6), out _);
        queue.MarkSent(10);
        Assert.That(queue.Peek(), Is.SameAs(first));
        Assert.That(first.NextAttempt, Is.EqualTo(15));
        queue.MarkSent(16);
        Assert.That(queue.Peek()!.Text, Has.Length.EqualTo(3000));
        Assert.That(queue.Accept(99, "message-a", true, "", 17), Is.EqualTo(PlayerTextOutbox.Acknowledgment.Unknown));
        Assert.That(queue.Accept(5, "old-message", true, "", 17), Is.EqualTo(PlayerTextOutbox.Acknowledgment.Unknown));
        Assert.That(queue.Count, Is.EqualTo(2));
        Assert.That(queue.Accept(5, "message-a", true, "", 17), Is.EqualTo(PlayerTextOutbox.Acknowledgment.Delivered));
        Assert.That(queue.Peek()!.NpcId, Is.EqualTo(902));
        Assert.That(queue.Accept(5, "message-a", true, "", 18), Is.EqualTo(PlayerTextOutbox.Acknowledgment.Unknown));
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void BackpressureRetainsTextButExplicitRejectionEndsDelivery()
    {
        var queue = new PlayerTextOutbox(1, 4096);
        queue.TryEnqueue(new(901, "a", "one", "сохрани", 5), out _);
        Assert.That(queue.TryEnqueue(new(901, "a", "two", "переполнение", 6), out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("OutboxFull"));
        Assert.That(queue.Accept(5, "one", false, "InboxFull", 10), Is.EqualTo(PlayerTextOutbox.Acknowledgment.Retry));
        Assert.That(queue.Peek()!.MessageId, Is.EqualTo("one"));
        Assert.That(queue.Peek()!.NextAttempt, Is.EqualTo(12));
        Assert.That(queue.Accept(5, "one", false, "AgentDetached", 15), Is.EqualTo(PlayerTextOutbox.Acknowledgment.Rejected));
        Assert.That(queue.Count, Is.Zero);
        Assert.That(queue.TryEnqueue(new(901, "a", "three", new string('я', 4097), 7), out reason), Is.False);
        Assert.That(reason, Is.EqualTo("TextTooLong"));
    }
}
