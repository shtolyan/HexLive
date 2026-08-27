using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexLive.Simulation.Content;

namespace HexLive.Server.Assets
{

/// <summary>§154.1 server-owned gameplay fields inside wear metadata.</summary>
public sealed class GarmentSimulationMetadata
{
    public string DisplayName { get; set; } = string.Empty;
    public string PrototypeId { get; set; } = string.Empty;
    public string Layer { get; set; } = nameof(WearLayer.Wear);
    public string Sex { get; set; } = nameof(GarmentSex.Any);
    public float Warmth { get; set; }
    public float Armor { get; set; }
    public float ThermalDelta { get; set; }
    public int DressDurationTicks { get; set; } = 8;
    public int Capacity { get; set; }
    public List<string> Covers { get; set; } = new();

    public void Validate(string id)
    {
        if (!ContentIdentity.IsId(id))
        {
            throw new InvalidDataException("Invalid garment id.");
        }
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 160)
        {
            throw new InvalidDataException("Display name must contain 1-160 characters.");
        }
        if (!ContentIdentity.IsId(PrototypeId))
        {
            throw new InvalidDataException("Prototype id is invalid.");
        }
        if (!Enum.TryParse<WearLayer>(Layer, true, out var layer) ||
            !Enum.IsDefined(typeof(WearLayer), layer))
        {
            throw new InvalidDataException($"Unknown wear layer '{Layer}'.");
        }
        if (!Enum.TryParse<GarmentSex>(Sex, true, out var sex) ||
            !Enum.IsDefined(typeof(GarmentSex), sex))
        {
            throw new InvalidDataException($"Unknown garment sex '{Sex}'.");
        }
        if (!float.IsFinite(Warmth) || Warmth < -10f || Warmth > 10f ||
            !float.IsFinite(Armor) || Armor < 0f || Armor > 1f ||
            !float.IsFinite(ThermalDelta) || ThermalDelta < -10f || ThermalDelta > 10f)
        {
            throw new InvalidDataException(
                "Warmth/thermal must be finite and within -10..10; armor must be within 0..1.");
        }
        if (DressDurationTicks < 1 || DressDurationTicks > 100000)
        {
            throw new InvalidDataException("Dress duration must be within 1..100000 ticks.");
        }
        if (Capacity < 0 || Capacity > 1000)
        {
            throw new InvalidDataException("Capacity must be within 0..1000.");
        }

        Covers ??= new List<string>();
        var seen = new HashSet<BodyPart>();
        foreach (var cover in Covers)
        {
            if (!Enum.TryParse<BodyPart>(cover, true, out var part) ||
                !Enum.IsDefined(typeof(BodyPart), part))
            {
                throw new InvalidDataException($"Unknown covered body part '{cover}'.");
            }
            if (!seen.Add(part))
            {
                throw new InvalidDataException($"Covered body part '{part}' is duplicated.");
            }
        }
    }
}

public sealed class GarmentCatalogEntry
{
    public ContentObjectRecord Record { get; set; } = new();
    public GarmentSimulationMetadata? Simulation { get; set; }
    public string ConfigurationSource { get; set; } = "missing";
    public bool Spawnable => Record.State == "active" && Simulation is not null;
}

public sealed class AssetCatalogTypeStats
{
    public string Type { get; set; } = string.Empty;
    public int Active { get; set; }
    public int Retired { get; set; }
    public int Variants { get; set; }
    public long Bytes { get; set; }
}

public sealed class AssetCatalogOverview
{
    public long RegistryRevision { get; set; }
    public IReadOnlyList<ContentObjectRecord> Records { get; set; } = Array.Empty<ContentObjectRecord>();
    public IReadOnlyList<GarmentCatalogEntry> Wear { get; set; } = Array.Empty<GarmentCatalogEntry>();
    public IReadOnlyList<AssetCatalogTypeStats> Types { get; set; } = Array.Empty<AssetCatalogTypeStats>();
    public AssetCoverageReport Coverage { get; set; } = new();
    public int UnconfiguredWear => Wear.Count(value => value.Record.State == "active" && value.Simulation is null);
}

public sealed class AssetGarmentCatalogSnapshot
{
    public long RegistryRevision { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Json { get; set; } = string.Empty;
    public int SpawnableWear { get; set; }
    public int RetiredWear { get; set; }
    public int UnconfiguredWear { get; set; }
}

/// <summary>
/// §154 compatibility bridge. Global tuning remains in the exported SimData;
/// only its garments array is derived from atomic wear records.
/// </summary>
public sealed class AssetGarmentCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly AssetRegistryStore _store;
    private readonly string _baseSimDataPath;
    private readonly string _runtimeDirectory;

    public AssetGarmentCatalog(AssetRegistryStore store, string baseSimDataPath)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _baseSimDataPath = Path.GetFullPath(baseSimDataPath);
        _runtimeDirectory = Path.Combine(store.RootPath, "runtime");
    }

    public long RegistryRevision => _store.RegistryRevision;

    public AssetCatalogOverview ReadOverview()
    {
        var snapshot = _store.ReadSnapshot();
        var records = snapshot.Records;
        var baseGarments = ReadBaseGarments();
        var wear = records.Where(value => value.Type == "wear")
            .Select(record => BuildEntry(record, baseGarments))
            .OrderBy(value => value.Record.Id, StringComparer.Ordinal)
            .ToArray();
        var types = records.GroupBy(value => value.Type, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AssetCatalogTypeStats
            {
                Type = group.Key,
                Active = group.Count(value => value.State == "active"),
                Retired = group.Count(value => value.State == "retired"),
                Variants = group.Sum(value => value.Variants.Count),
                Bytes = group.SelectMany(value => value.Variants)
                    .Sum(value => value.Size + value.Attachments.Sum(attachment => attachment.Size)),
            }).ToArray();
        return new AssetCatalogOverview
        {
            RegistryRevision = snapshot.RegistryRevision,
            Records = records,
            Wear = wear,
            Types = types,
            Coverage = _store.AuditCoverage(snapshot),
        };
    }

    public GarmentCatalogEntry? ReadWear(string id)
    {
        var record = _store.ReadCurrentRecord("wear", id);
        return record is null ? null : BuildEntry(record, ReadBaseGarments());
    }

    public Dictionary<string, JsonElement> WithSimulationMetadata(
        ContentObjectRecord record, GarmentSimulationMetadata simulation)
    {
        if (record.Type != "wear")
        {
            throw new InvalidOperationException("Simulation garment metadata belongs only to wear records.");
        }
        simulation.Validate(record.Id);
        var metadata = record.Metadata.ToDictionary(
            pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        metadata["simulation"] = JsonSerializer.SerializeToElement(simulation, JsonOptions);
        return metadata;
    }

    public AssetGarmentCatalogSnapshot Materialize()
    {
        var snapshot = _store.ReadSnapshot("wear");
        var revision = snapshot.RegistryRevision;
        var root = ReadBaseRoot();
        var baseGarments = ReadBaseGarments(root);
        var wearRecords = snapshot.Records;

        var output = new JsonArray();
        var spawnable = 0;
        var retired = 0;
        var unconfigured = 0;
        if (wearRecords.Count == 0)
        {
            foreach (var garment in baseGarments.Values.OrderBy(
                         value => String(value, "id"), StringComparer.Ordinal))
            {
                output.Add(garment.DeepClone());
                spawnable++;
            }
        }
        else
        {
            foreach (var record in wearRecords.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                var entry = BuildEntry(record, baseGarments);
                if (entry.Simulation is null)
                {
                    if (record.State == "active") unconfigured++;
                    continue;
                }

                output.Add(ToSimData(record.Id, entry.Simulation, record.State == "retired"));
                if (record.State == "retired") retired++;
                else spawnable++;
            }
        }

        root["garments"] = output;
        var json = root.ToJsonString(JsonOptions) + Environment.NewLine;
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()[..16];
        var effectivePath = Path.Combine(
            _runtimeDirectory,
            $"simdata-{revision.ToString(CultureInfo.InvariantCulture)}-{fingerprint}.json");
        WriteAtomic(effectivePath, json);
        return new AssetGarmentCatalogSnapshot
        {
            RegistryRevision = revision,
            Path = effectivePath,
            Json = json,
            SpawnableWear = spawnable,
            RetiredWear = retired,
            UnconfiguredWear = unconfigured,
        };
    }

    private GarmentCatalogEntry BuildEntry(
        ContentObjectRecord record, IReadOnlyDictionary<string, JsonObject> baseGarments)
    {
        if (record.Metadata.TryGetValue("simulation", out var value))
        {
            try
            {
                var metadata = value.Deserialize<GarmentSimulationMetadata>(JsonOptions)
                    ?? throw new InvalidDataException("simulation metadata is empty");
                metadata.Validate(record.Id);
                return new GarmentCatalogEntry
                {
                    Record = record,
                    Simulation = metadata,
                    ConfigurationSource = "record",
                };
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return new GarmentCatalogEntry
                {
                    Record = record,
                    Simulation = null,
                    ConfigurationSource = "invalid: " + ex.Message,
                };
            }
        }

        if (baseGarments.TryGetValue(record.Id, out var fallback))
        {
            return new GarmentCatalogEntry
            {
                Record = record,
                Simulation = FromSimData(fallback),
                ConfigurationSource = "base SimData",
            };
        }

        return new GarmentCatalogEntry { Record = record };
    }

    private Dictionary<string, JsonObject> ReadBaseGarments() =>
        ReadBaseGarments(ReadBaseRoot());

    private JsonObject ReadBaseRoot()
    {
        if (!File.Exists(_baseSimDataPath))
        {
            throw new FileNotFoundException("Base SimData does not exist.", _baseSimDataPath);
        }
        return JsonNode.Parse(File.ReadAllText(_baseSimDataPath)) as JsonObject
            ?? throw new InvalidDataException("Base SimData root must be a JSON object.");
    }

    private static Dictionary<string, JsonObject> ReadBaseGarments(JsonObject root)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (root["garments"] is not JsonArray garments)
        {
            throw new InvalidDataException("Base SimData has no garments array.");
        }
        foreach (var value in garments)
        {
            if (value is not JsonObject garment)
            {
                throw new InvalidDataException("Base SimData contains a non-object garment.");
            }
            var id = String(garment, "id");
            if (!ContentIdentity.IsId(id) || !result.TryAdd(id, garment))
            {
                throw new InvalidDataException($"Base SimData garment id '{id}' is invalid or duplicated.");
            }
        }
        return result;
    }

    private static GarmentSimulationMetadata FromSimData(JsonObject value)
    {
        var id = String(value, "id");
        var result = new GarmentSimulationMetadata
        {
            DisplayName = String(value, "displayName"),
            PrototypeId = String(value, "prototypeId", id),
            Layer = String(value, "layer", nameof(WearLayer.Wear)),
            Sex = String(value, "sex", nameof(GarmentSex.Any)),
            Warmth = Float(value, "warmth"),
            Armor = Float(value, "armor"),
            ThermalDelta = Float(value, "thermalDelta"),
            DressDurationTicks = Integer(value, "dressDurationTicks", 8),
            Capacity = Integer(value, "capacity"),
            Covers = value["covers"] is JsonArray covers
                ? covers.Select(item => item?.GetValue<string>() ?? string.Empty).ToList()
                : new List<string>(),
        };
        result.Validate(id);
        return result;
    }

    private static JsonObject ToSimData(
        string id, GarmentSimulationMetadata value, bool retired)
    {
        value.Validate(id);
        var covers = new JsonArray();
        foreach (var cover in value.Covers) covers.Add(cover);
        var result = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = value.DisplayName,
            ["layer"] = value.Layer,
            ["sex"] = value.Sex,
            ["warmth"] = value.Warmth,
            ["armor"] = value.Armor,
            ["thermalDelta"] = value.ThermalDelta,
            ["dressDurationTicks"] = value.DressDurationTicks,
            ["capacity"] = value.Capacity,
            ["covers"] = covers,
        };
        if (!string.Equals(value.PrototypeId, id, StringComparison.Ordinal))
        {
            result["prototypeId"] = value.PrototypeId;
        }
        if (retired) result["retired"] = true;
        return result;
    }

    private static string String(JsonObject value, string key, string fallback = "") =>
        value[key]?.GetValue<string>() ?? fallback;

    private static float Float(JsonObject value, string key, float fallback = 0f) =>
        value[key] is null ? fallback : value[key]!.GetValue<float>();

    private static int Integer(JsonObject value, string key, int fallback = 0) =>
        value[key] is null ? fallback : value[key]!.GetValue<int>();

    private static void WriteAtomic(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (!string.Equals(File.ReadAllText(path), value, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Effective SimData for one registry revision changed at '{path}'.");
            }
            return;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            using (var stream = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

}
