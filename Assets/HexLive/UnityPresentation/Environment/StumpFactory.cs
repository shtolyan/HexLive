#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: the felled-palm stump (the sim object <c>stump.palm</c>) — a
    /// short standing log with the ring-cut face on top, so it reads as cut from
    /// the same tree as the logs/beds. Falls back to a plain cylinder only if the
    /// log prefab is missing.
    /// </summary>
    public static class StumpFactory
    {
        public const float Height = 0.30f;         // how far the stump sticks up
        public const float BaseLift = Height * 0.5f; // lift so its base sits on the ground

        public static GameObject Build(float hexRadius)
        {
            var diameter = hexRadius * 0.42f;       // ~ the palm trunk base

            var logPrefab = WorldPropResources.Load("resource.log");
            GameObject stump;
            if (logPrefab != null && ObjectFit.HasRenderableGeometry(logPrefab))
            {
                stump = Object.Instantiate(logPrefab);
                stump.name = "Stump";
                // ScriptedImporter-backed prefab wrappers can be non-null but
                // empty in Player. Never let such a wrapper suppress fallback.
                if (!ObjectFit.HasRenderableGeometry(stump))
                {
                    Object.Destroy(stump);
                    stump = BuildFallback(diameter);
                    RemoveColliders(stump);
                    return stump;
                }
                // log_final lies along local X (length) with a centred pivot; scale
                // the length axis to a short stub and the girth to the trunk.
                if (!ObjectFit.WorldBounds(stump, out var nb))
                {
                    nb = new Bounds(Vector3.zero, Vector3.one);
                }

                var nativeLen = Mathf.Max(0.001f, nb.size.x);
                var nativeDia = Mathf.Max(0.001f, Mathf.Max(nb.size.y, nb.size.z));
                stump.transform.localScale = new Vector3(
                    Height / nativeLen, diameter / nativeDia, diameter / nativeDia);
                stump.transform.localRotation = Quaternion.Euler(0f, 0f, 90f); // length X → up
            }
            else
            {
                stump = BuildFallback(diameter);
            }

            RemoveColliders(stump);
            return stump;
        }

        private static GameObject BuildFallback(float diameter)
        {
            var stump = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stump.name = "Stump (build-safe fallback)";
            stump.transform.localScale = new Vector3(diameter, Height * 0.5f, diameter);
            var mr = stump.GetComponent<MeshRenderer>();
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (mr != null && shader != null)
            {
                var mat = new Material(shader);
                mat.SetColor("_BaseColor", new Color(0.40f, 0.28f, 0.17f));
                mat.SetFloat("_Smoothness", 0.1f);
                mr.sharedMaterial = mat;
            }
            return stump;
        }

        private static void RemoveColliders(GameObject stump)
        {
            foreach (var col in stump.GetComponentsInChildren<Collider>()) Object.Destroy(col);
        }
    }
}
