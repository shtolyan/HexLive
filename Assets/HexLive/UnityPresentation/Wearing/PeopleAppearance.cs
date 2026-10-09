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
