#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    private static Vector3[] PlaceSandalsRigidPanels(FinalFitInput input, Mesh seed, Vector3[] roundSeed,
        Vector3[] normals, Vector3[][] references, TriangleTree body, out JObject proof)
    {
        const float floor = .002f;
        const float solverCap = .1f;
        const string native = "Assets/HexLiveContent/Wear/clothing.sandals_summer4/Sandals4.prefab";
        const string nativeSha = "c88c5513174db5d38a7dc81cd2047ba9b8a8c46d1cd9ec7e7801237602e43f34";
        const string baseRowSha = "e58f552824b747ba8fed109001ddb3af2fff264ac786c3f75545ee98b27c34f7";
        const string roundSeedSha = "99a4838a4b325111ce58b375ebcc517623f22345613674cbf602c7474af86f65";
        const string commonSha = "b09bfefe99b0eb624b1fbd5422cf421df1faf72ae203ef552f274315eb441267";
        const string roundZeroSha = "46e98ad7023ecd3ed696249e6d5395ee7fdca3e3a83ada6a03604addf1fc40e9";
        const string candidateSha = "985acdce0459073a895f119ae37a3bf528b2b7b92f1e623d1eedf87ee191a141";
        if (input.NativeSource != native || FinalFileSHA(native) != nativeSha ||
            FinalJsonSHA(input.BaseRow) != baseRowSha || references.Length != 2 ||
            WorldPositionsSha(roundSeed) != roundSeedSha || WorldPositionsSha(references[0]) != roundSeedSha ||
            WorldPositionsSha(references[1]) != commonSha)
            throw new InvalidDataException("Reviewed Kshishtof Sandals panel input changed");

        var data = new EnvelopeTopology(seed, roundSeed, normals, references);
        int[] triangles = data.Triangles;
        int[] groups = data.MovementGroups();
        if (IntSha(triangles) != "5335c47757988cdf18d2d075cfea5877936083d1c880d93d08010622c178f1f0" || IntSha(groups) != "b0ff4e412149271236ca96ebd4ead5db9eebbb275bedc5393048b45dbf0023a6")
            throw new InvalidDataException("Reviewed Sandals panel topology changed");

        var volume = WardrobeBodyVolume.For(body);
        var placed = RefineEnvelope(roundSeed, triangles, volume, .006f, floor, 16, solverCap,
            out var originalRoundZero, groups);
        if (WorldPositionsSha(placed) != roundZeroSha)
            throw new InvalidDataException("Reviewed Kshishtof Sandals round zero shape changed");

        // The exact two disconnected panels stretched from sub-5 mm edges to
        // 180 mm in round zero. Recover their source shape before moving them
        // as rigid patches into independently measured exterior placement.
        var panels = new JArray
        {
            ApplyPanel("left", 17890, 266, 149, 162, new Vector3(-.012983327f, -.012983327f, .012983327f)),
            ApplyPanel("right", 18580, 264, 148, 161, new Vector3(.0145250615f, -.0072625307f, .0145250615f))
        };

        var residual = EnvelopeProbeResidualDispatchedDraft(placed, triangles, volume, floor);
        var roundDisplacement = RetargetProbeError(roundSeed, placed);
        var ownDisplacement = RetargetProbeError(references[0], placed);
        var commonDisplacement = RetargetProbeError(references[1], placed);
        if (!(bool)residual["passedSampledEnvelope"] || (float)roundDisplacement["maxMetres"] > solverCap ||
            (float)ownDisplacement["maxMetres"] > .12f || (float)commonDisplacement["maxMetres"] > .12f)
            throw new InvalidDataException("Reviewed Kshishtof Sandals placement failed strict geometry");
        proof = new JObject
        {
            ["reviewedCandidateSha256"] = candidateSha,
            ["nativeSourceSha256"] = nativeSha,
            ["originalRoundZero"] = originalRoundZero["after"].DeepClone(),
            ["panels"] = panels,
            ["placedEnvelope"] = residual,
            ["roundSeedDisplacement"] = roundDisplacement,
            ["ownDisplacement"] = ownDisplacement,
            ["commonDisplacement"] = commonDisplacement,
            ["topologyAndAttributesUnchanged"] = true
        };
        return placed;

        JObject ApplyPanel(string side, int first, int count, int expectedGroups, int expectedVertices, Vector3 shift)
        {
            var selected = triangles.Skip(first * 3).Take(count * 3).Select(vertex => groups[vertex]).ToHashSet();
            var vertices = Enumerable.Range(0, groups.Length).Where(vertex => selected.Contains(groups[vertex])).ToArray();
            if (selected.Count != expectedGroups || vertices.Length != expectedVertices)
                throw new InvalidDataException("Reviewed Sandals panel vertex membership changed: " + side);
            for (int face = 0; face < triangles.Length / 3; face++)
            {
                int at = face * 3;
                bool touched = selected.Contains(groups[triangles[at]]) || selected.Contains(groups[triangles[at + 1]]) ||
                    selected.Contains(groups[triangles[at + 2]]);
                if (touched != (face >= first && face < first + count))
                    throw new InvalidDataException("Reviewed Sandals panel is no longer disconnected: " + side);
            }
            foreach (int vertex in vertices) placed[vertex] = roundSeed[vertex] + shift;
            return new JObject
            {
                ["side"] = side, ["faces"] = count, ["movementGroups"] = expectedGroups, ["vertices"] = vertices.Length,
                ["worldTranslationMetres"] = Vec(shift), ["originalPanelShapePreserved"] = true
            };
        }
    }

    private static string WorldPositionsSha(Vector3[] positions)
    {
        var flat = positions.SelectMany(point => new[] { point.x, point.y, point.z }).ToArray();
        var bytes = new byte[flat.Length * sizeof(float)];
        Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);
        return FinalSHA(bytes);
    }

    private static string IntSha(int[] values)
    {
        var bytes = new byte[values.Length * sizeof(int)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return FinalSHA(bytes);
    }
}
}
#endif
