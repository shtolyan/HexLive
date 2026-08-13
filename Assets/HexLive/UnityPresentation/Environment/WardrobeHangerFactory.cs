#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>A lightweight wooden hanger created only for an occupied wardrobe slot.</summary>
    public static class WardrobeHangerFactory
    {
        public static GameObject Build()
        {
            var root = new GameObject("Occupied wardrobe hanger");
            AddTwig(root.transform, "shoulder L", new Vector3(0f, -.075f, 0f),
                new Vector3(-0.12f, -.18f, 0f), .010f);
            AddTwig(root.transform, "shoulder R", new Vector3(0f, -.075f, 0f),
                new Vector3(.12f, -.18f, 0f), .010f);
            AddTwig(root.transform, "base", new Vector3(-.12f, -.18f, 0f),
                new Vector3(.12f, -.18f, 0f), .008f);
            AddTwig(root.transform, "hook stem", new Vector3(0f, -.075f, 0f),
                Vector3.zero, .008f);
            AddTwig(root.transform, "hook", Vector3.zero,
                new Vector3(.055f, .035f, 0f), .008f);
            return root;
        }

        private static void AddTwig(Transform parent, string name, Vector3 a, Vector3 b, float radius)
        {
            var twig = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            twig.name = name;
            twig.transform.SetParent(parent, false);
            var direction = b - a;
            twig.transform.localPosition = (a + b) * .5f;
            twig.transform.localRotation = Quaternion.FromToRotation(Vector3.up, direction.normalized);
            twig.transform.localScale = new Vector3(radius, direction.magnitude * .5f, radius);
            var collider = twig.GetComponent<Collider>();
            if (Application.isPlaying) Object.Destroy(collider); else Object.DestroyImmediate(collider);
            var renderer = twig.GetComponent<Renderer>();
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { color = new Color(.36f, .20f, .09f, 1f) };
            renderer.sharedMaterial = material;
        }
    }
}
