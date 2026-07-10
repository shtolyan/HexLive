using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Spec 31B.2: a garment bone rides its body bone. Added at runtime by
// Wear.Construct — never serialized into prefabs.
public sealed class ParentConnection : MonoBehaviour
{
    public void ConnectTo(Transform bodyBone)
    {
        var t = transform;
        t.SetParent(bodyBone);
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
    }
}

}
