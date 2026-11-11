#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    const string ReikoOpenCuffsId = "clothing.outfit_reiko";
    const string ReikoOpenCuffsSeed = "fresh-bone-open-reiko-cuffs";
    const string ReikoCuffCommonReference = "retained vertex subset of original uncut fresh-bone target";
    const string ReikoNativePrefab = "Assets/HexLiveContent/Wear/clothing.outfit_reiko/ReikoOutfit.prefab";
    const string ReikoNativeMesh = "Assets/ImportedActors/Wear/ReikoOutfit/Meshes/Marta.mesh";
    const string ReikoTopology = "bbf3a00a0f8c2c0134fc5d3e972e3d86ac74907ecd4bfe3fcf238ba8cc3fb5a4";
    const int ReikoShoesSlot = 3;
    static readonly int[] ReikoCuffCenters = { 1709, 1741, 2421, 2453 };
    static readonly int[] ReikoCuffFaces = {
        2494, 2498, 2501, 2504, 2514, 2522, 2530, 2532, 2546, 2550, 2555, 2568, 2573, 2575, 2577, 2579,
        3590, 3594, 3597, 3600, 3610, 3618, 3626, 3628, 3642, 3646, 3651, 3664, 3669, 3671, 3673, 3675
    };

    static Mesh RemoveReikoCuffClosures(Mesh source, out int[] retained, out JObject proof)
    {
        if (source.vertexCount != 9372 || source.subMeshCount != 17 || source.blendShapeCount != 0 ||
            SourceCapTopologySHA(source) != ReikoTopology)
            throw new InvalidOperationException("Reiko native-equivalent shoe topology changed");

        var centers = new HashSet<int>(ReikoCuffCenters);
        var kept = Enumerable.Range(0, source.vertexCount).Where(i => !centers.Contains(i)).ToArray();
        var remap = Enumerable.Repeat(-1, source.vertexCount).ToArray();
        for (int i = 0; i < kept.Length; i++) remap[kept[i]] = i;

        var skipped = new List<int>();
        var slots = new int[source.subMeshCount][];
        var referenced = new HashSet<int>();
        int face = 0;
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (source.GetTopology(slot) != MeshTopology.Triangles)
                throw new InvalidOperationException("Reiko shoe closure requires triangles");
            int[] indices = source.GetTriangles(slot);
            var keptIndices = new List<int>(indices.Length);
            for (int i = 0; i < indices.Length; i += 3, face++)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                int central = (centers.Contains(a) ? 1 : 0) + (centers.Contains(b) ? 1 : 0) + (centers.Contains(c) ? 1 : 0);
                if (central != 0)
                {
                    if (slot != ReikoShoesSlot || central != 1)
                        throw new InvalidOperationException("A Reiko cuff center is incident outside its recorded Shoes fan");
                    skipped.Add(face);
                    continue;
                }
                keptIndices.Add(remap[a]);
                keptIndices.Add(remap[b]);
                keptIndices.Add(remap[c]);
                referenced.Add(a);
                referenced.Add(b);
                referenced.Add(c);
            }
            slots[slot] = keptIndices.ToArray();
        }
        if (face != 12062 || !skipped.SequenceEqual(ReikoCuffFaces) || !referenced.SetEquals(kept))
            throw new InvalidOperationException("Exact Reiko cuff removal coverage changed");

        var lineage = new JArray(kept.Select(i => new JObject {
            ["original"] = i, ["indices"] = new JArray(i, i, i), ["barycentric"] = new JArray(1, 0, 0)
        }));
        var payload = new JObject {
            ["lineage"] = lineage,
            ["submeshes"] = new JArray(slots.Select(slot => new JArray(slot))),
            ["triangles"] = new JArray(slots.SelectMany(slot => slot))
        };
        var mesh = SourceCapLineageMesh(source, payload, out var attributes);
        try
        {
            var counts = source.GetBonesPerVertex().ToArray();
            var weights = source.GetAllBoneWeights().ToArray();
            var offsets = new int[counts.Length];
            int at = 0;
            for (int i = 0; i < offsets.Length; i++)
            {
                offsets[i] = at;
                at += counts[i];
            }
            VerifyCollapsedKeptAttributes(source, mesh, kept, counts, weights, offsets);
            if (!mesh.bindposes.SequenceEqual(source.bindposes))
                throw new InvalidOperationException("Reiko cuff subset changed bindposes");
            retained = kept;
            proof = new JObject {
                ["scope"] = "only the reviewed closure faces and their four isolated center attributes",
                ["sourceTopologySha256"] = ReikoTopology,
                ["sourceNative"] = ReikoNativePrefab,
                ["commonReferencePolicy"] = ReikoCuffCommonReference,
                ["sourceTriangles"] = face,
                ["sourceVertices"] = source.vertexCount,
                ["removedSourceFaceIndices"] = new JArray(skipped),
                ["removedSourceVertexIndices"] = new JArray(ReikoCuffCenters),
                ["retainedSourceVertexIndices"] = new JArray(kept),
                ["retainedTriangleOrderAndMaterialSlotsExact"] = true,
                ["retainedNonSkinBytesFullSkinAndBindposesExact"] = true,
                ["clippedTriangles"] = mesh.triangles.Length / 3,
                ["clippedVertices"] = mesh.vertexCount,
                ["attributes"] = attributes,
                ["sourceAssetsUnchanged"] = true
            };
            return mesh;
        }
        catch
        {
            Object.DestroyImmediate(mesh);
            throw;
        }
    }

    static void ValidateReikoCuffInputs(FinalFitInput input)
    {
        if (input.Id != ReikoOpenCuffsId || input.Donor != "Marta" || input.NativeSource != ReikoNativePrefab ||
            FinalFileSHA(ReikoNativePrefab) != "014863885d40cc0cfa53b2449ab037032a7adb5d2c9c87ead1ff725429b1fb90" ||
            FinalFileSHA(ReikoNativeMesh) != "584a41081488793953b85ea31d0e4cb4f2b08667ac0375f668480a9ecd420f86" ||
            SourceCapTopologySHA(input.MartaMesh) != ReikoTopology || SourceCapTopologySHA(input.KshishtofMesh) != ReikoTopology)
            throw new InvalidOperationException("Reiko outfit/source identity does not match the reviewed cuff topology");
    }

    static bool ReikoCuffOpeningProofAccepted(JToken body, string actor, string source, string donor)
    {
        var topology = body?["topologyRefinement"];
        var cut = topology?["reikoCuffOpening"];
        if ((string)body?["seed"] != ReikoOpenCuffsSeed) return cut == null;
        if (!(cut is JObject) || actor != "Kshishtof" || donor != "Marta" || source != ReikoNativePrefab)
            return false;

        int? output = (int?)topology["outputTriangles"];
        const int sourceTriangles = 12062, originalBudget = 2048;
        return (string)cut["sourceNative"] == ReikoNativePrefab &&
            (string)cut["sourceTopologySha256"] == ReikoTopology &&
            (string)cut["commonReferencePolicy"] == ReikoCuffCommonReference &&
            (bool?)cut["retainedTriangleOrderAndMaterialSlotsExact"] == true &&
            (bool?)cut["retainedNonSkinBytesFullSkinAndBindposesExact"] == true &&
            (bool?)cut["sourceAssetsUnchanged"] == true &&
            cut["removedSourceFaceIndices"] is JArray faces && faces.Values<int>().SequenceEqual(ReikoCuffFaces) &&
            cut["removedSourceVertexIndices"] is JArray centers && centers.Values<int>().SequenceEqual(ReikoCuffCenters) &&
            cut["retainedSourceVertexIndices"] is JArray retained &&
            retained.Values<int>().SequenceEqual(Enumerable.Range(0, 9372).Except(ReikoCuffCenters)) &&
            (int?)cut["sourceVertices"] == 9372 && (int?)cut["sourceTriangles"] == sourceTriangles &&
            (int?)cut["clippedVertices"] == 9368 && (int?)cut["clippedTriangles"] == 12030 &&
            (int?)topology["inputVertices"] == 9368 && (int?)topology["inputTriangles"] == 12030 &&
            (int?)cut["aggregateOriginalAdditionalTriangleBudget"] == originalBudget &&
            output.HasValue && output.Value >= 12030 && output.Value <= sourceTriangles + originalBudget &&
            (int?)cut["aggregateOriginalAdditionalTriangles"] == output.Value - sourceTriangles;
    }
}
}
#endif
