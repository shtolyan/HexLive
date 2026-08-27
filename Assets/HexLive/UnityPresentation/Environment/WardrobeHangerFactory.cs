#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>A lightweight wooden hanger created only for an occupied wardrobe slot.</summary>
    public static class WardrobeHangerFactory
    {
        public static GameObject? Build(GameObject? wardrobePrefab = null)
        {
            var wardrobe = wardrobePrefab ??
                HexLive.UnityPresentation.Content.AtomicResources.Load<GameObject>(
                    WardrobeAssembly.ResourcePath);
            var template = wardrobe != null
                ? FindDescendant(wardrobe.transform, "HangerTemplate")
                : null;
            if (template == null) return null;

            var root = new GameObject("Occupied wardrobe hanger");
            var model = Object.Instantiate(template.gameObject, root.transform);
            model.name = "HangerTemplate (authored instance)";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;
            model.SetActive(true);
            foreach (var collider in model.GetComponentsInChildren<Collider>(true))
            {
                if (Application.isPlaying) Object.Destroy(collider);
                else Object.DestroyImmediate(collider);
            }
            return root;
        }

        private static Transform? FindDescendant(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindDescendant(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
