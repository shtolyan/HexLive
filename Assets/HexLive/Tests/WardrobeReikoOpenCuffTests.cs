#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HexLive.Tests
{
public sealed class WardrobeReikoOpenCuffTests
{
    const string NativeSource = "Assets/HexLiveContent/Wear/clothing.outfit_reiko/ReikoOutfit.prefab";

    static JObject Control()
    {
        int[] centers = { 1709, 1741, 2421, 2453 };
        return new JObject {
            ["seed"] = "fresh-bone-open-reiko-cuffs",
            ["topologyRefinement"] = new JObject {
                ["inputVertices"] = 9368, ["inputTriangles"] = 12030, ["outputTriangles"] = 12030,
                ["reikoCuffOpening"] = new JObject {
                    ["sourceNative"] = NativeSource,
                    ["sourceTopologySha256"] = "bbf3a00a0f8c2c0134fc5d3e972e3d86ac74907ecd4bfe3fcf238ba8cc3fb5a4",
                    ["commonReferencePolicy"] = "retained vertex subset of original uncut fresh-bone target",
                    ["sourceVertices"] = 9372, ["sourceTriangles"] = 12062,
                    ["clippedVertices"] = 9368, ["clippedTriangles"] = 12030,
                    ["retainedTriangleOrderAndMaterialSlotsExact"] = true,
                    ["retainedNonSkinBytesFullSkinAndBindposesExact"] = true,
                    ["sourceAssetsUnchanged"] = true,
                    ["removedSourceFaceIndices"] = new JArray(
                        2494, 2498, 2501, 2504, 2514, 2522, 2530, 2532, 2546, 2550, 2555, 2568, 2573, 2575, 2577, 2579,
                        3590, 3594, 3597, 3600, 3610, 3618, 3626, 3628, 3642, 3646, 3651, 3664, 3669, 3671, 3673, 3675),
                    ["removedSourceVertexIndices"] = new JArray(centers),
                    ["retainedSourceVertexIndices"] = new JArray(Enumerable.Range(0, 9372).Except(centers)),
                    ["aggregateOriginalAdditionalTriangleBudget"] = 2048,
                    ["aggregateOriginalAdditionalTriangles"] = -32,
                }
            }
        };
    }

    static bool Accepted(JObject body, string actor = "Kshishtof", string source = NativeSource)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation"))
            .Single(candidate => candidate != null);
        return (bool)type.GetMethod("ReikoCuffOpeningProofAccepted", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { body, actor, source, "Marta" });
    }

    [Test]
    public void ExactKshishtofCuffOpeningPasses()
    {
        Assert.That(Accepted(Control()), Is.True);
        Assert.That(Accepted(new JObject { ["seed"] = "fresh-bone-retarget" }), Is.True);
    }

    [TestCase("wrong-body")]
    [TestCase("wrong-source")]
    [TestCase("wrong-faces")]
    [TestCase("wrong-reference")]
    [TestCase("wrong-retained")]
    [TestCase("missing-proof")]
    [TestCase("unexpected-proof")]
    [TestCase("budget")]
    public void OpeningCannotChangeScopeReferencesOrBudget(string change)
    {
        var body = Control();
        var topology = (JObject)body["topologyRefinement"];
        var cut = (JObject)topology["reikoCuffOpening"];
        string actor = "Kshishtof", source = NativeSource;
        switch (change)
        {
            case "wrong-body": actor = "Marta"; break;
            case "wrong-source": source = "another.prefab"; break;
            case "wrong-faces": cut["removedSourceFaceIndices"][0] = 2495; break;
            case "wrong-reference": cut["commonReferencePolicy"] = "clipped seed"; break;
            case "wrong-retained": ((JArray)cut["retainedSourceVertexIndices"]).RemoveAt(0); break;
            case "missing-proof": topology.Remove("reikoCuffOpening"); break;
            case "unexpected-proof": body["seed"] = "fresh-bone-retarget"; break;
            case "budget":
                topology["outputTriangles"] = 14111;
                cut["aggregateOriginalAdditionalTriangles"] = 2049;
                break;
        }
        Assert.That(Accepted(body, actor, source), Is.False);
    }
}
}
#endif
