using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HexLive.Server.Assets
{

/// <summary>§152: validated logical names; never turn an API value into a path.</summary>
public static class ContentIdentity
{
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "wear", "actor", "hair", "prosthetic", "object", "building",
        "mob", "vfx", "audio", "config",
    };

    private static readonly Regex IdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._ -]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex VariantPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex ShaPattern = new(
        "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    public static bool IsType(string? value) => value is not null && Types.Contains(value);

    public static bool IsId(string? value) => value is not null && IdPattern.IsMatch(value);

    public static bool IsVariantName(string? value) =>
        value is not null && VariantPattern.IsMatch(value);

    public static bool IsSha256(string? value) => value is not null && ShaPattern.IsMatch(value);

}

public sealed class ContentObjectRecord
{
    public string Type { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string State { get; set; } = "active";
    public Dictionary<string, JsonElement> Metadata { get; set; } = new(StringComparer.Ordinal);
    public List<ContentObjectVariant> Variants { get; set; } = new();
    public DateTimeOffset PublishedAtUtc { get; set; }
}

public sealed class ContentObjectVariant
{
    public string Platform { get; set; } = string.Empty;
    public string RuntimeProfile { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string PayloadType { get; set; } = "assetBundle";
    public string EntryAsset { get; set; } = "main";

    /// <summary>
    /// §152.2: when present, this is an entry in THIS object's payload. It is
    /// deliberately not an icon object, icon record or shared icon bundle.
    /// </summary>
    public string? IconAsset { get; set; }
    public List<ContentObjectAttachment> Attachments { get; set; } = new();
}

/// <summary>
/// An immutable sidecar owned by the same object revision. This is used for
/// raw payload companions such as a voice clip's baked viseme timeline; it is
/// not a separately versioned content object.
/// </summary>
public sealed class ContentObjectAttachment
{
    public string Name { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}

/// <summary>Administrative candidate; StagedPath is never returned by the API.</summary>
public sealed class ContentPublishCandidate
{
    public string Type { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string State { get; set; } = "active";
    public Dictionary<string, JsonElement> Metadata { get; set; } = new(StringComparer.Ordinal);
    public List<ContentPublishVariant> Variants { get; set; } = new();
}

public sealed class ContentPublishVariant
{
    public string Platform { get; set; } = string.Empty;
    public string RuntimeProfile { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string PayloadType { get; set; } = "assetBundle";
    public string EntryAsset { get; set; } = "main";
    public string? IconAsset { get; set; }
    public string StagedPath { get; set; } = string.Empty;
    public List<ContentPublishAttachment> Attachments { get; set; } = new();
}

public sealed class ContentPublishAttachment
{
    public string Name { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string StagedPath { get; set; } = string.Empty;
}

public sealed class AssetResolvedObject
{
    public string Type { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string State { get; set; } = "active";
    public Dictionary<string, JsonElement> Metadata { get; set; } = new(StringComparer.Ordinal);
    public ContentObjectVariant? Variant { get; set; }
}

public sealed class AssetIndexResponse
{
    public long RegistryRevision { get; set; }
    public bool IsDelta { get; set; }
    public List<AssetResolvedObject> Objects { get; set; } = new();

    /// <summary>
    /// §152.4: active objects that exist in the registry but have no payload
    /// this platform/profile can load. They are NOT in <see cref="Objects"/>,
    /// and dropping them silently is what makes a half-published platform look
    /// like a complete index.
    /// </summary>
    public List<AssetObjectKey> PlatformMissing { get; set; } = new();
}

/// <summary>§152.4: what one platform/profile can actually load right now.</summary>
public sealed class AssetPlatformCoverage
{
    public string Platform { get; set; } = string.Empty;
    public string RuntimeProfile { get; set; } = string.Empty;
    public int Covered { get; set; }
    public List<AssetObjectKey> Missing { get; set; } = new();
}

public sealed class AssetCoverageReport
{
    public long RegistryRevision { get; set; }
    public int ActiveObjects { get; set; }
    public int RetiredObjects { get; set; }

    /// <summary>
    /// Every platform/profile pair the registry actually publishes for. A pair
    /// absent from this list has no payload at all — the loudest possible form
    /// of "this platform was never built".
    /// </summary>
    public List<AssetPlatformCoverage> Platforms { get; set; } = new();
}

public sealed class AssetResolveRequest
{
    public string Platform { get; set; } = string.Empty;
    public string RuntimeProfile { get; set; } = string.Empty;
    public List<AssetObjectKey> Objects { get; set; } = new();
}

public sealed class AssetObjectKey
{
    public string Type { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

public sealed class AssetResolveResponse
{
    public long RegistryRevision { get; set; }
    public List<AssetResolvedObject> Objects { get; set; } = new();
    public List<AssetObjectKey> Missing { get; set; } = new();
}

public sealed class RegistryChange
{
    public long RegistryRevision { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public long ObjectRevision { get; set; }
    public string State { get; set; } = "active";
    public DateTimeOffset PublishedAtUtc { get; set; }
}

public sealed class ContentPublishResult
{
    public bool Changed { get; set; }
    public long RegistryRevision { get; set; }
    public ContentObjectRecord Record { get; set; } = new();
}

}
