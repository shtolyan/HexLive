using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using EntityId = HexLive.Simulation.Common.EntityId;

namespace HexLive.Tests
{

/// <summary>
/// #351 / §31C.2: exact legacy local save, restored onto its canonical Feud
/// topology by the genuine historical reader/exporter named in the manifest.
/// Its public snapshot DTO is imported by name into the current renderer; this
/// does NOT claim the current save reader supports blob64. Controlled cases
/// were separately restored and entered via historical Stop/BedSleep helpers.
/// No SaveGame selection, runner, stepping, autosave or source-file writes.
/// </summary>
public sealed class SavedHutPresentationRuntimeTests
{
    private const string SaveHash = "d403257bd7965bc172199fdf45f034e7e843fdc2397de58bfd095bd6ee74890f";
    private static readonly int Sleep = Animator.StringToHash("Sleep");
    private static readonly int FallenIdle = Animator.StringToHash("FallenIdle");

    [UnitySetUp]
    public IEnumerator RequireContent() =>
        new CorpsePresentationRuntimeTests().RequireTheSingaporeRuntimeRegistry();

    [UnityTest, Explicit("Requires -hexlive-bug351-save with the verified legacy world.dat")]
    public IEnumerator SavedSleeperAndControlledSleeperUseBothActualHutBeds()
    {
        var directory = Argument("-hexlive-bug351-snapshots");
        var manifest = JsonConvert.DeserializeObject<LegacyManifest>(
            File.ReadAllText(Path.Combine(directory, "manifest.json")));
        Assert.That(manifest, Is.Not.Null);
        Assert.That(manifest.sourceSaveSha256, Is.EqualTo(SaveHash));
        Assert.That(manifest.serializerCommit, Is.EqualTo("2236e4a97a72f125d4cb889a398558666fc533d3"));
        Assert.That(manifest.seed, Is.EqualTo(-12054716));
        Assert.That(manifest.tick, Is.EqualTo(5812));
        Assert.That(manifest.cases, Has.Length.EqualTo(3));
        Assert.That(manifest.cases.Count(c => !c.controlled && c.npcId == 2), Is.EqualTo(1));
        Assert.That(manifest.cases.Count(c => c.controlled && c.npcId == 1), Is.EqualTo(2));
        HashSet<int> expectedBedIds = null;
        var controlledBeds = new HashSet<int>();
        foreach (var scenario in manifest.cases.OrderBy(c => c.controlled))
        {
            using var fixture = new Fixture(directory, scenario);
            var beds = fixture.Snapshot.Objects.Where(o => o.DefinitionId == ContentIds.BedBasic)
                .Where(o => fixture.Snapshot.Objects.Any(h => h.DefinitionId == ContentIds.HutPlan && h.Tile == o.Tile))
                .OrderBy(o => o.Id.Value).ToArray();
            Assert.That(beds, Has.Length.EqualTo(2), "The historical two-bed hut must be present.");
            expectedBedIds ??= new HashSet<int>(beds.Select(b => b.Id.Value));
            Assert.That(expectedBedIds.SetEquals(beds.Select(b => b.Id.Value)), Is.True);
            var sleeper = fixture.Snapshot.Npcs.Single(n => n.Id.Value == scenario.npcId);
            Assert.That(sleeper.CurrentInteraction, Is.EqualTo("Sleep"));
            Assert.That(sleeper.TargetObjectId, Is.EqualTo(scenario.bedId));
            var occupied = beds.Single(b => b.Id.Value == scenario.bedId);
            if (scenario.controlled) Assert.That(controlledBeds.Add(scenario.bedId), Is.True);
            var plan = fixture.Snapshot.Objects.Single(h => h.DefinitionId == ContentIds.HutPlan && h.Tile == occupied.Tile);
            Assert.That(plan.Id.Value, Is.EqualTo(1030));
            Assert.That(plan.BuildingBlueprintJson, Is.Not.Empty);
            var modules = fixture.Snapshot.Objects.Where(o => o.ArchitectureOwnerObjectId == plan.Id.Value).ToArray();
            Assert.That(modules, Has.Length.EqualTo(36), "Keep the real saved modular hut, not a replacement monolith.");
            yield return fixture.Ready(scenario.npcId, beds.Select(b => b.Id.Value), modules.Select(o => o.Id.Value));
            yield return CheckSleeper(fixture, scenario.npcId, occupied, scenario.name);
            Assert.That(fixture.Snapshot.Tick, Is.EqualTo(5812));
            fixture.AssertSourceUnchanged();
            // Destruction must complete before the next historical snapshot
            // registers the same NPC ids in production's live-view registry.
            fixture.Dispose();
            yield return null;
        }
        Assert.That(controlledBeds.SetEquals(expectedBedIds), Is.True);
    }

    private static string Argument(string key)
    {
        var args = System.Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, key);
        Assert.That(index >= 0 && index + 1 < args.Length, Is.True, "Explicit argument required: " + key);
        return args[index + 1];
    }

    private sealed class LegacyManifest
    {
        public string sourceSaveSha256;
        public string serializerCommit;
        public int seed;
        public int tick;
        public LegacyCase[] cases;
    }

    private sealed class LegacyCase
    {
        public string name;
        public int npcId;
        public int bedId;
        public string file;
        public string sha256;
        public bool controlled;
    }

    // JSON must not zero the immutable identifiers/coordinates by invoking a
    // struct's implicit default constructor and ignoring its get-only members.
    private sealed class PrimitiveConverter : JsonConverter
    {
        public override bool CanWrite => false;
        public override bool CanConvert(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t == typeof(EntityId) || t == typeof(ObjectId) || t == typeof(JunctionId) ||
                t == typeof(FragmentId) || t == typeof(TileCoord) || t == typeof(Float2);
        }
        public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            type = Nullable.GetUnderlyingType(type) ?? type;
            var value = JObject.Load(reader);
            if (type == typeof(TileCoord)) return new TileCoord((int)value["Q"], (int)value["R"]);
            if (type == typeof(Float2)) return new Float2((float)value["X"], (float)value["Y"]);
            var id = (int)value["Value"];
            if (type == typeof(EntityId)) return new EntityId(id);
            if (type == typeof(ObjectId)) return new ObjectId(id);
            if (type == typeof(FragmentId)) return new FragmentId(id);
            return new JunctionId(id);
        }
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) =>
            throw new NotSupportedException("Read-only historical snapshot adapter.");
    }

    private static IEnumerator CheckSleeper(Fixture fixture, int npcId, ObjectSnapshot bed, string label)
    {
        var bedView = fixture.Views[bed.Id.Value];
        Assert.That(bedView.name, Does.Contain("Integrated bed.basic"), "Must use the production hut furniture path.");
        var point = bedView.GetComponentsInChildren<Transform>(true).Single(t => t.name == "point");
        var hut = fixture.Snapshot.Objects.Single(h => h.DefinitionId == ContentIds.HutPlan && h.Tile == bed.Tile);
        var anchor = fixture.Snapshot.Junctions.Single(j => j.Id == bed.Junctions[0]).WorldPosition;
        // This save owns a modular hut_plan. Its bed uses the saved junction
        // plus the canonical footprint centroid, not the monolithic hut offset.
        var expected = anchor + BlueprintFurnitureFootprints.CentroidOffset(
            bed.DefinitionId, Mathf.RoundToInt(bed.RotationDegrees / 60f));
        Assert.That(Vector2.Distance(new Vector2(bedView.transform.position.x, bedView.transform.position.z),
            new Vector2(expected.X, expected.Y)), Is.LessThan(0.005f), "Saved plan anchor and authored footprint centroid must reach the rendered root.");
        Assert.That(bedView.transform.position.y, Is.EqualTo(fixture.Renderer.GroundTopY(bed.Tile) +
            HutFurnitureFactory.BedRootLift).Within(0.005f));
        var expectedYaw = Quaternion.Euler(0f, SimulationUnityMapper.ToUnityFootprintYawDegrees(bed.RotationDegrees), 0f);
        Assert.That(Quaternion.Angle(bedView.transform.rotation, expectedYaw), Is.LessThan(0.01f));
        Assert.That(fixture.Renderer.TryGetActorView(npcId, out var actor), Is.True);
        var animator = actor.GetComponentInChildren<Animator>(true);
        Assert.That(animator, Is.Not.Null);
        var savedNpc = fixture.Snapshot.Npcs.Single(n => n.Id.Value == npcId);
        // The actual saved NPC2 is unconscious while assigned to a bed.
        // SyncActorView selects the fallen chain, and attaches that chain to
        // the bed only for Sleep/InProgress with a non-null target (true here).
        var expectedState = savedNpc.IsDying || savedNpc.IsUnconscious || savedNpc.IsPlayingDead
            ? FallenIdle : Sleep;
        TestContext.WriteLine($"{label}: unconscious={savedNpc.IsUnconscious}, dying={savedNpc.IsDying}, " +
            $"fainted={savedNpc.IsFainted}, interaction={savedNpc.CurrentInteraction}, expectedState={expectedState}");
        var deadline = Time.realtimeSinceStartup + 15f;
        var minimum = Time.realtimeSinceStartup + 2f;
        do { fixture.Sync(); yield return null; }
        while ((Time.realtimeSinceStartup < minimum || animator.IsInTransition(0) ||
            animator.GetCurrentAnimatorStateInfo(0).shortNameHash != expectedState) && Time.realtimeSinceStartup < deadline);
        Assert.That(animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(expectedState));
        var mattress = BedSleepPoseRuntimeTests.MattressBounds(bedView, point);
        foreach (var phase in new[] { 0f, 0.25f, 0.5f, 0.75f })
        {
            animator.Play(expectedState, 0, phase);
            animator.Update(0f);
            animator.speed = 0f;
            yield return null;
            yield return new WaitForEndOfFrame();
            var head = animator.GetBoneTransform(HumanBodyBones.Head).position;
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips).position;
            TestContext.WriteLine($"{label} phase={phase:F2} hutYaw={hut.RotationDegrees} bedYaw={bed.RotationDegrees} " +
                $"root={bedView.transform.position:F4} point={point.position:F4} head={head:F4} hips={hips:F4}");
            Assert.That(Vector2.Distance(new Vector2(actor.transform.position.x, actor.transform.position.z),
                new Vector2(savedNpc.Position.X, savedNpc.Position.Y)), Is.LessThan(0.01f),
                "Actor root retains the canonical snapshot position, including its route anchor.");
            Assert.That(Vector3.Distance(animator.transform.position, point.position), Is.LessThan(0.01f),
                "SyncActorView pins the animated body root to the actual bed point/surface.");
            Assert.That(Quaternion.Angle(animator.transform.rotation, point.rotation), Is.LessThan(0.01f),
                "The real bed attachment owns the animated body orientation.");
            BedSleepPoseRuntimeTests.MeasureSupportedBody(animator, point, out var headBounds, out var torsoBounds);
            fixture.Capture(label + "-phase" + Mathf.RoundToInt(phase * 100));
            Assert.That(BedSleepPoseRuntimeTests.Supported(headBounds, mattress), Is.True,
                $"Head leaves the saved bed: {BedSleepPoseRuntimeTests.SupportFailure(headBounds, mattress)}; {headBounds} mattress={mattress}");
            Assert.That(BedSleepPoseRuntimeTests.Supported(torsoBounds, mattress), Is.True,
                $"Torso leaves the saved bed: {BedSleepPoseRuntimeTests.SupportFailure(torsoBounds, mattress)}; {torsoBounds} mattress={mattress}");
        }
        animator.speed = 1f;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _sourcePath;
        private readonly GameObject _root = new("Saved hut production renderer");
        private readonly int[] _selection = NpcSelection.SelectedIds.ToArray();
        private bool _disposed;
        public readonly HexWorldRenderer Renderer;
        public readonly Camera Camera;
        public WorldSnapshot Snapshot;
        public Dictionary<int, GameObject> Views => (Dictionary<int, GameObject>)Field("_objectViews").GetValue(Renderer);

        public Fixture(string directory, LegacyCase scenario)
        {
            _sourcePath = Argument("-hexlive-bug351-save");
            var bytes = File.ReadAllBytes(_sourcePath);
            Assert.That(Hash(bytes), Is.EqualTo(SaveHash));
            using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
            Assert.That(reader.ReadInt32(), Is.EqualTo(0x48584C56));
            Assert.That(reader.ReadInt32(), Is.EqualTo(3));
            Assert.That(reader.ReadInt32(), Is.EqualTo(-12054716));
            Assert.That(reader.ReadInt32(), Is.EqualTo(5812));
            reader.ReadInt64(); reader.ReadSingle();
            Assert.That(reader.ReadInt32(), Is.EqualTo((int)GameMode.Feud));
            Assert.That(reader.ReadInt32(), Is.EqualTo(64));
            Assert.That(Path.GetFileName(scenario.file), Is.EqualTo(scenario.file));
            var jsonBytes = File.ReadAllBytes(Path.Combine(directory, scenario.file));
            Assert.That(Hash(jsonBytes), Is.EqualTo(scenario.sha256));
            Snapshot = JsonConvert.DeserializeObject<WorldSnapshot>(System.Text.Encoding.UTF8.GetString(jsonBytes),
                new JsonSerializerSettings { Converters = { new PrimitiveConverter() } });
            Assert.That(Snapshot, Is.Not.Null);
            Assert.That(Snapshot.Seed, Is.EqualTo(-12054716));
            Assert.That(Snapshot.Tick, Is.EqualTo(5812));
            Assert.That(Snapshot.Tiles.Count, Is.GreaterThan(0));
            Assert.That(Snapshot.Junctions.Count, Is.GreaterThan(0));
            Assert.That(Snapshot.Npcs.Any(n => n.Id.Value == 1), Is.True);
            Assert.That(Snapshot.Npcs.Any(n => n.Id.Value == 2), Is.True);
            TestContext.WriteLine($"Legacy snapshot {scenario.name}: source={SaveHash}, json={scenario.sha256}, " +
                $"reader=2236e4a97a72f125d4cb889a398558666fc533d3, controlled={scenario.controlled}");
            Renderer = _root.AddComponent<HexWorldRenderer>();
            Renderer.enabled = false;
            Call("EnsureRoots");
            var cameraObject = new GameObject("Saved hut camera");
            cameraObject.transform.SetParent(_root.transform, false);
            Camera = cameraObject.AddComponent<Camera>();
            Camera.enabled = false;
            Camera.orthographic = true;
            Camera.orthographicSize = 2.2f;
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = new Color(0.15f, 0.17f, 0.2f);
            Field("_cutawayCamera").SetValue(Renderer, Camera);
            var light = new GameObject("Saved hut evidence light").AddComponent<Light>();
            light.transform.SetParent(_root.transform, false);
            light.type = LightType.Directional; light.intensity = 1.2f;
            light.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
        }

        public void Sync()
        {
            Field("_lastSnapshot").SetValue(Renderer, Snapshot);
            Call("RenderSnapshot", Snapshot);
            Call("InterpolateMovables", 1f);
            Call("UpdateHutCutaways", Snapshot);
        }
        public IEnumerator Ready(int npcId, IEnumerable<int> objectIds, IEnumerable<int> moduleIds)
        {
            NpcSelection.Replace(npcId, requestFrame: false);
            var npc = Snapshot.Npcs.Single(n => n.Id.Value == npcId);
            var centre = HexSpatialMath.TileToWorld(npc.Tile);
            Camera.transform.position = new Vector3(centre.X, 8f, centre.Y - 0.001f);
            Camera.transform.LookAt(new Vector3(centre.X, 0f, centre.Y), Vector3.forward);
            var ids = objectIds.ToArray();
            var modules = moduleIds.ToArray();
            var deadline = Time.realtimeSinceStartup + 120f;
            var ready = false;
            do
            {
                Sync();
                ready = ids.All(id => Views.ContainsKey(id)) && modules.All(id =>
                    Views.TryGetValue(id, out var view) && view.GetComponentsInChildren<MeshFilter>(true)
                        .Any(mesh => mesh.sharedMesh != null && mesh.sharedMesh.vertexCount > 0)) &&
                    Renderer.TryGetActorView(npcId, out var actor) &&
                    actor.IsCorpsePresentationReady(npc.WornItems, npc.SeveredParts, npc.BodyPartConditions);
                if (!ready) yield return null;
            } while (!ready && Time.realtimeSinceStartup < deadline);
            Assert.That(ready, Is.True, "Production saved-hut content/appearance did not become ready.");
            yield return null;
            yield return new WaitForEndOfFrame();
        }
        public void Capture(string label)
        {
            var target = new RenderTexture(800, 800, 24);
            var pixels = new Texture2D(800, 800, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                target.Create();
                Assert.That(GraphicsSettings.currentRenderPipeline, Is.Not.Null);
                RenderPipeline.SubmitRenderRequest(Camera, new RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 800, 800), 0, 0); pixels.Apply();
                var sampled = pixels.GetPixels32(); var bg = sampled[0];
                var nonBackground = sampled.Count(p => Math.Abs(p.r - bg.r) + Math.Abs(p.g - bg.g) + Math.Abs(p.b - bg.b) > 24);
                var directory = Path.Combine(Path.GetTempPath(), "hexlive-bug351-saved-hut");
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, label + ".png");
                File.WriteAllBytes(file, pixels.EncodeToPNG());
                TestContext.WriteLine("Saved hut rendered frame: " + file);
                Assert.That(nonBackground, Is.GreaterThan(1000), "Empty readback is not whole-hut evidence.");
            }
            finally { RenderTexture.active = previous; Object.Destroy(target); Object.Destroy(pixels); }
        }
        public void AssertSourceUnchanged() => Assert.That(Hash(File.ReadAllBytes(_sourcePath)), Is.EqualTo(SaveHash));
        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static FieldInfo Field(string name) => typeof(HexWorldRenderer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name);
        private object Call(string name, params object[] args) => typeof(HexWorldRenderer)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Renderer, args);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NpcSelection.ReplaceMany(_selection, requestFrame: false);
            Object.Destroy(_root);
            AssertSourceUnchanged();
        }
    }
}
}
