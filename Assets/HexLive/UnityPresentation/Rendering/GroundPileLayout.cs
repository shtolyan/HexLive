using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{
// §54: source seating happens once before Initialize; only this outer slot moves.
// The parent object remains at its simulation/navigation junction.
public sealed class GroundPileLayout : MonoBehaviour
{
    public GroundPileProfile Profile { get; private set; }
    public int SlotIndex { get; private set; }
    public int Capacity { get; private set; }
    public Transform SlotTransform { get; private set; }

    public void Initialize(Transform visual, GroundPileProfile profile)
    {
        Profile = profile;
        var slot = new GameObject("GroundPileSlot");
        slot.transform.SetParent(transform, false);
        SlotTransform = slot.transform;
        visual.SetParent(SlotTransform, false);
        visual.localPosition += new Vector3(profile.RecenterX, 0f, profile.RecenterZ);
        if (profile.CenterVisualXZ && ObjectFit.WorldBounds(visual.gameObject,out var bounds))
        {
            var center=SlotTransform.InverseTransformPoint(bounds.center);
            visual.localPosition -= new Vector3(center.x,0f,center.z);
        }
    }

    public void ApplyLegacy(int objectId)
    {
        SlotIndex = -1; Capacity = 0;
        SlotTransform.localPosition = Vector3.zero;
        SlotTransform.localRotation = Profile.Garment
            ? Quaternion.Euler(0f,(objectId*73L)%360,0f) : Quaternion.identity;
    }

    public void Apply(int slotIndex, int capacity, int objectId)
    {
        SlotIndex = slotIndex;
        Capacity = capacity;
        var pose = Profile.Slot(slotIndex, capacity, objectId);
        SlotTransform.localPosition = new Vector3(pose.X, pose.Y, pose.Z);
        SlotTransform.localRotation = Quaternion.Euler(0f, pose.Yaw, 0f);
    }
}
}
