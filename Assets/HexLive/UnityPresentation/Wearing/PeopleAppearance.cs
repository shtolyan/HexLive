using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    // §169: skin variants share one imported body and atlas. No donor body dependency.
    public sealed class PeopleAppearance : MonoBehaviour
    {
        [Serializable] public struct Skin
        {
            public string id;
            public Material material;
        }
        [SerializeField] private string bodyId;
        [SerializeField] private Skin[] skins = Array.Empty<Skin>();
        public string BodyId => bodyId;
        private readonly Dictionary<SkinnedMeshRenderer, Bounds> _authoredBounds = new();

        public void RestoreLocomotionBounds(SkinnedMeshRenderer skin)
        {
            if (_authoredBounds.TryGetValue(skin, out var bounds)) skin.localBounds = bounds;
        }

        private void Awake()
        {
            foreach (var skin in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                _authoredBounds[skin] = skin.localBounds;
        }

        private readonly Dictionary<Transform, Transform> _propAnchors = new();

        // Gear offsets are authored in actor metres. Imported FBX bones use
        // centimetres; a dedicated anchor preserves the old gear contract.
        public Transform PropAnchor(Transform bone)
        {
            if (bone == null) return null;
            if (!_propAnchors.TryGetValue(bone, out var anchor) || anchor == null)
            {
                anchor = new GameObject("PropAnchorMetres").transform;
                anchor.SetParent(bone, false);
                _propAnchors[bone] = anchor;
            }
            var actorScale = transform.lossyScale;
            var boneScale = bone.lossyScale;
            anchor.localScale = new Vector3(actorScale.x / boneScale.x,
                actorScale.y / boneScale.y, actorScale.z / boneScale.z);
            return anchor;
        }

        public Dictionary<string, Material> SkinMaterials(string id)
        {
            var result = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            foreach (var skin in skins)
                if (string.Equals(skin.id, id, StringComparison.OrdinalIgnoreCase) && skin.material != null)
                    result[skin.material.name] = skin.material;
            return result;
        }
    }
}
