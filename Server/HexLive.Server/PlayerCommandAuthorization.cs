using System;
using System.Collections.Generic;
using HexLive.Simulation.Runtime;

namespace HexLive.Server
{

/// <summary>
/// §145.4/§149.3: серверная граница прав сетевого игрока. Постоянное
/// назначение проверяется первым; временный manual-control lease нужен
/// только для приказов, которые перехватывают управление персонажем.
/// </summary>
public static class PlayerCommandAuthorization
{
    public static bool TryAuthorize(
        ISimulationCommand command,
        ISet<int> assignedNpcIds,
        ControlLeases leases,
        string owner,
        out string refusal)
    {
        refusal = string.Empty;

        // §159: only the authenticated local MCP bridge may write companion
        // memory. It calls WorldHost directly; a viewer must never smuggle this
        // metadata command through the ordinary player-command wire.
        if (command is RecordCompanionTurnCommand or RecordAgentSocialCommand or RespondDirectiveCommand)
        {
            refusal = "ServerOnlyCommand";
            return false;
        }

        // §149.3: лиз не может сам выдать игроку чужого персонажа.
        if (!PlayerCommandAssignment.Allows(command, assignedNpcIds))
        {
            refusal = "NotAssigned";
            return false;
        }

        // Инвентарь и одежда принадлежат игроку в обоих режимах. Эти команды
        // не захватывают и не продлевают manual-control lease.
        // §167.3: указание своим — обещание, а не управление: оно должно
        // работать именно в ИИ-режиме, поэтому lease не берёт и не продлевает.
        if (command is SetOutfitLockCommand or
            ManageInventoryCommand or
            TransferInventoryCommand or
            TransferContainerCommand or
            SetDirectiveCommand)
        {
            return true;
        }

        switch (command)
        {
            case SetManualControlCommand setManual:
                if (setManual.Enabled)
                {
                    if (!leases.TryAcquire(setManual.Npc.Value, owner, out _, out var heldBy))
                    {
                        Console.WriteLine(
                            $"[viewer {owner}] lease refused for NPC{setManual.Npc.Value}: held by {heldBy}");
                        refusal = "ControlledByOther";
                        return false;
                    }

                    return true;
                }

                if (leases.HolderOf(setManual.Npc.Value) is { Length: > 0 } current &&
                    !string.Equals(current, owner, StringComparison.Ordinal))
                {
                    refusal = "ControlledByOther";
                    return false;
                }

                leases.Release(setManual.Npc.Value, owner);
                return true;

            case SetGroupManualControlCommand groupManual:
                foreach (var actor in groupManual.Actors)
                {
                    var groupManualHolder = leases.HolderOf(actor.Value);
                    if (groupManualHolder.Length > 0 &&
                        !string.Equals(groupManualHolder, owner, StringComparison.Ordinal))
                    {
                        refusal = "ControlledByOther";
                        return false;
                    }
                }

                foreach (var actor in groupManual.Actors)
                {
                    if (groupManual.Enabled)
                    {
                        leases.TryAcquire(actor.Value, owner, out _, out _);
                    }
                    else
                    {
                        leases.Release(actor.Value, owner);
                    }
                }

                return true;

            case IGroupSimulationCommand group:
                foreach (var actor in group.Actors)
                {
                    if (!leases.TryRenew(actor.Value, owner, out var groupHolder))
                    {
                        refusal = groupHolder.Length > 0 ? "ControlledByOther" : "NoLease";
                        return false;
                    }
                }

                return true;

            default:
                if (command.TargetEntity is { } actorId &&
                    !leases.TryRenew(actorId.Value, owner, out var holder))
                {
                    refusal = holder.Length > 0 ? "ControlledByOther" : "NoLease";
                    return false;
                }

                return true;
        }
    }
}

}
