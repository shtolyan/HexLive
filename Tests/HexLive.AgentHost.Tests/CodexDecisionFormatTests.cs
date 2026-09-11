using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class CodexDecisionFormatTests
{
    [Test]
    public void StrictSchemaClosesEveryObjectAndRequiresEveryProperty()
    {
        using var schema = JsonDocument.Parse(CodexDecisionFormat.Schema(AgentProviders.DecisionSchemaJson()));
        Check(schema.RootElement);
        static void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
                foreach (var child in value.EnumerateArray()) Check(child);
            if (value.ValueKind != JsonValueKind.Object) return;
            if (value.TryGetProperty("properties", out var properties))
            {
                Assert.That(value.GetProperty("additionalProperties").GetBoolean(), Is.False);
                Assert.That(value.GetProperty("required").EnumerateArray().Select(v => v.GetString()),
                    Is.EquivalentTo(properties.EnumerateObject().Select(p => p.Name)));
            }
            foreach (var child in value.EnumerateObject()) Check(child.Value);
        }
    }

    [Test]
    public void ConvertsPlanAndReferenceArgumentsBeforeTheExistingGameValidator()
    {
        var json = CodexDecisionFormat.Decode("""
            {"speech":"","emotion":"neutral","action":null,"reaction":"None","relationshipAssessment":null,
            "intentSummary":"Собираю.","memoryUpserts":[],"journalText":"",
            "executionPlanUpdate":{"operation":"replace","reason":"Collect","steps":[
            {"id":"pick","tool":"interact","arguments":[{"name":"objectId","value":42},
            {"name":"interaction","value":"PickUp"}],"condition":null}]}}
            """);
        var decision = AgentProviders.ParseDecision(json, "heartbeat");
        Assert.That(decision.ExecutionPlanUpdate!.Steps[0].Arguments.GetProperty("objectId").GetInt32(), Is.EqualTo(42));
        using var reference = JsonDocument.Parse(CodexDecisionFormat.Decode("""
            {"memoryRequests":[{"operation":"spec.read","arguments":[{"name":"section","value":"153"}]}]}
            """));
        Assert.That(reference.RootElement.GetProperty("memoryRequests")[0].GetProperty("arguments").GetProperty("section").GetString(), Is.EqualTo("153"));
    }

    [TestCase("[{\"name\":\"x\",\"value\":1},{\"name\":\"x\",\"value\":2}]")]
    [TestCase("[{\"name\":\"x\",\"value\":{}}]")]
    [TestCase("[{\"name\":\"x\"}]")]
    [TestCase("{}")]
    public void AmbiguousOrUnsupportedArgumentsFailClosed(string arguments) =>
        Assert.Throws<InvalidDataException>(() => CodexDecisionFormat.Decode("{\"action\":{\"arguments\":" + arguments + "}}"));
}
