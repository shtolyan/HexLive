using System;
using System.IO;
using System.Threading;
using HexLive.Server.Assets;
using HexLive.Server.Llm;

namespace HexLive.Server
{

/// <summary>
/// Owns the running world and the thread that ticks it, so the world can be
/// replaced without restarting the process.
/// <para>
/// The admin panel needs to start a new world on a live server. That means
/// something has to hold the CURRENT one and be able to swap it — a
/// <see cref="WorldHost"/> cannot do that for itself, and every reader
/// (viewers, the status page) must see the swap atomically or it will serialise
/// half of one world and half of another.
/// </para>
/// </summary>
public sealed class WorldSupervisor : IDisposable
{
    private string _savePath;
    public WorldLibrary Library { get; }
    public PlayerCharacterAssignments Assignments { get; private set; }
    private readonly AssetGarmentCatalog _catalog;
    private readonly bool _verboseTrace;
    private readonly bool _includeDebugDetails;
    private readonly LlmHostOptions _llmOptions;
    private readonly string? _companionProfile;
    private readonly CancellationToken _appShutdown;

    private readonly object _swap = new();

    /// <summary>
    /// §145.4: мир заменён — подписчики обязаны забыть всё, что держали про
    /// старый (реестр лиз чистится, зеркало хроники MCP заводится на новом
    /// хосте). Иначе внешние контуры продолжают командовать людьми, которых
    /// больше нет.
    /// </summary>
    public event Action? WorldSwapped;

    private WorldHost _host;
    private CancellationTokenSource _hostLifetime;
    private CancellationTokenSource _viewerLifetime;
    private Thread _thread;
    private string _simData;
    private AssetGarmentCatalogSnapshot _catalogSnapshot;
    private int _worldGeneration;

    public WorldSupervisor(int seed, HexLive.Simulation.Bootstrap.GameMode mode,
        string savePath, AssetGarmentCatalog catalog, bool verboseTrace,
        bool includeDebugDetails, LlmHostOptions llmOptions, string? companionProfile,
        CancellationToken appShutdown, string? legacyAssignmentsPath = null, bool startPaused = false)
    {
        _savePath = savePath;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _verboseTrace = verboseTrace;
        _includeDebugDetails = includeDebugDetails;
        _llmOptions = llmOptions;
        _companionProfile = companionProfile;
        _appShutdown = appShutdown;

        _catalogSnapshot = _catalog.Materialize();
        Library = new WorldLibrary(savePath, seed, mode, _catalogSnapshot.Json, _catalogSnapshot.RegistryRevision, legacyAssignmentsPath);
        var active = Library.Read(Library.ActiveId);
        _savePath = Library.SavePath(active.Id);
        if (!Library.MigratedOnStartup && !File.Exists(_savePath))
            throw new InvalidDataException("The active library world's save is missing; refusing to reset its progress.");
        _host = new WorldHost(active.Seed, active.Mode, _savePath, Library.CatalogPath(active.Id),
            verboseTrace, includeDebugDetails, llmOptions, companionProfile, Library.Config(active.Id), active.Id);
        Assignments = PlayerCharacterAssignments.Load(Library.AssignmentPath(active.Id), File.Exists(_savePath));
        _hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(appShutdown);
        _viewerLifetime = CancellationTokenSource.CreateLinkedTokenSource(appShutdown);
        _host.Save(); // initial empty hosts also establish a recoverable library save before ticking
        if (startPaused) _host.PauseAsOperator();
        _thread = StartThread(_host, _hostLifetime.Token);
        _simData = File.ReadAllText(Library.CatalogPath(Library.ActiveId));
        _catalogSnapshot = new AssetGarmentCatalogSnapshot { Path = Library.CatalogPath(active.Id), Json = _simData, RegistryRevision = active.CatalogRevision };
    }

    /// <summary>
    /// The catalogs the CURRENT world runs on, for the handshake. Re-read on
    /// every world swap: `WorldHost`'s constructor re-applies the file, so a
    /// month-old string captured at process start would hand new viewers a
    /// different `DefinitionIdTable` than the world uses — every interned id
    /// silently resolving to the wrong definition.
    /// </summary>
    public string SimData
    {
        get
        {
            lock (_swap)
            {
                return _simData;
            }
        }
    }

    /// <summary>§154.2 registry revision pinned by the current world.</summary>
    public long CatalogRegistryRevision
    {
        get
        {
            lock (_swap)
            {
                return _catalogSnapshot.RegistryRevision;
            }
        }
    }

    public bool CatalogPending => _catalog.RegistryRevision != CatalogRegistryRevision;

    /// <summary>
    /// Cancelled when the world this viewer connected to stops existing. A
    /// viewer holds a reference to ITS world; after a swap that world is
    /// frozen, and a connection left open would answer pings forever while
    /// showing a dead colony. Closing it pushes the client through its
    /// reconnect path, where the new seed is detected honestly.
    /// </summary>
    public CancellationToken ViewerLifetime
    {
        get
        {
            lock (_swap)
            {
                return _viewerLifetime.Token;
            }
        }
    }

    /// <summary>
    /// The world right now. Callers hold it only for the duration of one
    /// operation — after a restart this returns a different object, and that is
    /// the point.
    /// </summary>
    public WorldHost Host
    {
        get
        {
            lock (_swap)
            {
                return _host;
            }
        }
    }

    /// <summary>
    /// §149: viewer handshake, assignment and lifetime must name ONE world.
    /// Reading the three properties separately allowed an admin world-swap to
    /// splice an old roster into a new host between reads.
    /// </summary>
    internal void ReconnectViewers()
    {
        lock (_swap)
        {
            var old = _viewerLifetime;
            _viewerLifetime = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown);
            old.Cancel(); old.Dispose();
        }
    }

    public ViewerSession CaptureViewerSession()
    {
        lock (_swap)
        {
            return new ViewerSession(
                _host, _simData, _viewerLifetime.Token, _worldGeneration, Assignments);
        }
    }

    /// <summary>Legacy admin entry point: preserve the current world and activate a new library entry.</summary>
    public void StartNewWorld(int seed,
        HexLive.Simulation.Bootstrap.GameMode mode = HexLive.Simulation.Bootstrap.GameMode.Feud)
    {
        CreateLibraryWorld(seed, mode, null, Guid.NewGuid().ToString("N"));
    }

    private string? _customRoster;
    public void RefreshCustomRoster()
    {
        lock (_swap)
        {
            var roster = _host.Read(w => w.CreationConfig == null ? null : string.Join(",",
                System.Linq.Enumerable.OrderBy(System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Where(w.Entities.Npcs.Values, n => w.PlayerControlledNpcs.Contains(n.Id.Value) && HexLive.Simulation.Runtime.FactionRelations.IsGirlCamp(n.Faction)), n => n.Id.Value), id => id)));
            if (_customRoster == roster) return;
            var previous = _customRoster; _customRoster = roster;
            if (previous == null) return;
            _viewerLifetime.Cancel(); _viewerLifetime.Dispose();
            _viewerLifetime = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown);
        }
    }

    public T WithCatalog<T>(Func<T> read)
    {
        lock (_swap) return read();
    }

    public string CreateLibraryWorld(int seed, HexLive.Simulation.Bootstrap.GameMode mode,
        HexLive.Simulation.Bootstrap.WorldCreationConfig? config, string requestId)
    {
        string id;
        lock (_swap)
        {
            var existing = System.Linq.Enumerable.FirstOrDefault(Library.List(includePreparing: true), w => w.RequestId == requestId);
            if (existing != null)
            {
                var savedConfig = Library.Config(existing.Id);
                if (existing.Seed != seed || existing.Mode != mode || (config == null) != (savedConfig == null))
                    throw new InvalidOperationException("Request id already belongs to another world configuration.");
                if (config != null && savedConfig != null)
                {
                    var incoming = HexLive.Simulation.Bootstrap.WorldCreationCodec.Decode(HexLive.Simulation.Bootstrap.WorldCreationCodec.Encode(config));
                    incoming.WorldId = savedConfig.WorldId;
                    if (HexLive.Simulation.Bootstrap.WorldCreationCodec.Encode(incoming) != HexLive.Simulation.Bootstrap.WorldCreationCodec.Encode(savedConfig))
                        throw new InvalidOperationException("Request id already belongs to another world configuration.");
                }
                if (!Library.CreationSucceeded(existing)) ActivateLibraryWorld(existing.Id);
                return existing.Id;
            }
            var record = new ServerWorldRecord { Id = Guid.NewGuid().ToString("N"),
                Name = config?.Name ?? mode.ToString(), Seed = seed, Mode = mode,
                RequestId = requestId, CatalogRevision = _catalogSnapshot.RegistryRevision };
            if (config != null) config.WorldId = record.Id;
            Library.Prepare(record, config, _simData);
            id = record.Id;
            ActivateLibraryWorld(id);
            return id;
        }
    }

    public void ActivateLibraryWorld(string id)
    {
        lock (_swap)
        {
            if (Library.ActiveId == id) return;
            var record = Library.Read(id);
            if (record.Ready && !File.Exists(Library.SavePath(id)))
                throw new InvalidDataException("The saved world is missing; refusing to replace its progress.");
            var previousPaused = _host.IsPaused;
            _host.PauseAsOperator();
            WorldHost? candidate = null;
            CancellationTokenSource? candidateLifetime = null;
            CancellationTokenSource? candidateViewers = null;
            Thread? candidateThread = null;
            try
            {
                // Freeze at a tick boundary before temporarily applying the candidate's catalogs.
                _host.Save();
                candidate = new WorldHost(record.Seed, record.Mode, Library.SavePath(id), Library.CatalogPath(id),
                    _verboseTrace, _includeDebugDetails, _llmOptions, _companionProfile, Library.Config(id), id);
                var candidateCatalog = File.ReadAllText(Library.CatalogPath(id));
                candidate.PauseAsOperator();
                candidate.Save();
                var assignments = PlayerCharacterAssignments.Load(Library.AssignmentPath(id), true);
                var creation = Library.Config(id);
                if (creation != null) assignments.Reconcile(candidate, creation.CreatorPlayerId);
                assignments.BindWorldGeneration(_worldGeneration + 1);
                var snapshot = new AssetGarmentCatalogSnapshot { Path = Library.CatalogPath(id), Json = candidateCatalog, RegistryRevision = record.CatalogRevision };
                candidateLifetime = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown);
                candidateViewers = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown);
                candidateThread = StartThread(candidate, candidateLifetime.Token); // still paused
                Library.MarkReady(id);
                // Preserve a completed request before its world stops being the active commit marker.
                Library.MarkCreationCompleted(Library.ActiveId);
                Library.Activate(id); // atomic commit: all fallible preparation precedes this write

                var previous = _host;
                var previousLifetime = _hostLifetime;
                var previousViewers = _viewerLifetime;
                var previousThread = _thread;
                _savePath = Library.SavePath(id);
                _host = candidate;
                _hostLifetime = candidateLifetime;
                _viewerLifetime = candidateViewers;
                _thread = candidateThread;
                candidate = null; candidateLifetime = null; candidateViewers = null; candidateThread = null;
                Assignments = assignments;
                _simData = candidateCatalog;
                _catalogSnapshot = snapshot;
                _worldGeneration++;
                _customRoster = null;
                // After commit, cleanup failures must not report a failed creation or roll back the pointer.
                try { previousLifetime.Cancel(); previousThread.Join(TimeSpan.FromSeconds(5)); previous.Dispose(); }
                catch (Exception ex) { Console.Error.WriteLine("[world] previous host cleanup: " + ex.Message); }
                finally { previousLifetime.Dispose(); }
                try { previousViewers.Cancel(); }
                catch (Exception ex) { Console.Error.WriteLine("[world] viewer cleanup: " + ex.Message); }
                finally { previousViewers.Dispose(); }
                _host.ResumeAsOperator();
            }
            catch
            {
                candidateLifetime?.Cancel();
                candidateThread?.Join(TimeSpan.FromSeconds(5));
                candidate?.Dispose(); candidateLifetime?.Dispose(); candidateViewers?.Dispose();
                HexLive.Simulation.Content.SimDataFile.Require(Library.CatalogPath(_host.WorldId));
                if (!previousPaused) _host.ResumeAsOperator();
                throw;
            }
        }
        try { WorldSwapped?.Invoke(); }
        catch (Exception ex) { Console.Error.WriteLine("[world] swap notification: " + ex.Message); }
    }

    /// <summary>
    /// §154.2 applies a pending catalogue to the same save. This is a world
    /// generation swap for connected viewers, but not a new colony: no save is
    /// archived and the same seed/mode/progress are restored.
    /// </summary>
    public bool ReloadCatalog()
    {
        var next = _catalog.Materialize();
        bool swapped;
        Exception? restoredFailure = null;
        lock (_swap)
        {
            if (next.RegistryRevision == _catalogSnapshot.RegistryRevision)
            {
                return false;
            }

            var previous = _catalogSnapshot;
            var seed = _host.Seed;
            var mode = _host.Mode;
            var wasPaused = _host.IsPaused;
            var speed = _host.SpeedMultiplier;

            // Freeze at a tick boundary before serialising. Saving and only
            // then cancelling left a small window in which the old clock could
            // advance after the save, losing those ticks on reload.
            if (!wasPaused) _host.PauseAsOperator();
            try
            {
                _host.Save();
            }
            catch
            {
                if (!wasPaused) _host.ResumeAsOperator();
                throw;
            }
            StopHostAndViewers();

            try
            {
                StartHost(seed, mode, next, speed, wasPaused);
                Library.StoreCatalog(Library.ActiveId, next.Json, next.RegistryRevision);
                swapped = true;
                Console.WriteLine(
                    $"[world] catalog applied: {previous.RegistryRevision} -> {next.RegistryRevision}");
            }
            catch (Exception applyError)
            {
                // Restore the old catalog against the just-written save. The
                // service remains usable even if a new definition is rejected
                // by world construction after materialisation.
                try
                {
                    StartHost(seed, mode, previous, speed, wasPaused);
                    swapped = true;
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException(
                        "Catalog apply failed and the previous world could not be restored.",
                        new AggregateException(applyError, rollbackError));
                }

                restoredFailure = new InvalidOperationException(
                    "Catalog apply failed; the previous catalog and saved world were restored.",
                    applyError);
            }
        }

        if (swapped) WorldSwapped?.Invoke();
        if (restoredFailure is not null) throw restoredFailure;
        return swapped;
    }

    /// <summary>Serialises save against an admin world/catalog swap.</summary>
    public void Save()
    {
        lock (_swap)
        {
            _host.Save();
        }
    }

    private void StopHostAndViewers()
    {
        _hostLifetime.Cancel();
        _thread.Join(TimeSpan.FromSeconds(5));
        _host.Dispose();
        _hostLifetime.Dispose();

        _viewerLifetime.Cancel();
        _viewerLifetime.Dispose();
        _viewerLifetime = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown);
    }

    private void StartHost(int seed, HexLive.Simulation.Bootstrap.GameMode mode,
        AssetGarmentCatalogSnapshot snapshot, float speed, bool paused)
    {
        var host = new WorldHost(seed, mode, _savePath, snapshot.Path,
            _verboseTrace, _includeDebugDetails, _llmOptions, _companionProfile, Library.Config(Library.ActiveId), Library.ActiveId);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown);
        try
        {
            host.SetSpeedAsOperator(speed);
            if (paused) host.PauseAsOperator();
            var thread = StartThread(host, lifetime.Token);
            _host = host;
            _hostLifetime = lifetime;
            _thread = thread;
            _catalogSnapshot = snapshot;
            _simData = snapshot.Json;
            _worldGeneration++;
            Assignments.BindWorldGeneration(_worldGeneration);
        }
        catch
        {
            lifetime.Dispose();
            host.Dispose();
            throw;
        }
    }

    private static Thread StartThread(WorldHost host, CancellationToken cancel)
    {
        var thread = new Thread(() => host.Run(cancel))
        {
            // The world's clock must not be stretched by a burst of HTTP work or
            // a GC pause in the web stack, so it gets its own thread.
            Name = "HexLive world",
            IsBackground = true,
        };
        thread.Start();
        return thread;
    }

    public void Dispose()
    {
        lock (_swap)
        {
            _hostLifetime.Cancel();
            _viewerLifetime.Cancel();
            _thread.Join(TimeSpan.FromSeconds(5));
            _host.Dispose();
            _hostLifetime.Dispose();
            _viewerLifetime.Dispose();
        }
    }
}

public readonly struct ViewerSession
{
    public ViewerSession(
        WorldHost host, string simData, CancellationToken lifetime, int worldGeneration, PlayerCharacterAssignments? assignments = null)
    {
        Host = host;
        SimData = simData;
        Lifetime = lifetime;
        WorldGeneration = worldGeneration;
        Assignments = assignments;
    }

    public PlayerCharacterAssignments? Assignments { get; }
    public WorldHost Host { get; }
    public string SimData { get; }
    public CancellationToken Lifetime { get; }
    public int WorldGeneration { get; }
}

}
