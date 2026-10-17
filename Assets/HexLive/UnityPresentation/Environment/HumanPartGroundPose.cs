using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
// §56.7: stable yaw plus back/side/front resting poses, seated AFTER rotation.
internal static class HumanPartGroundPose
{
    internal static Quaternion Rotation(string id, int objectId, Quaternion authored)
    {
        var hash = unchecked((uint)objectId * 2654435761u);
        var yaw = (hash >> 8) / 16777216f * 360f;
        var side = (hash & 3u) switch { 0 => 0f, 1 => 180f, 2 => 72f, _ => -72f };
        var axis = HumanMeatCatalog.IsArm(id) ? Vector3.right : Vector3.forward;
        return Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(side, axis) * authored;
    }
}
}
