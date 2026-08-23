using System;
using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Wire
{

/// <summary>§138.2: one player's authoritative crafting read model for one
/// NPC. The server sends only NPCs assigned to this viewer (§149).</summary>
public sealed class NpcCraftingOptionsSnapshot
{
    public int NpcId { get; set; }
    public List<CraftRecipeOption> Options { get; } = new();
}

/// <summary>Per-viewer batch. A list, rather than a single NPC, preserves the
/// future multi-character ownership seam from §149.</summary>
public sealed class CraftingOptionsSnapshot
{
    public List<NpcCraftingOptionsSnapshot> Npcs { get; } = new();
}

/// <summary>
/// §138 server crafting options. Hand-written beside the other wire codecs so
/// Unity and the headless server always share exactly one binary contract.
/// </summary>
public static class CraftingOptionsCodec
{
    public const int WireVersion = 1;

    private const int MaxNpcs = 256;
    private const int MaxRecipesPerNpc = 1024;
    private const int MaxIngredientsPerRecipe = 64;
    private const int MaxDefinitionIdLength = 512;

    public static void Write(CraftingOptionsSnapshot snapshot, BinaryWriter writer)
    {
        writer.Write(WireVersion);
        writer.Write(snapshot.Npcs.Count);
        for (var i = 0; i < snapshot.Npcs.Count; i++)
        {
            var npc = snapshot.Npcs[i];
            writer.Write(npc.NpcId);
            writer.Write(npc.Options.Count);
            for (var j = 0; j < npc.Options.Count; j++)
            {
                WriteOption(writer, npc.Options[j]);
            }
        }
    }

    public static CraftingOptionsSnapshot Read(BinaryReader reader)
    {
        var version = reader.ReadInt32();
        if (version != WireVersion)
        {
            throw new InvalidDataException(
                $"Crafting options wire version {version}, expected {WireVersion}.");
        }

        var result = new CraftingOptionsSnapshot();
        var npcIds = new HashSet<int>();
        var npcCount = ReadCount(reader, MaxNpcs, "NPCs");
        for (var i = 0; i < npcCount; i++)
        {
            var npc = new NpcCraftingOptionsSnapshot { NpcId = reader.ReadInt32() };
            if (npc.NpcId <= 0 || !npcIds.Add(npc.NpcId))
            {
                throw new InvalidDataException(
                    $"Crafting options contain invalid or duplicate NPC id {npc.NpcId}.");
            }
            var optionCount = ReadCount(reader, MaxRecipesPerNpc, "recipes");
            for (var j = 0; j < optionCount; j++)
            {
                npc.Options.Add(ReadOption(reader));
            }

            result.Npcs.Add(npc);
        }

        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException(
                "Crafting options frame has trailing bytes — codec and frame disagree.");
        }

        return result;
    }

    private static void WriteOption(BinaryWriter writer, CraftRecipeOption option)
    {
        writer.Write((int)option.Goal);
        WireIo.WriteString(writer, option.OutputDefinitionId);
        WireIo.WriteString(writer, option.StationTag);
        WireIo.WriteNullableInt(writer, option.StationObjectId?.Value);
        WireIo.WriteNullableInt(writer, option.ProjectObjectId?.Value);
        WireIo.WriteTile(writer, option.WorkTile);
        WireIo.WriteNullableInt(writer, option.WorkJunction?.Value);
        writer.Write(option.WorkDone);
        writer.Write(option.WorkRequired);
        writer.Write(option.CanCraft);
        writer.Write(option.IsActive);
        writer.Write(option.IsResume);
        writer.Write((int)option.BlockReason);
        writer.Write(option.Ingredients.Count);
        for (var i = 0; i < option.Ingredients.Count; i++)
        {
            var ingredient = option.Ingredients[i];
            WireIo.WriteString(writer, ingredient.DefinitionId);
            writer.Write(ingredient.Available);
            writer.Write(ingredient.Required);
        }
    }

    private static CraftRecipeOption ReadOption(BinaryReader reader)
    {
        var goalValue = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(GoalType), goalValue))
        {
            throw new InvalidDataException($"Unknown crafting goal {goalValue}.");
        }

        var option = new CraftRecipeOption
        {
            Goal = (GoalType)goalValue,
            OutputDefinitionId = ReadDefinitionId(reader, "output"),
            StationTag = ReadDefinitionId(reader, "station"),
        };
        var stationId = WireIo.ReadNullableInt(reader);
        option.StationObjectId = stationId.HasValue ? new ObjectId(stationId.Value) : (ObjectId?)null;
        var projectId = WireIo.ReadNullableInt(reader);
        option.ProjectObjectId = projectId.HasValue ? new ObjectId(projectId.Value) : (ObjectId?)null;
        option.WorkTile = WireIo.ReadTile(reader);
        var junctionId = WireIo.ReadNullableInt(reader);
        option.WorkJunction = junctionId.HasValue
            ? new JunctionId(junctionId.Value)
            : (JunctionId?)null;
        option.WorkDone = reader.ReadInt32();
        option.WorkRequired = reader.ReadInt32();
        option.CanCraft = reader.ReadBoolean();
        option.IsActive = reader.ReadBoolean();
        option.IsResume = reader.ReadBoolean();

        var blockReason = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(CraftBlockReason), blockReason))
        {
            throw new InvalidDataException($"Unknown craft block reason {blockReason}.");
        }
        option.BlockReason = (CraftBlockReason)blockReason;

        var ingredientCount = ReadCount(
            reader, MaxIngredientsPerRecipe, "ingredients");
        for (var i = 0; i < ingredientCount; i++)
        {
            option.Ingredients.Add(new CraftIngredientOption
            {
                DefinitionId = ReadDefinitionId(reader, "ingredient"),
                Available = reader.ReadInt32(),
                Required = reader.ReadInt32(),
            });
        }

        return option;
    }

    private static int ReadCount(BinaryReader reader, int maximum, string label)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException(
                $"Crafting options claim {count} {label}; maximum is {maximum}.");
        }

        return count;
    }

    private static string ReadDefinitionId(BinaryReader reader, string label)
    {
        var value = reader.ReadString();
        if (value.Length > MaxDefinitionIdLength)
        {
            throw new InvalidDataException(
                $"Crafting {label} id is longer than {MaxDefinitionIdLength} characters.");
        }

        return value;
    }
}

}
