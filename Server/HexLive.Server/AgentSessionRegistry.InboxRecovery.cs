using System;
using System.Collections.Generic;
using System.Linq;

namespace HexLive.Server;

public sealed partial class AgentSessionRegistry
{
    private readonly Dictionary<string, (Attachment Attachment, DateTimeOffset Until)> _recoverableInboxes = new(StringComparer.Ordinal);

    public string InboxResumeKey(string attachmentId, string owner, int generation)
    {
        lock (_gate)
            return TryOwned(attachmentId, owner, generation, out var attachment, out _) ? attachment.InboxResumeKey : "";
    }

    public bool PreserveInbox(string attachmentId, string owner, int generation)
    {
        lock (_gate)
        {
            if (!TryOwned(attachmentId, owner, generation, out var attachment, out _)) return false;
            PreserveInboxLocked(attachment); return true;
        }
    }

    private void PreserveInboxLocked(Attachment attachment)
    {
        SweepInboxesLocked();
        if (attachment.MessageIdSet.Count == 0) return;
        if (_recoverableInboxes.Count >= 64)
            _recoverableInboxes.Remove(_recoverableInboxes.OrderBy(p => p.Value.Until).First().Key);
        _recoverableInboxes[attachment.InboxResumeKey] = (attachment, _now().AddHours(24));
    }

    private void RestoreInboxLocked(Attachment attachment, string? key)
    {
        SweepInboxesLocked();
        if (key == null || !_recoverableInboxes.TryGetValue(key, out var saved) ||
            saved.Attachment.NpcId != attachment.NpcId || saved.Attachment.WorldGeneration != attachment.WorldGeneration) return;
        _recoverableInboxes.Remove(key);
        // Stable across retrying a lost attach response; possession never bypasses NPC scope.
        attachment.InboxResumeKey = saved.Attachment.InboxResumeKey;
        attachment.Inbox.AddRange(saved.Attachment.Inbox);
        foreach (var id in saved.Attachment.MessageIds)
        { attachment.MessageIds.Enqueue(id); attachment.MessageIdSet.Add(id); }
        // New transport starts reading from zero. An old read is not a new acknowledgment.
        attachment.AcknowledgedSequence = saved.Attachment.AcknowledgedSequence;
    }

    private void SweepInboxesLocked()
    {
        foreach (var key in _recoverableInboxes.Where(p => p.Value.Until <= _now()).Select(p => p.Key).ToArray())
            _recoverableInboxes.Remove(key);
    }
}
