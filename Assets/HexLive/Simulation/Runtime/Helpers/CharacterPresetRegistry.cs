using System;
using System.Collections.Generic;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§160 authored bodies. No entry here owns AI, memory, UI or voice transport.</summary>
public static class CharacterPresetRegistry
{
    public static IReadOnlyList<string> ProfileIds { get; } =
        Array.AsReadOnly(new[] { MashaCompanionProfile.ProfileId, NikaCharacterProfile.ProfileId });

    public static bool EnsureSpawned(WorldState world, string profileId, out int npcId)
    {
        switch (profileId)
        {
            case NikaCharacterProfile.ProfileId:
                npcId = NikaCharacterProfile.ReservedNpcId;
                return NikaCharacterProfile.EnsureSpawned(world);
            case MashaCompanionProfile.ProfileId:
                npcId = MashaCompanionProfile.ReservedNpcId;
                return MashaCompanionProfile.EnsureSpawned(world);
            default:
                throw new ArgumentException($"Unknown character preset '{profileId}'.",
                    nameof(profileId));
        }
    }
}

}
