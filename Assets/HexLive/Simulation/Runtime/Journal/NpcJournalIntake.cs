using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime.Journal
{

/// <summary>
/// Spec §136: приём события в дневники. Единственная точка входа, зовётся из
/// <c>Trace.Emit</c> — там же, где кормится бортовой самописец (§30.14).
///
/// <para>
/// ⭐ Почему хук в эмите, а не система, читающая <c>world.Events</c>: §30.17
/// прямо запрещает системам читать кольцо событий, и правильно — кольцо на
/// 2048 живёт ~11 тиков и трогать его как источник состояния значит потерять
/// половину. Хук в эмите видит КАЖДОЕ событие ровно один раз и одинаково в
/// игре, на сервере и в headless-соаке.
/// </para>
/// <para>
/// ⭐ Дневник НИЧЕГО не меняет в мире. Отсюда два запрета, каждый из которых
/// показал бы себя расхождением golden-trace: не звать
/// <c>SocialState.GetOrCreate</c> (он СОЗДАЁТ отношение, а оно сериализуется и
/// едет по проводу) и не трогать мировой RNG.
/// </para>
/// </summary>
public static class NpcJournalIntake
{
    public static void Offer(WorldState world, EntityId? entityId, string type, string message)
    {
        if (!Spec136.Enabled || world == null || string.IsNullOrEmpty(type))
        {
            return;
        }

        // Хроника и только она: отладочный шум физически не может попасть в
        // дневник, даже если где-то забудут гейт на диагностическом эмите.
        if (!GameEventTypes.IsPlayerVisible(type))
        {
            return;
        }

        var rule = JournalCatalog.For(type);
        if (rule.Weight <= 0 && rule.MirrorWeight <= 0)
        {
            return;
        }

        var author = entityId;
        var subject = Resolve(world, rule.Subject, rule.SubjectToken, message);

        // Системное событие («убита», «зверь унёс ногу») приходит без EntityId,
        // но человек в нём есть — каталог указывает, где именно. Тогда роль
        // субъекта и ЕСТЬ хозяин записи: это про неё, а не про колонию.
        if (!author.HasValue && subject.HasValue)
        {
            author = subject;
            subject = null;
        }

        // «Я помогла себе» — не запись. Если роль свелась к самому автору,
        // имени в записи просто не будет.
        if (subject.HasValue && author.HasValue && subject.Value == author.Value)
        {
            subject = null;
        }

        var extra = ExtraOf(rule, message);

        if (rule.Weight > 0)
        {
            OfferTo(world, author, rule.Weight, type, rule.Perspective, subject, extra);
        }

        // Зеркало: та же сцена глазами второй участницы. Автор и субъект
        // меняются местами, перспектива переворачивается.
        if (rule.MirrorWeight > 0 && subject.HasValue)
        {
            var mirrored = rule.Perspective == JournalPerspective.Did
                ? JournalPerspective.Received
                : JournalPerspective.Did;
            OfferTo(world, subject, rule.MirrorWeight, type, mirrored, author, extra);
        }
    }

    private static void OfferTo(
        WorldState world,
        EntityId? ownerId,
        int weight,
        string type,
        JournalPerspective perspective,
        EntityId? subjectId,
        string extra)
    {
        if (!ownerId.HasValue || !world.Entities.Npcs.TryGetValue(ownerId.Value, out var owner))
        {
            return;
        }

        var journal = owner.Journal;
        var hour = world.Tick / HourTicks();

        // Новый час — прежний кандидат уже закрыт системой; если система ещё не
        // добежала (первый эмит часа), кандидат просто начинается заново.
        if (journal.PendingHour != hour)
        {
            journal.ClearPending();
            journal.PendingHour = hour;
        }

        if (weight < Spec136.MinorWeight)
        {
            // Бытовое в отдельную запись не идёт никогда — только перечислением
            // внутри тихой («пилила дрова, грелась у костра»).
            journal.NoteChore(type);
            return;
        }

        if (weight <= journal.PendingWeight)
        {
            return;
        }

        journal.PendingWeight = weight;
        journal.PendingType = type;
        journal.PendingPerspective = perspective;
        journal.PendingExtra = extra;
        journal.PendingSubjectId = subjectId.HasValue && subjectId.Value != owner.Id
            ? subjectId
            : null;
        journal.PendingSubjectNameId = SubjectNameId(world, subjectId, owner);
    }

    /// <summary>
    /// Id имени второй участницы (§74: <c>DisplayName</c> — латинский id вроде
    /// <c>jolly</c>, вид превращает его в «Джоли»). Мёртвые не теряются: труп
    /// остаётся в реестре, и запись о нём должна называть её по имени.
    /// </summary>
    private static string SubjectNameId(WorldState world, EntityId? subjectId, NPCState owner)
    {
        if (!subjectId.HasValue || subjectId.Value == owner.Id)
        {
            return null;
        }

        return world.Entities.Npcs.TryGetValue(subjectId.Value, out var subject)
            ? subject.DisplayName
            : null;
    }

    private static EntityId? Resolve(WorldState world, JournalRole role, string token, string message)
    {
        int? value = role switch
        {
            JournalRole.FirstNpc => JournalMessageParse.FirstNpc(message),
            JournalRole.ArrowTarget => JournalMessageParse.ArrowTarget(message),
            JournalRole.Token => JournalMessageParse.TokenNpc(message, token),
            _ => null
        };

        if (!value.HasValue)
        {
            return null;
        }

        var id = new EntityId(value.Value);
        return world.Entities.Npcs.ContainsKey(id) ? id : (EntityId?)null;
    }

    private static string ExtraOf(JournalRule rule, string message) => rule.Extra switch
    {
        JournalExtra.LeadingWord => JournalMessageParse.LeadingWord(message),
        JournalExtra.Token => JournalMessageParse.TokenText(message, rule.ExtraToken),
        _ => null
    };

    internal static int HourTicks() => Spec136.HourTicks < 1 ? 1 : Spec136.HourTicks;
}

}
