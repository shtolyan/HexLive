using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// Spec 40.18: sharks — simple water roamers (like dogs, not NPCs) that bite
// any NPC caught swimming. Dormant against the land colony: they can't leave
// the water and NPCs don't yet swim (the ring is a dead-end), so the bite
// never fires until a second island gives the ring a far shore.
public sealed class SharkSystem : ISimulationSystem
{
    public string Name => nameof(SharkSystem);

    public TickLayer Layer => TickLayer.Medium;

    private static int MaxSharks => WildlifeBalance.MaxSharks;

    private static readonly System.Collections.Generic.List<Common.JunctionId> _waterScratch = new();

    public void Run(WorldState world)
    {
        if (world.Sharks.Count < MaxSharks && world.SwimJunctions.Count > 0)
        {
            var swims = new System.Collections.Generic.List<Common.JunctionId>(world.SwimJunctions);
            var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, world.Sharks.Count, 6101) * swims.Count);
            pick = System.Math.Min(pick, swims.Count - 1);
            if (world.Junctions.Items.TryGetValue(swims[pick], out var jn))
            {
                world.Sharks.Add(new Wildlife.SharkState
                {
                    Id = 900 + world.Sharks.Count,
                    Junction = swims[pick],
                    Tile = jn.Tiles.Count > 0 ? jn.Tiles[0] : default,
                    Position = jn.WorldPosition
                });
            }
        }

        foreach (var shark in world.Sharks)
        {
            RoamShark(world, shark);
            BiteSwimmers(world, shark);
        }
    }

    // Patrol among water junctions (the swim ring + open sea).
    private static void RoamShark(WorldState world, Wildlife.SharkState shark)
    {
        if (!world.Junctions.Items.TryGetValue(shark.Junction, out var junction) ||
            junction.Neighbors.Count == 0)
        {
            return;
        }

        _waterScratch.Clear();
        foreach (var nid in junction.Neighbors)
        {
            if (SpatialQueries.IsAllWaterJunction(world, nid) || world.SwimJunctions.Contains(nid))
            {
                _waterScratch.Add(nid);
            }
        }

        if (_waterScratch.Count == 0)
        {
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, shark.Id, 6203) * _waterScratch.Count);
        pick = System.Math.Min(pick, _waterScratch.Count - 1);
        if (world.Junctions.Items.TryGetValue(_waterScratch[pick], out var nj))
        {
            shark.Junction = _waterScratch[pick];
            shark.Tile = nj.Tiles.Count > 0 ? nj.Tiles[0] : shark.Tile;
            shark.Position = nj.WorldPosition;
        }
    }

    // Bite an NPC that's swimming on/next to the shark (dormant until swimming).
    private static void BiteSwimmers(WorldState world, Wildlife.SharkState shark)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f || npc.CurrentJunction is not { } njct ||
                !world.SwimJunctions.Contains(njct))
            {
                continue;
            }

            var adjacent = njct.Equals(shark.Junction) ||
                (world.Junctions.Items.TryGetValue(shark.Junction, out var sj) &&
                 sj.Neighbors.Contains(njct));
            if (!adjacent)
            {
                continue;
            }

            // §50: the shark goes for the right leg, but never bites a stump.
            var bitPart = AmputateSystemHelpers.RedirectFromStump(npc, BodyPart.LegR);
            // §76: Toughness only, deliberately NOT EquipmentMath.Mitigate. This
            // system has never applied armor — you are in the water, and the
            // garments that would absorb are soaked or ashore — and turning that
            // on would be a balance change wearing a feature's clothes. Grit
            // still counts, and it is hoisted into one local because the raw
            // figure used to be re-read four times: HP, adrenaline, the wound
            // decal and the §50 sever check must agree on what actually landed.
            var sharkBite = Content.MobCatalog.For(Content.MobIds.Shark).AttackDamage *
                AttributeMath.IncomingDamageMult(npc);
            npc.Body.Parts[bitPart] = System.Math.Max(0f, npc.Body.Parts[bitPart] - sharkBite);
            npc.Health = npc.Body.Mean();
            DamageReactionSystemHelpers.GrantAdrenaline(world, npc, sharkBite, "SharkBite");
            npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood - 0.15f);
            WoundMath.Inflict(world, npc, bitPart, sharkBite);
            // Spec §50: a shark's 0.2 bite clears the big-blow threshold — if it
            // takes the leg to 0, it comes off.
            AmputateSystemHelpers.TrySeverOnBite(world, npc, bitPart, sharkBite);
            Trace.Emit(world, npc.Id, "SharkBite", $"NPC{npc.Id.Value} bitten by shark {shark.Id}");
            break;
        }
    }
}

}
