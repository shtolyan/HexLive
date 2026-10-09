#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using BodyPart = HexLive.Simulation.Content.BodyPart;

namespace HexLive.UnityPresentation.Wearing.Editor
{
    // §169: authoring inventory only. Never calls BuildPipeline or a server.
    public static class PeopleCatalogPreparation
    {
        public const string Root = PeopleAssetPreparation.Root;
        public const string CatalogPath = Root + "/catalog.json";

        public static void Prepare()
        {
            var preparation = JObject.Parse(File.ReadAllText(Root + "/Validation/preparation.json"));
            var source = JObject.Parse(File.ReadAllText(Root + "/Source/source-manifest.json"));
            var wardrobe = JArray.Parse(File.ReadAllText(Root + "/Source/wardrobe-authoring.json"));
            Directory.CreateDirectory(Root + "/Definitions");
            AssetDatabase.Refresh();
            var records = new JArray();
            foreach (var prefab in preparation["prefabs"])
            {
                string kind = (string)prefab["kind"], actor = (string)prefab["actor"], id = (string)prefab["id"], main = (string)prefab["path"];
                if (kind == "actor" || kind == "hair")
                {
                    if (kind == "hair") id = id.Replace("_LOD0", "");
                    records.Add(new JObject {
                        ["type"] = kind, ["id"] = id, ["main"] = main,
                        ["metadata"] = new JObject {
                            ["peopleCatalog"] = PeopleIdMap.CatalogId,
                            ["legacyResourcePath"] = "HexLive/" + (kind == "actor" ? "Actors/" : "Hair/") + id,
                            ["colours"] = new JArray() } });
                    continue;
                }
                var item = wardrobe.FirstOrDefault(w => (string)w["name"] == id);
                string baseId = item != null ? (string)item["simId"] : "gear.backpack_primal_" + id.Replace("Primal", "").Replace("Pack", "").ToLowerInvariant();
                string artId = baseId + (actor == "Kshishtof" ? "_male" : "");
                var model = source["models"].First(m => (string)m["id"] == id && (string)m["actor"] == actor);
                var materialKeys = model["meshes"][0]["materials"].Values<string>().ToArray();
                var variantNames = source["materials"][materialKeys[0]]["variants"]?.Values<string>().ToArray() ?? new[] { "Base" };
                foreach (var variant in variantNames)
                {
                    string recordId = artId + (variant == "Base" ? "" : "_" + variant.ToLowerInvariant());
                    string definitionPath = Root + "/Definitions/" + recordId + ".asset";
                    var def = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(definitionPath);
                    if (def == null) { def = ScriptableObject.CreateInstance<GarmentDefinition>(); AssetDatabase.CreateAsset(def, definitionPath); }
                    var old = GarmentLibrary.Defaults.FirstOrDefault(g => g.Id == baseId);
                    def.id = recordId; def.prototypeId = artId;
                    def.displayName = (old?.DisplayName ?? id) + (variant == "Base" ? "" : " (" + variant + ")");
                    def.layer = old?.Layer ?? WearLayer.Bags;
                    def.warmth = old?.Warmth ?? 0f; def.armor = old?.Armor ?? 0f;
                    def.thermalDelta = old?.ThermalDelta ?? 0f;
                    def.capacity = old?.Capacity ?? 9; def.dressDurationTicks = old?.DressDurationTicks ?? 8;
                    def.covers = old?.Covers.ToList() ?? new List<BodyPart> { BodyPart.Torso };
                    def.category = old?.Category ?? GarmentCategory.Bag;
                    def.hasSexOverride = true;
                    def.sexOverride = actor == "Kshishtof" ? GarmentSex.Male : GarmentSex.Female;
                    def.variantMaterials = materialKeys.Select(key => {
                        var variants = preparation["materialVariants"][key];
                        return AssetDatabase.LoadAssetAtPath<Material>((string)(variants[variant] ?? variants["Base"]));
                    }).ToArray();
                    if (def.variantMaterials.Any(m => m == null)) throw new InvalidOperationException(recordId + ": missing variant material");
                    EditorUtility.SetDirty(def);
                    var slots = item?["slots"]?.DeepClone() ?? new JArray("Chest");
                    records.Add(new JObject {
                        ["type"] = "wear", ["id"] = recordId, ["main"] = main,
                        ["definition"] = definitionPath,
                        ["metadata"] = new JObject { ["peopleCatalog"] = PeopleIdMap.CatalogId,
                            ["artId"] = artId, ["sex"] = def.sexOverride.ToString(), ["slots"] = slots } });
                }
            }
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { Root + "/PaintMaps" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = Path.GetFileNameWithoutExtension(path);
                // Each map keeps the exact resource alias consumed by existing painters.
                const string folder = "PaintMaps";
                var aliases = new JArray();
                foreach (var body in new[] { "Marta", "Kshishtof" })
                foreach (var prefix in new[] { "skin_", "skinpos_" })
                    if (name == prefix + body)
                        foreach (var alias in Enum.GetNames(typeof(ActorName)).Where(a => PeopleIdMap.Geometry(a) == body))
                            aliases.Add("HexLive/PaintMaps/" + prefix + alias);
                records.Add(new JObject { ["type"] = "config", ["id"] = (folder + "." + name).ToLowerInvariant(), ["main"] = path,
                    ["metadata"] = new JObject { ["peopleCatalog"] = PeopleIdMap.CatalogId,
                        ["legacyResourcePath"] = "HexLive/" + folder + "/" + name,
                        ["legacyResourcePaths"] = aliases,
                        ["legacyResourceFolder"] = "HexLive/" + folder } });
            }
            AssetDatabase.SaveAssets();
            var errors = new JArray();
            var dependencies = new JObject();
            foreach (var row in records)
            {
                var roots = new[] { (string)row["main"], (string)row["definition"] }.Where(p => !string.IsNullOrEmpty(p)).ToArray();
                var deps = AssetDatabase.GetDependencies(roots, true).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                dependencies[(string)row["type"] + "/" + (string)row["id"]] = new JArray(deps);
                foreach (var path in deps)
                    if (path.StartsWith("Assets/HexLiveContent/Wear/", StringComparison.Ordinal) ||
                        path.StartsWith("Assets/HexLiveContent/RuntimeSource/Actors/", StringComparison.Ordinal) ||
                        path.StartsWith("Assets/ImportedActors/Hair/", StringComparison.Ordinal) ||
                        path.Contains("/Legacy/", StringComparison.Ordinal)) errors.Add(path);
            }
            File.WriteAllText(CatalogPath, new JObject { ["schemaVersion"] = 1, ["peopleCatalog"] = PeopleIdMap.CatalogId,
                ["gameReady"] = false, ["bundleBuildAuthorized"] = false, ["records"] = records }.ToString());
            File.WriteAllText(Root + "/Validation/catalog-dependencies.json", new JObject {
                ["passed"] = errors.Count == 0, ["errors"] = errors, ["dependencies"] = dependencies }.ToString());
            AssetDatabase.Refresh();
            if (errors.Count != 0) throw new InvalidOperationException("People catalog has legacy dependencies: " + errors);
            Debug.Log("PEOPLE_CATALOG: prepared " + records.Count + " records; no bundles built");
        }
    }
}
#endif
