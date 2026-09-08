using System;

namespace HexLive.Simulation.Wire
{
    /// <summary>§161 immutable-by-transfer view captured when recording starts.</summary>
    public sealed class AdminRequestContext
    {
        public string Epoch { get; set; } = string.Empty;
        public int PrimaryNpcId { get; set; }
        public int[] SelectedNpcIds { get; set; } = Array.Empty<int>();
        public int[] AssignedNpcIds { get; set; } = Array.Empty<int>();
        public float[] CameraPosition { get; set; } = Array.Empty<float>();
        public float[] CameraForward { get; set; } = Array.Empty<float>();
        public float[] GroundPosition { get; set; } = Array.Empty<float>();
        public int? GroundQ { get; set; }
        public int? GroundR { get; set; }
    }
}
