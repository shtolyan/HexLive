#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using BodyPart = HexLive.Simulation.Content.BodyPart;

namespace HexLive.Tests
{
// Executes the real editor resolvers and candidate writer, never BuildPipeline.
// Temporary authoring fixtures use distinct IDs and never alter a real garment.
public sealed class AtomicContentWearStateTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private Type _builder;
    private string _definitionFolder, _wearFolder, _id, _definitionPath, _prefabPath;
    private GarmentDefinition _definition;

    [SetUp]
    public void CreateIsolatedAuthoringFixture()
    {
        _builder = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("AtomicContentBatchBuild"))
            .FirstOrDefault(t => t != null);
        Assert.That(_builder, Is.Not.Null, "The actual AtomicContentBatchBuild editor assembly must be loaded.");
        _id = "test.state649." + Guid.NewGuid().ToString("N");
        var definitionRoot = (string)_builder.GetField("WearDefinitions", Hidden).GetRawConstantValue();
        var wearRoot = (string)_builder.GetField("WearRoot", Hidden).GetRawConstantValue();
        Assert.That(AssetDatabase.IsValidFolder(definitionRoot), Is.True);
        Assert.That(AssetDatabase.IsValidFolder(wearRoot), Is.True);
        _definitionFolder = definitionRoot + "/" + _id;
        _wearFolder = wearRoot + "/" + _id;
        Assert.That(AssetDatabase.CreateFolder(definitionRoot, _id), Is.Not.Empty);
        Assert.That(AssetDatabase.CreateFolder(wearRoot, _id), Is.Not.Empty);
        _definitionPath = _definitionFolder + "/definition.asset";
        _prefabPath = _wearFolder + "/Prototype.prefab";
        _definition = ScriptableObject.CreateInstance<GarmentDefinition>();
        _definition.id = _id; _definition.displayName = "Retirement fixture";
        _definition.layer = WearLayer.Wear; _definition.covers.Add(BodyPart.Torso);
        AssetDatabase.CreateAsset(_definition, _definitionPath);
        var go = new GameObject("RetirementFixture");
        try { Assert.That(PrefabUtility.SaveAsPrefabAsset(go, _prefabPath), Is.Not.Null); }
        finally { Object.DestroyImmediate(go); }
        AssetDatabase.SaveAssets();
    }

    [TearDown]
    public void RemoveOnlyThisFixturesAuthoringFolders()
    {
        foreach (var path in new[] { _wearFolder, _definitionFolder })
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
            {
                Assert.That(path.EndsWith("/" + _id, StringComparison.Ordinal), Is.True);
                Assert.That(AssetDatabase.DeleteAsset(path), Is.True, path);
            }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PeopleAndNativeCandidatesUseAuthoredRetirementInsteadOfSimulationMetadata(bool retired)
    {
        _definition.retired = retired;
        EditorUtility.SetDirty(_definition); AssetDatabase.SaveAssets();
        var row = new JObject
        {
            ["type"] = "wear", ["id"] = _id, ["main"] = _prefabPath, ["definition"] = _definitionPath,
            ["metadata"] = new JObject
            {
                ["sex"] = "Any", ["slots"] = new JArray("Chest"),
                // Deliberately contradictory: only canonical record.state governs retirement.
                ["simulation"] = new JObject { ["retired"] = !retired }
            }
        };
        var people = Candidate(Invoke("ResolvePeople", row));
        var native = Candidate(Invoke("ResolveWear", _id));
        foreach (var result in new[] { people, native })
        {
            Assert.That((string)result["state"], Is.EqualTo(retired ? "retired" : "active"));
            Assert.That((string)result["type"], Is.EqualTo("wear"));
            Assert.That((string)result["id"], Is.EqualTo(_id));
            Assert.That((string)result["metadata"]["displayName"], Is.EqualTo(_definition.displayName));
            Assert.That((string)result["variants"][0]["platform"], Is.EqualTo("WebGL"));
            Assert.That((long)result["variants"][0]["size"], Is.EqualTo(17L));
            Assert.That((string)result["variants"][0]["sha256"], Is.EqualTo(new string('a', 64)));
        }
        Assert.That((bool)people["metadata"]["simulation"]["retired"], Is.EqualTo(!retired),
            "The conflicting metadata fixture must actually reach the writer.");
    }

    [Test]
    public void NonWearBuildInputsRemainActiveByDefault()
    {
        var inputsType = _builder.GetNestedType("BuildInputs", BindingFlags.NonPublic);
        Assert.That(inputsType, Is.Not.Null);
        var inputs = Activator.CreateInstance(inputsType, nonPublic: true);
        var result = Candidate(inputs, "object");
        Assert.That((string)result["state"], Is.EqualTo("active"));
    }

    private JObject Candidate(object inputs, string type = "wear") =>
        JObject.Parse((string)Invoke("CandidateJson", type, _id, "WebGL", "unity6000-content1",
            "Build/Diagnostics/state649/payload.bundle", new string('a', 64), 17L, inputs));

    private object Invoke(string name, params object[] args)
    {
        var method = _builder.GetMethod(name, Hidden);
        Assert.That(method, Is.Not.Null, name);
        return method.Invoke(null, args);
    }
}
}
#endif
