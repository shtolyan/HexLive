using System.Text.Json;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;

/// <summary>§171.25: camps and acquaintances reach the voice prompt as words, not numbers.</summary>
public sealed class VoiceSocialKnowledgeTests
{
    [Test]
    public void CampsAreCommonKnowledgeWithDirectionDistanceAndStance()
    {
        var view = CharacterView.Build(JsonSerializer.SerializeToElement(new
        {
            camps = new object[]
            {
                new { camp = "Colony", number = 1, own = true, here = true, direction = "", distanceTiles = 0, stance = "own", visited = true },
                new { camp = "Colony3", number = 3, own = false, here = false, direction = "NE", distanceTiles = 40, stance = "hostile", visited = false },
                new { camp = "Colony4", number = 4, own = false, here = false, direction = "W", distanceTiles = 12, stance = "neutral", visited = true },
            },
        }));
        Assert.That(view, Does.Contain("Лагерь 1 — твой, ты сейчас в нём"));
        Assert.That(view, Does.Contain("Лагерь 3 — на северо-востоке, далеко; враждует с вашим лагерем; бывать там не доводилось"));
        Assert.That(view, Does.Contain("Лагерь 4 — на западе, недалеко; соседи, не враги; бывать там уже доводилось"));
    }

    [Test]
    public void KnownPeopleAndVisibleBondsAreWordsWithoutLifeClaims()
    {
        var view = CharacterView.Build(JsonSerializer.SerializeToElement(new
        {
            knownPeople = new object[]
            {
                new { npcId = 5, nameId = "Dasha", names = new { ru = "Даша" }, camp = "Colony", sameCamp = true,
                    relationship = new { trust = .45, familiarity = .83, affinity = .31, authority = .6 },
                    hoursSinceSeen = 30, hoursSinceTalk = 5, visibleNow = false },
                new { npcId = 9, nameId = "Elsa", names = new { ru = "Эльза" }, camp = "Colony3", sameCamp = false,
                    relationship = (object?)null, hoursSinceSeen = 2, hoursSinceTalk = (int?)null, visibleNow = false },
            },
            knownPeopleTotal = 7,
            visibleNpcs = new object[]
            {
                new { npcId = 9, names = new { ru = "Эльза" }, distance = 4.0, faction = "Colony3", hostile = true,
                    observerRelationship = new { trust = -.4, familiarity = .2, affinity = -.6, authority = 0.0 } },
            },
        }));
        Assert.That(view, Does.Contain("Даша (npcId=5), Лагерь 1 (твой): близко знакомы, симпатия, доверие, к советам этого человека прислушиваешься; виделись 1 дн назад; говорили 5 ч назад"));
        Assert.That(view, Does.Contain("Эльза (npcId=9), Лагерь 3: видели друг друга, больше ничего; виделись 2 ч назад; ни разу не говорили"));
        Assert.That(view, Does.Contain("и ещё 5 человек"));
        Assert.That(view, Does.Contain("npcId=9 Эльза дистанция=4.0 hostile Лагерь 3 отношение: немного знакомы, сильная неприязнь, недоверие"));
        Assert.That(view, Does.Not.Contain("0.83").And.Not.Contain("0.31").And.Not.Contain("жива"));
    }

    [Test]
    public void OldSnapshotsWithoutTheNewFieldsRenderAsBefore()
    {
        var view = CharacterView.Build(JsonSerializer.SerializeToElement(new { tick = 1 }));
        Assert.That(view, Does.Not.Contain("Лагеря острова").And.Not.Contain("Знакомые люди"));
    }
}
