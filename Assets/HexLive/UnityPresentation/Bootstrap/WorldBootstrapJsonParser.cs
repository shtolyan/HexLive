using System;
using System.Collections.Generic;
using HexLive.Simulation.Bootstrap;
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{

public static class WorldBootstrapJsonParser
{
    public static WorldBootstrapDefinition Parse(string json)
    {
        var dto = JsonUtility.FromJson<WorldBootstrapDto>(json);
        if (dto is null)
        {
            throw new InvalidOperationException("Failed to parse world bootstrap JSON.");
        }

        return new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings
            {
                TickDeltaTime = dto.simulation.tickDeltaTime,
                MediumTickInterval = dto.simulation.mediumTickInterval,
                SlowTickInterval = dto.simulation.slowTickInterval
            },
            Environment = new EnvironmentBootstrap
            {
                GlobalTemperature = dto.environment.globalTemperature
            },
            Fragments = ConvertFragments(dto.fragments),
            Objects = ConvertObjects(dto.objects),
            Npcs = ConvertNpcs(dto.npcs)
        };
    }

    private static List<FragmentBootstrap> ConvertFragments(FragmentDto[] fragments)
    {
        var result = new List<FragmentBootstrap>(fragments.Length);
        foreach (var fragment in fragments)
        {
            var converted = new FragmentBootstrap { Id = fragment.id };
            foreach (var tile in fragment.tiles)
            {
                converted.Tiles.Add(new TileBootstrap
                {
                    Q = tile.q,
                    R = tile.r,
                    Walkable = tile.walkable,
                    Indoor = tile.indoor,
                    Blocked = tile.blocked
                });
            }

            result.Add(converted);
        }

        return result;
    }

    private static List<ObjectBootstrap> ConvertObjects(ObjectDto[] objects)
    {
        var result = new List<ObjectBootstrap>(objects.Length);
        foreach (var entry in objects)
        {
            result.Add(new ObjectBootstrap
            {
                Id = entry.id,
                DefinitionId = entry.definitionId,
                FragmentId = entry.fragmentId,
                TileQ = entry.tileQ,
                TileR = entry.tileR,
                JunctionSlots = new List<int>(entry.pointSlots)
            });
        }

        return result;
    }

    private static List<NpcBootstrap> ConvertNpcs(NpcDto[] npcs)
    {
        var result = new List<NpcBootstrap>(npcs.Length);
        foreach (var npc in npcs)
        {
            result.Add(new NpcBootstrap
            {
                Id = npc.id,
                // §70: absent in every existing JSON, which parses to 0 =
                // Colony — exactly the pre-§70 meaning.
                Faction = (HexLive.Simulation.Agents.Faction)npc.faction,
                FragmentId = npc.fragmentId,
                TileQ = npc.tileQ,
                TileR = npc.tileR,
                Hunger = npc.hunger,
                Energy = npc.energy,
                Comfort = npc.comfort,
                Social = npc.social,
                ThermalDiscomfort = npc.thermalDiscomfort
            });
        }

        return result;
    }

    [Serializable]
    private sealed class WorldBootstrapDto
    {
        public SimulationDto simulation = new();
        public EnvironmentDto environment = new();
        public FragmentDto[] fragments = Array.Empty<FragmentDto>();
        public ObjectDto[] objects = Array.Empty<ObjectDto>();
        public NpcDto[] npcs = Array.Empty<NpcDto>();
    }

    [Serializable]
    private sealed class SimulationDto
    {
        public float tickDeltaTime = 0.25f;
        public int mediumTickInterval = 4;
        public int slowTickInterval = 16;
    }

    [Serializable]
    private sealed class EnvironmentDto
    {
        public float globalTemperature = 20f;
    }

    [Serializable]
    private sealed class FragmentDto
    {
        public int id;
        public TileDto[] tiles = Array.Empty<TileDto>();
    }

    [Serializable]
    private sealed class TileDto
    {
        public int q;
        public int r;
        public bool walkable = true;
        public bool indoor;
        public bool blocked;
    }

    [Serializable]
    private sealed class ObjectDto
    {
        public int id;
        public string definitionId = string.Empty;
        public int fragmentId;
        public int tileQ;
        public int tileR;
        public int[] pointSlots = Array.Empty<int>();
    }

    [Serializable]
    private sealed class NpcDto
    {
        public int id;
        public int faction;
        public int fragmentId;
        public int tileQ;
        public int tileR;
        public float hunger;
        public float energy;
        public float comfort;
        public float social;
        public float thermalDiscomfort;
    }
}

}
