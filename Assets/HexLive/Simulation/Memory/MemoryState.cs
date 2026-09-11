using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Memory
{

// Spec 27.18A: v1 spatial object memory — what the NPC knows exists and
// where. Sightings upsert; negative evidence and TTL remove.
public sealed class MemoryState
{
    public Dictionary<ObjectId, ObjectMemory> KnownObjects { get; } = new();

    // §27.18A r3: personal object-survey history, not shared player fog.
    public const int SurveyCapacity = 4096;
    public Dictionary<TileCoord, int> SurveyedTiles { get; } = new();

    public void RememberSurvey(TileCoord tile, int tick)
    {
        if (!SurveyedTiles.ContainsKey(tile) && SurveyedTiles.Count >= SurveyCapacity)
        {
            var oldest = default(TileCoord);
            var oldestTick = int.MaxValue;
            var found = false;
            foreach (var pair in SurveyedTiles)
                if (!found || pair.Value < oldestTick || pair.Value == oldestTick &&
                    (pair.Key.Q < oldest.Q || pair.Key.Q == oldest.Q && pair.Key.R < oldest.R))
                {
                    found = true;
                    oldest = pair.Key;
                    oldestTick = pair.Value;
                }
            SurveyedTiles.Remove(oldest);
        }
        SurveyedTiles[tile] = tick;
    }

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

/// <summary>§125.3: одна запись «кого, где и в каком виде я видела».
/// Намеренно лёгкая и НЕ <c>PerceivedAgent</c>: тот несёт живые флаги
/// (занятость, движение, достижимость), которым за пределами видимости взяться
/// неоткуда — и любая попытка их «помнить» была бы выдумкой.
/// <para>
/// §125.7 хранит ВЕРДИКТ, а не улики. Здоровье, кровь, раны, нужды и таймеры —
/// это пятнадцать полей, которые пришлось бы держать на каждую пару
/// наблюдатель×виденная (O(N²) байт на поле) и заново сворачивать второй
/// формулой. Но <c>AidAssessment.Assess</c> уже сворачивает их в два числа —
/// «чем помочь» и «насколько плохо», — и уже считает их для всего ростера
/// каждый medium-тик. Поэтому снимок стоит одно присваивание и ноль новых
/// вычислений, а порядок важности видов помощи наследуется сам.
/// </para></summary>
public sealed class AgentMemory
{
    public Common.EntityId Id { get; set; }

    public Agents.Faction Faction { get; set; } = Agents.Faction.Colony;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    /// <summary>Узел, на котором я её видела. План помощи требует именно узел
    /// (от него ищется точка подхода) — у памяти объектов поле есть ровно за
    /// этим же.</summary>
    public Common.JunctionId? Junction { get; set; }

    public int LastSeenTick { get; set; }

    /// <summary>Насколько плохо ей было в последний раз, 0..1.</summary>
    public float Suffering { get; set; }

    /// <summary>Чем ей было нужно помочь: вид припаса, который стоит взять.</summary>
    public AI.AidKind AidKind { get; set; } = AI.AidKind.None;

    /// <summary>Была ли она беспомощна — умирала или лежала без сознания.
    /// Один флаг закрывает оба вопроса §105 и §118.</summary>
    public bool Helpless { get; set; }
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
