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
        resident.SpeechOrigin = NpcSpeechOrigin.Modern;
        world.PlayerControlledNpcs.Remove(id.Value);
        resident.Inventory.Items.Clear();
        resident.Traits.Clear();
        resident.Traits.Add(TraitKind.Coward);
        resident.Needs.Stress = .8f;
        if (Spec76.Enabled && Spec76.AttributeSpread > 0f)
        {
            foreach (var attribute in AttributeSet.All)
            {
                var deviation = resident.Attributes.Get(attribute) - Spec76.AttributeMean - Spec76.AttributeBonus;
                resident.Attributes.Set(attribute, MathUtil.Clamp01(Spec76.AttributeMean + deviation * .35f));
            }
        }

        resident.WornItems.Clear();
        var clothes = AvailableWear(world).Where(item => GarmentLibrary.FitsSex(resident.Sex, item.Id)).ToArray();
        WearPiece(WearLayer.Underwear, BodyPart.Pelvis, 17666);
        WearPiece(WearLayer.Underwear, BodyPart.Torso, 17667);
        WearPiece(WearLayer.Wear, BodyPart.Pelvis, 17662);
        WearPiece(WearLayer.Wear, BodyPart.Torso, 17663);
        if (MathUtil.Hash01(world.Seed, visitSerial, 0, 17668) < .5f)
        {
            var extras = clothes.Where(item => item.Layer != WearLayer.Underwear ||
                !item.Covers.Contains(BodyPart.Pelvis) && !item.Covers.Contains(BodyPart.Torso));
            AddRandomPiece(extras, 17669);
        }

        EquipmentMath.Recalculate(world, resident);
        world.Portal.Resident = resident.Id;
        return resident;

        void WearPiece(WearLayer layer, BodyPart part, int salt)
        {
            if (layer == WearLayer.Wear && resident.WornItems.Any(item =>
                    world.Content.ObjectDefinitions[item.DefinitionId] is { Layer: WearLayer.Wear } definition &&
                    definition.Covers.Contains(part))) return;
            AddRandomPiece(clothes.Where(item => item.Layer == layer && item.Covers.Contains(part) &&
                (layer != WearLayer.Underwear || part != BodyPart.Torso || !item.Covers.Contains(BodyPart.Pelvis))), salt);
        }

        void AddRandomPiece(IEnumerable<GarmentParams> candidates, int salt)
        {
            var families = candidates.Where(item => resident.WornItems.All(existing => !WearSlotCatalog.Occupies(
                    world.Content.ObjectDefinitions[item.Id], world.Content.ObjectDefinitions[existing.DefinitionId])))
                .GroupBy(item => item.PrototypeId, StringComparer.Ordinal).ToArray();
            if (families.Length == 0) return;
            var variants = families[RollIndex(world.Seed, visitSerial, 0, salt, families.Length)].ToArray();
            var chosen = variants[RollIndex(world.Seed, visitSerial, 1, salt, variants.Length)];
            resident.WornItems.Add(new ItemInstance(chosen.Id) { OwnerId = resident.Id.Value });
        }
    }

    internal static int StockWardrobe(WorldState world, WorldObjectState wardrobe, int visitSerial)
    {
        if (visitSerial < 1 || wardrobe.DefinitionId != "furniture.modern_wardrobe" ||
            wardrobe.Fragment != PortalWorld.ApartmentFragment || wardrobe.Contents.Count > 0)
            return 0;

        // Bug #743: draw distinct MODELS first, then one of that model's
        // colourways, so a model with eighteen recolours does not crowd out
        // the rest and two pieces on one rail are never the same garment.
        var families = AvailableWear(world)
            .GroupBy(item => string.IsNullOrEmpty(item.PrototypeId) ? item.Id : item.PrototypeId, StringComparer.Ordinal)
            .Select(family => family.ToArray()).ToList();
        var count = Math.Min(families.Count, 2 + RollIndex(world.Seed, visitSerial, 0, 17664, 3));
        for (var i = 0; i < count; i++)
        {
            var index = RollIndex(world.Seed, visitSerial, i, 17665, families.Count);
            var variants = families[index];
            wardrobe.Contents.Add(new ItemInstance(variants[RollIndex(world.Seed, visitSerial, i, 17671, variants.Length)].Id));
            families.RemoveAt(index);
        }
        return count;
    }

    /// <summary>§176.3: a garment the apartment may stock or dress its resident in.</summary>
    internal static bool IsApartmentWear(WorldState world, string definitionId) =>
        AvailableWear(world).Any(item => item.Id == definitionId);

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

    // Bug #743: the approved Modern set is only as large as what the registry
    // has published of it (the portal release activated three garments), so
    // every apartment showed the same shorts, crop top and halter. The live civilian island
    // catalogue (sneakers, shirts, jackets, dresses and their colourways) joins
    // it. Only definitions the world actually loaded are eligible, so content
    // without a live record and model can never be generated.
    private static IEnumerable<GarmentParams> AvailableWear(WorldState world) =>
        GarmentLibrary.Active.Where(item => item != null && !item.Retired &&
                (PortalContentPolicy.IsPortalWear(item.Id) ||
                 PortalContentPolicy.IsCivilianIslandWear(item.Id, item.PrototypeId)) &&
                world.Content.ObjectDefinitions.TryGetValue(item.Id, out var definition) && definition.Layer == item.Layer)
            .GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.Ordinal);

    private static int RollIndex(int seed, int serial, int slot, int salt, int count) =>
        Math.Min(count - 1, (int)(MathUtil.Hash01(seed, serial, slot, salt) * count));
}

}
