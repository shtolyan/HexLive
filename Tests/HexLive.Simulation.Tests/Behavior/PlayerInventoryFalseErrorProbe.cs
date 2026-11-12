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
[Explicit("Measurement probe; prints a table instead of asserting.")]
public sealed class PlayerInventoryFalseErrorProbe
{
    [TestCase(12345)]
    public void InventoryOperationsAndTheirToasts(int seed)
    {
        var engine = TestWorld.CreateEngine(seed); var world = engine.World;
        long watermark = 0; var log = new List<string>();
        void Drain()
        {
            foreach (var e in world.Events.Items)
            {
                if (e.Seq <= watermark) continue;
                watermark = e.Seq;
                if (e.Type is "ManualOrderRejected" or "ManualOrderInterrupted" or "ManualControlExpired" or
                    "ManualForbiddenGoalDropped" or "ManualOrderFinished" or "PlayerInventoryCompleted" or "PlayerInventoryFailed")
                    log.Add($"t{e.Tick} [{e.EntityId}] {e.Type}: {e.Message}");
            }
        }
        void Step(int n) { for (var i = 0; i < n; i++) { engine.Step(); Drain(); } }
        Step(300);
        var colony = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony && n.Health > 0f)
            .OrderBy(n => n.Id.Value).ToArray();
        var assigned = colony.Select(n => n.Id.Value).ToArray();
        var sb = new StringBuilder();
        var npc = colony[0];
        void Case(string name, System.Action body, int ticks = 30)
        {
            log.Clear(); Drain(); log.Clear();
            var before = $"inv={npc.Inventory.Items.Count} worn={npc.WornItems.Count} manual={npc.Mind.ManualControl} goal={npc.Mind.CurrentGoal}";
            body(); Step(ticks);
            sb.AppendLine($"## {name}: before[{before}] after[inv={npc.Inventory.Items.Count} worn={npc.WornItems.Count} manual={npc.Mind.ManualControl} goal={npc.Mind.CurrentGoal}]");
            foreach (var l in log) sb.AppendLine("    " + l);
        }
        ManualCommandAdmission Send(ISimulationCommand c)
        {
            var r = engine.ApplyManualCommand(c, "probe", assigned);
            sb.AppendLine($"    send {c.GetType().Name} -> {(r.Accepted ? "OK" : "REJ:" + r.Reason)}");
            return r;
        }
        InventoryItemRef Carried(int i) => new(InventoryItemSource.Carried, i, npc.Inventory.Items[i].DefinitionId);
        void Give(string id) => npc.Inventory.Items.Insert(0, new ItemInstance(id));

        foreach (var manual in new[] { false, true })
        {
            engine.ApplyManualCommand(new SetManualControlCommand(npc.Id, manual), "probe", assigned); Step(5);
            var tag = manual ? "MANUAL" : "AI";
            Give(ContentIds.Stick);
            Case($"{tag} drop idle", () => Send(new ManageInventoryCommand(npc.Id, Carried(0), InventoryAction.Drop)));
            Give(ContentIds.Stick);
            Case($"{tag} drop twice same tick", () => { var r = Carried(0); Send(new ManageInventoryCommand(npc.Id, r, InventoryAction.Drop)); Send(new ManageInventoryCommand(npc.Id, r, InventoryAction.Drop)); });
            Give(ContentIds.Stick);
            Case($"{tag} drop while walking", () =>
            {
                var far = world.Junctions.Items.Values.Where(j => !j.Blocked)
                    .OrderByDescending(j => HexLive.Simulation.Spatial.HexSpatialMath.Distance(j.WorldPosition, npc.Position)).Skip(2000).First();
                Send(new QueuePlayerOrderCommand(new MoveToCommand(npc.Id, far.WorldPosition)));
                Step(6);
                Send(new ManageInventoryCommand(npc.Id, Carried(0), InventoryAction.Drop));
            }, 40);
            Send(new StopCommand(npc.Id)); Step(3);
            var worn = npc.WornItems.Count > 0 ? 0 : -1;
            if (worn >= 0)
            {
                Case($"{tag} stow worn[0]={npc.WornItems[0].DefinitionId}", () => Send(new ManageInventoryCommand(npc.Id,
                    new InventoryItemRef(InventoryItemSource.Worn, 0, npc.WornItems[0].DefinitionId), InventoryAction.Stow)));
                var idx = npc.Inventory.Items.FindIndex(i => world.Content.ObjectDefinitions.TryGetValue(i.DefinitionId, out var d) && d.HasTag("Clothing"));
                if (idx < 0) idx = npc.Inventory.Items.Count - 1;
                Case($"{tag} wear carried[{idx}]={npc.Inventory.Items[idx].DefinitionId}", () => Send(new ManageInventoryCommand(npc.Id, Carried(idx), InventoryAction.Wear)));
                Case($"{tag} drop worn[0]", () => Send(new ManageInventoryCommand(npc.Id,
                    new InventoryItemRef(InventoryItemSource.Worn, 0, npc.WornItems[0].DefinitionId), InventoryAction.Drop)));
            }
            Give(ContentIds.Stick);
            Case($"{tag} eat-order then drop", () =>
            {
                Send(new QueuePlayerOrderCommand(new SelfActionCommand(npc.Id, SelfActionKind.GroundSit)));
                Step(8);
                Send(new ManageInventoryCommand(npc.Id, Carried(0), InventoryAction.Drop));
            }, 40);
            Send(new StopCommand(npc.Id)); Step(3);
        }
        TestContext.Out.WriteLine(sb.ToString());
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hexlive-inv-probe.txt"), sb.ToString());
    }
}
}
