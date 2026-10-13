using System.Text.Json;
using HexLive.VoiceBrain;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;

// §171.7: the ticket is the only thing tying an ElevenLabs request to a character.
public sealed class SessionTicketTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Test]
    public void MintedTicketVerifiesAndCarriesTheCharacter()
    {
        var tickets = new SessionTickets(Key);
        Assert.That(tickets.TryVerify(tickets.Mint("owner-1", "w:7", "w", 7), out var t), Is.True);
        Assert.That((t.OwnerUserId, t.CharacterId, t.NpcId), Is.EqualTo(("owner-1", "w:7", 7)));
    }

    [Test]
    public void EditedOrForeignTicketsAreRejected()
    {
        var tickets = new SessionTickets(Key);
        var good = tickets.Mint("owner-1", "w:7", "w", 7);
        var body = good[..good.IndexOf('.')];
        var forged = tickets.Sign(new SessionTicket("s", "owner-1", "w:8", "w", 8, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));
        var swapped = forged[..forged.IndexOf('.')] + good[good.IndexOf('.')..];
        Assert.Multiple(() =>
        {
            Assert.That(tickets.TryVerify(swapped, out _), Is.False, "another character's body with this signature");
            Assert.That(new SessionTickets(new byte[32]).TryVerify(good, out _), Is.False, "different server key");
            Assert.That(tickets.TryVerify(body, out _), Is.False);
            Assert.That(tickets.TryVerify("", out _), Is.False);
            Assert.That(tickets.TryVerify(new string('a', 5000), out _), Is.False);
        });
    }

    [Test]
    public void ExpiredTicketIsRejected()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tickets = new SessionTickets(Key, clock);
        var ticket = tickets.Mint("o", "c", "w", 1);
        clock.Now += SessionTickets.Lifetime + TimeSpan.FromSeconds(1);
        Assert.That(tickets.TryVerify(ticket, out _), Is.False);
    }
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class CharacterViewTests
{
    [Test]
    public void NeedScalesBecomeWordsInTheRightDirection()
    {
        // Hunger: 1 = starving. Energy: 1 = rested. The model misread the raw numbers.
        using var doc = JsonDocument.Parse("""
            {"hunger":0.71,"thirst":0.1,"energy":{"value":0.2},"stamina":{"value":0.9},"health":1,"blood":1,"hygiene":1}
            """);
        var feelings = CharacterView.Feelings(doc.RootElement).ToList();
        Assert.That(feelings, Is.EquivalentTo(new[] { "голодна", "очень устала, хочет спать" }));
    }

    [Test]
    public void ProjectionStaysSmall()
    {
        var items = string.Join(",", Enumerable.Range(1, 200).Select(i =>
            $$"""{"itemId":"tree.palm","objectId":{{i}},"interactions":[{"type":"Harvest"}]}"""));
        using var doc = JsonDocument.Parse($$"""{"name":"Аня","stateSummary":"ok","visibleItems":[{{items}}],"perceptionSummary":"{{new string('x', 9000)}}"}""");
        Assert.That(CharacterView.Build(doc.RootElement).Length, Is.LessThan(4000));
    }

    [TestCase("{}", "нет достоверных данных")]
    [TestCase("{\"wornItems\":null}", "нет достоверных данных")]
    [TestCase("{\"wornItems\":[]}", "ничего, список надетых вещей пуст")]
    public void MissingWardrobeIsNotReportedAsNudity(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.That(CharacterView.Build(doc.RootElement), Does.Contain(expected));
    }

    [Test]
    public void OutfitIsSeparateFromCarriedPantsAndUnknownItemsKeepUnknownNames()
    {
        using var doc = JsonDocument.Parse("""
            {"tick":12,"wornItems":[{"itemId":"underwear.briefs_primal","displayName":"Primal briefs","layer":"Underwear","covers":["Pelvis"],"conditionPercent":88},{"itemId":"unregistered"}],
             "inventoryItems":[{"itemId":"clothing.pants","displayName":"Походные штаны"}],"execution":{"status":"Moving"}}
            """);
        var text = CharacterView.Build(doc.RootElement);
        var outfit = text[..text.IndexOf("Текущее выполнение:", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(outfit, Does.Contain("Primal briefs").And.Contain("Underwear").And.Contain("88"));
            Assert.That(outfit, Does.Not.Contain("Походные штаны"));
            Assert.That(outfit, Does.Contain("unregistered\" название неизвестно"));
            Assert.That(text, Does.Contain("В инвентаре (это НЕ надето)").And.Contain("Походные штаны"));
            Assert.That(text, Does.Contain("tick=12").And.Contain("Moving"));
        });
    }

    [Test]
    public void LargeWardrobeAndInventoryHaveExplicitBoundedPrefixes()
    {
        var items = Enumerable.Range(0, 300).Select(i => new { itemId="item"+i, displayName=new string('x',10000) });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { wornItems=items, inventoryItems=items }));
        var text = CharacterView.Build(doc.RootElement);
        Assert.That(text.Length, Is.LessThan(9000));
        Assert.That(text, Does.Contain("список выше неполный").And.Contain("прочие вещи опущены"));
        Assert.That(text, Does.Not.Contain("item299"));
    }
}
