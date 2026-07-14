#nullable enable
using System.Collections;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54: fells a tree view instead of hard-cutting it. When the sim
    /// despawns a chopped tree, the renderer hands its GameObject to this
    /// component, which tips the trunk over about its base, leaves a low stump,
    /// and destroys the fallen trunk a couple of seconds later. The logs the sim
    /// scattered spawn in the same frame, so it reads as "timber!".
    /// </summary>
    public sealed class TreeFall : MonoBehaviour
    {
        public void Fell(float hexRadius)
        {
            LeaveStump(hexRadius);
            StartCoroutine(FallRoutine());
        }

        private void LeaveStump(float hexRadius)
        {
            var stump = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stump.name = "Stump";
            // Parent to the world root (this object's parent) so it survives when
            // the fallen trunk is destroyed.
            stump.transform.SetParent(transform.parent, false);
            stump.transform.position = transform.position;
            stump.transform.localScale = new Vector3(hexRadius * 0.5f, 0.12f, hexRadius * 0.5f);

            var mr = stump.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.SetColor("_BaseColor", new Color(0.34f, 0.22f, 0.12f));
                mat.SetFloat("_Smoothness", 0.1f);
                mr.sharedMaterial = mat;
            }

            var col = stump.GetComponent<Collider>();
            if (col != null)
            {
                Destroy(col);
            }
        }

        private IEnumerator FallRoutine()
        {
            var basePoint = transform.position;
            // A per-tree fall direction (deterministic from the instance) so a
            // grove doesn't fall in lockstep. Tip over a horizontal axis.
            var yaw = Mathf.Abs(GetInstanceID() % 360);
            var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            var axis = Vector3.Cross(Vector3.up, dir).normalized;

            const float totalAngle = 86f;
            const float duration = 0.9f;
            var applied = 0f;
            var t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                var k = Mathf.Clamp01(t / duration);
                var eased = k * k; // accelerate as it topples
                var target = totalAngle * eased;
                transform.RotateAround(basePoint, axis, target - applied);
                applied = target;
                yield return null;
            }

            yield return new WaitForSeconds(2f);
            Destroy(gameObject);
        }
    }
}
