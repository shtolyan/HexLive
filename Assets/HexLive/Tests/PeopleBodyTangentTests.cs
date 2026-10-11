#if UNITY_EDITOR
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HexLive.Tests
{
    public sealed class PeopleBodyTangentTests
    {
        [TestCase("Marta", 4410)]
        [TestCase("Kshishtof", 8957)]
        public void ImportedBodyKeepsPaintMapTopologyAndUsableNormalBasis(string actor, int vertices)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/HexLiveContent/People/Prefabs/Actors/" + actor + ".prefab");
            Assert.That(ActorBodyResolver.TryResolve(prefab, out var body, out var error), Is.True, error);
            var mesh = body.sharedMesh;
            Assert.That(mesh.vertexCount, Is.EqualTo(vertices), "Existing paint maps address these vertex indices");
            var tangents = mesh.tangents;
            var normals = mesh.normals;
            Assert.That(tangents.Length, Is.EqualTo(vertices), "Normal maps need a tangent at every vertex");
            for (var i = 0; i < vertices; i++)
            {
                var t = (Vector3)tangents[i];
                Assert.That(t.sqrMagnitude, Is.InRange(.99f, 1.01f), actor + " tangent " + i);
                Assert.That(Mathf.Abs(Vector3.Dot(t, normals[i])), Is.LessThan(.01f), actor + " orthogonality " + i);
                Assert.That(Mathf.Abs(tangents[i].w), Is.EqualTo(1f), actor + " handedness " + i);
            }
        }
    }
}
#endif
