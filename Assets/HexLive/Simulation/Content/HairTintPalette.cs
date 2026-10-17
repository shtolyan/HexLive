using System;
using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
// §171.20: explicit creation allow-list, shared by server and rendering.
public static class HairTintPalette
{
    public readonly struct Tone
    {
        public readonly string Id;
        public readonly byte R, G, B;
        public Tone(string name, byte r, byte g, byte b) { Id = "tint." + name; R = r; G = g; B = b; }
    }
    public static readonly IReadOnlyList<Tone> All = new[] {
        new Tone("black", 38, 35, 34), new Tone("graphite", 75, 76, 79),
        new Tone("espresso", 77, 48, 35), new Tone("chestnut", 126, 78, 46),
        new Tone("copper", 182, 88, 42), new Tone("auburn", 132, 49, 37),
        new Tone("honey", 211, 158, 73), new Tone("gold", 240, 202, 111),
        new Tone("ash", 170, 161, 142), new Tone("platinum", 237, 225, 196),
        new Tone("silver", 193, 199, 207), new Tone("white", 250, 247, 234),
        new Tone("rose", 217, 132, 161), new Tone("violet", 139, 111, 190),
        new Tone("ocean", 73, 130, 187), new Tone("mint", 110, 181, 157)
    };
    public static bool TryGet(string id, out Tone tone)
    {
        foreach (var value in All) if (string.Equals(value.Id, id, StringComparison.Ordinal)) { tone = value; return true; }
        tone = default; return false;
    }
}
}
