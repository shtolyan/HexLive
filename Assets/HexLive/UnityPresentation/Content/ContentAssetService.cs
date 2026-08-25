using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace HexLive.UnityPresentation.Content
{

/// <summary>
/// §152 single runtime door: live registry → verified SHA cache → AssetBundle.
/// No Addressables locator, catalog, release id or Player-adjacent content.
/// </summary>
public sealed class ContentAssetService
{
    public const string RuntimeProfile = "unity6000-content1";
    public const long DefaultCacheLimitBytes = 12L * 1024L * 1024L * 1024L;

    [Serializable]
    private sealed class LocalRecordFile
    {
        public ContentRecord current;
        public ContentRecord previous;
    }

    private sealed class BundleState
    {
        public AssetBundle Bundle;
        public bool Loading;
        public int References;
        public readonly List<Action<AssetBundle>> Waiters = new();
    }

    private sealed class BlobState
    {
        public readonly List<Action<bool>> Waiters = new();
    }

    private static ContentAssetService _instance;

    private readonly Dictionary<string, ContentRecord> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentRecord> _pinned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentRecord> _verified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentRecord> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BundleState> _bundles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlobState> _blobRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifiedHashes = new(StringComparer.Ordinal);
    private readonly List<Action> _registryWaiters = new();

    private readonly string _root;
    private readonly string _blobs;
    private readonly string _records;
    private readonly string _partial;
    private readonly string _registryStatePath;
    private ContentRegistryState _state = new();
    private bool _refreshStarted;
    private bool _registryReady;

    private ContentAssetService()
    {
        _root = Path.Combine(Application.persistentDataPath, "HexLiveAssetCache");
        _blobs = Path.Combine(_root, "blobs");
        _records = Path.Combine(_root, "records");
        _partial = Path.Combine(_root, "partial");
        _registryStatePath = Path.Combine(_root, "registry-state.json");
        Directory.CreateDirectory(_blobs);
        Directory.CreateDirectory(_records);
        Directory.CreateDirectory(_partial);
        LoadLocalState();
    }

    public static ContentAssetService Instance => _instance ??= new ContentAssetService();

    public bool RegistryReady => _registryReady;
    public event Action RegistryRefreshed;
    public string Status { get; private set; } = string.Empty;
    public string LastError { get; private set; } = string.Empty;
    public ulong DownloadedBytes { get; private set; }
    public long DownloadTotalBytes { get; private set; }

    public IReadOnlyList<ContentRecord> Records(string type) => _pinned.Values
        .Where(value => value.type == type && value.state == "active")
        .OrderBy(value => value.id, StringComparer.Ordinal)
        .ToArray();

    public bool TryResolveLegacyPath(string path, out ContentRecord record)
    {
        record = _pinned.Values.FirstOrDefault(value => value.IsActive &&
            string.Equals(
                (string)value.metadata?["legacyResourcePath"],
                path,
                StringComparison.Ordinal));
        return record != null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        if (_instance != null)
        {
            foreach (var bundle in _instance._bundles.Values)
            {
                bundle.Bundle?.Unload(true);
            }
        }

        _instance = null;
    }

    public void RefreshRegistry()
    {
        if (_refreshStarted)
        {
            return;
        }

        _refreshStarted = true;
        ContentQueue.Begin(ContentQueue.Kind.Registry);
        ContentCoroutines.Run(RefreshRegistryRoutine());
    }

    public bool TryGetRecord(string type, string id, out ContentRecord record)
    {
        return _pinned.TryGetValue(Key(type, id), out record) && record.IsActive;
    }

    public void LoadMain<T>(string type, string id, Action<ContentAssetHandle<T>> completed)
        where T : UnityEngine.Object => LoadAsset(type, id, null, completed);

    public void LoadIcon(string type, string id, Action<ContentAssetHandle<Sprite>> completed) =>
        LoadAsset(type, id, "icon", completed);

    public void LoadAsset<T>(
        string type, string id, string entry, Action<ContentAssetHandle<T>> completed)
        where T : UnityEngine.Object
    {
        if (completed == null)
        {
            return;
        }

        void Start()
        {
            if (!_pinned.TryGetValue(Key(type, id), out var record) || !record.IsActive ||
                record.variant.payloadType != "assetBundle")
            {
                completed(null);
                return;
            }

            var assetEntry = entry ?? record.variant.entryAsset ?? "main";
            if (assetEntry == "icon" && record.variant.iconAsset != "icon")
            {
                completed(null);
                return;
            }

            LoadBundleWithFallback(record, (bundle, loadedSha) =>
            {
                if (bundle == null)
                {
                    completed(null);
                    return;
                }

                var request = bundle.LoadAssetAsync<T>(assetEntry);
                request.completed += _ =>
                {
                    var asset = request.asset as T;
                    if (asset == null)
                    {
                        completed(null);
                        return;
                    }

                    Retain(loadedSha);
                    completed(new ContentAssetHandle<T>(
                        asset, () => Release(loadedSha)));
                };
            });
        }

        if (_registryReady)
        {
            Start();
            return;
        }

        _registryWaiters.Add(Start);
        RefreshRegistry();
    }

    public void LoadAllAssets<T>(
        string type, string id, Action<IReadOnlyList<ContentAssetHandle<T>>> completed)
        where T : UnityEngine.Object
    {
        void Start()
        {
            if (!_pinned.TryGetValue(Key(type, id), out var record) || !record.IsActive ||
                record.variant.payloadType != "assetBundle")
            {
                completed?.Invoke(Array.Empty<ContentAssetHandle<T>>());
                return;
            }

            LoadBundleWithFallback(record, (bundle, loadedSha) =>
            {
                if (bundle == null)
                {
                    completed?.Invoke(Array.Empty<ContentAssetHandle<T>>());
                    return;
                }

                var request = bundle.LoadAllAssetsAsync<T>();
                request.completed += _ =>
                {
                    var handles = request.allAssets.OfType<T>()
                        .Select(asset =>
                        {
                            Retain(loadedSha);
                            return new ContentAssetHandle<T>(
                                asset, () => Release(loadedSha));
                        })
                        .ToArray();
                    completed?.Invoke(handles);
                };
            });
        }

        if (_registryReady)
        {
            Start();
        }
        else
        {
            _registryWaiters.Add(Start);
            RefreshRegistry();
        }
    }

    public void GetRawFile(string type, string id, Action<string> completed)
    {
        void Start()
        {
            if (!_pinned.TryGetValue(Key(type, id), out var record) || !record.IsActive ||
                record.variant.payloadType != "file")
            {
                completed?.Invoke(null);
                return;
            }

            EnsureRecordBlob(record, success =>
            {
                if (success)
                {
                    PersistVerifiedRecord(record);
                    completed?.Invoke(BlobPath(record.variant.sha256));
                    return;
                }

                var fallback = VerifiedFallback(record.Key, record.variant.sha256);
                completed?.Invoke(fallback == null ? null : BlobPath(fallback.variant.sha256));
            });
        }

        if (_registryReady)
        {
            Start();
        }
        else
        {
            _registryWaiters.Add(Start);
            RefreshRegistry();
        }
    }

    /// <summary>
    /// Downloads a raw payload and one sidecar from the same pinned object
    /// revision. The local record advances only when both hashes verify, so a
    /// voice WAV and its viseme timeline can never be mixed across revisions.
    /// </summary>
    public void GetRawFileWithAttachment(
        string type, string id, string attachmentName, Action<string, string> completed)
    {
        void Start()
        {
            if (!_pinned.TryGetValue(Key(type, id), out var record) || !record.IsActive ||
                record.variant.payloadType != "file" ||
                !TryAttachment(record, attachmentName, out var attachment))
            {
                completed?.Invoke(null, null);
                return;
            }

            var primaryDone = false;
            var attachmentDone = false;
            var primaryOk = false;
            var attachmentOk = false;
            void FinishWhenReady()
            {
                if (!primaryDone || !attachmentDone)
                {
                    return;
                }

                if (primaryOk && attachmentOk)
                {
                    PersistVerifiedRecord(record);
                    completed?.Invoke(
                        BlobPath(record.variant.sha256), BlobPath(attachment.sha256));
                    return;
                }

                var fallback = VerifiedFallbackWithAttachment(
                    record.Key, record.variant.sha256, attachmentName);
                if (fallback == null || !TryAttachment(fallback, attachmentName, out var oldAttachment))
                {
                    completed?.Invoke(null, null);
                    return;
                }

                completed?.Invoke(
                    BlobPath(fallback.variant.sha256), BlobPath(oldAttachment.sha256));
            }

            EnsureRecordBlob(record, success =>
            {
                primaryOk = success;
                primaryDone = true;
                FinishWhenReady();
            });
            EnsureBlob(record, attachment.sha256, attachment.size,
                attachmentName, success =>
                {
                    attachmentOk = success;
                    attachmentDone = true;
                    FinishWhenReady();
                });
        }

        if (_registryReady)
        {
            Start();
        }
        else
        {
            _registryWaiters.Add(Start);
            RefreshRegistry();
        }
    }

    /// <summary>Resolve the exact current-world working set in one request.</summary>
    public void Resolve(
        IEnumerable<ContentObjectKey> identities,
        Action<IReadOnlyList<ContentObjectKey>> completed = null)
    {
        var unique = identities?
            .Where(value => value != null && !string.IsNullOrWhiteSpace(value.type) &&
                            !string.IsNullOrWhiteSpace(value.id))
            .GroupBy(value => Key(value.type, value.id), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(2048)
            .ToList() ?? new List<ContentObjectKey>();

        void Start() => ContentCoroutines.Run(ResolveRoutine(unique, completed));
        if (_registryReady)
        {
            Start();
        }
        else
        {
            _registryWaiters.Add(Start);
            RefreshRegistry();
        }
    }

    private IEnumerator ResolveRoutine(
        List<ContentObjectKey> identities,
        Action<IReadOnlyList<ContentObjectKey>> completed)
    {
        if (identities.Count == 0)
        {
            completed?.Invoke(Array.Empty<ContentObjectKey>());
            yield break;
        }

        var payload = new ContentResolveRequest
        {
            platform = PlatformName(),
            runtimeProfile = RuntimeProfile,
            objects = identities,
        };
        using var request = new UnityWebRequest(
            ContentEndpoint.Current + "/resolve", UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(
            System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.timeout = 15;
        ContentQueue.Begin(ContentQueue.Kind.Registry);
        yield return request.SendWebRequest();
        ContentQueue.End(ContentQueue.Kind.Registry);

        if (request.result != UnityWebRequest.Result.Success)
        {
            LastError = $"Resolve контента недоступен: {request.error}";
            Debug.LogWarning("[AtomicContent] " + LastError);
            completed?.Invoke(identities);
            yield break;
        }

        try
        {
            var response = JsonConvert.DeserializeObject<ContentResolveResponse>(
                request.downloadHandler.text) ?? new ContentResolveResponse();
            completed?.Invoke(response.missing ?? new List<ContentObjectKey>());
        }
        catch (Exception exception)
        {
            LastError = $"Resolve контента повреждён: {exception.Message}";
            Debug.LogError("[AtomicContent] " + LastError);
            completed?.Invoke(identities);
        }
    }

    private IEnumerator RefreshRegistryRoutine()
    {
        Status = "Получаем реестр контента";
        var platform = PlatformName();
        var url = $"{ContentEndpoint.Current}/index/{platform}/{RuntimeProfile}" +
                  (_state.registryRevision > 0 ? $"?after={_state.registryRevision}" : string.Empty);
        using var request = UnityWebRequest.Get(url);
        request.timeout = 10;
        if (!string.IsNullOrEmpty(_state.etag))
        {
            request.SetRequestHeader("If-None-Match", _state.etag);
        }

        yield return request.SendWebRequest();
        if (request.responseCode == 304)
        {
            PinKnownRecords();
            CompleteRegistry();
            yield break;
        }

        if (request.result != UnityWebRequest.Result.Success)
        {
            LastError = $"Реестр недоступен: {request.error}";
            Debug.LogWarning($"[AtomicContent] {LastError}; используем проверенный локальный кэш.");
            PinOfflineRecords();
            CompleteRegistry();
            yield break;
        }

        ContentIndexResponse response;
        try
        {
            response = JsonConvert.DeserializeObject<ContentIndexResponse>(request.downloadHandler.text);
            if (response == null || response.objects == null)
            {
                throw new InvalidDataException("empty registry response");
            }
        }
        catch (Exception exception)
        {
            LastError = $"Реестр повреждён: {exception.Message}";
            Debug.LogError($"[AtomicContent] {LastError}; используем локальный кэш.");
            PinOfflineRecords();
            CompleteRegistry();
            yield break;
        }

        if (!response.isDelta)
        {
            _known.Clear();
        }
        foreach (var record in response.objects)
        {
            if (ValidRecord(record))
            {
                _known[record.Key] = record;
            }
        }

        _state.registryRevision = response.registryRevision;
        _state.etag = request.GetResponseHeader("ETag") ?? string.Empty;
        _state.knownRecords = _known.Values
            .OrderBy(value => value.Key, StringComparer.Ordinal).ToList();
        WriteAtomic(_registryStatePath, JsonConvert.SerializeObject(_state, Formatting.Indented));
        PinKnownRecords();
        CompleteRegistry();
        UpdatePreviouslyCachedInBackground();
    }

    private void CompleteRegistry()
    {
        _registryReady = true;
        Status = LastError.Length == 0 ? "Реестр контента готов" : LastError;
        ContentQueue.End(ContentQueue.Kind.Registry);
        var callbacks = _registryWaiters.ToArray();
        _registryWaiters.Clear();
        foreach (var callback in callbacks)
        {
            callback();
        }
        RegistryRefreshed?.Invoke();
    }

    private void PinKnownRecords()
    {
        _pinned.Clear();
        foreach (var pair in _known)
        {
            _pinned[pair.Key] = pair.Value;
        }
    }

    private void PinOfflineRecords()
    {
        _pinned.Clear();
        foreach (var pair in _known)
        {
            if (pair.Value.state == "retired")
            {
                _pinned[pair.Key] = pair.Value;
                continue;
            }

            if (_verified.TryGetValue(pair.Key, out var verified))
            {
                _pinned[pair.Key] = verified;
            }
        }

        // A cache created before registry-state existed still remains usable.
        if (_pinned.Count != 0 || _known.Count != 0)
        {
            return;
        }
        foreach (var pair in _verified)
        {
            _pinned[pair.Key] = pair.Value;
        }
    }

    private void UpdatePreviouslyCachedInBackground()
    {
        foreach (var pair in _verified.ToArray())
        {
            if (!_known.TryGetValue(pair.Key, out var target) || !target.IsActive ||
                target.revision <= pair.Value.revision || target.variant.sha256 == pair.Value.variant.sha256)
            {
                continue;
            }

            EnsureRecordBlob(target, success =>
            {
                if (success)
                {
                    PersistVerifiedRecord(target);
                }
            });
        }
    }

    private void LoadBundleWithFallback(
        ContentRecord requested, Action<AssetBundle, string> completed)
    {
        EnsureRecordBlob(requested, success =>
        {
            var selected = requested;
            if (success)
            {
                PersistVerifiedRecord(requested);
            }
            else
            {
                selected = VerifiedFallback(requested.Key, requested.variant.sha256);
                if (selected == null)
                {
                    completed(null, null);
                    return;
                }
            }

            var selectedSha = selected.variant.sha256;
            LoadBundle(selectedSha, bundle => completed(bundle, selectedSha));
        });
    }

    private void LoadBundle(string sha256, Action<AssetBundle> completed)
    {
        if (!_bundles.TryGetValue(sha256, out var state))
        {
            state = new BundleState();
            _bundles[sha256] = state;
        }

        if (state.Bundle != null)
        {
            Touch(BlobPath(sha256));
            completed(state.Bundle);
            return;
        }

        state.Waiters.Add(completed);
        if (state.Loading)
        {
            return;
        }

        state.Loading = true;
        var request = AssetBundle.LoadFromFileAsync(BlobPath(sha256));
        request.completed += _ =>
        {
            state.Loading = false;
            state.Bundle = request.assetBundle;
            var waiters = state.Waiters.ToArray();
            state.Waiters.Clear();
            if (state.Bundle == null)
            {
                LastError = $"Bundle {sha256} не открывается";
                _bundles.Remove(sha256);
            }
            foreach (var waiter in waiters)
            {
                waiter(state.Bundle);
            }
        };
    }

    private void Retain(string sha256)
    {
        if (_bundles.TryGetValue(sha256, out var state))
        {
            state.References++;
        }
    }

    private void Release(string sha256)
    {
        if (!_bundles.TryGetValue(sha256, out var state))
        {
            return;
        }

        state.References = Math.Max(0, state.References - 1);
        if (state.References == 0 && !state.Loading && state.Waiters.Count == 0)
        {
            state.Bundle?.Unload(false);
            _bundles.Remove(sha256);
        }
    }

    private void EnsureRecordBlob(ContentRecord record, Action<bool> completed)
    {
        EnsureBlob(
            record, record.variant.sha256, record.variant.size, "payload", completed);
    }

    private void EnsureBlob(
        ContentRecord record, string sha256, long size, string label, Action<bool> completed)
    {
        if (HasVerifiedBlob(sha256, size))
        {
            completed(true);
            return;
        }

        if (_blobRequests.TryGetValue(sha256, out var pending))
        {
            pending.Waiters.Add(completed);
            return;
        }

        pending = new BlobState();
        pending.Waiters.Add(completed);
        _blobRequests[sha256] = pending;
        ContentQueue.Begin(ContentQueue.Kind.Download);
        ContentCoroutines.Run(DownloadBlob(record, sha256, size, label, success =>
        {
            ContentQueue.End(ContentQueue.Kind.Download);
            var waiters = pending.Waiters.ToArray();
            _blobRequests.Remove(sha256);
            foreach (var waiter in waiters)
            {
                waiter(success);
            }
        }));
    }

    private IEnumerator DownloadBlob(
        ContentRecord record, string sha256, long size, string label, Action<bool> completed)
    {
        var partial = Path.Combine(_partial, sha256 + ".part");
        if (File.Exists(partial) && new FileInfo(partial).Length > size)
        {
            File.Delete(partial);
        }

        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing == size && existing > 0)
        {
            var completeVerification = Task.Run(
                () => VerifyFile(partial, sha256, size));
            while (!completeVerification.IsCompleted)
            {
                Status = $"Проверяем {record.type}/{record.id} ({label})";
                yield return null;
            }
            if (!completeVerification.IsFaulted && completeVerification.Result)
            {
                PromotePartial(partial, sha256);
                completed(true);
                yield break;
            }

            File.Delete(partial);
            existing = 0;
        }

        Status = $"Загружаем {record.type}/{record.id} ({label})";
        DownloadedBytes = (ulong)existing;
        DownloadTotalBytes = size;
        using var request = UnityWebRequest.Get($"{ContentEndpoint.Current}/blobs/{sha256}");
        var handler = new DownloadHandlerFile(partial, append: existing > 0)
        {
            removeFileOnAbort = false,
        };
        request.downloadHandler = handler;
        request.timeout = 120;
        if (existing > 0)
        {
            request.SetRequestHeader("Range", $"bytes={existing}-");
        }

        var operation = request.SendWebRequest();
        while (!operation.isDone)
        {
            DownloadedBytes = (ulong)existing + request.downloadedBytes;
            yield return null;
        }

        DownloadedBytes = (ulong)existing + request.downloadedBytes;
        if (request.result != UnityWebRequest.Result.Success ||
            (existing > 0 && request.responseCode != 206))
        {
            LastError = $"{record.type}/{record.id}: {request.error ?? "сервер не продолжил download"}";
            if (existing > 0 && request.responseCode == 200 && File.Exists(partial))
            {
                File.Delete(partial);
            }
            completed(false);
            yield break;
        }

        var verification = Task.Run(() => VerifyFile(partial, sha256, size));
        while (!verification.IsCompleted)
        {
            Status = $"Проверяем {record.type}/{record.id} ({label})";
            yield return null;
        }

        if (verification.IsFaulted || !verification.Result)
        {
            LastError = $"{record.type}/{record.id}: размер или SHA-256 не совпадает";
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
            completed(false);
            yield break;
        }

        PromotePartial(partial, sha256);
        completed(true);
    }

    private void PromotePartial(string partial, string sha256)
    {
        var destination = BlobPath(sha256);
        if (!File.Exists(destination))
        {
            File.Move(partial, destination);
        }
        else if (File.Exists(partial))
        {
            File.Delete(partial);
        }
        _verifiedHashes.Add(sha256);
        Touch(destination);
        TrimCache();
    }

    private bool HasVerifiedBlob(ContentRecord record)
    {
        return HasVerifiedBlob(record.variant.sha256, record.variant.size);
    }

    private bool HasVerifiedBlob(string sha256, long size)
    {
        var path = BlobPath(sha256);
        if (!File.Exists(path) || new FileInfo(path).Length != size)
        {
            return false;
        }

        if (!_verifiedHashes.Contains(sha256) && !VerifyFile(path, sha256, size))
        {
            return false;
        }

        _verifiedHashes.Add(sha256);
        Touch(path);
        return true;
    }

    private void PersistVerifiedRecord(ContentRecord record)
    {
        if (_verified.TryGetValue(record.Key, out var current) &&
            current.revision == record.revision && current.variant.sha256 == record.variant.sha256)
        {
            return;
        }

        var envelope = new LocalRecordFile { current = record };
        if (current != null && current.variant != null &&
            current.variant.sha256 != record.variant.sha256)
        {
            envelope.previous = current;
            _previous[record.Key] = current;
        }
        else if (_previous.TryGetValue(record.Key, out var previous))
        {
            envelope.previous = previous;
        }

        var path = RecordPath(record.type, record.id);
        WriteAtomic(path, JsonConvert.SerializeObject(envelope, Formatting.Indented));
        _verified[record.Key] = record;
    }

    private ContentRecord VerifiedFallback(string key, string rejectedSha)
    {
        if (_verified.TryGetValue(key, out var current) && current.IsActive &&
            current.variant.sha256 != rejectedSha && HasVerifiedBlob(current))
        {
            return current;
        }

        if (_previous.TryGetValue(key, out var previous) && previous.IsActive &&
            previous.variant.sha256 != rejectedSha && HasVerifiedBlob(previous))
        {
            return previous;
        }

        return null;
    }

    private ContentRecord VerifiedFallbackWithAttachment(
        string key, string rejectedSha, string attachmentName)
    {
        bool Usable(ContentRecord value)
        {
            return value != null && value.IsActive && value.variant.sha256 != rejectedSha &&
                   HasVerifiedBlob(value) && TryAttachment(value, attachmentName, out var attachment) &&
                   HasVerifiedBlob(attachment.sha256, attachment.size);
        }

        if (_verified.TryGetValue(key, out var current) && Usable(current))
        {
            return current;
        }
        return _previous.TryGetValue(key, out var previous) && Usable(previous)
            ? previous
            : null;
    }

    private static bool TryAttachment(
        ContentRecord record, string name, out ContentAttachment attachment)
    {
        attachment = record?.variant?.attachments?.FirstOrDefault(value =>
            value != null && string.Equals(value.name, name, StringComparison.Ordinal));
        return attachment != null && attachment.sha256?.Length == 64 && attachment.size >= 0;
    }

    private void LoadLocalState()
    {
        if (File.Exists(_registryStatePath))
        {
            try
            {
                _state = JsonConvert.DeserializeObject<ContentRegistryState>(
                    File.ReadAllText(_registryStatePath)) ?? new ContentRegistryState();
                foreach (var record in _state.knownRecords ?? new List<ContentRecord>())
                {
                    if (ValidRecord(record))
                    {
                        _known[record.Key] = record;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[AtomicContent] registry-state не читается: {exception.Message}");
                _state = new ContentRegistryState();
            }
        }

        if (Directory.Exists(_records))
        {
            foreach (var path in Directory.GetFiles(_records, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var envelope = JsonConvert.DeserializeObject<LocalRecordFile>(File.ReadAllText(path));
                    if (ValidRecord(envelope?.current))
                    {
                        _verified[envelope.current.Key] = envelope.current;
                    }
                    if (ValidRecord(envelope?.previous))
                    {
                        _previous[envelope.previous.Key] = envelope.previous;
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[AtomicContent] local record {path} не читается: {exception.Message}");
                }
            }
        }

        foreach (var pair in _known.Count > 0 ? _known : _verified)
        {
            _pinned[pair.Key] = pair.Value;
        }
    }

    private void TrimCache()
    {
        var files = Directory.GetFiles(_blobs)
            .Select(path => new FileInfo(path))
            .OrderBy(info => info.LastAccessTimeUtc)
            .ToArray();
        var total = files.Sum(info => info.Length);
        if (total <= DefaultCacheLimitBytes)
        {
            return;
        }

        var protectedHashes = new HashSet<string>(
            _verified.Values.Where(value => value.variant != null)
                .Select(value => value.variant.sha256), StringComparer.Ordinal);
        protectedHashes.UnionWith(_verified.Values
            .Where(value => value.variant?.attachments != null)
            .SelectMany(value => value.variant.attachments)
            .Where(value => value != null)
            .Select(value => value.sha256));
        protectedHashes.UnionWith(_bundles.Where(pair => pair.Value.Bundle != null)
            .Select(pair => pair.Key));
        foreach (var file in files)
        {
            if (total <= DefaultCacheLimitBytes || protectedHashes.Contains(file.Name))
            {
                continue;
            }

            var length = file.Length;
            file.Delete();
            _verifiedHashes.Remove(file.Name);
            total -= length;
        }
    }

    private string BlobPath(string sha256) => Path.Combine(_blobs, sha256);
    private string RecordPath(string type, string id) =>
        Path.Combine(_records, type, id + ".json");

    private static string Key(string type, string id) => type + "/" + id;

    private static bool ValidRecord(ContentRecord record)
    {
        if (record == null || string.IsNullOrEmpty(record.type) ||
            string.IsNullOrEmpty(record.id) || record.revision <= 0)
        {
            return false;
        }
        if (record.state == "retired")
        {
            return true;
        }
        if (record.variant == null || record.variant.sha256?.Length != 64 || record.variant.size < 0)
        {
            return false;
        }
        return record.variant.attachments == null || record.variant.attachments.All(value =>
            value != null && !string.IsNullOrEmpty(value.name) &&
            value.sha256?.Length == 64 && value.size >= 0);
    }

    private static bool VerifyFile(string path, string sha256, long expectedSize)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSize)
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        var actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty)
            .ToLowerInvariant();
        return string.Equals(actual, sha256, StringComparison.Ordinal);
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception)
        {
        }
    }

    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, json);
        if (File.Exists(path))
        {
            File.Replace(temporary, path, null);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    private static string PlatformName()
    {
        return Application.platform switch
        {
            RuntimePlatform.WindowsPlayer => "StandaloneWindows64",
            RuntimePlatform.WindowsEditor => "StandaloneWindows64",
            _ => "StandaloneOSX",
        };
    }
}

}
