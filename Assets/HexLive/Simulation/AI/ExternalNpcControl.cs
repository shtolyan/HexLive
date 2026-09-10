using System;
using System.Collections.Generic;
using System.Threading;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.AI
{

/// <summary>§160.1: attachment lifetime, separate from a physical action lease.
/// Runtime only. Disposing a server attachment immediately removes its veto on AI
/// without writing any saved player-control flag or touching the world off-thread.</summary>
public sealed class ExternalNpcControl : IDisposable
{
    private int _disposed;
    public bool IsActive => Volatile.Read(ref _disposed) == 0;
    internal HashSet<EntityId> SeenIntruders { get; } = new();
    internal HashSet<EntityId> VisibleIntruders { get; } = new();

    public bool Bind(WorldState world, NPCState npc, bool keepCurrentAction = false)
    {
        if (!IsActive) return false;
        if (ReferenceEquals(npc.Mind.ExternalControl, this)) return true;
        npc.Mind.ExternalControl = this;
        CampExpulsionSystem.CancelVoluntaryForControl(world, npc);
        // Taking over a mind must not stop an ongoing fight or wake an
        // unconscious/recovering body. Those use the ordinary manual reflexes.
        if (!keepCurrentAction && !npc.IsFighting &&
            npc.Execution.CurrentInteraction != InteractionType.Sleep)
        {
            PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand,
                "External agent took control");
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Mind.GoalLock = null;
        }
        return IsActive;
    }

    /// <summary>Release a physical MCP action while preserving the player's
    /// saved switch. Also works just after detach/expiry disposed the token.</summary>
    public static ManualCommandAdmission? TryReleaseAction(WorldState world, NPCState npc)
    {
        if (npc.Mind.ExternalControl is null) return null;
        var playerControl = npc.Mind.PersistedManualControl;
        try
        {
            ManualCommandExecutor.ReleaseToAi(world, npc,
                "External action lease released", expired: false);
        }
        finally { npc.Mind.ManualControl = playerControl; }
        return new ManualCommandAdmission(ManualCommandAdmissionStatus.Accepted,
            npc.Id, "SetManual", string.Empty);
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

}
