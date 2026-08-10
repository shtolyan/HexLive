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

// §28.15C v5: человеческий труп двое ИГРОВЫХ суток остаётся телом,
// затем тяжёлый NPCState заменяется одним лёгким объектом «скелет + мешок»;
// ещё через двое суток останки окончательно исчезают.
// Все карманы и одежда складываются в Contents этого ОДНОГО объекта — никакой
// россыпи десятков предметов по гексу. Поза, курс и якорь переезжают без изменений.
// §50/§54: по тегу Decays продолжают истлевать отдельные конечности и звериные туши.
public sealed class CorpseSystem : ISimulationSystem
{
    private const int HumanCorpseLifetimeDays = 2;
    private const int HumanRemainsLifetimeDays = 2;

    public static int HumanCorpseLifetimeTicks =>
        HumanCorpseLifetimeDays * EnvironmentSystem.DayLengthTicks;

    public static int HumanRemainsLifetimeTicks =>
        HumanRemainsLifetimeDays * EnvironmentSystem.DayLengthTicks;

    public string Name => nameof(CorpseSystem);

    public TickLayer Layer => TickLayer.Slow;

    private readonly System.Collections.Generic.List<ObjectId> _decayed = new();
    private readonly System.Collections.Generic.List<ObjectId> _skeletonized = new();

    public void Run(WorldState world)
    {
        _decayed.Clear();
        _skeletonized.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.CorpseNpc)
            {
                var body = CorpseMath.BodyOf(world, obj);
                // Нельзя заменить актёра мешком прямо в руках. Часы не
                // останавливаются: просроченное тело сменит стадию сразу после
                // выкладывания, потому что SpawnTick остаётся исходным.
                if (body is not null && body.IsBeingCarried)
                {
                    continue;
                }

                if (world.Tick - obj.SpawnTick >= HumanCorpseLifetimeTicks)
                {
                    _skeletonized.Add(obj.Id);
                }
                continue;
            }

            if (obj.DefinitionId == ContentIds.HumanRemains &&
                world.Tick - obj.SpawnTick >= HumanRemainsLifetimeTicks)
            {
                _decayed.Add(obj.Id);
                continue;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains(ObjectTags.Decays))
            {
                continue;
            }

            obj.ResourceAmount -= 16f;
            if (obj.ResourceAmount <= 0f)
            {
                _decayed.Add(obj.Id);
            }
        }

        foreach (var id in _skeletonized)
        {
            SkeletonizeHumanCorpse(world, id);
        }

        foreach (var id in _decayed)
        {
            WorldObjectMutations.DespawnObject(world, id);
            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "CorpseGone", $"Obj={id.Value} decayed");

            }
        }
    }

    private static void SkeletonizeHumanCorpse(WorldState world, ObjectId corpseId)
    {
        if (!world.Entities.Objects.TryGetValue(corpseId, out var anchor) ||
            anchor.Junctions.Count == 0 ||
            CorpseMath.BodyOf(world, anchor) is not { } body)
        {
            return;
        }

        var junction = anchor.Junctions[0];
        var fragment = anchor.Fragment;
        var tile = anchor.Tile;

        WorldObjectMutations.DespawnObject(world, corpseId);
        var remains = WorldObjectMutations.SpawnObject(
            world, ContentIds.HumanRemains, fragment, tile, junction);
        remains.CurrentUser = body.Id;
        remains.RotationDegrees = body.RotationDegrees;
        remains.Variant = (body.DeathAnimVariant & 1).ToString();

        // Порядок важен и после истления: сначала карманы, затем одежда.
        remains.Contents.AddRange(body.Inventory.Items);
        remains.Contents.AddRange(body.WornItems);
        world.Entities.Corpses.Remove(body.Id);

        // Тот, кто уже оплакал тело, не переживает ту же смерть повторно
        // только потому, что якорь сменил id.
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.GrievedCorpses.Contains(corpseId) &&
                !npc.Mind.GrievedCorpses.Contains(remains.Id))
            {
                npc.Mind.GrievedCorpses.Add(remains.Id);
            }
        }

        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "CorpseSkeletonized",
                $"NPC{body.Id.Value} Obj={corpseId.Value}->{remains.Id.Value} " +
                $"Items={remains.Contents.Count} Variant={remains.Variant}");
        }
    }
}

}
