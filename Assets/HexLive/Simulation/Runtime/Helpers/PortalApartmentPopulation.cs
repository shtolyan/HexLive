using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§176: civilian residents and household clothing for a generated apartment visit.</summary>
internal static class PortalApartmentPopulation
{
    private const int RuntimeResidentIdStart = 1_000_000;

    internal static NPCState TrySpawnResident(WorldState world, int visitSerial,
        IReadOnlyList<PopulationArrivalMath.Landing> landings)
    {
        if (world.Portal.Resident.HasValue || !HasResident(world.Seed, visitSerial)) return null;

        var available = landings.Where(point => IsAvailableLanding(world, point)).ToArray();
        if (available.Length == 0 || !TryAllocateResidentId(world, out var id)) return null;
        var landing = available[RollIndex(world.Seed, visitSerial, 0, 17661, available.Length)];
        var resident = ColonyArrivalSystem.CreateArrival(world, id, Faction.Castaway, landing);
        resident.ProfileId = PortalWorld.ResidentProfileId;
        world.PlayerControlledNpcs.Remove(id.Value);
        resident.Inventory.Items.Clear();
        resident.Traits.Clear();
        resident.Traits.Add(TraitKind.Coward);
        resident.Needs.Stress = .8f;
        resident.WornItems.RemoveAll(item =>
            !world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition) ||
            definition.Layer != WearLayer.Underwear);

        var clothes = AvailableWear(world).Where(item => GarmentLibrary.FitsSex(resident.Sex, item))
            .Select(item => world.Content.ObjectDefinitions[item]).ToArray();
        WearPiece(BodyPart.Pelvis, 17662);
        WearPiece(BodyPart.Torso, 17663);

        EquipmentMath.Recalculate(world, resident);
        world.Portal.Resident = resident.Id;
        return resident;

        void WearPiece(BodyPart part, int salt)
        {
            var choices = clothes.Where(definition => definition.Covers.Contains(part) &&
                resident.WornItems.All(existing => !WearSlotCatalog.Occupies(
                    definition, world.Content.ObjectDefinitions[existing.DefinitionId]))).ToArray();
            if (choices.Length == 0) return;
            var chosen = choices[RollIndex(world.Seed, visitSerial, 0, salt, choices.Length)];
            resident.WornItems.Add(new ItemInstance(chosen.Id) { OwnerId = resident.Id.Value });
        }
    }

    internal static int StockWardrobe(WorldState world, WorldObjectState wardrobe, int visitSerial)
    {
        if (visitSerial < 1 || wardrobe.DefinitionId != "furniture.modern_wardrobe" ||
            wardrobe.Fragment != PortalWorld.ApartmentFragment || wardrobe.Contents.Count > 0)
            return 0;

        var eligible = AvailableWear(world).ToList();
        var count = Math.Min(eligible.Count, 2 + RollIndex(world.Seed, visitSerial, 0, 17664, 3));
        for (var i = 0; i < count; i++)
        {
            var index = RollIndex(world.Seed, visitSerial, i, 17665, eligible.Count);
            wardrobe.Contents.Add(new ItemInstance(eligible[index]));
            eligible.RemoveAt(index);
        }
        return count;
    }

    private static bool HasResident(int seed, int visitSerial)
    {
        if (visitSerial <= 1) return visitSerial == 1;
        // After the guaranteed first meeting, each pair of visits has one
        // resident. The seed chooses which of the two apartments is occupied.
        var followingVisit = visitSerial - 2;
        return followingVisit % 2 == RollIndex(seed, followingVisit / 2, 0, 17660, 2);
    }

    private static bool IsAvailableLanding(WorldState world, PopulationArrivalMath.Landing point) =>
        point.Fragment == PortalWorld.ApartmentFragment &&
        SpatialQueries.IsTileWalkable(world, point.Tile) &&
        world.Junctions.Items.TryGetValue(point.Junction, out var junction) &&
        junction.Fragment == point.Fragment && !junction.Blocked && !junction.Door &&
        junction.Tiles.Count == 1 && junction.Tiles.Contains(point.Tile) &&
        SpatialQueries.IsJunctionFree(world, point.Junction) &&
        (!world.Reservations.Junctions.TryGetValue(point.Junction, out var reservation) ||
         reservation.EndTick < world.Tick);

    private static bool TryAllocateResidentId(WorldState world, out EntityId id)
    {
        // The persisted runtime counter also allocates administrative NPCs.
        // A resident can join a camp or die, so never recycle an identity when
        // the same game day opens another apartment.
        for (var next = Math.Max(RuntimeResidentIdStart, world.NextRuntimeObjectId); next < int.MaxValue; next++)
        {
            var candidate = new EntityId(next);
            if (world.Entities.Npcs.ContainsKey(candidate) || world.Entities.Corpses.ContainsKey(candidate) ||
                world.PlayerControlledNpcs.Contains(next) || world.DeathRecords.Any(record => record.EntityId == candidate) ||
                world.CreationConfig?.Characters.Any(character => character?.Id == next) == true) continue;

            world.NextRuntimeObjectId = next + 1;
            id = candidate;
            return true;
        }

        id = default;
        return false;
    }

    private static IEnumerable<string> AvailableWear(WorldState world) =>
        PortalContentPolicy.Wear.Where(id => GarmentLibrary.IsPortalWearAvailable(id) &&
            world.Content.ObjectDefinitions.TryGetValue(id, out var definition) && definition.Layer == WearLayer.Wear);

    private static int RollIndex(int seed, int serial, int slot, int salt, int count) =>
        Math.Min(count - 1, (int)(MathUtil.Hash01(seed, serial, slot, salt) * count));
}

}
