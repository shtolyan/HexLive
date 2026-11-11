#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HexLive.Tests
{
    public sealed class WardrobeGlovesOpeningTests
    {
        const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        const string Source = "Assets/HexLiveContent/Wear/clothing.gloves_stars/StarsGloves.prefab";

        static Type Producer() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation"))
            .Single(t => t != null);

        static T Field<T>(string name) => (T)Producer().GetField(name, PrivateStatic).GetValue(null);

        static bool Accept(JToken body, string actor = "Kshishtof", string source = Source, string donor = "Marta") =>
            (bool)Producer().GetMethod("GlovesOpeningProofAccepted", PrivateStatic)
                .Invoke(null, new object[] { body, actor, source, donor });

        static JObject Proof()
        {
            var faces = Field<int[]>("GlovesOpeningFaces");
            var isolated = Field<int[]>("GlovesOpeningIsolatedVertices");
            const int sourceVertices = 2173, sourceTriangles = 3564;
            int kept = sourceVertices - isolated.Length, triangles = sourceTriangles - faces.Length;
            return new JObject
            {
                ["seed"] = Field<string>("GlovesOpeningSeed"),
                ["topologyRefinement"] = new JObject
                {
                    ["inputVertices"] = kept, ["outputVertices"] = kept,
                    ["inputTriangles"] = triangles, ["outputTriangles"] = triangles,
                    ["glovesOpening"] = new JObject
                    {
                        ["sourceNative"] = Source, ["sourceTopologySha256"] = Field<string>("GlovesOpeningTopology"),
                        ["commonReferencePolicy"] = "retained vertex subset of original uncut fresh-bone target",
                        ["sourceVertices"] = sourceVertices, ["sourceTriangles"] = sourceTriangles,
                        ["retainedTriangleOrderAndMaterialSlotsExact"] = true,
                        ["retainedNonSkinBytesFullSkinAndBindposesExact"] = true,
                        ["sourceAssetsUnchanged"] = true,
                        ["removedSourceFaceIndices"] = new JArray(faces),
                        ["removedSourceVertexIndices"] = new JArray(isolated),
                        ["retainedSourceVertexIndices"] = new JArray(Enumerable.Range(0, sourceVertices).Except(isolated)),
                        ["clippedVertices"] = kept, ["clippedTriangles"] = triangles,
                        ["aggregateOriginalAdditionalTriangleBudget"] = 2048,
                        ["aggregateOriginalAdditionalTriangles"] = -faces.Length,
                        ["watchLayer"] = new JObject
                        {
                            ["axis"] = "lForearmBend to lHand", ["radialScale"] = 1.16f,
                            ["translationsApplied"] = false, ["affectedRetainedWatchVertices"] = 136,
                            ["restoredBridgeSourceVertex"] = 28, ["authoredBridgeRestoreFraction"] = .375f
                        },
                        ["watchLayerEnvelope"] = new JObject
                        {
                            ["passedSampledEnvelope"] = true, ["allVerticesAnd13BarycentricSamplesPerTriangle"] = true,
                            ["sampleCount"] = kept + 13 * triangles, ["targetClearanceMetres"] = .002f,
                            ["minimumSignedDistanceMetres"] = .0020037f,
                            ["samplesBelowMinimum"] = 0, ["insideSamples"] = 0,
                            ["ambiguousSamples"] = 0, ["onSurfaceSamples"] = 0
                        }
                    }
                }
            };
        }

        [Test]
        public void ExactGlovesRecipientCutAndPhysicalWatchLayerPass()
        {
            Assert.That(Accept(Proof()), Is.True);
            Assert.That(Accept(Proof(), "Marta"), Is.False);
            Assert.That(Accept(Proof(), source: "Assets/OtherGloves.prefab"), Is.False);
            Assert.That(Accept(Proof(), donor: "Kshishtof"), Is.False);
        }

        [TestCase("face")]
        [TestCase("vertex")]
        [TestCase("weights")]
        [TestCase("scale")]
        [TestCase("bridge")]
        [TestCase("coverage")]
        [TestCase("floor")]
        public void AlteredOpeningOrWatchLayerCannotReuseSavedProof(string mutation)
        {
            var body = Proof();
            var cut = body["topologyRefinement"]["glovesOpening"];
            switch (mutation)
            {
                case "face": cut["removedSourceFaceIndices"][0] = 0; break;
                case "vertex": cut["removedSourceVertexIndices"][0] = 0; break;
                case "weights": cut["retainedNonSkinBytesFullSkinAndBindposesExact"] = false; break;
                case "scale": cut["watchLayer"]["radialScale"] = 1.15f; break;
                case "bridge": cut["watchLayer"]["restoredBridgeSourceVertex"] = 29; break;
                case "coverage": cut["watchLayerEnvelope"]["sampleCount"] = 1; break;
                case "floor": cut["watchLayerEnvelope"]["minimumSignedDistanceMetres"] = .001999f; break;
            }
            Assert.That(Accept(body), Is.False);
        }
    }
}
#endif

