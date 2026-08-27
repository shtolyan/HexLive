using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json;
using UnityEngine;

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
        public int PendingAssetLoads;
        public bool UnloadScheduled;
        public readonly List<Action<AssetBundle>> Waiters = new();
    }

    private sealed class BlobState
    {
        public readonly List<Action<bool>> Waiters = new();
    }

    private sealed class BlobDownloadJob
    {
        public ContentRecord Record;
        public string Sha256;
        public long Size;
        public string Label;
        public Action<bool> Completed;
    }

    private sealed class HttpTextResponse
    {
        public HttpStatusCode StatusCode;
        public bool Success;
        public string Text = string.Empty;
        public string ETag = string.Empty;
        public string Error = string.Empty;
    }

    private sealed class BlobHttpProgress
    {
        public long Bytes;
    }

    private sealed class BlobHttpResponse
    {
        public HttpStatusCode StatusCode;
        public bool Success;
        public string Error = string.Empty;
    }

    private static ContentAssetService _instance;
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly Dictionary<string, ContentRecord> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentRecord> _pinned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentRecord> _verified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentRecord> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BundleState> _bundles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlobState> _blobRequests = new(StringComparer.Ordinal);
    private readonly Queue<BlobDownloadJob> _priorityBlobDownloadQueue = new();
    private readonly Queue<BlobDownloadJob> _blobDownloadQueue = new();
    private readonly HashSet<string> _verifiedHashes = new(StringComparer.Ordinal);
    private readonly List<Action> _registryWaiters = new();

    private readonly string _root;
    private readonly string _blobs;
    private readonly string _records;
    private readonly string _partial;
    private readonly string _registryStatePath;
    private ContentRegistryState _state = new();
    private int _activeBlobDownloads;
    private bool _refreshStarted;
    private bool _registryReady;
    private string _registryEndpoint = string.Empty;

    // Six is deliberately browser-like. A fresh client can request more than
    // two thousand audio payloads and attachments during §41.4 prewarm; opening
    // one HTTP connection per object exhausted an SSH tunnel (and is equally
    // hostile to a reverse proxy). Queued requests still participate in
    // ContentQueue progress, but only this many touch the network at once.
    private const int MaxConcurrentBlobDownloads = 6;
    private const int BlobDownloadAttempts = 3;

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
        SessionConfig.ServerChanged += ServerChanged;
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
            (string.Equals(
                 (string)value.metadata?["legacyResourcePath"], path,
                 StringComparison.Ordinal) ||
             value.metadata?["legacyResourcePaths"] is Newtonsoft.Json.Linq.JArray aliases &&
             aliases.Values<string>().Any(alias => string.Equals(
                 alias, path, StringComparison.Ordinal))));
        return record != null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        if (_instance != null)
        {
            SessionConfig.ServerChanged -= _instance.ServerChanged;
            foreach (var bundle in _instance._bundles.Values)
            {
                // With domain reload disabled Unity destroys native bundle
                // objects when Play Mode exits, while the managed wrappers
                // survive until SubsystemRegistration. Null-conditional access
                // only checks the CLR reference and therefore calls Unload on
                // a destroyed UnityEngine.Object. Use Unity's overloaded null
                // check so a second Play starts from a clean cache.
                // A failed/aborted interactive smoke may leave LoadFromFileAsync
                // or LoadAssetAsync in flight while Play Mode exits. Unity owns
                // those native requests and tears them down with the play world;
                // explicitly unloading here blocks the main thread and emits one
                // error per working-set object. Only close bundles which reached
                // a quiescent state. The managed service is discarded below in
                // either case.
                if (bundle.Bundle != null && !bundle.Loading &&
                    bundle.PendingAssetLoads == 0)
                {
                    bundle.Bundle.Unload(true);
                }
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

        var endpoint = ContentEndpoint.Current;
        if (!string.IsNullOrEmpty(_registryEndpoint) &&
            !string.Equals(_registryEndpoint, endpoint, StringComparison.OrdinalIgnoreCase))
        {
            _registryReady = false;
        }
        _registryEndpoint = endpoint;
        _refreshStarted = true;
        ContentQueue.Begin(ContentQueue.Kind.Registry);
        ContentCoroutines.Run(RefreshRegistryRoutine(endpoint));
    }

    private void ServerChanged() => RefreshRegistry();

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
            if (assetEntry == "icon" && !record.HasRealIcon)
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

                // §152.2: `main` and `icon` are two entries of this one atomic
                // owner bundle. When a current-world object opens its model,
                // request its Sprite immediately through the same SHA-backed
                // BundleState. This cannot download/open an icon bundle and it
                // never touches owners which the world did not request.
                if (assetEntry == "main" && record.HasRealIcon)
                {
                    ItemIcons.PrewarmOwner(record.type, record.id);
                }

                BeginAssetLoad(loadedSha);
                // Держим бандл ПОКА идёт асинхронная загрузка ассета. Раньше
                // Retain стоял в completed, и Release чужого хэндла успевал
                // выгрузить бандл из-под живого AssetBundleRequest: на Windows
                // это падение в нативном коде, а не пустой asset.
                Retain(loadedSha);
                var request = bundle.LoadAssetAsync<T>(assetEntry);
                request.completed += _ =>
                {
                    var asset = request.asset as T;
                    if (asset == null)
                    {
                        EndAssetLoad(loadedSha);
                        Release(loadedSha);
                        completed(null);
                        return;
                    }

                    Retain(loadedSha);
                    EndAssetLoad(loadedSha);
                    Release(loadedSha);
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

                BeginAssetLoad(loadedSha);
                // Та же страховка, что и в LoadAsset: до конца запроса бандл
                // не может быть выгружен чужим Release.
                Retain(loadedSha);
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
                    EndAssetLoad(loadedSha);
                    Release(loadedSha);
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

            var priority = string.Equals(
                (string)record.metadata?["kind"], "music", StringComparison.Ordinal);
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
            }, priority);
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
        var request = SendTextAsync(
            ContentEndpoint.Current + "/resolve",
            HttpMethod.Post,
            JsonConvert.SerializeObject(payload),
            null,
            15);
        ContentQueue.Begin(ContentQueue.Kind.Registry);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        ContentQueue.End(ContentQueue.Kind.Registry);

        var result = CompletedTextResponse(request);
        if (!result.Success)
        {
            LastError = $"Resolve контента недоступен: {result.Error}";
            Debug.LogWarning("[AtomicContent] " + LastError);
            completed?.Invoke(identities);
            yield break;
        }

        try
        {
            var response = JsonConvert.DeserializeObject<ContentResolveResponse>(
                result.Text) ?? new ContentResolveResponse();
            completed?.Invoke(response.missing ?? new List<ContentObjectKey>());
        }
        catch (Exception exception)
        {
            LastError = $"Resolve контента повреждён: {exception.Message}";
            Debug.LogError("[AtomicContent] " + LastError);
            completed?.Invoke(identities);
        }
    }

    private IEnumerator RefreshRegistryRoutine(string endpoint)
    {
        Status = "Получаем реестр контента";
        if (!string.IsNullOrEmpty(_state.endpoint) &&
            !string.Equals(_state.endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
        {
            // Delta revisions and ETags belong to one registry endpoint only.
            // Blobs remain SHA-addressed and reusable, but records from two
            // independent servers must never be merged.
            _state = new ContentRegistryState();
            _known.Clear();
        }
        _state.endpoint = endpoint;
        var platform = PlatformName();
        var url = $"{endpoint}/index/{platform}/{RuntimeProfile}" +
                  (_state.registryRevision > 0 ? $"?after={_state.registryRevision}" : string.Empty);
        var request = SendTextAsync(
            url,
            HttpMethod.Get,
            null,
            _state.etag,
            10);
        while (!request.IsCompleted)
        {
            yield return null;
        }

        if (!string.Equals(endpoint, ContentEndpoint.Current,
                StringComparison.OrdinalIgnoreCase))
        {
            ContentQueue.End(ContentQueue.Kind.Registry);
            _refreshStarted = false;
            RefreshRegistry();
            yield break;
        }

        var result = CompletedTextResponse(request);
        if (result.StatusCode == HttpStatusCode.NotModified)
        {
            LastError = string.Empty;
            _state.endpoint = endpoint;
            WriteAtomic(_registryStatePath,
                JsonConvert.SerializeObject(_state, Formatting.Indented));
            PinKnownRecords();
            CompleteRegistry();
            yield break;
        }

        if (!result.Success)
        {
            LastError = $"Реестр недоступен: {result.Error}";
            Debug.LogWarning($"[AtomicContent] {LastError}; используем проверенный локальный кэш.");
            PinOfflineRecords();
            CompleteRegistry();
            yield break;
        }

        ContentIndexResponse response;
        try
        {
            response = JsonConvert.DeserializeObject<ContentIndexResponse>(result.Text);
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
        _state.etag = result.ETag;
        _state.knownRecords = _known.Values
            .OrderBy(value => value.Key, StringComparer.Ordinal).ToList();
        WriteAtomic(_registryStatePath, JsonConvert.SerializeObject(_state, Formatting.Indented));
        LastError = string.Empty;
        PinKnownRecords();
        CompleteRegistry();
        UpdatePreviouslyCachedInBackground();
    }

    private void CompleteRegistry()
    {
        _registryReady = true;
        _refreshStarted = false;
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
                Debug.LogWarning($"[AtomicContent] {LastError} ({BlobPath(sha256)})");
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

    private void BeginAssetLoad(string sha256)
    {
        if (_bundles.TryGetValue(sha256, out var state))
        {
            state.PendingAssetLoads++;
        }
    }

    private void EndAssetLoad(string sha256)
    {
        if (!_bundles.TryGetValue(sha256, out var state))
        {
            return;
        }

        state.PendingAssetLoads = Math.Max(0, state.PendingAssetLoads - 1);
        TryUnloadBundle(sha256, state);
    }

    private void Release(string sha256)
    {
        if (!_bundles.TryGetValue(sha256, out var state))
        {
            return;
        }

        state.References = Math.Max(0, state.References - 1);
        TryUnloadBundle(sha256, state);
    }

    private void TryUnloadBundle(string sha256, BundleState state)
    {
        if (state.References == 0 && state.PendingAssetLoads == 0 &&
            !state.Loading && state.Waiters.Count == 0 && !state.UnloadScheduled)
        {
            state.UnloadScheduled = true;
            ContentCoroutines.Run(UnloadBundleDeferred(sha256, state));
        }
    }

    // Never Unload(false) synchronously from inside an AssetBundleRequest
    // completion callback: Unity still counts that request as "an async load
    // operation in progress", the unload cannot complete, and the NATIVE
    // bundle survives as a ghost while _bundles already forgot the SHA. The
    // next LoadFromFileAsync of the same SHA then fails forever with "another
    // AssetBundle with the same files is already loaded" and the item stays
    // invisible until restart. One frame later the operation is truly retired
    // and the unload is clean; a re-request that landed in between simply
    // fails the recheck and keeps the bundle.
    private IEnumerator UnloadBundleDeferred(string sha256, BundleState state)
    {
        yield return null;
        state.UnloadScheduled = false;
        if (_bundles.TryGetValue(sha256, out var current) &&
            ReferenceEquals(current, state) &&
            state.References == 0 && state.PendingAssetLoads == 0 &&
            !state.Loading && state.Waiters.Count == 0)
        {
            if (state.Bundle != null)
            {
                state.Bundle.Unload(false);
            }
            _bundles.Remove(sha256);
        }
    }

    private void EnsureRecordBlob(
        ContentRecord record, Action<bool> completed, bool priority = false)
    {
        EnsureBlob(
            record, record.variant.sha256, record.variant.size, "payload", completed, priority);
    }

    private void EnsureBlob(
        ContentRecord record, string sha256, long size, string label, Action<bool> completed,
        bool priority = false)
    {
        // Мгновенный ответ — только по уже проверенному В ЭТОЙ СЕССИИ хешу.
        // Первый запрос сессии на кэшированный blob обязан пройти через
        // очередь: там его SHA-256 считается на worker (DownloadBlob), а не
        // здесь. Прежний синхронный HasVerifiedBlob хешировал каждый wear-blob
        // на главном потоке — ~180 бандлов тёплого кэша держали занавес
        // минутами при нулевом трафике.
        if (_verifiedHashes.Contains(sha256))
        {
            var path = BlobPath(sha256);
            if (IsStandaloneFile(path) && new FileInfo(path).Length == size)
            {
                Touch(path);
                completed(true);
                return;
            }
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
        var job = new BlobDownloadJob
        {
            Record = record,
            Sha256 = sha256,
            Size = size,
            Label = label,
            Completed = success => CompleteBlobRequest(sha256, pending, success),
        };
        if (priority)
        {
            _priorityBlobDownloadQueue.Enqueue(job);
        }
        else
        {
            _blobDownloadQueue.Enqueue(job);
        }
        StartQueuedBlobDownloads();
    }

    private void StartQueuedBlobDownloads()
    {
        while (_activeBlobDownloads < MaxConcurrentBlobDownloads &&
               (_priorityBlobDownloadQueue.Count > 0 || _blobDownloadQueue.Count > 0))
        {
            var job = _priorityBlobDownloadQueue.Count > 0
                ? _priorityBlobDownloadQueue.Dequeue()
                : _blobDownloadQueue.Dequeue();
            _activeBlobDownloads++;
            ContentCoroutines.Run(DownloadBlobWithRetries(job, success =>
            {
                _activeBlobDownloads--;
                ContentQueue.End(ContentQueue.Kind.Download);
                job.Completed(success);
                StartQueuedBlobDownloads();
            }));
        }
    }

    private void CompleteBlobRequest(string sha256, BlobState pending, bool success)
    {
        var waiters = pending.Waiters.ToArray();
        _blobRequests.Remove(sha256);
        foreach (var waiter in waiters)
        {
            waiter(success);
        }
    }

    private IEnumerator DownloadBlobWithRetries(
        BlobDownloadJob job, Action<bool> completed)
    {
        string transientError = null;
        for (var attempt = 1; attempt <= BlobDownloadAttempts; attempt++)
        {
            var succeeded = false;
            yield return DownloadBlob(
                job.Record, job.Sha256, job.Size, job.Label,
                success => succeeded = success);
            if (succeeded)
            {
                if (!string.IsNullOrEmpty(transientError) && LastError == transientError)
                {
                    LastError = string.Empty;
                }
                completed(true);
                yield break;
            }

            transientError = LastError;
            if (attempt < BlobDownloadAttempts)
            {
                Status = $"Повторяем {job.Record.type}/{job.Record.id} " +
                         $"({job.Label}, {attempt + 1}/{BlobDownloadAttempts})";
                yield return new WaitForSecondsRealtime(0.5f * attempt);
            }
        }

        completed(false);
    }

    private IEnumerator DownloadBlob(
        ContentRecord record, string sha256, long size, string label, Action<bool> completed)
    {
        // Кэшированный blob прошлых сессий: полное совпадение размера — это
        // кандидат, но правда только в хеше. Считаем его на worker (см.
        // EnsureBlob: главный поток не хеширует), успех — без сети.
        var cachedBlob = BlobPath(sha256);
        if (!_verifiedHashes.Contains(sha256) &&
            IsStandaloneFile(cachedBlob) && new FileInfo(cachedBlob).Length == size)
        {
            var cachedVerification = Task.Run(() => VerifyFile(cachedBlob, sha256, size));
            while (!cachedVerification.IsCompleted)
            {
                Status = $"Проверяем {record.type}/{record.id} ({label})";
                yield return null;
            }
            if (!cachedVerification.IsFaulted && cachedVerification.Result)
            {
                _verifiedHashes.Add(sha256);
                Touch(cachedBlob);
                completed(true);
                yield break;
            }

            // Кэш повреждён — честная перекачка ниже.
            File.Delete(cachedBlob);
        }

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
        var progress = new BlobHttpProgress();
        var request = DownloadToPartialAsync(
            $"{ContentEndpoint.Current}/blobs/{sha256}", partial, existing, progress, 120);
        while (!request.IsCompleted)
        {
            DownloadedBytes = (ulong)Math.Max(0, existing + Interlocked.Read(ref progress.Bytes));
            yield return null;
        }

        DownloadedBytes = (ulong)Math.Max(0, existing + Interlocked.Read(ref progress.Bytes));
        var result = CompletedBlobResponse(request);
        if (!result.Success)
        {
            LastError = $"{record.type}/{record.id}: {result.Error}";
            if (existing > 0 && result.StatusCode == HttpStatusCode.OK && File.Exists(partial))
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

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        return new HttpClient(handler)
        {
            // Each operation owns a bounded CancellationTokenSource. A global
            // timeout would also cover time spent waiting for a pooled socket.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    private static async Task<HttpTextResponse> SendTextAsync(
        string url,
        HttpMethod method,
        string json,
        string etag,
        int timeoutSeconds)
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(method, url);
        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        try
        {
            using var response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, cancellation.Token)
                .ConfigureAwait(false);
            var text = response.Content == null
                ? string.Empty
                : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpTextResponse
            {
                StatusCode = response.StatusCode,
                Success = response.IsSuccessStatusCode,
                Text = text,
                ETag = response.Headers.ETag?.ToString() ?? string.Empty,
                Error = response.IsSuccessStatusCode
                    ? string.Empty
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
            };
        }
        catch (OperationCanceledException)
        {
            return new HttpTextResponse { Error = $"timeout {timeoutSeconds}s" };
        }
        catch (Exception exception)
        {
            return new HttpTextResponse { Error = exception.Message };
        }
    }

    private static HttpTextResponse CompletedTextResponse(Task<HttpTextResponse> task)
    {
        if (task.IsCanceled)
        {
            return new HttpTextResponse { Error = "request canceled" };
        }
        if (task.IsFaulted)
        {
            return new HttpTextResponse
            {
                Error = task.Exception?.GetBaseException().Message ?? "request failed",
            };
        }
        return task.Result;
    }

    private static async Task<BlobHttpResponse> DownloadToPartialAsync(
        string url,
        string partial,
        long existing,
        BlobHttpProgress progress,
        int timeoutSeconds)
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        try
        {
            using var response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
                .ConfigureAwait(false);
            if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                return new BlobHttpResponse
                {
                    StatusCode = response.StatusCode,
                    Error = response.StatusCode == HttpStatusCode.OK
                        ? "сервер не продолжил download"
                        : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                };
            }
            if (!response.IsSuccessStatusCode)
            {
                return new BlobHttpResponse
                {
                    StatusCode = response.StatusCode,
                    Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                };
            }

            var rangeStart = response.Content?.Headers.ContentRange?.From;
            if (existing > 0 && rangeStart.HasValue && rangeStart.Value != existing)
            {
                return new BlobHttpResponse
                {
                    StatusCode = response.StatusCode,
                    Error = $"сервер продолжил download с {rangeStart.Value}, ожидалось {existing}",
                };
            }

            using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var destination = new FileStream(
                partial,
                existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(
                        buffer, 0, buffer.Length, cancellation.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                await destination.WriteAsync(buffer, 0, read, cancellation.Token)
                    .ConfigureAwait(false);
                Interlocked.Add(ref progress.Bytes, read);
            }
            await destination.FlushAsync(cancellation.Token).ConfigureAwait(false);
            return new BlobHttpResponse
            {
                StatusCode = response.StatusCode,
                Success = true,
            };
        }
        catch (OperationCanceledException)
        {
            return new BlobHttpResponse { Error = $"timeout {timeoutSeconds}s" };
        }
        catch (Exception exception)
        {
            return new BlobHttpResponse { Error = exception.Message };
        }
    }

    private static BlobHttpResponse CompletedBlobResponse(Task<BlobHttpResponse> task)
    {
        if (task.IsCanceled)
        {
            return new BlobHttpResponse { Error = "download canceled" };
        }
        if (task.IsFaulted)
        {
            return new BlobHttpResponse
            {
                Error = task.Exception?.GetBaseException().Message ?? "download failed",
            };
        }
        return task.Result;
    }

    private void PromotePartial(string partial, string sha256)
    {
        var destination = BlobPath(sha256);
        var expectedSize = new FileInfo(partial).Length;
        if (IsStandaloneFile(destination) &&
            VerifyFile(destination, sha256, expectedSize))
        {
            File.Delete(partial);
        }
        else
        {
            // Early atomic-content dev builds populated this cache with
            // symlinks into a worktree. Once that tree is removed Mono still
            // reports File.Exists=true and Length=the link-text length; the old
            // code consequently threw away the freshly verified .part and
            // preserved a dead link. Cache blobs are standalone files only.
            File.Delete(destination); // also unlinks a broken ReparsePoint
            File.Move(partial, destination);
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
        if (!IsStandaloneFile(path) || new FileInfo(path).Length != size)
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
        // Все известные бандлы, а не только уже открытые: пока идёт
        // LoadFromFileAsync, файл держит Unity, и на Windows File.Delete по нему
        // бросает sharing violation (на POSIX unlink проходит молча — поэтому
        // дыра не видна на маке). То же для хэша, в который прямо сейчас
        // докачивается блоб.
        protectedHashes.UnionWith(_bundles.Keys);
        protectedHashes.UnionWith(_blobRequests.Keys);
        foreach (var file in files)
        {
            if (total <= DefaultCacheLimitBytes || protectedHashes.Contains(file.Name))
            {
                continue;
            }

            var length = file.Length;
            try
            {
                file.Delete();
            }
            catch (Exception exception)
            {
                // Исключение отсюда убивало корутину загрузки вместе с
                // ContentQueue.End — очередь контента больше не сходилась.
                Debug.LogWarning(
                    $"[AtomicContent] блоб {file.Name} занят, чистка отложена: {exception.Message}");
                continue;
            }

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
        if (!IsStandaloneFile(path) || new FileInfo(path).Length != expectedSize)
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        var actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty)
            .ToLowerInvariant();
        return string.Equals(actual, sha256, StringComparison.Ordinal);
    }

    private static bool IsStandaloneFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception)
        {
            return false;
        }
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
