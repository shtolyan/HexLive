using System.Collections.Generic;
using System.Linq;
using System.Text;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
// Measurement harness for bugs #737/#741/#742: replays the exact command
// sequences the client sends for social orders against a LIVE colony (people
// busy with their own plans), and reports how often the order reaches its act.
[Explicit("Measurement probe; prints a table instead of asserting.")]
public sealed class PlayerSocialOrderProbe
{
    private sealed class Trial
    {
        public string Mode, Outcome; public int Ticks;
        public readonly List<string> Notes = new();
    }

    private static readonly HashSet<string> Interesting = new()
    {
        "ManualOrderRejected", "ManualOrderInterrupted", "ManualControlExpired", "ManualOrderFinished",
        "GiftGiven", "TalkStarted", "TalkRejected", "TalkEnded", "ManualForbiddenGoalDropped",
    };

    [TestCase(12345)]
    [TestCase(777)]
    [TestCase(424242)]
    public void GiftAndTalkAgainstLiveColony(int seed)
    {
        var engine = TestWorld.CreateEngine(seed); var world = engine.World;
        long watermark = 0;
        var log = new List<(long tick, int? npc, string type, string message)>();
        void Drain()
        {
            foreach (var e in world.Events.Items)
            {
                if (e.Seq <= watermark) continue;
                watermark = e.Seq;
                if (Interesting.Contains(e.Type)) log.Add((e.Tick, e.EntityId, e.Type, e.Message));
            }
        }
        void Step(int n) { for (var i = 0; i < n; i++) { engine.Step(); Drain(); } }

        Step(600);
        var trials = new List<Trial>();
        var colony = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony && n.Health > 0f)
            .OrderBy(n => n.Id.Value).ToArray();
        foreach (var mode in new[] { "gift-ai", "gift-manual", "talk-queue" })
        foreach (var giver in colony)
        foreach (var receiver in colony)
        {
            if (giver == receiver) continue;
            if (giver.Health <= 0f || receiver.Health <= 0f) continue;
            var trial = new Trial { Mode = mode }; trials.Add(trial);
            // Needs are not what this probe measures: keep everybody fed so a
            // long run does not turn into "everyone is dying of thirst".
            foreach (var n in colony) { n.Needs.Hunger = System.Math.Min(n.Needs.Hunger, 0.3f); n.Needs.Thirst = System.Math.Min(n.Needs.Thirst, 0.3f); }
            log.Clear();
            var owner = "probe"; var assigned = colony.Select(n => n.Id.Value).ToArray();
            ManualCommandAdmission Send(ISimulationCommand c)
            {
                var r = engine.ApplyManualCommand(c, owner, assigned);
                if (!r.Accepted) trial.Notes.Add($"{c.GetType().Name.Replace("Command", "")}:REJ:{r.Reason}");
                return r;
            }

            var before = $"recv goal={receiver.Mind.CurrentGoal} exec={receiver.Execution.CurrentInteraction} moving={receiver.Movement.IsMoving}";
            ItemInstance gift = null;
            if (mode.StartsWith("gift"))
            {
                gift = new ItemInstance(ContentIds.Stick);
                giver.Inventory.Items.Insert(0, gift);
                if (mode == "gift-manual") Send(new SetManualControlCommand(giver.Id, true));
                Send(new BeginGiftSessionCommand(giver.Id, receiver.Id));
                Send(new TransferInventoryCommand(giver.Id, receiver.Id,
                    new InventoryItemRef(InventoryItemSource.Carried, 0, gift.DefinitionId), 1,
                    InventoryTransferDirection.Give));
                Send(new EndGiftSessionCommand(giver.Id, receiver.Id));
            }
            else
            {
                Send(new QueuePlayerOrderCommand(new TalkToCommand(giver.Id, receiver.Id)));
            }

            var done = false; var t = 0;
            for (; t < 500 && !done; t++)
            {
                Step(1);
                if (gift != null)
                {
                    if (receiver.Inventory.Items.Any(x => ReferenceEquals(x, gift)) ||
                        receiver.WornItems.Any(x => ReferenceEquals(x, gift))) { trial.Outcome = "DELIVERED"; done = true; }
                    else if (giver.Plan.Status != PlanStatus.Active && giver.Execution.Status != ExecutionStatus.InProgress &&
                             giver.Mind.GiftRecipientId == null && t > 2) { trial.Outcome = "LOST"; done = true; }
                }
                else
                {
                    if (log.Any(e => e.type == "TalkStarted" && e.npc == giver.Id.Value)) { trial.Outcome = "TALKED"; done = true; }
                    else if (giver.Mind.PlayerOrders.Active == null && giver.Mind.PlayerOrders.Pending.Count == 0 &&
                             giver.Mind.CurrentGoal == GoalType.None && t > 2) { trial.Outcome = "LOST"; done = true; }
                }
            }
            trial.Outcome ??= "TIMEOUT"; trial.Ticks = t;
            trial.Notes.Add(before);
            foreach (var e in log.Where(e => e.type != "ManualOrderFinished" || trial.Outcome != "DELIVERED").Take(6))
                trial.Notes.Add($"{e.type}[{e.npc}]:{e.message}");
            if (gift != null) { giver.Inventory.Items.Remove(gift); receiver.Inventory.Items.Remove(gift); }
            engine.ApplyManualCommand(new SetManualControlCommand(giver.Id, false), owner, assigned);
            Step(40);
        }

        var sb = new StringBuilder();
        foreach (var group in trials.GroupBy(t => t.Mode))
        {
            sb.AppendLine($"== seed {seed} {group.Key}: " + string.Join(", ",
                group.GroupBy(t => t.Outcome).Select(g => $"{g.Key}={g.Count()}")));
            foreach (var t in group.Where(t => t.Outcome is not ("DELIVERED" or "TALKED")))
                sb.AppendLine($"   {t.Outcome} after {t.Ticks}: " + string.Join(" | ", t.Notes));
        }
        TestContext.Out.WriteLine(sb.ToString());
        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hexlive-social-probe.txt"), sb.ToString());
    }
}
}
