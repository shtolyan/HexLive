using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §118.8: отлёживание раненых. Ниже WoundedRestEnterHealth колонистка сама
/// ложится (сон — тот же канал, кровать предпочитается штатным планировщиком
/// сна) и не встаёт, пока восстановимое здоровье не дойдёт до
/// WoundedRestExitHealth. Гистерезис enter&lt;exit гасит дребезг «лёг-встал».
/// </summary>
internal static class WoundedRestMath
{
    // Health (среднее по СЕМИ зонам) для этой цели врёт: отсечённая нога
    // прибита к нулю навсегда, и калека с Health ≤ 0.857 никогда не достигла
    // бы порога подъёма — латч держал бы её в кровати до конца игры. Меряем
    // только то, что в принципе может зажить: среднее по неотсечённым зонам.
    internal static float RecoverableHealth(NPCState npc)
    {
        var sum = 0f;
        var count = 0;
        foreach (var pair in npc.Body.Parts)
        {
            if (npc.Body.IsSevered(pair.Key))
            {
                continue;
            }

            sum += pair.Value;
            count++;
        }

        return count > 0 ? sum / count : 1f;
    }

    // Вход: пора ложиться (решение, DecisionSystem). Мёртвых и лежащих в
    // умирании/коме сюда не заносит — они до аукциона не доходят.
    internal static bool NeedsRest(NPCState npc) =>
        Spec118.Enabled && Spec118.WoundedRestEnabled &&
        RecoverableHealth(npc) < Spec118.WoundedRestEnterHealth;

    // Выход: рано вставать (перевзвод сна, ExecutionSystem.ShouldKeepSleeping).
    // Вызывается ПОСЛЕ общего прерывателя сна, так что голод/жажда за потолком
    // и опасность будят раненую как всех — отлежаться насмерть нельзя.
    internal static bool ShouldKeepLying(NPCState npc) =>
        Spec118.Enabled && Spec118.WoundedRestEnabled &&
        RecoverableHealth(npc) < Spec118.WoundedRestExitHealth;
}

}
