using System.Collections.Generic;
using MagicaCloth2;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// The body's collision proxy for cloth garments (see GarmentCloth). Capsules
// live on the SKELETON, not on the garment, so every cloth piece a girl wears
// collides against the same legs and hips instead of building its own set.
//
// Added at runtime to the BodyBones GameObject on first use and cached there —
// never serialized into the actor prefabs.
//
// Sizes are read off the skirt's own cross-sections (the garment is near
// skin-tight at the hip, so it measures the body underneath): at hip height
// the fabric sits ~0.11 m from the axis front-to-back and ~0.19 m side-to-side,
// and the thigh joints sit at x = +-0.079 m. Hence a hip capsule laid ACROSS
// the body (radius = the shallow front-back axis, length = the wide one) plus
// one tapered capsule down each thigh, which together reach the 0.17 m hip
// half-width without a single sphere having to bulge that far forward.
//
// The capsules keep a ~1 cm gap to the fabric on purpose. A collider that
// reaches the cloth inflates the silhouette instead of just catching it.
[DisallowMultipleComponent]
public sealed class ClothBodyColliders : MonoBehaviour
{
    // --- hips: a ball in the seat, hand-tuned in the WardrobeTest scene ---
    // The `hip` BONE sits at y ~= 1.06, ABOVE the skirt's top edge (~1.03), so
    // this has to be dropped to the height the fabric actually drapes over and
    // nudged back into the buttocks (the mesh runs 0.22 m behind the bone and
    // only 0.13 m in front of it, so -Z is the back).
    //
    // Deliberately a SPHERE and deliberately modest. Fitting the minimum
    // enclosing cylinder of the whole pelvis instead (r = 0.125 across the
    // hips) measured WORSE, not better — 100 penetrating vertices became 147 —
    // because a capsule that wide overlaps the thigh capsules and the two then
    // shove the same fabric in different directions.
    private const float HipRadius = 0.100f;
    private const float HipDrop = 0.182f;   // down from the hip bone
    private const float HipBack = 0.061f;   // back from the hip bone
    private const float HipSide = -0.004f;  // sideways trim

    // --- seat: the buttocks, which the hip ball alone does not reach ---
    // From the hip ball's centre the buttock skin runs out to 0.142 m, well past
    // its 0.100 radius, so the whole back of a garment had nothing to rest on:
    // 98 of 101 measured penetrations were back-side. It matters more the looser
    // the fit — at fit scale 1.02 the fabric was tight enough to get away with
    // it, at 1.10 the slack sagged straight through the buttocks (1583 fabric
    // vertices inside the skin; with this capsule, 388).
    //
    // Smallest capsule that encloses the hip/pelvis-weighted skin BEHIND the
    // bone. Sitting behind the thigh capsules rather than on top of them is the
    // whole trick: an earlier attempt at one big cylinder across the hips
    // (r = 0.125, centred on the bone) overlapped them and made things worse.
    private const float SeatRadius = 0.089f;
    private const float SeatLength = 0.278f;  // spans the buttocks left-to-right
    private static readonly Vector3 SeatOffset = new(0f, -0.100f, -0.070f); // hip-local

    // --- thighs: tapered capsule from the hip joint down toward the knee ---
    // Sized off the REAL skinned body, not guessed: thigh skin radius measures
    // 0.119 m at the joint, 0.114 at 0.08 m down, 0.112 at 0.16, 0.101 at 0.24.
    // With a 5 mm particle radius, 0.120 -> 0.108 over 0.30 m keeps a steady
    // ~6 mm of air over the whole band the skirt covers. The first pass used
    // 0.088 -> 0.062, i.e. 3 cm INSIDE the skin, which is why the legs came
    // straight through the cloth.
    private const float ThighTopRadius = 0.120f;
    private const float ThighEndRadius = 0.108f;
    private const float ThighLength = 0.300f;
    private const float ThighSide = -0.010f;  // shift onto the limb's true axis

    // NO crotch collider here, on purpose. The thigh capsules do leave a notch
    // between them, and filling it with a ball did cut deep (>8 mm) penetrations
    // while lying from 62 to 13 — but standing, that same ball sits exactly
    // where the skirt's inner wall hangs and punches a visible hole through it,
    // and standing is the pose you actually see. The lying-pose gap it was meant
    // to close is not a cloth problem at all: with blendWeight = 0 (simulation
    // fully off) the identical gap is still there, because that zone is the belt
    // plus the anchor band, i.e. plain skinning, which no collider can reach.
    // Fix that one in the garment's skin weights, not here.

    private static readonly string[] ThighBones = { "lThighBend", "rThighBend" };
    private static readonly string[] ThighTips = { "lShin", "rShin" };

    private readonly List<ColliderComponent> _colliders = new();
    private bool _built;

    // Get (building once) the capsules a cloth garment should collide with.
    public static List<ColliderComponent> For(BodyBones bodyBones)
    {
        if (bodyBones == null)
        {
            return null;
        }

        if (bodyBones.TryGetComponent<ClothBodyColliders>(out var set) == false)
        {
            set = bodyBones.gameObject.AddComponent<ClothBodyColliders>();
        }

        set.Build(bodyBones);
        return set._colliders;
    }

    private void Build(BodyBones bodyBones)
    {
        if (_built)
        {
            return;
        }

        _built = true;

        var hip = bodyBones.GetBone("hip");
        if (hip != null)
        {
            // Daz bones are identity-rotated in bind pose, so the hip bone's
            // local axes really are the body's left/up/forward.
            var capsule = NewCapsule(hip, "ClothCollider_Hip", Quaternion.identity,
                new Vector3(HipSide, -HipDrop, -HipBack));
            capsule.direction = MagicaCapsuleCollider.Direction.Y;
            capsule.alignedOnCenter = true;
            capsule.SetSize(HipRadius, HipRadius, 0.001f); // length ~0 => sphere
            Register(capsule);

            var seat = NewCapsule(hip, "ClothCollider_Seat", Quaternion.identity, SeatOffset);
            seat.direction = MagicaCapsuleCollider.Direction.X;
            seat.alignedOnCenter = true;
            seat.SetSize(SeatRadius, SeatRadius, SeatLength);
            Register(seat);
        }

        for (int i = 0; i < ThighBones.Length; i++)
        {
            var thigh = bodyBones.GetBone(ThighBones[i]);
            if (thigh == null)
            {
                continue;
            }

            // Aim the capsule at the knee instead of trusting a bone axis, so it
            // rides the leg whatever the rig's convention is. A Start-aligned
            // capsule grows along local -Y (ColliderManager: epos = pos - dir *
            // len), so it is DOWN that has to land on the knee, not up.
            // Daz bones are identity-rotated in bind pose, hence identity is
            // already "straight down" if the knee is missing.
            var rotation = Quaternion.identity;
            var knee = bodyBones.GetBone(ThighTips[i]);
            if (knee != null)
            {
                var toKnee = thigh.InverseTransformPoint(knee.position);
                if (toKnee.sqrMagnitude > 1e-8f)
                {
                    rotation = Quaternion.FromToRotation(Vector3.down, toKnee.normalized);
                }
            }

            var capsule = NewCapsule(thigh, $"ClothCollider_{ThighBones[i]}", rotation, Vector3.zero);
            capsule.direction = MagicaCapsuleCollider.Direction.Y;
            capsule.alignedOnCenter = false; // start at the hip joint, run to the knee
            capsule.center = new Vector3(ThighSide, 0f, 0f);
            capsule.SetSize(ThighTopRadius, ThighEndRadius, ThighLength);
            Register(capsule);
        }
    }

    private static MagicaCapsuleCollider NewCapsule(Transform bone, string name,
        Quaternion localRotation, Vector3 localPosition)
    {
        var host = new GameObject(name);
        var t = host.transform;
        t.SetParent(bone, false);
        t.localPosition = localPosition;
        t.localRotation = localRotation;
        t.localScale = Vector3.one;
        return host.AddComponent<MagicaCapsuleCollider>();
    }

    private void Register(MagicaCapsuleCollider capsule)
    {
        capsule.UpdateParameters(); // required after touching size/direction
        _colliders.Add(capsule);
    }
}

}
