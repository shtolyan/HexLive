using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentWorldKnowledgeTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Index() => Json(new { index = "| [§54](Spec/54.md) | Sleep |\n| [§137](Spec/137.md) | Rest |\n| [§121](Spec/121.md) | Manual |" });
    private static JsonElement Page(string text, int offset = 0, bool truncated = false)
        => Json(new { text, offset, truncated, nextOffset = truncated ? offset + text.Length : (int?)null });

    [Test]
    public async Task RestQueryReadsActualSectionsOnceAndSeparatesEnergyFromStamina()
    {
        var calls = new List<string>();
        var knowledge = new AgentWorldKnowledge((section, _, _) =>
        {
            calls.Add(section);
            return Task.FromResult(section == "" ? Index() : Page(section == "54"
                ? "### Sleep\n\n#### r1 historical\n\nOLD EnergyDelta obsolete rule.\n\n#### r2 current\n\nEnergy sleep recovery ONLY-SLEEP."
                : "### Rest\n\nRest stamina STAMINA-REST."));
        });
        var context = await knowledge.BuildAsync("Сяду на пенёк восстановить энергию", "{}", default);
        Assert.That(calls, Is.EqualTo(new[] { "", "54", "137" }));
        Assert.That(context, Does.Contain("ONLY-SLEEP").And.Contain("STAMINA-REST").And.Not.Contain("OLD EnergyDelta"));
        Assert.That(context.Length, Is.LessThanOrEqualTo(AgentWorldKnowledge.ContextLimit));
        await knowledge.BuildAsync("Сяду на пенёк восстановить энергию", "{}", default);
        Assert.That(calls.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task ExplicitSectionWinsAndPaginationIsFollowed()
    {
        var offsets = new List<int>();
        var knowledge = new AgentWorldKnowledge((section, offset, _) =>
        {
            if (section == "") return Task.FromResult(Index());
            Assert.That(section, Is.EqualTo("121"));
            offsets.Add(offset);
            return Task.FromResult(offset == 0 ? Page("First\n\n", 0, true) : Page("Manual NEXT-PAGE", 7));
        });
        var context = await knowledge.BuildAsync("Объясни §121", "{}", default);
        Assert.That(context, Does.Contain("NEXT-PAGE"));
        Assert.That(offsets, Is.EqualTo(new[] { 0, 7 }));
    }

    [Test]
    public async Task FailureIsExplicitAndBackedOffNotInvented()
    {
        var calls = 0;
        var knowledge = new AgentWorldKnowledge((_, _, _) =>
        {
            calls++;
            throw new HttpRequestException("fixture");
        });
        Assert.That(await knowledge.BuildAsync("", "{}", default), Does.Contain("не подтверждены"));
        await knowledge.BuildAsync("", "{}", default);
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void PlayerDepartureCancellationPropagates()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var knowledge = new AgentWorldKnowledge((_, _, token) => Task.FromCanceled<JsonElement>(token));
        Assert.ThrowsAsync<TaskCanceledException>(async () => await knowledge.BuildAsync("", "{}", stop.Token));
    }

    [Test]
    public async Task OversizedParagraphsRemainBoundedAndLabelled()
    {
        var knowledge = new AgentWorldKnowledge((section, _, _) => Task.FromResult(section == ""
            ? Index() : Page(string.Join("\n\n", Enumerable.Repeat(new string('я', 2000), 8)))));
        var context = await knowledge.BuildAsync("пенек", "{}", default);
        Assert.That(context.Length, Is.LessThanOrEqualTo(AgentWorldKnowledge.ContextLimit));
        Assert.That(context, Does.Contain("обрезана"));
    }

    [Test]
    public async Task RealSpecificationRetrievesCurrentSleepClockAndRestRule()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Spec"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        var knowledge = new AgentWorldKnowledge((section, offset, _) =>
        {
            if (section == "") return Task.FromResult(Index());
            var text = File.ReadAllText(Path.Combine(root!.FullName, "Spec", section + ".md"));
            var part = text.Substring(offset, Math.Min(24000, text.Length - offset));
            return Task.FromResult(Page(part, offset, offset + part.Length < text.Length));
        });
        var context = await knowledge.BuildAsync("Сяду на пенёк восстановить энергию", "{}", default);
        Assert.That(context, Does.Contain("only owner of sleep energy").And.Contain("StaminaRestGain"));
        Assert.That(context, Does.Not.Contain("TWO independent"));
        Assert.That(context.Length, Is.LessThanOrEqualTo(AgentWorldKnowledge.ContextLimit));
    }

    [Test]
    public async Task InvalidContinuationDoesNotPresentPartialChapterAsKnown()
    {
        var knowledge = new AgentWorldKnowledge((section, _, _) => Task.FromResult(section == ""
            ? Index() : Json(new { text = "Incomplete", offset = 0, truncated = true, nextOffset = 0 })));
        var context = await knowledge.BuildAsync("§121", "{}", default);
        Assert.That(context, Does.Contain("не подтверждены").And.Not.Contain("Incomplete"));
    }
}
