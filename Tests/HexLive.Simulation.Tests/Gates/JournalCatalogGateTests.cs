using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Journal;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §136: полнота каталога дневника.
///
/// <para>
/// Тот же класс болезни, что ловит <see cref="EventWhitelistGateTests"/>, но с
/// другого конца. Хроника пополняется постоянно, а дневник читает её через
/// <see cref="JournalCatalog"/> — тип, которого в каталоге нет, молча получает
/// вес 0 и не попадает в дневники ВООБЩЕ. Молчание выглядит как «просто редкое
/// событие», поэтому глазами это не находится: ровно так «Collapsed» и
/// «MeatRoasted» годами не доходили до истории колонии.
/// </para>
/// <para>
/// Поэтому решение об исключении должно быть ЗАПИСАНО, а не получиться само.
/// Тип, которому в дневнике не место, живёт в <see cref="DeliberatelySilent"/>
/// с причиной; всё остальное обязано иметь вес.
/// </para>
/// </summary>
public sealed class JournalCatalogGateTests
{
    /// <summary>
    /// События, которых в личном дневнике быть не должно, и почему. Каждое —
    /// решение, а не пропуск.
    /// </summary>
    /// <remarks>
    /// Пусто по замыслу — как <c>KnownUnemitted</c> у соседнего гейта. Строка
    /// здесь это обещание, а не оправдание: если событие видно игроку в ленте
    /// колонии, у него есть основание быть и в чьём-то дне. Тихим его делает
    /// малый вес в каталоге, а не отсутствие в нём.
    /// </remarks>
    private static readonly Dictionary<string, string> DeliberatelySilent =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Test]
    public void EveryPlayerVisibleTypeHasAJournalRuleOrAWrittenReason()
    {
        var missing = GameEventTypes.ListedTypes
            .Where(type => !JournalCatalog.Knows(type))
            .Where(type => !DeliberatelySilent.ContainsKey(type))
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.That(missing, Is.Empty,
            "§136: эти игровые события не попадут ни в один дневник — у них нет " +
            "правила в JournalCatalog. Либо задай вес, либо внеси в " +
            "DeliberatelySilent с причиной:\n  " + string.Join("\n  ", missing));
    }

    [Test]
    public void SilenceListDoesNotRotAgainstTheCatalog()
    {
        // Обратная сторона: тип, которому одновременно объявлено «молчим» и
        // задан вес, — это спор двух решений, и выигрывает молча каталог.
        var contradictory = DeliberatelySilent.Keys
            .Where(JournalCatalog.Knows)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.That(contradictory, Is.Empty,
            "§136: у этих типов есть и правило в каталоге, и запись «молчим». " +
            "Убери одно из двух:\n  " + string.Join("\n  ", contradictory));
    }

    [Test]
    public void WeightsStayInsideTheDeclaredTiers()
    {
        // Ярусы — единственное, что отличает «про это пишут» от «это фон».
        // Вес выше 100 или отрицательный означал бы, что кто-то правил таблицу,
        // не глядя на Spec136, и один тип навсегда выиграл бы все часы.
        var broken = GameEventTypes.ListedTypes
            .Where(JournalCatalog.Knows)
            .Select(type => (type, rule: JournalCatalog.For(type)))
            .Where(x => x.rule.Weight < 0 || x.rule.Weight > 100 ||
                        x.rule.MirrorWeight < 0 || x.rule.MirrorWeight > 100)
            .Select(x => $"{x.type}: {x.rule.Weight}/{x.rule.MirrorWeight}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        Assert.That(broken, Is.Empty,
            "§136: вес обязан лежать в 0..100 — иначе ярусы Spec136 " +
            "перестают что-либо значить:\n  " + string.Join("\n  ", broken));
    }

    [Test]
    public void MirrorRulesNameSomebodyToMirrorTo()
    {
        // Зеркало без роли субъекта — это правило, которое никогда не сработает:
        // вторую участницу неоткуда взять. Тихая ошибка ровно того же сорта.
        var orphaned = GameEventTypes.ListedTypes
            .Where(JournalCatalog.Knows)
            .Select(type => (type, rule: JournalCatalog.For(type)))
            .Where(x => x.rule.MirrorWeight > 0 && x.rule.Subject == JournalRole.None)
            .Select(x => x.type)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.That(orphaned, Is.Empty,
            "§136: у этих правил есть зеркальный вес, но не объявлено, откуда " +
            "брать вторую участницу — зеркало не сработает никогда:\n  " +
            string.Join("\n  ", orphaned));
    }

    [Test]
    public void TokenRolesCarryTheirToken()
    {
        var tokenless = GameEventTypes.ListedTypes
            .Where(JournalCatalog.Knows)
            .Select(type => (type, rule: JournalCatalog.For(type)))
            .Where(x => (x.rule.Subject == JournalRole.Token &&
                         string.IsNullOrEmpty(x.rule.SubjectToken)) ||
                        (x.rule.Extra == JournalExtra.Token &&
                         string.IsNullOrEmpty(x.rule.ExtraToken)))
            .Select(x => x.type)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.That(tokenless, Is.Empty,
            "§136: роль объявлена как Token, но имя токена не задано — разбор " +
            "сообщения вернёт пустоту:\n  " + string.Join("\n  ", tokenless));
    }
}

}
