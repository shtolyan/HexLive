using HexLive.Simulation.Agents;

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
}

}
