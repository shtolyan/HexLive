using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Wildlife
{

// §147.1: патрульный слот виртуального зверя. Ключевой инвариант: слот в
// состоянии Virtual НЕ владеет MobState/RabbitState вовсе — все ~20
// потребителей world.Mobs (Perception, ThreatAlert, DangerRing, overlap,
// бой, движение, решения) не знают о слотах по построению. Игрок при этом
// зверя ВИДИТ: клиент рисует превью по чистой функции MobPreview.PreviewPose
// от сида — ноль байт wire в стедистейте.
public enum MobSlotState
{
    // Превью гуляет по кольцу; симуляция зверя не видит совсем.
    Virtual = 0,

    // Зверь материализован: живёт обычный MobState/RabbitState с
    // Id == ReservedMobId; превью не рисуется (вью один и тот же).
    Live = 1,

    // Зверя убили; после CooldownUntilTick слот детерминированно
    // переезжает на новый дом и снова становится Virtual.
    Cooldown = 2
}

// Вэйпоинт кольца несёт всё, что нужно рендеру (позиция + тайл для GroundY)
// и симуляции (джанкшен для материализации) — клиент не лазит в топологию.
public struct PatrolWaypoint
{
    public JunctionId Junction;

    public TileCoord Tile;

    public Float2 Position;
}

public sealed class MobSpawnSlot
{
    public int SlotId;

    // "dog" | "crab" — каталожный id вида.
    public string MobId = string.Empty;

    public MobSlotState State;

    // Выделен из world.NextMobId/NextRabbitId ОДИН раз при создании слота:
    // живой зверь и превью-вью делят один id, поэтому переход превью→живой
    // бесшовен и полю поворота в MobState неоткуда понадобиться (§147.5).
    public int ReservedMobId;

    public JunctionId HomeJunction;

    // §147.1: кольцо печётся один раз при создании/ре-хоуминге слота,
    // сохраняется в блоб и едет клиенту — класс багов «клиент испёк кольцо
    // по другой топологии» убит структурно.
    public List<PatrolWaypoint> Ring = new();

    public int CooldownUntilTick;

    // Здоровье, с которым слот материализуется (задел под фазу 2 §147.7 —
    // ре-виртуализацию недобитых; сейчас всегда максимум вида).
    public float StoredHealth;

    // Растёт на каждом цикле смерти — соль детерминированного ре-хоуминга.
    public int CycleIndex;
}

}
