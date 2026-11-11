#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
// Â§169.10. Offline graphics PlayMode acceptance of the STAGED collection.
// Loading imported fixture assets does not opt the collection into the live
// catalog, contact the registry, generate content, or publish anything.
public sealed partial class LegacyWardrobeRuntimeTests
{
    const string Root = "Assets/HexLiveContent/People";
    const string Staged = Root + "/Wardrobe";
    const string NativeDefinitions = "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets";
    const string OrdinaryOutput = "Build/Diagnostics/bug-wave2-actions/legacy-runtime";
    static string Output => Discovery.Value.Curated ? OrdinaryOutput + "-release" : OrdinaryOutput;
    const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags HiddenInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    readonly List<(IDictionary cache, Hashtable previous)> _caches = new();
    readonly List<GameObject> _objects = new();
    readonly List<(FieldInfo field, object previous)> _fields = new();
    // Imported assets and parsed metadata are fixture inputs, never per-case globals.
    // Read-only views prevent a test from replacing entries in the shared input cache.
    IReadOnlyList<JToken> _live, _legacy, _combined;
    IReadOnlyDictionary<string, GarmentDefinition> _definitions;
    IReadOnlyDictionary<string, Wear> _prefabs;
    IReadOnlyDictionary<string, JToken> _prototypeRows;
    IReadOnlyDictionary<ActorName, GameObject> _actorPrefabs;
    IReadOnlyList<PaintPointMap> _paintMaps;
    IReadOnlyList<(string pose, AnimationClip clip, float time, bool laying)> _poses;
    string _liveJson, _releaseSelectionJson;
    readonly HashSet<string> _excludedReleaseIds = new(StringComparer.Ordinal);
    bool _async;

    static IReadOnlyList<JToken> ParseRecords(string json) => Array.AsReadOnly(
        ((JArray)JObject.Parse(json)["records"]).Where(r => (string)r["type"] == "wear").ToArray());
    sealed class DiscoveryInputs
    {
        public readonly bool Curated;
        public readonly string CatalogPath, Json;
        public readonly IReadOnlyList<JToken> Rows;
        public readonly IReadOnlyList<string> Keys;
        public DiscoveryInputs()
        {
            Curated = Environment.GetEnvironmentVariable("HEXLIVE_MODERN_RELEASE_VALIDATION") == "1";
            CatalogPath = Curated ? ReleaseCatalog : CompleteCatalog;
            Json = File.ReadAllText(CatalogPath);
            Rows = ParseRecords(Json);
            Keys = Array.AsReadOnly(Rows.Where(r => (bool?)r["metadata"]?["legacyWardrobe"] == true)
                .Select(r => (string)r["main"]).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)
                .SelectMany(path => new[] { path + "|Marta", path + "|Kshishtof" }).ToArray());
        }
    }
    // NUnit can request ValueSource during discovery, before OneTimeSetUp and off
    // the Unity main thread. Discovery performs file/JSON reads only, no AssetDatabase calls.
    static readonly Lazy<DiscoveryInputs> Discovery = new(() => new DiscoveryInputs());
    // Unity's coroutine parameterization supports ValueSource, not TestCaseSource.
    public static IEnumerable<string> GeometryCases => Discovery.Value.Keys;

    const string CompleteCatalog = Staged + "/catalog.json";
    const string FinalManifest = Staged + "/final-fit.json";
    const string ReleaseCatalog = Staged + "/release-catalog.json";
    const string ReleaseManifest = Staged + "/release-fit.json";
    const string ReleaseSelection = Staged + "/release-selection.json";
    const string BaseRunManifest = "Build/Diagnostics/bug-wave-20261110/wardrobe/prepared-all.json";
    const string FinalPrefabs = Staged + "/Prefabs/FinalFit/";
    const string FinalMeshes = Staged + "/Meshes/FinalFit/";
    const string FinalMaps = Staged + "/PaintMaps/FinalFit";
    bool _finalFit;
    string _geometryManifestPath, _geometryManifestJson, _baseCatalogPath, _baseCatalogJson;
    IReadOnlyDictionary<string, JToken> _geometryRows;

    void LoadGeometryReceipts()
    {
        var discovery = Discovery.Value;
        var catalog = JObject.Parse(discovery.Json);
        _finalFit = (bool?)catalog["finalFit"] == true || _legacy.Any(r => (bool?)r["metadata"]?["finalFit"] == true);
        if (discovery.Curated)
            Assert.That(_finalFit, Is.True, "Curated release acceptance requires final-fit geometry");
        var rows = new Dictionary<string, JToken>(StringComparer.Ordinal);
        if (_finalFit)
        {
            Assert.That((bool?)catalog["finalFit"], Is.True, "Never mix a base catalog with final-fit metadata");
            Assert.That(_legacy.All(r => (bool?)r["metadata"]?["finalFit"] == true), Is.True, "Every staged owner selects final-fit art");
            _geometryManifestPath = discovery.Curated ? ReleaseManifest : FinalManifest;
            Assert.That((string)catalog["finalFitManifest"], Is.EqualTo(_geometryManifestPath));
            _geometryManifestJson = File.ReadAllText(_geometryManifestPath);
            var receipt = JObject.Parse(_geometryManifestJson);
            Assert.That((int?)receipt["schemaVersion"], Is.EqualTo(1));
            Assert.That((string)receipt["stage"], Is.EqualTo("final-fit-prepared-only"));
            Assert.That((bool?)receipt["passed"], Is.True); Assert.That((bool?)receipt["catalogPromoted"], Is.True);
            int models = discovery.Curated ? _prototypeRows.Count : 183;
            int logicalIdsCount = discovery.Curated ? _legacy.Count : 660;
            int bodyFits = models * 2;
            foreach (var pair in new[] { ("models", models), ("logicalIds", logicalIdsCount), ("meshes", bodyFits), ("maps", bodyFits) })
                Assert.That((int?)receipt["counts"]?[pair.Item1], Is.EqualTo(pair.Item2), "Final receipt coverage " + pair.Item1);
            Assert.That(_prototypeRows.Count, Is.EqualTo(models));
            Assert.That(discovery.Keys.Count, Is.EqualTo(bodyFits));
            Assert.That(_legacy.Count, Is.EqualTo(logicalIdsCount));
            _baseCatalogPath = (string)receipt["baseGeneration"]?["stagedCatalogSnapshot"];
            Assert.That(_baseCatalogPath, Is.Not.Null.And.Not.Empty, "Base generation snapshot provenance");
            _baseCatalogJson = File.ReadAllText(_baseCatalogPath);
            var baseCatalog = JObject.Parse(_baseCatalogJson);
            Assert.That((bool?)baseCatalog["finalFit"] != true, Is.True, "Base snapshot must not silently become the final catalog");
            var baseRows = ParseRecords(_baseCatalogJson).ToDictionary(r => (string)r["id"], StringComparer.Ordinal);
            var manifestRows = receipt["rows"] as JObject;
            Assert.That(manifestRows, Is.Not.Null);
            Assert.That(manifestRows.Count, Is.EqualTo(models));
            if (discovery.Curated)
                AssertReleaseSelection(catalog, receipt, baseRows, manifestRows);
            Assert.That(baseRows.Keys.Where(id => !_excludedReleaseIds.Contains(id)),
                Is.EquivalentTo(_legacy.Select(r => (string)r["id"])));
            foreach (var property in manifestRows.Properties())
            {
                var entry = property.Value; var baseline = entry["base"]; var final = entry["final"];
                Assert.That(baseline, Is.Not.Null, property.Name + " distinct base provenance");
                Assert.That(final, Is.Not.Null, property.Name + " distinct final provenance");
                string path = (string)final["prefab"];
                Assert.That(path, Does.StartWith(FinalPrefabs));
                Assert.That((string)baseline["prepared"], Does.StartWith(Staged + "/Prefabs/").And.Not.StartWith(FinalPrefabs));
                Assert.That((string)baseline["source"], Is.Not.Null.And.Not.Empty);
                Assert.That((string)baseline["inputFP"], Is.Not.Null.And.Not.Empty);
                Assert.That((string)baseline["rowSHA"], Is.Not.Null.And.Not.Empty);
                Assert.That((string)baseline["donor"], Is.EqualTo("Marta").Or.EqualTo("Kshishtof"));
                AssertReductionQuality(baseline["quality"], property.Name + "/base reduction only");
                var logicalIds = final["logicalIds"]?.Values<string>().ToArray();
                Assert.That(logicalIds, Is.Not.Null.And.Not.Empty);
                Assert.That(logicalIds, Is.EquivalentTo(_legacy.Where(r => (string)r["main"] == path).Select(r => (string)r["id"])));
                foreach (string id in logicalIds)
                    Assert.That((string)baseRows[id]["main"], Is.EqualTo((string)baseline["prepared"]), id + " same base-generation owner");
                Assert.That(JToken.DeepEquals(SettingsWithoutMeshes(baseline["settings"]), SettingsWithoutMeshes(final["settings"])), Is.True,
                    property.Name + " final art must preserve reviewed layer/coverage/hiding/scale/height/heel/headwear settings");
                Assert.That((string)final["mode"], Is.Not.Null.And.Not.Empty);
                var proof = final["processorEvidence"];
                Assert.That((bool?)proof?["finalQualityPassed"], Is.True, property.Name + " final quality must not inherit base quality");
                string fitKind = (string)proof?["fitKind"];
                Assert.That(fitKind, Is.EqualTo(RigidHeadwearKind).Or.EqualTo("deformable-garment"), property.Name + " explicit physical acceptance policy");
                var fit = final["settings"]?["headwearFit"];
                bool headwear = final["settings"]?["slots"]?.Values<string>().Contains("Head") == true && HeadwearFitAny(fit);
                if (fitKind == RigidHeadwearKind) Assert.That(headwear, Is.True, property.Name + " semantic Head-slot authored fit required");
                foreach (string actor in new[] { "Marta", "Kshishtof" })
                {
                    var body = proof["bodies"]?[actor];
                    if (fitKind == RigidHeadwearKind) AssertRigidHeadwearReceipt(body, property.Name + "/" + actor);
                    else if((string)body?["geometryMode"]=="authored-donor-preserved")
                        AssertAuthoredDonorReceipt(body,final["meshes"]?[actor],baseline,actor,property.Name+"/"+actor);
                    else
                    {
                        var descriptor=final["meshes"]?[actor];
                        if((string)body?["geometryMode"]=="revalidated-unmodified-topology")
                            AssertRevalidatedGeometry(body,descriptor,proof["revalidationProvenance"],entry,property.Name,actor,property.Name+"/"+actor);
                        else AssertFinalEnvelope(body?["surfaceEnvelope"], descriptor, property.Name + "/" + actor, body?["topologyRefinement"]);
                        var postBind=body?["postBindSurfaceEnvelope"];Assert.That(postBind,Is.Not.Null,"Actual post-bind query proof");
                        AssertFinalSamples(postBind,descriptor,property.Name+"/"+actor+"/post-bind");
                        foreach(string field in new[]{"baseToFinalDisplacement","commonBoneReferenceDisplacement"})
                        {
                            Assert.That((string)body?[field]?["measurementScope"],Is.EqualTo("all-output-vertices"));
                            Assert.That((string)body?[field]?["positionScope"],Is.EqualTo("actual-post-bind-world-vertices"));
                            Assert.That((int?)body?[field]?["measuredVertices"],Is.EqualTo((int?)descriptor["vertices"]));
                            Assert.That((float?)body?[field]?["maxMetres"],Is.Not.Null.And.InRange(0f,.12f));
                        }
                    }
                    Assert.That((string)body?["runtimeConstruction"],Is.EqualTo("BodyBones.Equip"));
                    Assert.That((float?)body?["reconstruction"]?["maxMetres"], Is.Not.Null.And.InRange(0f, .0002f),
                        property.Name + "/" + actor + " final binding reconstruction in metres");
                }
                if (headwear)
                {
                    Assert.That((bool?)proof?["headwearAuthoringUnits"]?["passed"], Is.True, property.Name + " corrected native authoring units");
                    AssertReductionQuality(proof["correctedHeadwearReduction"], property.Name + "/corrected headwear reduction; old tiny proof forbidden", true);
                }
                rows.Add(path, entry);
            }
            Assert.That(rows.Keys, Is.EquivalentTo(_prototypeRows.Keys), "No final model omitted or replaced by a base prefab");
            var maps = ((JArray)catalog["records"]).Where(r => (string)r["type"] == "config").ToArray();
            Assert.That(maps.Length, Is.EqualTo(bodyFits));
            foreach (var map in maps) Assert.That((string)map["main"], Does.StartWith(FinalMaps + "/"));
            if (discovery.Curated)
            {
                var selectedMaps = manifestRows.Properties().SelectMany(row => (JArray)row.Value["final"]["maps"])
                    .ToDictionary(row => (string)row["path"], StringComparer.Ordinal);
                Assert.That(selectedMaps.Count, Is.EqualTo(bodyFits));
                Assert.That(maps.Select(row => (string)row["main"]), Is.EquivalentTo(selectedMaps.Keys),
                    "Only the two paint maps of each selected release model may enter the test cache.");
                foreach (var map in selectedMaps)
                {
                    AssertReceiptHash(map.Key, (string)map.Value["sha"]);
                    AssertReceiptHash(map.Key + ".meta", (string)map.Value["metaSha"]);
                }
            }
        }
        else if (File.Exists(BaseRunManifest))
        {
            // Historical prepared-only fixtures remain supported. If a full base receipt
            // is present, verify it rather than silently accepting mismatched geometry.
            _geometryManifestPath = BaseRunManifest; _geometryManifestJson = File.ReadAllText(BaseRunManifest);
            var receipt = JObject.Parse(_geometryManifestJson);
            Assert.That((bool?)receipt["activeCatalogUnchanged"], Is.True);
            foreach (var row in receipt["prototypes"])
                rows.Add((string)row["prepared"], row);
            Assert.That(rows.Keys, Is.EquivalentTo(_prototypeRows.Keys));
            if (rows.Count == 183)
            { Assert.That(_prototypeRows.Count, Is.EqualTo(183)); Assert.That(Discovery.Value.Keys.Count, Is.EqualTo(366)); }
        }
        _geometryRows = new ReadOnlyDictionary<string, JToken>(rows);
    }

    void AssertReleaseSelection(JObject catalog, JObject receipt, IReadOnlyDictionary<string, JToken> baseRows,
        JObject manifestRows)
    {
        Assert.That((string)catalog["stage"], Is.EqualTo("prepared-only"));
        Assert.That((string)catalog["contentOrigin"], Is.EqualTo("modern"));
        Assert.That((bool?)catalog["gameReady"], Is.False);
        Assert.That((bool?)catalog["bundleBuildAuthorized"], Is.False);
        Assert.That((string)receipt["finalCatalog"]?["path"], Is.EqualTo(ReleaseCatalog));
        AssertReceiptHash(ReleaseCatalog, (string)receipt["finalCatalog"]?["sha"]);
        string selectionSha = (string)receipt["releaseSelectionSHA256"];
        Assert.That((string)catalog["releaseSelectionSHA256"], Is.EqualTo(selectionSha));
        AssertReceiptHash(ReleaseSelection, selectionSha);
        _releaseSelectionJson = File.ReadAllText(ReleaseSelection);

        var selection = JObject.Parse(_releaseSelectionJson);
        Assert.That((int?)selection["schemaVersion"], Is.EqualTo(1));
        var policy = ((JArray)selection["excludedModels"]).ToDictionary(row => (string)row["id"], StringComparer.Ordinal);
        var exclusions = ((JArray)receipt["excludedModels"]).ToDictionary(row => (string)row["id"], StringComparer.Ordinal);
        Assert.That(exclusions.Keys, Is.EquivalentTo(policy.Keys));
        var generation = receipt["baseGeneration"];
        AssertReceiptHash(_baseCatalogPath, (string)generation["stagedCatalogSHA"]);
        string sourcePath = (string)generation["fullReportPath"];
        AssertReceiptHash(sourcePath, (string)generation["fullReportSHA"]);
        var full = JObject.Parse(File.ReadAllText(sourcePath));
        var sourceRows = ((JArray)full["prototypes"]).ToDictionary(row => (string)row["id"], StringComparer.Ordinal);
        Assert.That(sourceRows.Count, Is.EqualTo(183), "The source-generation inventory stays complete.");
        Assert.That(baseRows.Count, Is.EqualTo(660));
        foreach (var entry in exclusions)
        {
            Assert.That(sourceRows.TryGetValue(entry.Key, out var source), Is.True, entry.Key);
            var excluded = (JObject)entry.Value.DeepClone();
            var logicalIds = (JArray)excluded["logicalIds"];
            Assert.That(logicalIds, Is.Not.Null.And.Not.Empty, entry.Key);
            excluded.Remove("logicalIds");
            Assert.That(JToken.DeepEquals(excluded, policy[entry.Key]), Is.True, entry.Key + " exclusion policy");
            Assert.That((string)excluded["source"], Is.EqualTo((string)source["source"]), entry.Key);
            Assert.That((string)excluded["reason"], Is.Not.Null.And.Not.Empty, entry.Key);
            var originalVariants = baseRows.Values.Where(row => (string)row["main"] == (string)source["prepared"])
                .Select(row => (string)row["id"]);
            Assert.That(logicalIds.Values<string>(), Is.EquivalentTo(originalVariants),
                entry.Key + " omission must cover every original logical variant");
            foreach (string id in logicalIds.Values<string>())
                Assert.That(_excludedReleaseIds.Add(id), Is.True, id + " belongs to exactly one excluded model");
        }
        Assert.That(manifestRows.Properties().Select(row => row.Name),
            Is.EquivalentTo(sourceRows.Keys.Where(id => !exclusions.ContainsKey(id))),
            "Only policy-selected original models may disappear from the release.");

        var records = ((JArray)catalog["records"]).ToArray();
        Assert.That(records.All(row => ((string)row["type"] == "wear" || (string)row["type"] == "config") &&
            (string)row["metadata"]?["contentOrigin"] == "modern" &&
            (bool?)row["metadata"]?["legacyWardrobe"] == true && (bool?)row["metadata"]?["finalFit"] == true), Is.True);
        Assert.That(records.Select(row => (string)row["type"] + "/" + (string)row["id"]).Distinct().Count(),
            Is.EqualTo(records.Length), "No duplicate selected owner.");
        Assert.That(_combined.Select(row => (string)row["id"]).Any(_excludedReleaseIds.Contains), Is.False,
            "An excluded logical ID must not leak into either fixture input.");
    }

    static void AssertRevalidatedGeometry(JToken body,JToken descriptor,JToken provenance,JToken entry,string id,string actor,string label)
    {
        Assert.That((string)provenance?["mode"],Is.EqualTo("revalidated-unmodified-topology"),label);
        Assert.That((bool?)provenance?["originalInputReconstructedExactly"],Is.True,label);
        Assert.That((bool?)provenance?["originalOutputArtifactsVerified"],Is.True,label);
        Assert.That(((string)provenance?["oldRowSHA"])?.Length,Is.EqualTo(64),label);
        using(var digest=System.Security.Cryptography.SHA256.Create())
        {
            string rowSha=BitConverter.ToString(digest.ComputeHash(System.Text.Encoding.UTF8.GetBytes(provenance["originalRow"].ToString(Newtonsoft.Json.Formatting.None)))).Replace("-","").ToLowerInvariant();
            Assert.That(rowSha,Is.EqualTo((string)provenance["oldRowSHA"]),label);
        }
        Assert.That((string)provenance["originalRow"]["final"]["inputHash"],Is.EqualTo((string)provenance["priorOriginalInputHash"]),label);
        Assert.That(JToken.DeepEquals(body["historicalSolverEvidence"],provenance["originalRow"]["final"]["processorEvidence"]["bodies"][actor]),Is.True,label);
        Assert.That(provenance?["producerSources"],Is.TypeOf<JArray>(),label);
        Assert.That(((JArray)provenance["producerSources"]).Count,Is.GreaterThan(0),label);
        Assert.That(JToken.DeepEquals(provenance["originalInput"]?["sources"],provenance["producerSources"]),Is.True,label);
        using(var hash=System.Security.Cryptography.SHA256.Create())
        {
            string actual=BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(provenance["originalInput"].ToString(Newtonsoft.Json.Formatting.None)))).Replace("-","").ToLowerInvariant();
            Assert.That(actual,Is.EqualTo((string)provenance["priorOriginalInputHash"]),label+" exact full original input");
        }
        Assert.That((string)provenance["originalInput"]["id"],Is.EqualTo(id),label);
        Assert.That((string)provenance["originalInput"]["baseRowSHA"],Is.EqualTo((string)entry["base"]["rowSHA"]),label);
        Assert.That((string)provenance["originalInput"]["baseFP"],Is.EqualTo((string)entry["base"]["inputFP"]),label);
        Assert.That(JToken.DeepEquals(provenance["originalInput"]["settings"],entry["base"]["settings"]),Is.True,label);
        var proof=body["currentGeometryRevalidation"];
        Assert.That((string)proof?["priorOriginalInputHash"],Is.EqualTo((string)provenance["priorOriginalInputHash"]),label);
        Assert.That((bool?)proof?["passed"],Is.True,label);Assert.That((bool?)proof?["unchangedAttributesExact"],Is.True,label);
        Assert.That((bool?)proof?["originalOutputAttributesPinned"],Is.True,label);
        Assert.That((int?)proof?["vertices"],Is.EqualTo((int?)descriptor["vertices"]),label);
        Assert.That((long?)proof?["triangles"],Is.EqualTo((long?)descriptor["triangles"]),label);
        Assert.That(body["surfaceEnvelope"],Is.Null,label+" no invented current solve");Assert.That(body["topologyRefinement"],Is.Null,label);
        Assert.That((string)body["historicalSolverEvidence"]?["seed"],Is.EqualTo((string)body["seed"]),label);
    }

    static bool HeadwearFitAny(JToken fit)
    {
        Assert.That(fit, Is.TypeOf<JObject>());
        foreach (string field in new[] { "position", "rotation", "scale" })
        {
            Assert.That(fit[field], Is.TypeOf<JObject>(), field);
            Assert.That(((JObject)fit[field]).Properties().Select(p => p.Name),
                Is.EquivalentTo(new[] { "x", "y", "z" }), field);
        }
        return JsonUtility.FromJson<HeadwearFit>(fit.ToString(Newtonsoft.Json.Formatting.None)).Any;
    }

    static JToken SettingsWithoutMeshes(JToken settings)
    {
        Assert.That(settings, Is.Not.Null);
        var clone = settings.DeepClone();
        foreach (var config in clone["configs"]) ((JObject)config).Remove("mesh");
        return clone;
    }

    const string RigidHeadwearKind = "authored-rigid-headwear";
    const float RigidFrameToleranceMetres = .0002f;

    static void AssertRigidHeadwearReceipt(JToken body, string label)
    {
        var proof = body?["rigidHeadwearProof"];
        Assert.That(proof, Is.Not.Null, label);
        Assert.That((bool?)proof["passed"], Is.True, label);
        Assert.That((int?)proof["headBoneIndex"], Is.Not.Null.And.GreaterThanOrEqualTo(0), label);
        Assert.That((string)proof["headBoneName"], Is.EqualTo("head"), label);
        Assert.That((bool?)proof["rigidBoneOnly"], Is.True, label);
        Assert.That((bool?)proof["shapeDeformationAllowed"], Is.False, label);
        Assert.That((float?)proof["maxAllowedDeformationMetres"], Is.EqualTo(RigidFrameToleranceMetres), label);
        Assert.That((float?)proof["deformationMaxMetres"], Is.Not.Null.And.InRange(0f, RigidFrameToleranceMetres), label);
        Assert.That(body["surfaceEnvelope"], Is.Null, label + " rigid body intersections must not masquerade as a passed cloth envelope");
        var diagnostic = body["bodyContactDiagnostic"]; Assert.That(diagnostic, Is.Not.Null, label);
        Assert.That((bool?)diagnostic["acceptance"], Is.False, label);
        Assert.That((string)diagnostic["scope"], Is.Not.Null.And.Not.Empty, label + " diagnostic scope is descriptive prose; acceptance:false and actual counts are the contract");
        Assert.That((int?)diagnostic["sampleCount"], Is.Not.Null.And.InRange(1, 4096), label);
        Assert.That((int?)diagnostic["insideSamples"], Is.Not.Null.And.InRange(0, (int)diagnostic["sampleCount"]), label);
        Assert.That((int?)diagnostic["ambiguousSamples"], Is.Not.Null.And.InRange(0, (int)diagnostic["sampleCount"]), label);
        float? minimum = (float?)diagnostic["minimumSignedDistanceMetres"];
        Assert.That(minimum, Is.Not.Null, label + " preserve actual diagnostic distance, including negative authored intersections");
        Assert.That(float.IsFinite(minimum.Value), Is.True, label);
    }

    static bool AllInfluencesOnNamedHead(Mesh mesh, Transform[] bones, out int headIndex)
    {
        headIndex = Array.FindIndex(bones, b => b != null && b.name == "head");
        if (headIndex < 0) return false;
        // These Mesh APIs return borrowed native views. Copy their data; do not
        // Dispose a view whose storage belongs to the imported Mesh.
        var counts = mesh.GetBonesPerVertex().ToArray();
        var weights = mesh.GetAllBoneWeights().ToArray();
        if (counts.Length != mesh.vertexCount || counts.Length == 0) return false;
        int at = 0;
        for (int vertex = 0; vertex < counts.Length; vertex++)
        {
            float sum = 0;
            if (counts[vertex] == 0) return false;
            for (int i = 0; i < counts[vertex]; i++)
            {
                if (at >= weights.Length) return false;
                var influence = weights[at++];
                if (!float.IsFinite(influence.weight) || influence.weight < 0) return false;
                if (influence.weight == 0) continue;
                if (influence.boneIndex != headIndex) return false;
                sum += influence.weight;
            }
            if (Mathf.Abs(sum - 1) > .000001f) return false;
        }
        return at == weights.Length;
    }

    static void AssertAdaptiveTopology(JToken surface, JToken topology, JToken descriptor, string label)
    {
        Assert.That(topology, Is.Not.Null, label + " bounded adaptive topology provenance");
        Assert.That((bool?)topology["passed"], Is.True, label);
        Assert.That((bool?)topology["sourceUnchanged"], Is.True, label);
        Assert.That((bool?)topology["allSourceSubmeshesPreserved"], Is.True, label);
        Assert.That((bool?)topology["allUVChannelsPreserved"], Is.True, label);
        Assert.That((int?)topology["inputTriangles"], Is.Not.Null.And.GreaterThan(0), label);
        Assert.That((int?)topology["inputVertices"], Is.Not.Null.And.GreaterThan(0), label);
        int inputTriangles = (int)topology["inputTriangles"], inputVertices = (int)topology["inputVertices"];
        int outputTriangles = (int)descriptor["triangles"], outputVertices = (int)descriptor["vertices"];
        Assert.That((int?)topology["outputTriangles"], Is.EqualTo(outputTriangles), label);
        Assert.That((int?)topology["outputVertices"], Is.EqualTo(outputVertices), label);
        Assert.That(outputTriangles, Is.GreaterThanOrEqualTo(inputTriangles), label);
        Assert.That(outputVertices, Is.GreaterThanOrEqualTo(inputVertices), label);
        Assert.That((int?)topology["addedVertices"], Is.EqualTo(outputVertices - inputVertices), label);
        int budget = Math.Max((int)Math.Ceiling(inputTriangles * .10), 2048);
        Assert.That((int?)topology["additionalTriangleBudget"], Is.EqualTo(budget), label);
        Assert.That((string)topology["additionalTriangleBudgetPolicy"], Is.EqualTo("MAX(ceil(inputTriangles*0.10),2048)"), label);
        Assert.That(outputTriangles - inputTriangles, Is.LessThanOrEqualTo(budget), label);
        Assert.That((int?)topology["maximumRefinementRounds"], Is.EqualTo(3), label);
        Assert.That((int?)topology["refinementRounds"], Is.Not.Null.And.InRange(0, 3), label);
        bool unchanged = outputTriangles == inputTriangles && outputVertices == inputVertices;
        Assert.That((bool?)topology["topologyUnchanged"], Is.EqualTo(unchanged), label);
        Assert.That((bool?)surface["topologyUnchanged"], Is.EqualTo(unchanged), label);
        Assert.That((bool?)surface["solverTopologyUnchangedWithinRound"], Is.True, label);
        Assert.That((int?)surface["outputTriangles"], Is.EqualTo(outputTriangles), label);
        Assert.That((int?)surface["outputVertices"], Is.EqualTo(outputVertices), label);
        Assert.That(JToken.DeepEquals(surface, topology["surfaceEnvelope"]), Is.True, label + " final topology and physical proof must describe the SAME final mesh");
        if(topology["sourceCapAnnulus"]!=null)Assert.That(topology["sourceCapAnnulus"],Is.TypeOf<JObject>(),label);
        if(topology["sourceCapAnnulus"] is JObject sourceCap)
        {
            Assert.That((bool?)sourceCap["applicable"],Is.True,label);Assert.That((bool?)sourceCap["automaticSemanticDetection"],Is.False,label);
            Assert.That((bool?)sourceCap["acceptanceOverride"],Is.False,label);Assert.That((bool?)sourceCap["discardedMarginMayHaveBeenVisible"],Is.True,label);
            Assert.That((bool?)sourceCap["requiresActualPostBindAllSampleAndReferenceAndVisualAcceptance"],Is.True,label);
            string sourceEvidence=Staged+"/cap-attachments.json";
            Assert.That((string)sourceCap["sourceEvidenceManifest"],Is.EqualTo(sourceEvidence),label);
            string Digest(byte[] bytes){using var sha=System.Security.Cryptography.SHA256.Create();return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
            Assert.That(Digest(File.ReadAllBytes(sourceEvidence)),Is.EqualTo((string)sourceCap["sourceEvidenceManifestSha"]),label);
            var manifest=JObject.Parse(File.ReadAllText(sourceEvidence));
            Assert.That((string)manifest["stage"],Is.EqualTo("source-evidenced-attachment-fans"),label);
            var record=manifest["records"].Single(r=>(string)r["nativeSource"]==(string)sourceCap["sourceNative"]);
            Assert.That(Digest(System.Text.Encoding.UTF8.GetBytes(record.ToString(Newtonsoft.Json.Formatting.None))),Is.EqualTo((string)sourceCap["sourceRecordSha"]),label);
            if(sourceCap["recipientTransfer"]!=null)
            {
                Assert.That(sourceCap["recipientTransfer"],Is.TypeOf<JObject>(),label);
                var transfer=sourceCap["recipientTransfer"];
                string targetActor=label.Substring(label.LastIndexOf('/')+1),sourceActor=(string)record["actor"];
                Assert.That((string)transfer["sourceActor"],Is.EqualTo(sourceActor),label);
                Assert.That((string)transfer["targetActor"],Is.EqualTo(targetActor),label);
                Assert.That(targetActor,Is.Not.EqualTo(sourceActor),label);
                Assert.That((bool?)transfer["targetClipWasRecomputed"],Is.False,label);
                Assert.That((string)transfer["sourceClipScope"],Is.EqualTo("SOURCE-evidenced donor geometry before target bone retarget and strict repair"),label);
                string own=(string)transfer["ownReferencePolicy"];
                Assert.That(own,Is.EqualTo("actual cut-retarget target seed before underlayers").Or.EqualTo("actual cut-retarget-clearance target seed before underlayers"),label);
                if(own=="actual cut-retarget-clearance target seed before underlayers")
                {
                    Assert.That((string)transfer["seedOperator"],Is.EqualTo("CreateSourceClearanceSeed on SOURCE-evidenced clipped topology"),label);
                    Assert.That(transfer["targetClearanceSeed"],Is.TypeOf<JObject>(),label);Assert.That((bool?)transfer["targetClearanceSeed"]["passed"],Is.True,label);
                }
                else Assert.That(transfer["targetClearanceSeed"],Is.Null,label);
                Assert.That((string)transfer["commonReferencePolicy"],Is.EqualTo("source lineage interpolated ORIGINAL UNCUT fresh-bone target reference"),label);
            }
            Assert.That((string)sourceCap["sourceNativeSha"],Is.EqualTo((string)record["nativeSha"]),label);
            Assert.That((string)sourceCap["sourceTopologySha"],Is.EqualTo((string)record["sourceTopologySha256"]),label);
            Assert.That((int?)sourceCap["clippedTriangles"],Is.EqualTo(inputTriangles),label);Assert.That((int?)sourceCap["clippedVertices"],Is.EqualTo(inputVertices),label);
            int original=(int)sourceCap["originalSourceTriangles"],sharedBudget=Math.Max((int)Math.Ceiling(original*.10),2048);
            Assert.That((int?)sourceCap["canonicalOriginalAdditionalTriangleBudget"],Is.EqualTo(sharedBudget),label);
            Assert.That((int?)topology["aggregateSourceAdditionalTriangleBudget"],Is.EqualTo(sharedBudget),label);
            Assert.That((int?)topology["aggregateSourceAdditionalTriangles"],Is.EqualTo(outputTriangles-original),label);
            Assert.That(outputTriangles-original,Is.LessThanOrEqualTo(sharedBudget),label+" clipping and adaptive refinement share one original budget");
            var capGeometry=sourceCap["geometry"];Assert.That((bool?)capGeometry["retainedFanVerticesAnd13SamplesPassed"],Is.True,label);
            Assert.That((bool?)capGeometry["outerRingOriginalPositionsUnchanged"],Is.True,label);Assert.That((bool?)capGeometry["otherOriginalFacesUnchanged"],Is.True,label);
            Assert.That((int?)capGeometry["originalRetainedPositionsChanged"],Is.Zero,label);
            Assert.That((bool?)sourceCap["attributes"]?["retainedFullSkinFloatBitsExact"],Is.True,label);
            Assert.That((bool?)sourceCap["attributes"]?["channelFormatsAndDimensionsExact"],Is.True,label);
            Assert.That((float?)sourceCap["attributes"]?["maximumDroppedWeightFraction"],Is.Not.Null.And.InRange(0f,.05f),label);
        }
        if(topology["collapsedZeroIslandCleanup"] is JObject cleanup)
        {
            Assert.That((bool?)cleanup["passed"],Is.True,label);Assert.That((bool?)cleanup["sourceUnchanged"],Is.True,label);
            Assert.That((bool?)cleanup["keptNonSkinAttributeBytesVerifiedAfterSkinWrite"],Is.True,label);
            Assert.That((bool?)cleanup["fullSkinInfluencesVerifiedAfterWrite"],Is.True,label);
            Assert.That((bool?)cleanup["allCompanionReferencesRemapped"],Is.True,label);
            Assert.That((bool?)cleanup["submeshSlotsAndRetainedTriangleOrderPreserved"],Is.True,label);
            Assert.That((int?)cleanup["removedVertices"],Is.GreaterThan(0),label);
            Assert.That((int?)topology["originalSourceVertices"]-(int?)cleanup["removedVertices"],Is.EqualTo(inputVertices),label);
            Assert.That((int?)topology["originalSourceTriangles"]-(int?)cleanup["removedTriangles"],Is.EqualTo(inputTriangles),label);
            var native=cleanup["nativeActualEvidence"];Assert.That((bool?)native?["passed"],Is.True,label);
            Assert.That((bool?)native?["actualBakeMesh"],Is.True,label);Assert.That((int?)native?["actualBakedNondegenerateFaces"],Is.EqualTo(0),label);
            Assert.That((int?)native?["mixedFaces"],Is.EqualTo(0),label);Assert.That((bool?)native?["allUnweightedBakedLocalVerticesExactlyZero"],Is.True,label);
            Assert.That((string)native?["equipmentFactory"],Is.EqualTo("BodyBones.Equip"),label);
        }
        var attributes = topology["attributes"]; Assert.That(attributes, Is.Not.Null, label);
        Assert.That((bool?)attributes["bindposesPreserved"], Is.True, label);
        bool? normalizedSkinWeights = (bool?)attributes["normalizedTop4BoneWeights"];
        Assert.That(normalizedSkinWeights, Is.Not.Null, label);
        if (normalizedSkinWeights == false)
        {
            // Rigid accessories attached to one transform can have no skin channel.
            Assert.That(unchanged, Is.True, label);
            Assert.That((int?)topology["splitEdges"], Is.Zero, label);
            Assert.That((float?)attributes["maximumDroppedWeightFraction"], Is.Zero, label);
            var imported = AssetDatabase.LoadAssetAtPath<Mesh>((string)descriptor["path"]);
            Assert.That(imported, Is.Not.Null, label);
            Assert.That(imported.HasVertexAttribute(VertexAttribute.BlendWeight), Is.False, label);
        }
        Assert.That((bool?)attributes["boneWeightDropPassed"], Is.True, label);
        Assert.That((float?)attributes["maximumAllowedDroppedWeightFraction"], Is.EqualTo(.05f), label);
        Assert.That((float?)attributes["maximumDroppedWeightFraction"], Is.Not.Null.And.InRange(0f, .05f), label);
    }

    void AssertActualFitKind(JToken entry, ActorName actor, Wear finalWear, Mesh finalMesh, string label)
    {
        var baseline = entry["base"]; var proof = entry["final"]["processorEvidence"];
        string kind = (string)proof["fitKind"];
        bool headwear = finalWear.Slots.Contains(VisualWearSlot.Head) && HeadwearFitAny(entry["final"]["settings"]["headwearFit"]);
        bool nativeRigid = false;
        if (headwear)
        {
            var native = AssetDatabase.LoadAssetAtPath<GameObject>((string)baseline["source"]).GetComponent<Wear>();
            var skin = native.GetComponentInChildren<SkinnedMeshRenderer>(true); var mesh = skin.sharedMesh;
            var configs = new SerializedObject(native).FindProperty("configs");
            for (int i = 0; i < configs.arraySize; i++)
            {
                var config = configs.GetArrayElementAtIndex(i);
                if (config.FindPropertyRelative("actorName").intValue != (int)actor) continue;
                mesh = config.FindPropertyRelative("mesh").objectReferenceValue as Mesh ?? mesh; break;
            }
            nativeRigid = AllInfluencesOnNamedHead(mesh, skin.bones, out _);
        }
        Assert.That(kind, Is.EqualTo(headwear && nativeRigid ? RigidHeadwearKind : "deformable-garment"),
            label + " acceptance kind must follow ACTUAL native influences, never a model-ID override");
        if (kind != RigidHeadwearKind) return;
        var finalSkin = finalWear.GetComponentInChildren<SkinnedMeshRenderer>(true);
        Assert.That(AllInfluencesOnNamedHead(finalMesh, finalSkin.bones, out int finalHead), Is.True,
            label + " every final nonzero influence must remain on the named head and sum to one");
        Assert.That((int?)proof["bodies"][actor.ToString()]["rigidHeadwearProof"]["headBoneIndex"], Is.EqualTo(finalHead), label);
    }

    sealed class RigidHeadFrameOracle
    {
        readonly Vector3[] _reference;
        readonly int[] _triangles;
        public RigidHeadFrameOracle(Vector3[] world, Transform head, Transform actor, int[] triangles)
        { _reference = InHeadMetres(world, head, actor); _triangles = (int[])triangles.Clone(); }
        static Vector3[] InHeadMetres(Vector3[] world, Transform head, Transform actor)
        {
            Assert.That(head, Is.Not.Null);
            var scale = actor.lossyScale;
            Assert.That(scale.x, Is.GreaterThan(0));
            Assert.That(scale.y, Is.EqualTo(scale.x).Within(.000001f), "Fixture requires uniform actor-root scale");
            Assert.That(scale.z, Is.EqualTo(scale.x).Within(.000001f), "Fixture requires uniform actor-root scale");
            var rotation = Quaternion.Inverse(head.rotation);
            // InverseTransformPoint would return centimetres on an imported .01 head.
            // Rotation-only head frame / actor-root scale gives ACTOR METRES, and
            // does not hide an animated nonuniform head scale deforming the metal.
            return world.Select(p => rotation * (p - head.position) / scale.x).ToArray();
        }
        public JObject AssertMatches(Vector3[] world, Transform head, Transform actor, string label)
        {
            var actual = InHeadMetres(world, head, actor);
            Assert.That(actual.Length, Is.EqualTo(_reference.Length), label);
            float vertexError = 0, edgeError = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                Assert.That(float.IsFinite(actual[i].x) && float.IsFinite(actual[i].y) && float.IsFinite(actual[i].z), Is.True, label);
                vertexError = Mathf.Max(vertexError, Vector3.Distance(actual[i], _reference[i]));
            }
            for (int i = 0; i < _triangles.Length; i += 3)
            for (int edge = 0; edge < 3; edge++)
            {
                int a = _triangles[i + edge], b = _triangles[i + (edge + 1) % 3];
                edgeError = Mathf.Max(edgeError, Mathf.Abs(Vector3.Distance(actual[a], actual[b]) - Vector3.Distance(_reference[a], _reference[b])));
            }
            Assert.That(vertexError, Is.LessThanOrEqualTo(RigidFrameToleranceMetres), label + " actual head-frame vertex drift in metres");
            Assert.That(edgeError, Is.LessThanOrEqualTo(RigidFrameToleranceMetres), label + " actual rigid edge-length drift in metres");
            return new JObject { ["passed"] = true, ["vertices"] = actual.Length, ["triangleEdges"] = _triangles.Length,
                ["headFrameVertexMaxMetres"] = vertexError, ["edgeLengthMaxMetres"] = edgeError,
                ["limitMetres"] = RigidFrameToleranceMetres, ["headFrameUsesFbxCentimetres"] = false };
        }
    }

    static void AssertFinalEnvelope(JToken proof, JToken descriptor, string label, JToken topology)
    {
        Assert.That(proof, Is.Not.Null, label + " final envelope provenance");
        Assert.That(descriptor, Is.Not.Null, label + " final actor mesh descriptor");
        Assert.That((bool?)proof["passed"], Is.True, label);
        Assert.That((float?)proof["requestedClearanceMetres"], Is.EqualTo(.006f), label + " requested solve clearance");
        Assert.That((float?)proof["minimumAcceptedClearanceMetres"], Is.EqualTo(.002f), label + " physical acceptance floor");
        Assert.That((float?)proof["targetClearanceMetres"], Is.EqualTo(.002f), label);
        Assert.That((int?)proof["constraintSamplesPerTriangle"], Is.EqualTo(13), label);
        AssertAdaptiveTopology(proof, topology, descriptor, label);
        AssertFinalSamples(proof["after"],descriptor,label);
    }

    static void AssertFinalSamples(JToken actual,JToken descriptor,string label)
    {
        Assert.That(actual, Is.Not.Null, label + " independent final queries");
        Assert.That((bool?)actual["allVerticesAnd13BarycentricSamplesPerTriangle"], Is.True, label);
        Assert.That((long?)descriptor["vertices"], Is.Not.Null.And.GreaterThan(0), label);
        Assert.That((long?)descriptor["triangles"], Is.Not.Null.And.GreaterThan(0), label);
        long expectedSamples = (long)descriptor["vertices"] + 13L * (long)descriptor["triangles"];
        Assert.That((long?)actual["sampleCount"], Is.EqualTo(expectedSamples), label + " every vertex plus all 13 samples of EVERY triangle");
        Assert.That((float?)actual["targetClearanceMetres"], Is.EqualTo(.002f), label);
        Assert.That((float?)actual["toleranceMetres"], Is.EqualTo(.0005f), label + " diagnostic tolerance must not relax the physical floor");
        Assert.That((float?)actual["minimumSignedDistanceMetres"], Is.Not.Null.And.GreaterThanOrEqualTo(.002f), label + " ACTUAL minimum, without subtracting tolerance");
        Assert.That((long?)actual["samplesBelowMinimum"], Is.EqualTo(0), label + " no point below the physical 2mm floor");
        Assert.That((long?)actual["samplesBelowTargetMinusTolerance"], Is.EqualTo(0), label);
        Assert.That((long?)actual["ambiguousSamples"], Is.EqualTo(0), label);
        Assert.That((long?)actual["onSurfaceSamples"], Is.EqualTo(0), label);
        Assert.That((long?)actual["insideSamples"], Is.EqualTo(0), label + " no parity-inside query");
        Assert.That((long?)actual["samplesBelowMinus1mm"], Is.EqualTo(0), label);
        Assert.That((long?)actual["samplesBelowMinus3mm"], Is.EqualTo(0), label);
        Assert.That((float?)actual["maximumPenetrationDepthMetres"], Is.EqualTo(0f), label + " no inside sample, including submillimetre penetration");
        Assert.That((int?)actual["minimumValidRays"], Is.Not.Null.And.GreaterThanOrEqualTo(2), label);
        Assert.That((bool?)actual["passedSampledEnvelope"], Is.True, label);
    }

    static void AssertReductionQuality(JToken proof, string label, bool allowValidatedBaseCandidate = false)
    {
        Assert.That(proof, Is.Not.Null, label);
        string mode = (string)proof["mode"];
        if (mode == "validated-base-candidate")
        {
            Assert.That(allowValidatedBaseCandidate, Is.True, label + " only a NEW corrected headwear measurement may reuse a base candidate");
            Assert.That((bool?)proof["passed"], Is.True, label);
            Assert.That((long?)proof["inputTriangles"], Is.Not.Null.And.GreaterThan(0), label);
            Assert.That((long?)proof["actualTriangles"], Is.Not.Null.And.GreaterThan(0), label);
            Assert.That((float?)proof["maxErrorLimitMetres"], Is.EqualTo(.003f), label + " new world-unit max error limit");
            Assert.That((float?)proof["p95ErrorLimitMetres"], Is.EqualTo(.0005f), label + " new world-unit p95 error limit");
            foreach (string direction in new[] { "sourceToCandidate", "candidateToSource" })
            {
                Assert.That((int?)proof[direction]?["sampleCount"], Is.Not.Null.And.GreaterThan(0), label + "/" + direction + " freshly sampled");
                Assert.That((float?)proof[direction]?["max"], Is.Not.Null.And.InRange(0f, .003f), label + "/" + direction);
                Assert.That((float?)proof[direction]?["p95"], Is.Not.Null.And.InRange(0f, .0005f), label + "/" + direction);
            }
            return;
        }
        Assert.That(mode, Is.EqualTo("adaptive").Or.EqualTo("unchanged").Or.EqualTo("source-preserved"), label + " explicit reduction mode");
        Assert.That((long?)proof["inputTriangles"], Is.Not.Null.And.GreaterThan(0), label);
        if (mode == "adaptive")
        {
            var last = (proof["attempts"] as JArray)?.LastOrDefault();
            Assert.That((bool?)last?["passed"], Is.True, label);
            foreach (string direction in new[] { "sourceToCandidate", "candidateToSource" })
            {
                Assert.That((float?)last?[direction]?["max"], Is.Not.Null.And.LessThanOrEqualTo(.003f), label + "/" + direction);
                Assert.That((float?)last?[direction]?["p95"], Is.Not.Null.And.LessThanOrEqualTo(.0005f), label + "/" + direction);
            }
        }
        else Assert.That((long?)proof["actualTriangles"], Is.EqualTo((long?)proof["inputTriangles"]), label + " preserved whole source geometry");
    }

    JObject AssertGeometryReceipt(string path, ActorName actor, Wear wear, Mesh mesh)
    {
        if (!_geometryRows.TryGetValue(path, out var entry))
        { Assert.That(_finalFit, Is.False, "Missing final-fit geometry receipt " + path); return null; }
        if (!_finalFit)
        {
            AssertReductionQuality(entry["quality"], path + "/base");
            Assert.That((bool?)entry["sourceSettingsUnchanged"], Is.True, path);
            Assert.That(mesh.vertexCount, Is.EqualTo((int?)entry["verticesPerBody"]), path + "/" + actor);
            Assert.That(mesh.triangles.LongLength / 3, Is.EqualTo((long?)entry["trianglesPerBody"]), path + "/" + actor);
            return new JObject { ["stage"] = "base", ["quality"] = entry["quality"].DeepClone() };
        }
        var final = entry["final"]; var descriptor = final["meshes"]?[actor.ToString()];
        Assert.That(descriptor, Is.Not.Null, path + "/" + actor);
        string meshPath = AssetDatabase.GetAssetPath(mesh);
        Assert.That(meshPath, Does.StartWith(FinalMeshes));
        Assert.That(meshPath, Is.EqualTo((string)descriptor["path"]));
        Assert.That(AssetDatabase.AssetPathToGUID(meshPath), Is.EqualTo((string)descriptor["guid"]));
        AssertReceiptHash(meshPath, (string)descriptor["sha"]); AssertReceiptHash(meshPath + ".meta", (string)descriptor["metaSha"]);
        Assert.That(AssetDatabase.GetAssetDependencyHash(meshPath).ToString(), Is.EqualTo((string)descriptor["dependencyHash"]));
        Assert.That(mesh.vertexCount, Is.EqualTo((int?)descriptor["vertices"]), path + "/" + actor + " final vertex count, not base count");
        Assert.That(mesh.triangles.LongLength / 3, Is.EqualTo((long?)descriptor["triangles"]), path + "/" + actor + " final triangles");
        var correctedHeadwear = final["processorEvidence"]?["correctedHeadwearReduction"];
        if (correctedHeadwear != null)
            Assert.That(mesh.triangles.LongLength / 3, Is.EqualTo((long?)correctedHeadwear["actualTriangles"]),
                path + "/" + actor + " actual final topology must match its NEW corrected-headwear quality measurement");
        Assert.That(JToken.DeepEquals(ActualWearSettings(wear), final["settings"]), Is.True, path + " actual final settings match final receipt");
        AssertActualFitKind(entry, actor, wear, mesh, path + "/" + actor);
        return new JObject { ["stage"] = "final-fit", ["fitKind"] = final["processorEvidence"]["fitKind"]?.DeepClone(), ["baseQuality"] = entry["base"]["quality"].DeepClone(),
            ["finalQuality"] = final["processorEvidence"].DeepClone(), ["descriptor"] = descriptor.DeepClone() };
    }

    static void AssertReceiptHash(string path, string expected)
    {
        Assert.That(expected, Is.Not.Null.And.Not.Empty, path + " receipt SHA256");
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] digest = sha.ComputeHash(File.ReadAllBytes(path));
        string hex = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        Assert.That(expected, Is.EqualTo(hex).Or.EqualTo(Convert.ToBase64String(digest)), path + " exact SHA256 content (hex or base64 encoding)");
    }

    static JObject ActualWearSettings(Wear wear)
    {
        var data = new SerializedObject(wear); var result = new JObject(); var configs = data.FindProperty("configs");
        var fits = new JArray();
        for (int i = 0; i < configs.arraySize; i++)
        {
            var config = configs.GetArrayElementAtIndex(i);
            fits.Add(new JObject { ["actor"] = ((ActorName)config.FindPropertyRelative("actorName").intValue).ToString(),
                ["scale"] = config.FindPropertyRelative("scale").floatValue, ["heightOffset"] = config.FindPropertyRelative("heightOffset").floatValue,
                ["mesh"] = AssetDatabase.GetAssetPath(config.FindPropertyRelative("mesh").objectReferenceValue) });
        }
        result["configs"] = fits;
        foreach (string field in new[] { "slots", "noHideUnderwearSlots", "hideWearSlots" })
        {
            var values = data.FindProperty(field); var names = new JArray();
            for (int i = 0; i < values.arraySize; i++) names.Add(((VisualWearSlot)values.GetArrayElementAtIndex(i).intValue).ToString());
            result[field] = names;
        }
        result["layer"] = wear.Layer.ToString(); result["gender"] = wear.Gender.ToString(); result["hidesHair"] = wear.HidesHair;
        result["heel"] = JObject.Parse(JsonUtility.ToJson(wear.Heel));
        result["headwearFit"] = JObject.Parse(JsonUtility.ToJson(wear.GetHeadwearFit()));
        // Compare the exact persisted representation: imported floats are Single,
        // receipt JSON values are Double. No epsilon hides actual tuning changes.
        return JObject.Parse(result.ToString(Newtonsoft.Json.Formatting.None));
    }

    IDictionary Isolate(Type type, string name)
    {
        var field = type.GetField(name, HiddenStatic); Assert.That(field, Is.Not.Null, type.Name + "." + name);
        var cache = (IDictionary)field.GetValue(null); var saved = new Hashtable();
        foreach (DictionaryEntry entry in cache) saved.Add(entry.Key, entry.Value);
        _caches.Add((cache, saved)); cache.Clear(); return cache;
    }
    void SaveField(Type type, string name)
    {
        var field = type.GetField(name, HiddenStatic); Assert.That(field, Is.Not.Null, name);
        _fields.Add((field, field.GetValue(null)));
    }
    [OneTimeSetUp] public void LoadImmutableImportedInputsOnce()
    {
        Assert.That(Application.isPlaying, Is.True, "Run this fixture in graphics PlayMode");
        Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
        var discovery = Discovery.Value;
        Assert.That(File.ReadAllText(discovery.CatalogPath), Is.EqualTo(discovery.Json),
            "Staged inputs changed after discovery; rediscover the full fixture instead of running stale/subset geometry cases.");
        var previousAsync = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
        try
        {
            _liveJson = File.ReadAllText(Root + "/catalog.json");
            _live = ParseRecords(_liveJson); _legacy = discovery.Rows;
            Assert.That(_legacy, Is.Not.Empty, "Prepare the full staged wardrobe first; do not silently skip missing content");
            Assert.That(_legacy.All(r => (bool?)r["metadata"]?["legacyWardrobe"] == true), Is.True);
            Assert.That(_live.All(r => (bool?)r["metadata"]?["legacyWardrobe"] != true), Is.True,
                "Preparation must not add the staged collection to the current live wardrobe");
            _combined = Array.AsReadOnly(_live.Concat(_legacy).ToArray());
            Assert.That(_combined.Select(r => (string)r["id"]).Distinct().Count(), Is.EqualTo(_combined.Count));
            var definitions = new Dictionary<string, GarmentDefinition>(StringComparer.Ordinal);
            var prefabs = new Dictionary<string, Wear>(StringComparer.Ordinal);
            var definitionPaths = new Dictionary<string, GarmentDefinition>(StringComparer.Ordinal);
            var prefabPaths = new Dictionary<string, Wear>(StringComparer.Ordinal);
            foreach (var row in _combined)
            {
                string id = (string)row["id"], definitionPath = (string)row["definition"], prefabPath = (string)row["main"];
                if (!definitionPaths.TryGetValue(definitionPath, out var definition))
                {
                    definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(definitionPath);
                    definitionPaths.Add(definitionPath, definition);
                }
                if (!prefabPaths.TryGetValue(prefabPath, out var prefab))
                {
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath)?.GetComponent<Wear>();
                    prefabPaths.Add(prefabPath, prefab);
                }
                Assert.That(definition, Is.Not.Null, id + " definition");
                Assert.That(prefab, Is.Not.Null, id + " prefab");
                definitions.Add(id, definition); prefabs.Add(id, prefab);
            }
            _definitions = new ReadOnlyDictionary<string, GarmentDefinition>(definitions);
            _prefabs = new ReadOnlyDictionary<string, Wear>(prefabs);
            _prototypeRows = new ReadOnlyDictionary<string, JToken>(_legacy.GroupBy(r => (string)r["main"], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => (string)r["id"], StringComparer.Ordinal).First(), StringComparer.Ordinal));
            Assert.That(discovery.Keys.Count, Is.EqualTo(_prototypeRows.Count * 2), "Every prototype keeps both canonical bodies.");
            LoadGeometryReceipts();
            var mapRoots = discovery.Curated ? new[] { Root + "/PaintMaps" } :
                new[] { Root + "/PaintMaps", _finalFit ? FinalMaps : Staged + "/PaintMaps" };
            IEnumerable<string> importedMaps = AssetDatabase.FindAssets("t:PaintPointMap", mapRoots)
                .Select(AssetDatabase.GUIDToAssetPath);
            if (discovery.Curated)
                importedMaps = importedMaps.Concat(((JArray)JObject.Parse(discovery.Json)["records"])
                    .Where(row => (string)row["type"] == "config").Select(row => (string)row["main"]));
            var mapPaths = importedMaps.Distinct(StringComparer.Ordinal).ToArray();
            _paintMaps = Array.AsReadOnly(mapPaths.Select(path =>
            {
                var map = AssetDatabase.LoadAssetAtPath<PaintPointMap>(path);
                Assert.That(map, Is.Not.Null, path);
                Assert.That(map.name, Is.EqualTo(Path.GetFileNameWithoutExtension(path)), path + " runtime resource key");
                return map;
            }).ToArray());
            Assert.That(_paintMaps.Select(map => map.name).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(_paintMaps.Count),
                "Imported runtime map keys must not shadow each other across active/staged roots.");
            _actorPrefabs = new ReadOnlyDictionary<ActorName, GameObject>(new[] { ActorName.Marta, ActorName.Kshishtof }
                .ToDictionary(actor => actor, actor =>
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab");
                    Assert.That(prefab, Is.Not.Null, actor + " actual body prefab"); return prefab;
                }));
            var clips = new[] { "Male@Idle.fbx", "Male@Run.fbx", "Sleep.fbx", "LayingBelly.anim" }
                .ToDictionary(file => file, ImportedClip, StringComparer.Ordinal);
            _poses = Array.AsReadOnly(new[] { ("standing", clips["Male@Idle.fbx"], .4f, false),
                ("run_early", clips["Male@Run.fbx"], .1f, false), ("run_mid", clips["Male@Run.fbx"], .45f, false),
                ("run_late", clips["Male@Run.fbx"], .8f, false), ("sleep", clips["Sleep.fbx"], .5f, true),
                ("belly", clips["LayingBelly.anim"], .5f, true) });
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output + "/input-cache.json", new JObject {
                ["geometryCases"] = discovery.Keys.Count, ["uniqueLegacyModels"] = _prototypeRows.Count,
                ["posesPerGeometryCase"] = _poses.Count, ["logicalVariants"] = _legacy.Count,
                ["finalFit"] = _finalFit, ["geometryManifest"] = _geometryManifestPath,
                ["curatedRelease"] = discovery.Curated,
                ["excludedReleaseVariants"] = new JArray(_excludedReleaseIds.OrderBy(id => id, StringComparer.Ordinal)),
                ["uniqueDefinitionAssetsLoadedOnce"] = definitionPaths.Count, ["uniqueWearPrefabsLoadedOnce"] = prefabPaths.Count,
                ["paintMapsLoadedOnce"] = _paintMaps.Count, ["actorPrefabsLoadedOnce"] = _actorPrefabs.Count,
                ["animationClipsLoadedOnce"] = clips.Count, ["perCaseGlobalIsolation"] = true,
                ["remoteRequests"] = false, ["published"] = false }.ToString());
        }
        finally { ShaderUtil.allowAsyncCompilation = previousAsync; }
    }

    [SetUp] public void InjectOnlyImportedAssets()
    {
        Assert.That(Application.isPlaying, Is.True, "Run this fixture in graphics PlayMode");
        Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
        _async = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
        SaveField(typeof(GarmentLibrary), "_active"); SaveField(typeof(GarmentLibrary), "_spawnable"); SaveField(typeof(GarmentLibrary), "_byId");
        // Keep fresh mutable simulation rows and per-case cache lists, as before.
        // Only parsed metadata/imported asset inputs are shared between cases.
        GarmentLibrary.Override(_definitions.Values.Select(d => d.ToParams()));
        var cache = Isolate(typeof(ActorWardrobe), "Cache"); var variants = Isolate(typeof(GarmentVariants), "ById");
        foreach (var row in _combined)
        {
            var id = (string)row["id"]; cache[id] = new List<Wear> { _prefabs[id] }; variants[id] = _definitions[id];
        }
        var maps = Isolate(typeof(PaintPointMap), "Cache");
        foreach (var map in _paintMaps) maps[map.name] = map;
        foreach (string id in _excludedReleaseIds)
        {
            Assert.That(cache.Contains(id) || variants.Contains(id), Is.False, id + " excluded from fixture art caches");
            Assert.That(GarmentLibrary.Active.Any(row => row.Id == id), Is.False, id + " excluded from fixture simulation");
        }
    }

    [OneTimeTearDown] public void VerifyInputCatalogsWereNotModified()
    {
        if (_liveJson != null)
            Assert.That(File.ReadAllText(Root + "/catalog.json"), Is.EqualTo(_liveJson), "The active wardrobe inputs changed during acceptance.");
        if (Discovery.IsValueCreated)
            Assert.That(File.ReadAllText(Discovery.Value.CatalogPath), Is.EqualTo(Discovery.Value.Json), "The staged wardrobe inputs changed during acceptance.");
        if (_releaseSelectionJson != null)
            Assert.That(File.ReadAllText(ReleaseSelection), Is.EqualTo(_releaseSelectionJson), "Release selection changed during acceptance.");
        if (_geometryManifestPath != null)
            Assert.That(File.ReadAllText(_geometryManifestPath), Is.EqualTo(_geometryManifestJson), "Geometry receipt changed during acceptance.");
        if (_baseCatalogPath != null)
            Assert.That(File.ReadAllText(_baseCatalogPath), Is.EqualTo(_baseCatalogJson), "Base-generation snapshot changed during acceptance.");
    }

    [TearDown] public void RestoreGlobalCaches()
    {
        foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
        _objects.Clear();
        foreach (var (cache, previous) in _caches)
        { cache.Clear(); foreach (DictionaryEntry entry in previous) cache.Add(entry.Key, entry.Value); }
        _caches.Clear();
        foreach (var (field, previous) in _fields) field.SetValue(null, previous);
        _fields.Clear(); ShaderUtil.allowAsyncCompilation = _async;
    }

    GameObject Actor(ActorName actor)
    {
        var prefab = _actorPrefabs[actor];
        Assert.That(prefab, Is.Not.Null);
        var go = Object.Instantiate(prefab); _objects.Add(go);
        go.GetComponent<Animator>().enabled = false;
        go.GetComponent<BodyBones>().Construct(actor, 649);
        var view = go.GetComponent<NpcActorView>() ?? go.AddComponent<NpcActorView>(); view.enabled = false;
        Set(view, "_bodyBones", go.GetComponent<BodyBones>()); Set(view, "_actorMesh", actor);
        Set(view, "_bodyRoot", go.transform);
        Set(view, "_bodySkins", go.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        return go;
    }
    static void Set(object instance, string name, object value) => instance.GetType().GetField(name, HiddenInstance).SetValue(instance, value);
    static void Call(object instance, string name) => instance.GetType().GetMethod(name, HiddenInstance).Invoke(instance, null);
    static Mesh FittedMesh(Wear wear, ActorName actor)
    {
        var configs = new SerializedObject(wear).FindProperty("configs");
        for (int i = 0; i < configs.arraySize; i++)
        {
            var config = configs.GetArrayElementAtIndex(i);
            if (config.FindPropertyRelative("actorName").intValue == (int)actor)
                return config.FindPropertyRelative("mesh").objectReferenceValue as Mesh;
        }
        Assert.Fail(wear.name + ": missing canonical body fit " + actor); return null;
    }
    static PaintPointMap ImportedMap(Mesh mesh)
    {
        string key = "garment_" + mesh.name + "_" + mesh.vertexCount;
        var cache = (IDictionary)typeof(PaintPointMap).GetField("Cache", HiddenStatic).GetValue(null);
        Assert.That(cache.Contains(key), Is.True, "Missing imported map " + key + "; never start a remote fallback in an offline test");
        var map = PaintPointMap.Load(key, mesh.vertexCount);
        Assert.That(map, Is.Not.Null, key); Assert.That(map.VertexCount, Is.EqualTo(mesh.vertexCount), key);
        return map;
    }
    static AnimationClip ImportedClip(string file)
    {
        var clips = AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/" + file).OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
        Assert.That(clips, Is.Not.Empty, file + " must be a real imported animation");
        return clips[0];
    }
    static Vector3[] Capture(SkinnedMeshRenderer skin)
    {
        var mesh = new Mesh();
        try { skin.BakeMesh(mesh, true); return mesh.vertices.Select(skin.transform.TransformPoint).ToArray(); }
        finally { Object.DestroyImmediate(mesh); }
    }
    static Bounds BoundsOf(Vector3[] vertices)
    {
        Assert.That(vertices, Is.Not.Empty); var bounds = new Bounds(vertices[0], Vector3.zero);
        foreach (var p in vertices)
        {
            if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) Assert.Fail("Nonfinite skinned vertex");
            bounds.Encapsulate(p);
        }
        return bounds;
    }
    static JArray Vec(Vector3 p) => new JArray(p.x, p.y, p.z);
    static JObject Box(Bounds b) => new JObject { ["center"] = Vec(b.center), ["size"] = Vec(b.size) };
    static void FiniteAndNearBody(SkinnedMeshRenderer garment, Vector3[] points, SkinnedMeshRenderer body, float scale, string label)
    {
        Assert.That(garment.enabled && garment.gameObject.activeInHierarchy, Is.True, label + " visible garment");
        var bounds = BoundsOf(points); var bodyBounds = BoundsOf(Capture(body));
        Assert.That(bounds.size.magnitude, Is.InRange(.001f * scale, 4f * scale), label + " world size");
        Assert.That(Vector3.Distance(bounds.center, bodyBounds.ClosestPoint(bounds.center)), Is.LessThan(.75f * scale), label + " detached from body");
        var culling = garment.bounds; culling.Expand(.01f * scale);
        Assert.That(points.Count(p => !culling.Contains(p)), Is.Zero, label + " skinned vertices outside actual culling bounds");
    }

    [UnityTest]
    public IEnumerator EveryUniqueLegacyPrototypeSyncsBothCanonicalBodiesAndFollowsRealPoses(
        [ValueSource(nameof(GeometryCases))] string key)
    {
        var parts = key.Split('|'); var path = parts[0]; var actor = (ActorName)Enum.Parse(typeof(ActorName), parts[1]);
        var row = _prototypeRows[path];
        string id = (string)row["id"], equipKey = id + "#0";
        var prefab = _prefabs[id]; Assert.That(prefab.SupportsActor(actor), Is.True, key);
        var expected = FittedMesh(prefab, actor); Assert.That(expected, Is.Not.Null);
        var geometryProvenance = AssertGeometryReceipt(path, actor, prefab, expected);
        bool rigidHeadwear = (string)geometryProvenance?["fitKind"] == RigidHeadwearKind;
        var go = Actor(actor); var bones = go.GetComponent<BodyBones>(); var view = go.GetComponent<NpcActorView>();
        int originalBones = bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length;
        var originalTransforms = new HashSet<Transform>(go.GetComponentsInChildren<Transform>(true));
        view.SyncWorn(new[] { id }); var wear = bones.GetWorn(equipKey); Assert.That(wear, Is.Not.Null, key);
        var skin = wear.GetComponentInChildren<SkinnedMeshRenderer>(true); Assert.That(skin, Is.Not.Null);
        Assert.That(skin.sharedMesh, Is.SameAs(expected), key + " canonical actor selected the wrong body mesh");
        Assert.That(wear.DefinitionId, Is.EqualTo(id), "Prepared fitting must retain the simulation identity");
        int equippedBones = bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length;
        for (int i = 0; i < 3; i++) view.SyncWorn(new[] { id });
        Assert.That(bones.GetWorn(equipKey), Is.SameAs(wear), key + " identical snapshot rebuilt clothing");
        Assert.That(bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(equippedBones));
        var body = ActorBodyResolver.ResolveOrNull(go.transform); Assert.That(body, Is.Not.Null);
        var animator = go.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
        animator.enabled = true; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.Rebind();
        var poses = _poses;
        var audit = new JArray(); var graph = PlayableGraph.Create("LegacyWardrobeActualRuntime"); Vector3[] standingBody = null;
        RigidHeadFrameOracle rigidFrame = null; var rigidAudit = rigidHeadwear ? new JArray() : null;
        var rigidControlAudit = rigidHeadwear ? new JObject { ["visualAcceptanceClaimed"] = false } : null;
        try
        {
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(graph, "real humanoid clips", animator);
            AnimationClipPlayable previous = default;
            foreach (var (pose, clip, time, laying) in poses)
            {
                if (previous.IsValid()) graph.DestroyPlayable(previous);
                var play = AnimationClipPlayable.Create(graph, clip); previous = play; output.SetSourcePlayable(play);
                graph.Play(); play.SetTime(clip.length * time); graph.Evaluate(0);
                bones.SetHeelPoseSuppressed(laying); Set(view, "_laying", laying);
                // Let the real heel transition finish. Re-evaluate the same manual
                // clip, so geometry comparison never conflates motion with movement.
                float until = Time.realtimeSinceStartup + 1f;
                while (Mathf.Abs(bones.HeelPoseWeight - (laying ? 0 : 1)) > .001f && Time.realtimeSinceStartup < until)
                { graph.Evaluate(0); yield return null; }
                Assert.That(bones.HeelPoseWeight, Is.EqualTo(laying ? 0 : 1).Within(.001f));
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); go.transform.localScale = Vector3.one * .7764706f;
                graph.Evaluate(0); Call(bones, "LateUpdate"); Call(view, "RefreshSkinBounds");
                yield return null; yield return null;
                var before = Capture(skin);
                var local = before.Select(go.transform.InverseTransformPoint).ToArray();
                var posedBody = Capture(body).Select(go.transform.InverseTransformPoint).ToArray();
                if (pose == "standing") standingBody = posedBody;
                float bodyPoseChange = 0;
                if (pose.StartsWith("run_", StringComparison.Ordinal))
                {
                    Assert.That(posedBody.Length, Is.EqualTo(standingBody.Length));
                    for (int i = 0; i < posedBody.Length; i++) bodyPoseChange = Mathf.Max(bodyPoseChange, Vector3.Distance(posedBody[i], standingBody[i]));
                    Assert.That(bodyPoseChange, Is.GreaterThan(.01f), key + "/" + pose + " animation graph did not pose the real body");
                }
                FiniteAndNearBody(skin, before, body, .7764706f, key + "/" + pose);
                if (rigidHeadwear)
                {
                    var head = bones.GetBone("head");
                    if (rigidFrame == null) rigidFrame = new RigidHeadFrameOracle(before, head, go.transform, expected.triangles);
                    rigidAudit.Add(new JObject { ["pose"] = pose, ["stage"] = "posed",
                        ["frame"] = rigidFrame.AssertMatches(before, head, go.transform, key + "/" + pose) });
                }
                go.transform.SetPositionAndRotation(new Vector3(13.25f, 2.1f, -8.75f), Quaternion.Euler(0, 137f, 0));
                Call(view, "RefreshSkinBounds"); yield return null; yield return null;
                var moved = Capture(skin);
                Assert.That(moved.Length, Is.EqualTo(before.Length));
                float followError = 0;
                for (int i = 0; i < moved.Length; i++) followError = Mathf.Max(followError, Vector3.Distance(moved[i], go.transform.TransformPoint(local[i])));
                Assert.That(followError, Is.LessThan(.0003f), key + "/" + pose + " garment failed translation/yaw equivariance");
                FiniteAndNearBody(skin, moved, body, .7764706f, key + "/" + pose + "/moved");
                if (rigidHeadwear) rigidAudit.Add(new JObject { ["pose"] = pose, ["stage"] = "translated-and-yawed",
                    ["frame"] = rigidFrame.AssertMatches(moved, bones.GetBone("head"), go.transform, key + "/" + pose + "/moved") });
                Assert.That(bones.GetWorn(equipKey), Is.SameAs(wear));
                audit.Add(new JObject { ["pose"] = pose, ["clip"] = AssetDatabase.GetAssetPath(clip), ["clipTime"] = clip.length * time,
                    ["heelPoseWeight"] = bones.HeelPoseWeight, ["boundsBefore"] = Box(BoundsOf(before)),
                    ["boundsMoved"] = Box(BoundsOf(moved)), ["translationYawMaxError"] = followError,
                    ["bodyPoseChangeFromStanding"] = bodyPoseChange });
            }
        }
        finally { if (graph.IsValid()) graph.Destroy(); }
        animator.enabled = false;
        if (rigidHeadwear)
        {
            Assert.That(rigidAudit.Count, Is.EqualTo(12), "Six real poses plus six moved-root head-frame proofs");
            var head = bones.GetBone("head"); var saved = head.localRotation; var rotationBefore = head.rotation;
            try
            {
                // Independent of the clip: force real head motion even if its head track is static.
                head.localRotation = saved * Quaternion.Euler(17, 23, 11); Call(bones, "LateUpdate"); Call(view, "RefreshSkinBounds");
                Assert.That(Quaternion.Angle(rotationBefore, head.rotation), Is.GreaterThan(1f), "Independent head control did not move the actual head");
                rigidControlAudit["independentHeadRotation"] = rigidFrame.AssertMatches(Capture(skin), head, go.transform, key + "/controlled-head-rotation");
            }
            finally { head.localRotation = saved; Call(bones, "LateUpdate"); Call(view, "RefreshSkinBounds"); }
            rigidControlAudit["restoredHeadRotation"] = rigidFrame.AssertMatches(Capture(skin), head, go.transform, key + "/restored-head-rotation");
        }
        var attached = go.GetComponentsInChildren<Transform>(true).Where(t => !originalTransforms.Contains(t)).ToArray();
        view.SyncWorn(Array.Empty<string>()); Assert.That(bones.GetWorn(equipKey), Is.Null);
        yield return null; yield return null; // BodyBones.TakeOff uses runtime Destroy, not DestroyImmediate.
        Assert.That(bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(originalBones), key + " leaked garment skeleton");
        Assert.That(attached.All(t => t == null), Is.True, key + " left detached garment objects/bones");
        if (rigidHeadwear)
        {
            // Actual destruction followed by a NEW Equip, not just repeated identical SyncWorn.
            view.SyncWorn(new[] { id }); var reequip = bones.GetWorn(equipKey);
            Assert.That(reequip, Is.Not.Null); Assert.That(reequip, Is.Not.SameAs(wear));
            Assert.That(reequip.DefinitionId, Is.EqualTo(id));
            var newSkin = reequip.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.That(newSkin.sharedMesh, Is.SameAs(expected));
            Call(bones, "LateUpdate"); Call(view, "RefreshSkinBounds"); yield return null; yield return null;
            rigidControlAudit["actualReequip"] = rigidFrame.AssertMatches(Capture(newSkin), bones.GetBone("head"), go.transform, key + "/actual-reequip");
            var reattached = go.GetComponentsInChildren<Transform>(true).Where(t => !originalTransforms.Contains(t)).ToArray();
            view.SyncWorn(Array.Empty<string>()); Assert.That(bones.GetWorn(equipKey), Is.Null);
            yield return null; yield return null;
            Assert.That(bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(originalBones), key + " re-equip leaked garment skeleton");
            Assert.That(reattached.All(t => t == null), Is.True, key + " re-equip left detached garment objects/bones");
            rigidControlAudit["secondTakeOffRestoredBoneCount"] = true;
        }
        JObject authoredDonorActual=null;
        if((string)geometryProvenance?["finalQuality"]?["bodies"]?[actor.ToString()]?["geometryMode"]=="authored-donor-preserved")
        {
            var entry=_geometryRows[path];
            var editor=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
            authoredDonorActual=(JObject)editor.GetMethod("AuditAuthoredDonorFinalRuntime",BindingFlags.Public|BindingFlags.Static)
                .Invoke(null,new object[]{prefab.gameObject,(string)entry["base"]["source"],actor.ToString(),(string)entry["base"]["donor"]});
            Assert.That((bool?)authoredDonorActual["passed"],Is.True);
            Assert.That(((JArray)authoredDonorActual["sixActualPoses"]).Count,Is.EqualTo(6));
        }
        File.WriteAllText(Output + "/" + Path.GetFileNameWithoutExtension(path) + "_" + actor + ".json", new JObject {
            ["requestedId"] = id, ["main"] = path, ["actor"] = actor.ToString(), ["mesh"] = AssetDatabase.GetAssetPath(expected),
            ["vertices"] = expected.vertexCount, ["geometryProvenance"] = geometryProvenance, ["poses"] = audit,
            ["authoredDonorActualSixPoseEquivalence"]=authoredDonorActual,["rigidHeadFramePoses"] = rigidAudit, ["rigidHeadFrameControls"] = rigidControlAudit, ["repeatSyncSameInstance"] = true,
            ["takeOffRestoredBoneCount"] = true, ["clippingVisuallyProven"] = false, ["remoteRequests"] = false }.ToString());
    }

    [Test]
    public void StagedVariantsCoverRetainedDefaultsAndPreserveCanonicalStatsAndOwnedDependencies()
    {
        var native = AssetDatabase.FindAssets("t:GarmentDefinition", new[] { NativeDefinitions })
            .Select(g => AssetDatabase.LoadAssetAtPath<GarmentDefinition>(AssetDatabase.GUIDToAssetPath(g)))
            .ToDictionary(d => d.id, StringComparer.Ordinal);
        var defaults = GarmentLibrary.Defaults.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var liveIds = new HashSet<string>(_live.Select(r => (string)r["id"]), StringComparer.Ordinal);
        var expected = new HashSet<string>(defaults.Keys.Where(id => !liveIds.Contains(id)), StringComparer.Ordinal);
        var actual = new HashSet<string>(_legacy.Select(r => (string)r["id"]), StringComparer.Ordinal);
        Assert.That(_excludedReleaseIds.IsSubsetOf(expected), Is.True, "Every excluded release variant was a retained default.");
        expected.ExceptWith(_excludedReleaseIds);
        Assert.That(actual, Is.EquivalentTo(expected), "Cover every selected retained default without importing excluded variants or source-only orphans");
        var seen = new HashSet<string>(); var report = new JArray();
        foreach (var row in _legacy)
        {
            string id = (string)row["id"], path = (string)row["main"];
            Assert.That(native.TryGetValue(id, out var source), Is.True, id + " canonical native definition");
            var target = _definitions[id]; var a = source.ToParams(); var b = target.ToParams(); var baseline = defaults[id];
            {
                Assert.That(b.Id, Is.EqualTo(a.Id), id); Assert.That(b.DisplayName, Is.EqualTo(a.DisplayName), id);
                Assert.That(b.PrototypeId, Is.EqualTo(a.PrototypeId), id); Assert.That(b.Warmth, Is.EqualTo(a.Warmth), id);
                Assert.That(b.Armor, Is.EqualTo(a.Armor), id); Assert.That(b.ThermalDelta, Is.EqualTo(a.ThermalDelta), id);
                Assert.That(b.Capacity, Is.EqualTo(a.Capacity), id); Assert.That(b.DressDurationTicks, Is.EqualTo(a.DressDurationTicks), id);
                Assert.That(b.Covers, Is.EqualTo(a.Covers), id); Assert.That(target.category, Is.EqualTo(source.category), id);
                Assert.That(b.Retired, Is.EqualTo(a.Retired || baseline.Retired), id + " retirement must not revive saved definitions");
                Assert.That(b.Sex, Is.EqualTo(GarmentSex.Any), id + " both prepared body fits");
                Assert.That(b.AlternateFitId, Is.Empty, id + " one item with two fits");
                Assert.That(target.layer.ToString(), Is.EqualTo(_prefabs[id].Layer.ToString()), id + " actual authored visual layer");
            }
            Assert.That(path, Does.StartWith(_finalFit ? FinalPrefabs : Staged + "/Prefabs/"));
            Assert.That((string)row["definition"], Does.StartWith(Staged + "/Definitions/"));
            var metadata = row["metadata"]; var sim = metadata["simulation"];
            Assert.That((string)metadata["artId"], Is.EqualTo(target.ArtId), id);
            Assert.That((string)metadata["sex"], Is.EqualTo("Any"), id);
            Assert.That(metadata["slots"].Values<string>(), Is.EqualTo(_prefabs[id].Slots.Select(s => s.ToString())), id);
            Assert.That((float)sim["warmth"], Is.EqualTo(b.Warmth), id); Assert.That((float)sim["armor"], Is.EqualTo(b.Armor), id);
            Assert.That((float)sim["thermalDelta"], Is.EqualTo(b.ThermalDelta), id);
            Assert.That((string)sim["displayName"], Is.EqualTo(b.DisplayName), id);
            Assert.That((string)sim["prototypeId"], Is.EqualTo(b.PrototypeId), id);
            Assert.That((string)sim["sex"], Is.EqualTo(b.Sex.ToString()), id);
            Assert.That((int)sim["capacity"], Is.EqualTo(b.Capacity), id);
            Assert.That((int)sim["dressDurationTicks"], Is.EqualTo(b.DressDurationTicks), id);
            Assert.That((bool)sim["retired"], Is.EqualTo(b.Retired), id);
            Assert.That((string)sim["layer"], Is.EqualTo(b.Layer.ToString()), id);
            Assert.That(sim["covers"].Values<string>(), Is.EqualTo(b.Covers.Select(p => p.ToString())), id);
            foreach (var dependency in AssetDatabase.GetDependencies(new[] { path, (string)row["definition"] }, true))
            {
                Assert.That(dependency, Does.Not.StartWith("Assets/HexLiveContent/Wear/").And.Not.StartWith("Assets/HexLiveContent/RuntimeSource/Wear/").And.Not.StartWith("Assets/ImportedActors/"), id + " old high-poly dependency");
                var type = AssetDatabase.GetMainAssetTypeAtPath(dependency);
                if (type != null && (typeof(Texture).IsAssignableFrom(type) || typeof(Material).IsAssignableFrom(type) || typeof(Mesh).IsAssignableFrom(type)))
                    Assert.That(dependency, Does.StartWith(Staged + "/"), id + " prepared asset ownership");
                if (_finalFit && type != null && typeof(Mesh).IsAssignableFrom(type))
                    Assert.That(dependency, Does.StartWith(FinalMeshes), id + " final prefab must not depend on a base mesh");
                if (_finalFit && dependency.EndsWith(".prefab", StringComparison.Ordinal))
                    Assert.That(dependency, Is.EqualTo(path), id + " final prefab must not depend on another/base prefab");
            }
            if (seen.Add(path))
            {
                var wear = _prefabs[id]; var data = new SerializedObject(wear).FindProperty("configs");
                if (_finalFit) Assert.That(PrefabUtility.GetPrefabAssetType(wear.gameObject), Is.EqualTo(PrefabAssetType.Regular),
                    path + " final prefab must be standalone, never a variant of the base prefab");
                Assert.That(data.arraySize, Is.EqualTo(2), path + " canonical two-fit contract");
                var sources = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/HexLiveContent/Wear/" + source.ArtId })
                    .Select(AssetDatabase.GUIDToAssetPath).ToArray();
                Assert.That(sources.Length, Is.EqualTo(1), source.ArtId + " unambiguous canonical native prototype");
                var nativeWear = AssetDatabase.LoadAssetAtPath<GameObject>(sources[0]).GetComponent<Wear>();
                Assert.That(wear.Layer, Is.EqualTo(nativeWear.Layer), path + " preserved physical layer");
                Assert.That(wear.Slots, Is.EqualTo(nativeWear.Slots), path + " preserved visual coverage");
                Assert.That(wear.NoHideUnderwearSlots, Is.EqualTo(nativeWear.NoHideUnderwearSlots), path);
                Assert.That(wear.HideWearSlots, Is.EqualTo(nativeWear.HideWearSlots), path);
                Assert.That(wear.HidesHair, Is.EqualTo(nativeWear.HidesHair), path);
                Assert.That(wear.Heel, Is.EqualTo(nativeWear.Heel), path);
                Assert.That(wear.GetHeadwearFit(), Is.EqualTo(nativeWear.GetHeadwearFit()), path);
                var female = FittedMesh(wear, ActorName.Marta); var male = FittedMesh(wear, ActorName.Kshishtof);
                Assert.That(female, Is.Not.SameAs(male), path + " male geometry must have its own fitted mesh");
                foreach (var actor in new[] { ActorName.Marta, ActorName.Kshishtof })
                {
                    Assert.That(wear.SupportsActor(actor), Is.True); var mesh = FittedMesh(wear, actor);
                    Assert.That(mesh, Is.Not.Null); Assert.That(mesh.vertexCount, Is.GreaterThan(0));
                    Assert.That(mesh.isReadable, Is.True, path + " CPU topology is needed by the actual wear painter");
                    Assert.That(AssetDatabase.GetAssetPath(mesh), Does.StartWith(_finalFit ? FinalMeshes : Staged + "/Meshes/"));
                    ImportedMap(mesh);
                }
                Assert.That(wear.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length, Is.EqualTo(1));
                Assert.That(wear.GetComponentsInChildren<MonoBehaviour>(true).All(c => c is Wear), Is.True,
                    path + " no runtime cloth/refitting components are allowed in prepared art");
            }
            report.Add(new JObject { ["id"] = id, ["source"] = AssetDatabase.GetAssetPath(source), ["definition"] = (string)row["definition"],
                ["retired"] = b.Retired, ["statsMatchCanonicalNative"] = true, ["main"] = path });
        }
        File.WriteAllText(Output + "/metadata-coverage.json", new JObject { ["variants"] = report, ["uniqueLegacyModels"] = seen.Count,
            ["retainedDefaultsOutsideLiveCatalog"] = expected.Count,
            ["sourceOnlyOrphansExcluded"] = new JArray(native.Keys.Where(id => !defaults.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal)),
            ["liveCatalogChanged"] = false, ["published"] = false }.ToString());
    }

    [Test]
    public void EveryStagedVariantHasItsImportedOwnerIconAndActualRussianEnglishNamesAndDescriptions()
    {
        var previous = Loc.Current;
        try
        {
            var imported = AssetDatabase.LoadMainAssetAtPath("Assets/Resources/I2Languages.asset");
            Assert.That(imported, Is.Not.Null);
            var terms = new SerializedObject(imported).FindProperty("mSource.mTerms");
            Assert.That(terms, Is.Not.Null, "Terms must be inside the actual imported I2 source");
            var authored = new Dictionary<string, string[]>();
            for (int i = 0; i < terms.arraySize; i++)
            {
                var term = terms.GetArrayElementAtIndex(i); var translations = term.FindPropertyRelative("Languages");
                authored[term.FindPropertyRelative("Term").stringValue] = Enumerable.Range(0, translations.arraySize)
                    .Select(index => translations.GetArrayElementAtIndex(index).stringValue).ToArray();
            }
            foreach (var row in _legacy)
            {
                var id = (string)row["id"];
                // Same concrete per-owner icon path as canonical ResolvePeople;
                // no ItemIcons.Load/Address call that would initialize a remote service.
                var icon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/HexLiveContent/Icons/" + id + ".png");
                Assert.That(icon, Is.Not.Null, id + " owner icon must import as Sprite");
                Assert.That(icon.rect.width, Is.GreaterThanOrEqualTo(128)); Assert.That(icon.rect.height, Is.GreaterThanOrEqualTo(128));
                var item = ItemCatalog.Resolve(id);
                foreach (var language in new[] { Language.Russian, Language.English })
                {
                    Loc.Current = language;
                    foreach (var term in new[] { item.NameKey, item.DescKey })
                    {
                        Assert.That(authored.TryGetValue(term, out var values), Is.True, term + " imported I2 term");
                        Assert.That(values.Length, Is.GreaterThan((int)language));
                        Assert.That(values[(int)language], Is.Not.Empty.And.Not.EqualTo(term), "English fallback must not mask missing Russian authoring");
                        Assert.That(Loc.Has(term), Is.True, language + "/" + term); Assert.That(Loc.Get(term), Is.Not.Empty.And.Not.EqualTo(term));
                    }
                }
            }
        }
        finally { Loc.Current = previous; }
    }

    [UnityTest]
    public IEnumerator FourRepresentativeLegacyModelsPaintFromActualMapsWithoutAllocatingTheWholeCollection()
    {
        foreach (var id in new[] { "clothing.ankleboots_amy", "clothing.skirt_anarchy", "underwear.top_vapor", "FCO Pants Male" })
        foreach (var actor in new[] { ActorName.Marta, ActorName.Kshishtof })
        {
            Assert.That(_legacy.Any(r => (string)r["id"] == id), Is.True, "Missing representative " + id);
            var go = Actor(actor); var bones = go.GetComponent<BodyBones>(); var view = go.GetComponent<NpcActorView>();
            view.SyncWorn(new[] { id }); var wear = bones.GetWorn(id + "#0"); Assert.That(wear, Is.Not.Null);
            var skin = wear.GetComponentInChildren<SkinnedMeshRenderer>(); var mesh = skin.sharedMesh;
            var map = ImportedMap(mesh);
            var zones = map.Zones.Where(z => z.Points.Any(p => p.Valid)).Select(z => z.Zone).ToArray(); Assert.That(zones, Is.Not.Empty);
            var pristine = skin.sharedMaterials.Select(m => m.GetTexture("_BaseMap")).ToArray();
            var dryColor = skin.sharedMaterial.GetColor("_BaseColor"); float drySmoothness = skin.sharedMaterial.GetFloat("_Smoothness");
            wear.SetErosion(.3f); wear.SetGrime(.8f, zones, zones.Select(_ => .8f).ToArray(), zones.Length, .6f); wear.SetWetness(.65f);
            var paint = wear.GetComponent<GarmentWearPainter>() ?? wear.GetComponentInChildren<GarmentWearPainter>(); Assert.That(paint, Is.Not.Null);
            Assert.That(typeof(GarmentWearPainter).GetField("_map", HiddenInstance).GetValue(paint), Is.SameAs(map), "Painter must use the injected imported map, not a network/runtime fallback");
            float until = Time.realtimeSinceStartup + 20f;
            while (!paint.TryPaintPresentation() && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(paint.PresentationReady, Is.True, actor + "/" + id);
            var painted = skin.sharedMaterials.Select(m => m.GetTexture("_BaseMap")).ToArray();
            Assert.That(painted.Select((t, i) => t is RenderTexture && pristine[i] != null && PixelDifference(t, pristine[i]) > .0001f).Any(x => x), Is.True,
                id + " GPU paint did not change albedo");
            var masks = skin.sharedMaterials.Where(m => m.HasProperty("_TearMaskTex")).Select(m => m.GetTexture("_TearMaskTex")).OfType<RenderTexture>().ToArray();
            Assert.That(masks, Is.Not.Empty, id + " actual tear mask");
            var block = new MaterialPropertyBlock(); skin.GetPropertyBlock(block, 0);
            Assert.That(block.GetFloat("_Smoothness"), Is.EqualTo(Mathf.Lerp(drySmoothness, .72f, .65f)).Within(.0001f), id + " wetness must reach the real renderer");
            Assert.That(Vector4.Distance(block.GetColor("_BaseColor"), dryColor), Is.GreaterThan(.001f));
            wear.SetWetness(0); skin.GetPropertyBlock(block, 0);
            Assert.That(block.GetFloat("_Smoothness"), Is.EqualTo(drySmoothness).Within(.0001f));
            Assert.That(Vector4.Distance(block.GetColor("_BaseColor"), dryColor), Is.LessThan(.0001f));
            view.SyncWorn(Array.Empty<string>()); yield return null; yield return null;
            Assert.That(painted.OfType<RenderTexture>().All(t => t == null || !t.IsCreated()), Is.True, id + " leaked paint RenderTexture");
            Object.DestroyImmediate(go); // Dispose each painter before allocating the next representative.
        }
    }
    static Color[] Pixels(Texture texture)
    {
        var old = RenderTexture.active; var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGB32);
        var image = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        try { Graphics.Blit(texture, rt); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); image.Apply(); return image.GetPixels(); }
        finally { RenderTexture.active = old; RenderTexture.ReleaseTemporary(rt); Object.DestroyImmediate(image); }
    }
    static float PixelDifference(Texture left, Texture right)
    { var a = Pixels(left); var b = Pixels(right); return a.Select((c, i) => Mathf.Abs(c.r - b[i].r) + Mathf.Abs(c.g - b[i].g) + Mathf.Abs(c.b - b[i].b)).Average(); }
}
}
#endif
