using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
/// <summary>§107.4a: a level full-body shot, independent of animated bones.</summary>
public static class LyingPortraitFraming
{
    public const float FrameMargin = 1.18f;
    // Visual envelope, separate from the collision footprint. Both actual
    // sleep loops reach about z ±0.82, x +0.58 and y -0.104..0.409 wu at
    // production scale. Round outward before adding the projection margin.
    public const float HalfLengthFactor = 0.60f;
    public const float HalfWidthFactor = 0.40f;
    public const float BelowSupportFactor = 0.10f;
    public const float AboveSupportFactor = 0.30f;

    public static Bounds Envelope(float actorScale)
    {
        var relativeScale = Mathf.Max(0.01f, Mathf.Abs(actorScale)) /
                            HexWorldRenderer.ActorScale;
        var radius = HexSpatialMath.HexRadius * relativeScale;
        var below = radius * BelowSupportFactor;
        var above = radius * AboveSupportFactor;
        return new Bounds(Vector3.up * ((above - below) * 0.5f), new Vector3(
            radius * Mathf.Max(Spec49.LieBodyWidthFactor, HalfWidthFactor * 2f),
            above + below,
            radius * Mathf.Max(Spec49.LieBodyLengthFactor, HalfLengthFactor * 2f)));
    }

    public static Pose CameraPose(Pose support, float actorScale, float verticalFov, float aspect)
    {
        var envelope = Envelope(actorScale);
        var half = envelope.extents;
        var verticalTan = Mathf.Tan(Mathf.Clamp(verticalFov, 1f, 170f) * 0.5f * Mathf.Deg2Rad);
        var horizontalTan = verticalTan * Mathf.Max(0.01f, aspect);
        // Include the near side's depth: fitting only the centre plane clips
        // the nearest hands/feet even when the centre-plane rectangle fits.
        var distance = Mathf.Max(half.z / horizontalTan, half.y / verticalTan) *
                       FrameMargin + half.x;
        var forward = Vector3.ProjectOnPlane(support.rotation * Vector3.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();
        var side = Vector3.Cross(Vector3.up, forward);
        return new Pose(support.position + envelope.center + side * distance,
            Quaternion.LookRotation(-side, Vector3.up));
    }
}
}
