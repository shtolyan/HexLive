using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;
using UnityEngine;

namespace HexLive.UnityPresentation.Spatial
{
    public static class SimulationUnityMapper
    {
        private const float TileHeightFactor = 2f / 15f;
        private const float PointMarkerLiftFactor = 1f / 50f;
        private const float CameraTargetHeightFactor = 9f / 10f;

        public static float HexRadius => HexSpatialMath.HexRadius;

        public static float TileHeight => HexRadius * TileHeightFactor;

        public static float PointMarkerLift => HexRadius * PointMarkerLiftFactor;

        public static float CameraTargetHeight => HexRadius * CameraTargetHeightFactor;

        public static Vector3 ToUnityPosition(Float2 simulationPosition, float y = 0f)
        {
            return new Vector3(simulationPosition.X, y, simulationPosition.Y);
        }

        public static Vector3 ToUnityTilePosition(TileCoord coord, float y = 0f)
        {
            return ToUnityPosition(HexSpatialMath.TileToWorld(coord), y);
        }

        public static Vector3 ToUnityPointPosition(TileCoord anchorTile, Float2 localOffset, float y = 0f)
        {
            return ToUnityPosition(HexSpatialMath.PointToWorld(anchorTile, localOffset), y);
        }

        public static Vector3 ToUnityPointPosition(Float2 worldPosition, float y = 0f)
        {
            return ToUnityPosition(worldPosition, y);
        }

        /// <summary>
        /// Converts a simulation angle (atan2(Y,X), 0° = +X) to Unity Y-axis
        /// rotation (0° = +Z forward). Accounts for the axis swap: sim Y → Unity Z.
        /// </summary>
        public static float ToUnityYawDegrees(float simAngleDegrees)
        {
            return 90f - simAngleDegrees;
        }

        /// <summary>
        /// Rotates geometry already authored directly in the simulation X/Y
        /// footprint plane (Blender X/Y -> Unity X/Z). Unlike a character or
        /// furniture forward vector, a complete hex has no +Z-facing offset:
        /// simulation 0° must remain the prefab's identity pose.
        /// </summary>
        public static float ToUnityFootprintYawDegrees(float simAngleDegrees)
        {
            return -simAngleDegrees;
        }
    }
}
