#nullable enable
using HexLive.Simulation.Content;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Compatibility seam for callers that ask whether an object is a bed.
    /// The former procedural two-bed factory was retired; rendering and staged
    /// construction now always use BedAssembly + bed_basic_final_native.fbx.
    /// </summary>
    public static class BedFactory
    {
        public static bool IsBed(string definitionId) =>
            definitionId == ContentIds.BedBasic;
    }
}
