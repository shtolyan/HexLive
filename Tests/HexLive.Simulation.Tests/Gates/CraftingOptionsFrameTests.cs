using System;
using System.IO;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§138.2: remote crafting UI receives the authoritative server read
/// model without inventing a second recipe resolver.</summary>
public sealed class CraftingOptionsFrameTests
{
    [Test]
    public void BatchRoundTripsEveryUiFieldForMultipleAssignedNpcs()
    {
        var sent = new CraftingOptionsSnapshot();
        var first = new NpcCraftingOptionsSnapshot { NpcId = 7 };
        var option = new CraftRecipeOption
        {
            Goal = GoalType.CraftAxe,
            OutputDefinitionId = "tool.axe",
            StationTag = "Workbench",
            StationObjectId = new ObjectId(41),
            ProjectObjectId = new ObjectId(52),
            WorkTile = new TileCoord(3, -2),
            WorkJunction = new JunctionId(123),
            WorkDone = 6,
            WorkRequired = 24,
            CanCraft = true,
            IsActive = false,
            IsResume = true,
            BlockReason = CraftBlockReason.None,
        };
        option.Ingredients.Add(new CraftIngredientOption
        {
            DefinitionId = "resource.stick",
            Available = 4,
            Required = 2,
        });
        first.Options.Add(option);
        sent.Npcs.Add(first);
        sent.Npcs.Add(new NpcCraftingOptionsSnapshot { NpcId = 9 });

        var framed = Frame.CraftingOptions(sent);
        Assert.That((FrameKind)framed[0], Is.EqualTo(FrameKind.CraftingOptions));
        var payload = new byte[framed.Length - 1];
        Buffer.BlockCopy(framed, 1, payload, 0, payload.Length);
        var got = Frame.ReadCraftingOptions(payload);

        Assert.Multiple(() =>
        {
            Assert.That(got.Npcs.Count, Is.EqualTo(2));
            Assert.That(got.Npcs[0].NpcId, Is.EqualTo(7));
            Assert.That(got.Npcs[1].NpcId, Is.EqualTo(9));
            Assert.That(got.Npcs[1].Options, Is.Empty);
            Assert.That(got.Npcs[0].Options.Count, Is.EqualTo(1));
        });

        var actual = got.Npcs[0].Options[0];
        Assert.Multiple(() =>
        {
            Assert.That(actual.Goal, Is.EqualTo(option.Goal));
            Assert.That(actual.OutputDefinitionId, Is.EqualTo(option.OutputDefinitionId));
            Assert.That(actual.StationTag, Is.EqualTo(option.StationTag));
            Assert.That(actual.StationObjectId, Is.EqualTo(option.StationObjectId));
            Assert.That(actual.ProjectObjectId, Is.EqualTo(option.ProjectObjectId));
            Assert.That(actual.WorkTile, Is.EqualTo(option.WorkTile));
            Assert.That(actual.WorkJunction, Is.EqualTo(option.WorkJunction));
            Assert.That(actual.WorkDone, Is.EqualTo(6));
            Assert.That(actual.WorkRequired, Is.EqualTo(24));
            Assert.That(actual.CanCraft, Is.True);
            Assert.That(actual.IsActive, Is.False);
            Assert.That(actual.IsResume, Is.True);
            Assert.That(actual.BlockReason, Is.EqualTo(CraftBlockReason.None));
            Assert.That(actual.Ingredients.Count, Is.EqualTo(1));
            Assert.That(actual.Ingredients[0].DefinitionId, Is.EqualTo("resource.stick"));
            Assert.That(actual.Ingredients[0].Available, Is.EqualTo(4));
            Assert.That(actual.Ingredients[0].Required, Is.EqualTo(2));
            Assert.That(Frame.CraftingOptions(sent), Is.EqualTo(framed),
                "Stable input must produce byte-identical push frames.");
        });
    }

    [Test]
    public void ImplausibleNpcCountIsRejectedBeforeAllocation()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(CraftingOptionsCodec.WireVersion);
        writer.Write(257);
        writer.Flush();

        Assert.Throws<InvalidDataException>(() =>
            Frame.ReadCraftingOptions(stream.ToArray()));
    }
}

}
