using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

[NonParallelizable]
public sealed class RetiredBoilingTests
{
    [Test]
    public void DefaultFireDoesNotAdvertiseBottleFilling()
    {
        var fire = PrototypeContentCatalog.CreateDefaults()["campfire.spot"];
        Assert.That(fire.Interactions.Any(i => i.Type == InteractionType.FillBottle), Is.False);
    }

    [Test]
    public void LegacyOverrideCannotReintroduceBoiling()
    {
        var saved = WorldObjectLibrary.Registered.ToArray();
        try
        {
            WorldObjectLibrary.Clear();
            var legacy = new ObjectDefinition { Id = "campfire.spot" };
            legacy.Interactions.Add(new InteractionDefinition { Id = "fill.boiled", Type = InteractionType.FillBottle });
            WorldObjectLibrary.Override(legacy);
            var definitions = new Dictionary<string, ObjectDefinition>(PrototypeContentCatalog.CreateDefaults());
            WorldObjectLibrary.ApplyTo(definitions);
            Assert.That(definitions["campfire.spot"].Interactions.Any(i => i.Type == InteractionType.FillBottle), Is.False);
            Assert.That(definitions["campfire.spot"].Interactions.Any(i => i.Type == InteractionType.Fuel), Is.True);
        }
        finally
        {
            WorldObjectLibrary.Clear();
            foreach (var definition in saved) WorldObjectLibrary.Override(definition);
        }
    }
}
