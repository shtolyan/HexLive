using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HexLive.UnityPresentation.Content
{

[Serializable]
public sealed class ContentVariant
{
    public string platform = string.Empty;
    public string runtimeProfile = string.Empty;
    public string sha256 = string.Empty;
    public long size;
    public string payloadType = "assetBundle";
    public string entryAsset = "main";
    public string iconAsset;
    public List<ContentAttachment> attachments = new();
}

[Serializable]
public sealed class ContentAttachment
{
    public string name = string.Empty;
    public string sha256 = string.Empty;
    public long size;
}

[Serializable]
public sealed class ContentRecord
{
    public string type = string.Empty;
    public string id = string.Empty;
    public long revision;
    public string state = "active";
    public JObject metadata = new();
    public ContentVariant variant;

    public string Key => type + "/" + id;
    public bool IsActive => state == "active" && variant != null;
}

[Serializable]
internal sealed class ContentIndexResponse
{
    public long registryRevision;
    public bool isDelta;
    public List<ContentRecord> objects = new();
}

[Serializable]
internal sealed class ContentRegistryState
{
    public long registryRevision;
    public string etag = string.Empty;
    public List<ContentRecord> knownRecords = new();
}

[Serializable]
public sealed class ContentObjectKey
{
    public string type = string.Empty;
    public string id = string.Empty;
}

[Serializable]
internal sealed class ContentResolveRequest
{
    public string platform = string.Empty;
    public string runtimeProfile = string.Empty;
    public List<ContentObjectKey> objects = new();
}

[Serializable]
internal sealed class ContentResolveResponse
{
    public long registryRevision;
    public List<ContentRecord> objects = new();
    public List<ContentObjectKey> missing = new();
}

/// <summary>A loaded entry and the reference that pins its owning bundle.</summary>
public sealed class ContentAssetHandle<T> : IDisposable where T : UnityEngine.Object
{
    private Action _release;

    internal ContentAssetHandle(T asset, Action release)
    {
        Asset = asset;
        _release = release;
    }

    public T Asset { get; }

    public void Dispose()
    {
        var release = _release;
        _release = null;
        release?.Invoke();
    }
}

}
