using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Memory
{

// Spec 27.18A: v1 spatial object memory — what the NPC knows exists and
// where. Sightings upsert; negative evidence and TTL remove.
public sealed class MemoryState
{
    public Dictionary<ObjectId, ObjectMemory> KnownObjects { get; } = new();

    // §22.7/27.18A: ЛЮБАЯ мутация состава KnownObjects (добавление, удаление)
    // обязана поднять Version — по нему PerceptionSystem понимает, что его
    // кэшированный вид памяти устарел. Правки полей УЖЕ видимой записи
    // (Tile/LastSeenTick при живом взгляде) бампа не требуют: видимые записи
    // в вид памяти не входят. Не сериализуется: после загрузки кэш строится
    // заново от несовпадения ключа.
    public int Version { get; set; }

    // §125.3: память последней встречи с ЛЮДЬМИ — «видела её там-то тогда-то».
    // Живые списки восприятия радиусные (§125.2), поэтому знание о тех, кто
    // сейчас вне глаз, живёт здесь и НЕ подмешивается в Perception.Agents:
    // потребитель «сходить туда, где я её видела» читает словарь напрямую,
    // как KnownObjects. Version эта память не трогает — он ключ кэша вида
    // ОБЪЕКТОВ, и лишние бампы стоили бы лишних перестроек.
    public Dictionary<Common.EntityId, AgentMemory> KnownAgents { get; } = new();

    // Spec 29C.4A: places where this NPC was attacked. TTL 2400 ticks, cap 8.
    public List<DangerMemory> Dangers { get; } = new();

    // Behavior audit (Jul 2026): objects that turned out occupied on arrival.
    // A short personal "don't chase that one again" note so the planner picks
    // a DIFFERENT source next time instead of oscillating against the same
    // contested coconut for half a day (the thirst-death class of seed 12345).
    // Transient by design — not persisted; an empty table after load is fine.
    public Dictionary<ObjectId, int> ShunnedUntil { get; } = new();

    public void Shun(ObjectId id, int untilTick) => ShunnedUntil[id] = untilTick;

    public bool IsShunned(ObjectId id, int tick) =>
        ShunnedUntil.TryGetValue(id, out var until) && until > tick;
}

/// <summary>§125.3: одна запись «кого и где я видела». Намеренно лёгкая и НЕ
/// <c>PerceivedAgent</c>: тот несёт живые флаги (занятость, страдание,
/// достижимость), которым за пределами видимости взяться неоткуда.</summary>
public sealed class AgentMemory
{
    public Common.EntityId Id { get; set; }

    public Agents.Faction Faction { get; set; } = Agents.Faction.Colony;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public int LastSeenTick { get; set; }
}

public sealed class DangerMemory
{
    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public int Tick { get; set; }
}

public sealed class ObjectMemory
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId? Junction { get; set; }

    // Seeded home knowledge (bootstrap objects) never expires.
    public bool IsPermanent { get; set; }

    public int LastSeenTick { get; set; }
}

}
