#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.UnityDebug.Editor
{
    public static partial class LegacyWebGLWardrobePreparation
    {
        const string GlovesOpeningId = "clothing.gloves_stars";
        const string GlovesOpeningSource = "Assets/HexLiveContent/Wear/clothing.gloves_stars/StarsGloves.prefab";
        const string GlovesOpeningRawMesh = "Assets/ImportedActors/Wear/StarsGloves/Meshes/Marta.mesh";
        const string GlovesOpeningTopology = "8fe0001b5f4f00d05909ae37b12ad6cbd9752b63c44f054b2dd93b5ee1f99039";

        const string GlovesOpeningSeed = "fresh-bone-open-stars-gloves";
        const string GlovesOpeningCommonReference = "retained vertex subset of original uncut fresh-bone target";
        const int GlovesOpeningSourceTriangles = 3564;
        const int GlovesOpeningSourceVertices = 2173;
        static readonly int[] GlovesOpeningFaces =
        {
            28, 29, 30, 37, 39, 41, 42, 43, 44, 54, 55, 56, 57, 58, 59, 60,
            61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76,
            77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92,
            93, 94, 95, 336, 337, 338, 339, 340, 341, 342, 343, 344, 345, 346, 347, 348,
            349, 350, 351, 352, 353, 354, 355, 356, 357, 358, 359, 360, 361, 362, 363, 364,
            365, 366, 367, 368, 369, 370, 371, 372, 373, 383, 384, 385, 386, 387, 388, 389,
            390, 392, 393, 395, 490, 626, 627, 628, 629, 630, 631, 1669, 1675, 1676, 1677, 1678,
            1679, 1681, 1686, 1688, 1690, 1692, 1693, 1694, 1695, 1696, 1697, 1698, 1699, 1700, 1701, 1702,
            1703, 1704, 1705, 1706, 1708, 1709, 1710, 1711, 1712, 1713, 1715, 1716, 1717, 1718, 1719, 1721,
            1728,
        };
        static readonly int[] GlovesOpeningIsolatedVertices =
        {
            27, 37, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59,
            60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75,
            76, 77, 78, 79, 80, 81, 82, 230, 231, 232, 233, 234, 235, 236, 237, 238,
            239, 240, 241, 242, 243, 244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254,
            255, 256, 257, 258, 259, 260, 261, 262, 263, 264, 265, 266, 267, 268, 269, 270,
            271, 272, 273, 284, 285, 286, 287, 288, 289, 427, 428, 429, 430, 431, 432, 433,
            1056, 1058, 1063, 1067, 1068, 1069, 1070, 1071, 1072, 1073, 1074, 1075, 1077, 1079, 1081, 1083,
            1084, 1087,
        };

        private static Mesh OpenGlovesKClosures(Mesh source, out int[] retained, out JObject proof)
        {
            if (source.vertexCount != GlovesOpeningSourceVertices || source.subMeshCount != 2 || source.blendShapeCount != 0 ||
                SourceCapTopologySHA(source) != GlovesOpeningTopology)
                throw new InvalidDataException("Stars Gloves source topology changed");
            var removed = GlovesOpeningFaces.ToHashSet();
            int faceIndex = 0;
            var keptBySlot = new int[source.subMeshCount][];
            for (int slot = 0; slot < source.subMeshCount; slot++)
            {
                var indices = source.GetTriangles(slot);
                var kept = new List<int>(indices.Length);
                for (int at = 0; at < indices.Length; at += 3, faceIndex++)
                    if (!removed.Contains(faceIndex))
                        kept.AddRange(new[] { indices[at], indices[at + 1], indices[at + 2] });
                keptBySlot[slot] = kept.ToArray();
            }
            if (faceIndex != GlovesOpeningSourceTriangles)
                throw new InvalidDataException("Stars Gloves triangle count changed");
            retained = keptBySlot.SelectMany(indices => indices).Distinct().OrderBy(index => index).ToArray();
            if (!retained.SequenceEqual(Enumerable.Range(0, GlovesOpeningSourceVertices).Except(GlovesOpeningIsolatedVertices)))
                throw new InvalidDataException("Stars Gloves removed isolated vertices changed");
            var indexOf = retained.Select((original, index) => (original, index)).ToDictionary(item => item.original, item => item.index);
            var slots = keptBySlot.Select(indices => indices.Select(index => indexOf[index]).ToArray()).ToArray();
            var lineage = new JArray(retained.Select(i => new JObject
            {
                ["original"] = i, ["indices"] = new JArray(i, i, i), ["barycentric"] = new JArray(1, 0, 0)
            }));
            var payload = new JObject { ["lineage"] = lineage, ["submeshes"] = new JArray(slots.Select(slot => new JArray(slot))),
                ["triangles"] = new JArray(slots.SelectMany(slot => slot)) };
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
                VerifyCollapsedKeptAttributes(source, mesh, retained, counts, weights, offsets);
                if (!mesh.bindposes.SequenceEqual(source.bindposes))
                    throw new InvalidDataException("Stars Gloves subset changed bindposes");
                proof = new JObject {
                    ["sourceNative"] = GlovesOpeningSource,
                    ["sourceTopologySha256"] = GlovesOpeningTopology,
                    ["commonReferencePolicy"] = GlovesOpeningCommonReference,
                    ["sourceTriangles"] = GlovesOpeningSourceTriangles,
                    ["sourceVertices"] = GlovesOpeningSourceVertices,
                    ["retainedTriangleOrderAndMaterialSlotsExact"] = true,
                    ["retainedNonSkinBytesFullSkinAndBindposesExact"] = true,
                    ["sourceAssetsUnchanged"] = true,
                    ["removedSourceFaceIndices"] = new JArray(GlovesOpeningFaces),
                    ["removedSourceVertexIndices"] = new JArray(GlovesOpeningIsolatedVertices),
                    ["retainedSourceVertexIndices"] = new JArray(retained),
                    ["clippedTriangles"] = mesh.triangles.Length / 3,
                    ["clippedVertices"] = mesh.vertexCount,
                    ["attributes"] = attributes
                };
                return mesh;
            }
            catch
            {
                Object.DestroyImmediate(mesh);
                throw;
            }
        }


        static readonly int[] GlovesWatchOriginalVertices =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
            32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47,
            48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63,
            64, 65, 66, 67, 68, 69, 70, 434, 435, 436, 437, 438, 439, 440, 441, 442,
            443, 444, 445, 446, 447, 448, 449, 450, 451, 452, 453, 454, 455, 456, 457, 458,
            459, 460, 461, 462, 463, 464, 465, 466, 467, 468, 469, 470, 471, 472, 473, 474,
            475, 476, 477, 478, 479, 480, 481, 482, 483, 484, 485, 486, 487, 488, 489, 490,
            491, 492, 493, 494, 495, 496, 497, 498, 499, 500, 501, 502, 503, 504, 505, 506,
            507, 508, 509, 510, 511, 512, 513, 514, 515, 516, 517, 518, 519, 520, 521, 522,
            523, 524, 525,
        };

        static JObject PlaceWatchOverLeftCuff(Fit fit, int[] retained, Vector3[] points, Vector3[] normals)
        {
            if (points.Length != retained.Length || normals.Length != retained.Length)
                throw new InvalidDataException("Gloves watch recipe requires unchanged original compact topology");
            const float radiusScale = 1.16f;
            var wrist = fit.Bones.Single(b => b.name == "lHand").position;
            var forearm = fit.Bones.Single(b => b.name == "lForearmBend").position;
            var axis = (wrist - forearm).normalized;
            if (axis.sqrMagnitude < .99f)
                throw new InvalidDataException("Invalid left forearm anatomical axis");
            int moved = 0;
            for (int i = 0; i < retained.Length; i++)
            {
                if (Array.BinarySearch(GlovesWatchOriginalVertices, retained[i]) < 0) continue;
                var offset = points[i] - wrist;
                var radial = offset - axis * Vector3.Dot(offset, axis);
                points[i] += radial * (radiusScale - 1f);
                var axialNormal = axis * Vector3.Dot(normals[i], axis);
                normals[i] = (axialNormal + (normals[i] - axialNormal) / radiusScale).normalized;
                moved++;
            }
            if (moved != 136)
                throw new InvalidDataException("Stars Gloves watch source component changed");

            // One retained strap vertex folded across the dial. Restore
            // part of its authored, radially scaled recipient position.
            const int watchBridge = 28;
            const float restoreFraction = .375f;
            int bridge = Array.IndexOf(retained, watchBridge);
            if (bridge < 0) throw new InvalidDataException("Stars Gloves watch bridge topology changed");
            var authored = fit.WorldVertices[watchBridge];
            var authoredOffset = authored - wrist;
            var authoredRadial = authoredOffset - axis * Vector3.Dot(authoredOffset, axis);
            var authoredScaled = authored + authoredRadial * (radiusScale - 1f);
            points[bridge] = Vector3.Lerp(points[bridge], authoredScaled, restoreFraction);
            return new JObject {
                ["restoredBridgeSourceVertex"] = watchBridge,
                ["authoredBridgeRestoreFraction"] = restoreFraction,
                ["recipe"] = "scale radial watch extent over the fitted cuff",
                ["axis"] = "lForearmBend to lHand",
                ["radialScale"] = radiusScale,
                ["translationsApplied"] = false,
                ["affectedRetainedWatchVertices"] = moved
            };
        }


        static void ValidateGlovesOpeningInputs(FinalFitInput input)
        {
            if (input.Id != GlovesOpeningId || input.Donor != "Marta" || input.NativeSource != GlovesOpeningSource ||
                FinalFileSHA(GlovesOpeningSource) != "35abd6e9c8961b3b19c7567df0f2f17bba4ee581fe9d0e6244d764339bee4ef5" ||
                FinalFileSHA(GlovesOpeningRawMesh) != "4107214df94902cbe03adb85cf1874bbad8e3c44129468afb9276954edc458f8" ||
                SourceCapTopologySHA(input.MartaMesh) != GlovesOpeningTopology ||
                SourceCapTopologySHA(input.KshishtofMesh) != GlovesOpeningTopology)
                throw new InvalidDataException("Stars Gloves source does not match the reviewed closure topology");
        }

        static bool GlovesOpeningProofAccepted(JToken body, string actor, string source, string donor)
        {
            var topology = body?["topologyRefinement"];
            var cut = topology?["glovesOpening"];
            if ((string)body?["seed"] != GlovesOpeningSeed) return cut == null;
            if (!(cut is JObject) || actor != "Kshishtof" || source != GlovesOpeningSource || donor != "Marta")
                return false;

            int? output = (int?)topology["outputTriangles"];
            const int budget = 2048;
            int clippedTriangles = GlovesOpeningSourceTriangles - GlovesOpeningFaces.Length;
            int clippedVertices = GlovesOpeningSourceVertices - GlovesOpeningIsolatedVertices.Length;
            var watch = cut["watchLayer"];
            var adjusted = cut["watchLayerEnvelope"];
            return (string)cut["sourceNative"] == GlovesOpeningSource &&
                (string)cut["sourceTopologySha256"] == GlovesOpeningTopology &&
                (string)cut["commonReferencePolicy"] == GlovesOpeningCommonReference &&
                (bool?)cut["retainedTriangleOrderAndMaterialSlotsExact"] == true &&
                (bool?)cut["retainedNonSkinBytesFullSkinAndBindposesExact"] == true &&
                (bool?)cut["sourceAssetsUnchanged"] == true &&
                cut["removedSourceFaceIndices"] is JArray faces && faces.Values<int>().SequenceEqual(GlovesOpeningFaces) &&
                cut["removedSourceVertexIndices"] is JArray isolated && isolated.Values<int>().SequenceEqual(GlovesOpeningIsolatedVertices) &&
                cut["retainedSourceVertexIndices"] is JArray kept &&
                kept.Values<int>().SequenceEqual(Enumerable.Range(0, GlovesOpeningSourceVertices).Except(GlovesOpeningIsolatedVertices)) &&
                (int?)cut["sourceVertices"] == GlovesOpeningSourceVertices && (int?)cut["sourceTriangles"] == GlovesOpeningSourceTriangles &&
                (int?)cut["clippedVertices"] == clippedVertices && (int?)cut["clippedTriangles"] == clippedTriangles &&
                (int?)topology["inputVertices"] == clippedVertices && (int?)topology["inputTriangles"] == clippedTriangles &&
                (int?)topology["outputVertices"] == clippedVertices && output == clippedTriangles &&
                (int?)cut["aggregateOriginalAdditionalTriangleBudget"] == budget &&
                (int?)cut["aggregateOriginalAdditionalTriangles"] == clippedTriangles - GlovesOpeningSourceTriangles &&
                (string)watch?["axis"] == "lForearmBend to lHand" &&
                (float?)watch?["radialScale"] == 1.16f && (bool?)watch?["translationsApplied"] == false &&
                (int?)watch?["affectedRetainedWatchVertices"] == 136 &&
                (int?)watch?["restoredBridgeSourceVertex"] == 28 &&
                (float?)watch?["authoredBridgeRestoreFraction"] == .375f &&
                (bool?)adjusted?["passedSampledEnvelope"] == true &&
                (bool?)adjusted?["allVerticesAnd13BarycentricSamplesPerTriangle"] == true &&
                (int?)adjusted?["sampleCount"] == clippedVertices + 13 * clippedTriangles &&
                (float?)adjusted?["targetClearanceMetres"] == .002f &&
                (float?)adjusted?["minimumSignedDistanceMetres"] >= .002f &&
                (int?)adjusted?["samplesBelowMinimum"] == 0 && (int?)adjusted?["insideSamples"] == 0 &&
                (int?)adjusted?["ambiguousSamples"] == 0 && (int?)adjusted?["onSurfaceSamples"] == 0;
        }
    }
}
#endif
