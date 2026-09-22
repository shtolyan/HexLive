using System.IO;

namespace HexLive.Simulation.Wire
{
    /// <summary>§149.6: durable, player-private introduction or loss notice.</summary>
    public sealed class PlayerAssignmentNotice
    {
        public long Sequence { get; set; }
        public int NpcId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = "assigned";

        public void Write(BinaryWriter writer)
        {
            writer.Write(Sequence);
            writer.Write(NpcId);
            writer.Write(Name);
            writer.Write(Kind);
        }

        public static PlayerAssignmentNotice Read(BinaryReader reader) => new()
        {
            Sequence = reader.ReadInt64(), NpcId = reader.ReadInt32(),
            Name = reader.ReadString(), Kind = reader.ReadString(),
        };
    }
}
