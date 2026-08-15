using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{

/// <summary>§125: радиус восприятия ЛЮДЕЙ — единственное место, где
/// характеристика Восприятие превращается в гексы. Презентация получает
/// готовое число полем снапшота и второго мнения не заводит.</summary>
internal static class PerceptionMath
{
    // round(attr × 16): среднее тело (0.5) видит на 8 гексов, полный ролл —
    // 3..16. Сама характеристика не поднимается: расширяется только сенсор.
    // Объектное зрение §22.7 этой формулой НЕ управляется (§76.11).
    //
    // ⭐ ПОЛ В ТРИ ГЕКСА. Бюджет §76.2 обязан положить чью-то ось на дно
    // полосы, и замер на 200 сидах дал ноль у 10.8% колонисток. Ноль означал
    // «не вижу человека, стоящего вплотную»: она не заговорит, не поможет, не
    // заметит врага и не накопит ни одной встречи в памяти — то есть выпадет
    // из жизни колонии целиком. Заметить того, кто рядом, — это не зоркость,
    // а присутствие, поэтому оно не роллится. Ненаблюдательная всё равно
    // остаётся менее зоркой: 37 гексов обзора против 217 у среднего тела.
    public const int MinRadiusTiles = 3;

    public static int RadiusTiles(NPCState npc) =>
        System.Math.Max(
            MinRadiusTiles,
            (int)System.MathF.Round(
                npc.Attributes.Perception * Spec76.PerceptionRadiusPerAttribute));

    /// <summary>§125.6: ВИДИТ ЛИ ОНА ЕЁ ПРЯМО СЕЙЧАС — один вопрос и один
    /// ответ на всю игру. Спрашивает готовые списки восприятия, а не меряет
    /// дистанцию заново: PerceptionSystem уже прошёл кольцо в начале этого же
    /// medium-тика (он четвёртый в реестре, все потребители — после), и второй
    /// замер отличался бы от первого ровно тогда, когда кто-то поменяет радиус
    /// в одном месте и забудет в другом.
    /// <para>
    /// Списки короткие (соседи, а не остров), поэтому линейный поиск здесь
    /// дешевле словаря: словарь на каждую NPC стоил бы памяти и промахов кэша
    /// ради десятка элементов.
    /// </para></summary>
    public static bool Sees(NPCState observer, EntityId target)
    {
        var allies = observer.Perception.Agents;
        for (var i = 0; i < allies.Count; i++)
        {
            if (allies[i].Id.Equals(target))
            {
                return true;
            }
        }

        var hostiles = observer.Perception.Hostiles;
        for (var i = 0; i < hostiles.Count; i++)
        {
            if (hostiles[i].Id.Equals(target))
            {
                return true;
            }
        }

        return false;
    }
}

}
