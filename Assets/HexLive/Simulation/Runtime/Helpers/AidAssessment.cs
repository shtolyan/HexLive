using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// Spec §53.3: чем именно этой соседке нужно помочь прямо сейчас и насколько
// это срочно (0..1). Порядок срочности: Treat → Medicate → Hydrate → Feed →
// Console; жажда стоит ПЕРЕД голодом и выигрывает ничьи, потому что убивает
// быстрее.
//
// ⭐ Почему это отдельный файл. Формула жила в ДВУХ копиях — в перцепции
// (кого вообще стоит идти спасать) и в ExecutionSystem.Social.AssessAidKind
// (переоценка по прибытии: она могла поправиться, ухудшиться или умереть,
// пока помощница шла). Копии обязаны совпадать по построению — расхождение
// читается не как баг, а как «дошла и передумала», то есть как аборт плана на
// ровном месте. §105 добавил в формулу умирающую, и это был ровно тот момент,
// когда одну из двух копий забыли бы.
internal static class AidAssessment
{
    public static AidKind Assess(NPCState target, int tick, out float severity)
    {
        severity = 0f;
        if (target.Health <= 0f)
        {
            return AidKind.None;
        }

        // §105: УМИРАЮЩАЯ бьёт всё остальное. Срочность максимальна, а вид
        // помощи диктует причина: кровь и разбитая грудь просят перевязки,
        // голод — еды, жажда — воды. Ждать, пока обычная формула догадается,
        // нельзя: у неё запас тикает.
        if (target.IsDying)
        {
            severity = 1f;
            return target.Mind.DyingCause switch
            {
                DyingCause.Starvation => AidKind.Feed,
                DyingCause.Dehydration => AidKind.Hydrate,
                _ => AidKind.Treat
            };
        }

        // Treat — open wounds / blood loss (a bleed-out is on a clock).
        var treatSev = target.Wounds.Count > 0 || target.Needs.Blood < 0.6f
            ? System.Math.Max(1f - target.Needs.Blood, 1f - target.Health)
            : 0f;
        // Medicate — actively sick, or gravely weak with nothing to dress.
        var medSev = target.Mind.SickUntilTick > tick
            ? 0.6f
            : (target.Health < 0.4f && target.Wounds.Count == 0 ? 1f - target.Health : 0f);
        // Hydrate — parched (thirst kills faster than hunger, so it is checked
        // before Feed and wins ties). Without this a dehydrating housemate
        // registered NO helpable suffering and got fed while dying of thirst
        // (seed 1104049673).
        var hydrateSev = target.Needs.Thirst >= 0.55f ? target.Needs.Thirst : 0f;
        // Feed — genuinely hungry (not a passing dip).
        var feedSev = target.Needs.Hunger >= 0.55f ? target.Needs.Hunger : 0f;
        // Console — grieving or breaking under stress (soft, lowest).
        var consoleSev = tick < target.Mind.GrievingUntilTick ? 0.5f : 0f;
        if (target.Needs.Stress > 0.6f)
        {
            consoleSev = System.Math.Max(consoleSev, target.Needs.Stress * 0.6f);
        }

        severity = treatSev;
        var kind = AidKind.Treat;
        if (medSev > severity) { severity = medSev; kind = AidKind.Medicate; }
        if (hydrateSev > severity) { severity = hydrateSev; kind = AidKind.Hydrate; }
        if (feedSev > severity) { severity = feedSev; kind = AidKind.Feed; }
        if (consoleSev > severity) { severity = consoleSev; kind = AidKind.Console; }
        if (severity <= 0f) { kind = AidKind.None; }
        return kind;
    }
}

}
