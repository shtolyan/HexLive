using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{

/// <summary>§125: радиус восприятия ЛЮДЕЙ — единственное место, где
/// характеристика Восприятие превращается в гексы. Презентация получает
/// готовое число полем снапшота и второго мнения не заводит.</summary>
internal static class PerceptionMath
{
    // round(attr × 10): среднее тело (0.5) видит на 5 гексов, «Восприятие 8»
    // с листа персонажа — буквально 8 гексов, полный ролл — 0..10. Радиус 0 —
    // осознанно допустимая слепая (§125.1). Объектное зрение §22.7 этой
    // формулой НЕ управляется (§76.11).
    public static int RadiusTiles(NPCState npc) =>
        (int)System.MathF.Round(
            npc.Attributes.Perception * Spec76.PerceptionRadiusPerAttribute);

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
