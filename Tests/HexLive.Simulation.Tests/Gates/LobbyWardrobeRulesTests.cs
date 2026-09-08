using System.Collections.Generic;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
public sealed class LobbyWardrobeRulesTests
{
    private static LobbyWardrobeRules.Item Item(string id, string model, string slot, string layer = "Wear", string sex = "Female", bool authored = true) =>
        new() { Id = id, Prototype = model, Slots = new[] { slot }, Covers = new[] { "Torso" }, Layer = layer, Sex = sex, AuthoredSlots = authored };

    [Test]
    public void RecolourReplacesTheSameModelAndPreservesOtherItems()
    {
        var red = Item("skirt.red", "skirt", "Skirt"); var blue = Item("skirt.blue", "skirt", "Skirt");
        var shirt = Item("shirt", "", "Shirt"); var catalog = new[] { red, blue, shirt };
        var worn = new List<string> { red.Id, shirt.Id };
        Assert.That(LobbyWardrobeRules.Equip(catalog, worn, blue, out var conflict), Is.True);
        Assert.That(conflict, Is.Null);
        Assert.That(worn, Is.EquivalentTo(new[] { blue.Id, shirt.Id }));
        Assert.That(LobbyWardrobeRules.Equip(catalog, worn, blue, out _), Is.True);
        Assert.That(worn, Has.Count.EqualTo(2));
    }
    [Test]
    public void ConflictLeavesTheEntireOutfitUntouched()
    {
        var skirt = Item("skirt", "", "Skirt"); var dress = Item("dress", "", "Skirt");
        var worn = new List<string> { skirt.Id };
        Assert.That(LobbyWardrobeRules.Equip(new[] { skirt, dress }, worn, dress, out var conflict), Is.False);
        Assert.That(conflict, Is.EqualTo(skirt.Id)); Assert.That(worn, Is.EqualTo(new[] { skirt.Id }));
    }
    [Test]
    public void AuthoredSlotsAllowSameCoverageAndDifferentLayersAllowSameSlot()
    {
        var shirt = Item("shirt", "", "Shirt"); var belt = Item("belt", "", "Belt");
        var jacket = Item("jacket", "", "Shirt", "Outerwear"); var catalog = new[] { shirt, belt, jacket };
        var worn = new List<string> { shirt.Id };
        Assert.That(LobbyWardrobeRules.Equip(catalog, worn, belt, out _), Is.True);
        Assert.That(LobbyWardrobeRules.Equip(catalog, worn, jacket, out _), Is.True);
    }
    [Test]
    public void LegacyCoverageIsUsedOnlyWhenSlotsAreNotAuthored()
    {
        var shirt = Item("shirt", "", "Shirt"); var legacy = Item("legacy", "", "Belt", authored: false);
        Assert.That(LobbyWardrobeRules.Conflict(new[] { shirt, legacy }, new[] { shirt.Id }, legacy), Is.EqualTo(shirt.Id));
    }
    [Test]
    public void BodyChangeKeepsCompatibleAndUnisexItemsOnly()
    {
        var female = Item("dress", "", "Dress"); var male = Item("trousers", "", "Trousers", sex: "Male");
        var bag = Item("bag", "", "Back", "Bags", "Any"); var catalog = new[] { female, male, bag };
        var worn = new List<string> { female.Id, bag.Id };
        Assert.That(LobbyWardrobeRules.RemoveIncompatible(catalog, worn, false), Is.Empty);
        Assert.That(worn, Is.EqualTo(new[] { female.Id, bag.Id }));
        Assert.That(LobbyWardrobeRules.RemoveIncompatible(catalog, worn, true), Is.EqualTo(new[] { female.Id }));
        Assert.That(worn, Is.EqualTo(new[] { bag.Id }));
    }
}
}
