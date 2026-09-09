using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Core
{

public sealed class EntityRepository
{
    public Dictionary<EntityId, NPCState> Npcs { get; } = new();

    /// <summary>
    /// §28.15C v4: свежие тела. Умершая на двое игровых суток переезжает сюда
    /// целиком: имя, тело с ранами, надетое и карманы. Затем <c>CorpseSystem</c> заменяет её
    /// одним лёгким <c>remains.human</c>, а вещи переносит в его мешок-<c>Contents</c>.
    ///
    /// <para>
    /// ⭐ Почему ОТДЕЛЬНЫЙ словарь, а не флаг <c>IsDead</c> в <see cref="Npcs"/>:
    /// живой ростер обходят несколько десятков систем, и КАЖДАЯ из них должна
    /// была бы вспомнить про флаг. Забытая проверка — это труп, который решает,
    /// голосует в аукционе и идёт за водой; молчаливо и не сразу. Здесь же
    /// «мёртвых не тикает никто» — свойство структуры, а не дисциплины: чтобы
    /// труп ожил, его пришлось бы сначала явно достать отсюда.
    /// </para>
    /// <para>
    /// Обратная сторона — тело не участвует ни в чём само: всё, что с ним
    /// делают (оплакать, обобрать, разделать), идёт через объект-якорь
    /// <c>corpse.npc</c>, который стоит на том же джанкшене и хранит id
    /// покойной в <c>CurrentUser</c>.
    /// </para>
    /// </summary>
    public Dictionary<EntityId, NPCState> Corpses { get; } = new();

    public Dictionary<ObjectId, WorldObjectState> Objects { get; } = new();

    // §26.26: only objects carrying occupancy/user state, never the whole map.
    internal HashSet<WorldObjectState> ObjectReservations { get; } = new();
    internal HashSet<(ObjectId, EntityId)> LiveObjectReservations { get; } = new();
    internal HashSet<ObjectId> UsedBodyObjects { get; } = new();
    internal List<WorldObjectState> ObjectReservationScratch { get; } = new();

    public void RegisterObject(WorldObjectState obj)
    {
        if (Objects.TryGetValue(obj.Id, out var previous))
            previous.AttachReservationIndex(null);
        Objects[obj.Id] = obj;
        obj.AttachReservationIndex(ObjectReservations);
    }

    public void UnregisterObject(WorldObjectState obj)
    {
        obj.AttachReservationIndex(null);
        Objects.Remove(obj.Id);
    }

    public void ClearObjects()
    {
        foreach (var obj in Objects.Values) obj.AttachReservationIndex(null);
        Objects.Clear();
        ObjectReservations.Clear();
    }
}

}
