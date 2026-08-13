using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime.Journal
{

/// <summary>
/// Spec §136: каким голосом сделана запись.
///
/// <para>
/// Регистр берётся из состояния NPC в момент закрытия часа, а не из типа
/// события: «меня перевязали» от умирающей и от здоровой — это разные фразы.
/// Проверки идут по убыванию неотложности: смертельное перекрывает телесное,
/// телесное — душевное.
/// </para>
/// </summary>
public static class JournalMood
{
    // Пороги подобраны под уже существующие: 0.45 — тот же «хочешь спать — спи»
    // из §49.9 r2, 0.7 — вход в голод/жажду, при которых §63 уже считает NPC
    // в беде. Отдельными ручками они не вынесены намеренно: это не баланс, это
    // тон, и он должен ехать за балансом, а не спорить с ним.
    private const float DyingBlood = 0.35f;
    private const float DyingHealth = 0.3f;
    private const float SufferingHealth = 0.62f;
    private const float SufferingNeed = 0.7f;
    private const float WearyEnergy = 0.3f;
    private const float BitterStress = 0.7f;
    private const float TenderStress = 0.25f;

    public static JournalRegister Of(NPCState npc, JournalPerspective perspective)
    {
        if (npc == null)
        {
            return JournalRegister.Plain;
        }

        // §105: идёт отсчёт — всё остальное неважно.
        if (npc.IsDying || npc.Needs.Blood <= DyingBlood || npc.Health <= DyingHealth)
        {
            return JournalRegister.Dying;
        }

        if (npc.Health <= SufferingHealth ||
            npc.Wounds.Count > 0 ||
            npc.Needs.Hunger >= SufferingNeed ||
            npc.Needs.Thirst >= SufferingNeed)
        {
            return JournalRegister.Suffering;
        }

        if (npc.Needs.Energy <= WearyEnergy || npc.Needs.Stamina <= WearyEnergy)
        {
            return JournalRegister.Weary;
        }

        if (npc.Needs.Stress >= BitterStress)
        {
            return JournalRegister.Bitter;
        }

        // Тёплый регистр достаётся только тому, с кем что-то СДЕЛАЛИ: «мне
        // помогли» звучит нежно, «я помогла» — обыденно. Иначе каждая спокойная
        // запись была бы умилённой.
        if (perspective == JournalPerspective.Received && npc.Needs.Stress <= TenderStress)
        {
            return JournalRegister.Tender;
        }

        return JournalRegister.Plain;
    }

    /// <summary>
    /// Тепло автора ко второй участнице.
    ///
    /// ⚠️ Читает <c>Relationships</c> напрямую, а НЕ через
    /// <c>SocialState.GetOrCreate</c>: тот создаёт отношение, а отношение
    /// сериализуется в сейв и едет по проводу — дневник изменил бы мир одним
    /// фактом того, что его открыли.
    /// </summary>
    public static JournalBond BondTo(NPCState npc, EntityId? subjectId)
    {
        if (npc == null || !subjectId.HasValue ||
            !npc.Social.Relationships.TryGetValue(subjectId.Value, out var rel))
        {
            return JournalBond.Neutral;
        }

        if (rel.Affinity >= 0.6f) return JournalBond.Beloved;
        if (rel.Affinity >= 0.2f) return JournalBond.Friend;
        if (rel.Affinity <= -0.5f) return JournalBond.Hated;
        if (rel.Affinity <= -0.15f) return JournalBond.Cold;
        return JournalBond.Neutral;
    }

    /// <summary>
    /// Какой из вариантов фразы взять.
    ///
    /// ⭐ ЧИСТЫЙ хеш, а не мировой RNG. Дневник обязан быть косметикой: возьми
    /// он бросок из мира — сдвинулась бы вся последующая цепочка случайности, и
    /// <c>Tools/golden_trace.sh</c> показал бы расхождение поведения там, где
    /// поведение не менялось. FNV-1a взят потому, что он одинаков на всех
    /// платформах, в отличие от <c>string.GetHashCode</c>.
    /// </summary>
    public static byte Variant(int npcId, int tick, string type)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;

            var hash = offset;
            hash = (hash ^ (uint)npcId) * prime;
            hash = (hash ^ (uint)tick) * prime;

            if (!string.IsNullOrEmpty(type))
            {
                foreach (var c in type)
                {
                    hash = (hash ^ c) * prime;
                }
            }

            // Вид сам решает, сколько вариантов у термина, и берёт остаток по
            // их числу. Здесь — просто равномерный байт.
            return (byte)(hash & 0xFF);
        }
    }
}

}
