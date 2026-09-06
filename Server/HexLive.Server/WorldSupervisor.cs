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
    private readonly string _savePath;
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
        CancellationToken appShutdown)
    {
        _savePath = savePath;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _verboseTrace = verboseTrace;
        _includeDebugDetails = includeDebugDetails;
        _llmOptions = llmOptions;
        _companionProfile = companionProfile;
        _appShutdown = appShutdown;

        _catalogSnapshot = _catalog.Materialize();
        _host = new WorldHost(seed, mode, savePath, _catalogSnapshot.Path,
            verboseTrace, includeDebugDetails, llmOptions, companionProfile);
        _hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(appShutdown);
        _viewerLifetime = CancellationTokenSource.CreateLinkedTokenSource(appShutdown);
        _thread = StartThread(_host, _hostLifetime.Token);
        _simData = _catalogSnapshot.Json;
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
                _host, _simData, _viewerLifetime.Token, _worldGeneration);
        }
    }

    /// <summary>
    /// Throws the current colony away and starts a fresh one.
    /// <para>
    /// Destructive and irreversible, which is why the caller (the admin panel)
    /// asks for confirmation and why the old save is MOVED aside rather than
    /// deleted — "new world" is one misclick away from "the colony I watched
    /// for a month", and a backup file costs nothing.
    /// </para>
    /// </summary>
    public void StartNewWorld(int seed,
        HexLive.Simulation.Bootstrap.GameMode mode = HexLive.Simulation.Bootstrap.GameMode.Feud)
    {
        // Validate and persist the next immutable effective catalog before the
        // running world is touched.
        var snapshot = _catalog.Materialize();
        lock (_swap)
        {
            // Stop the old world first so nothing steps it while we swap.
            _hostLifetime.Cancel();
            _thread.Join(TimeSpan.FromSeconds(5));
            _host.Dispose();
            _hostLifetime.Dispose();

            // Disconnect everyone watching the old world. Their connections
            // hold the old host and would keep answering pings over a frozen
            // colony forever; a close puts each client into its reconnect
            // path against the NEW world instead.
            _viewerLifetime.Cancel();
            _viewerLifetime.Dispose();
            _viewerLifetime = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown);

            ArchiveSave();

            _host = new WorldHost(seed, mode, _savePath, snapshot.Path,
                _verboseTrace, _includeDebugDetails, _llmOptions, _companionProfile);
            _hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown);
            _thread = StartThread(_host, _hostLifetime.Token);
            _catalogSnapshot = snapshot;
            _simData = snapshot.Json;
            _worldGeneration++;
            Console.WriteLine(
                $"[world] NEW WORLD started, seed {seed}, catalog revision {snapshot.RegistryRevision}");
        }

        WorldSwapped?.Invoke();
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
            _verboseTrace, _includeDebugDetails, _llmOptions, _companionProfile);
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
        }
        catch
        {
            lifetime.Dispose();
            host.Dispose();
            throw;
        }
    }

    private void ArchiveSave()
    {
        if (!File.Exists(_savePath))
        {
            return;
        }

        // Timestamped so repeated "new world" clicks do not overwrite each
        // other's backups.
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var archived = _savePath + "." + stamp + ".bak";
        try
        {
            File.Move(_savePath, archived, overwrite: true);
            Console.WriteLine($"[world] previous colony archived to {Path.GetFileName(archived)}");
        }
        catch (Exception ex)
        {
            // Better to refuse the new world than to silently destroy the old
            // one because the disk was full or read-only.
            throw new InvalidOperationException(
                $"Could not archive the existing save ({ex.Message}) — refusing to overwrite it.", ex);
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
        WorldHost host, string simData, CancellationToken lifetime, int worldGeneration)
    {
        Host = host;
        SimData = simData;
        Lifetime = lifetime;
        WorldGeneration = worldGeneration;
    }

    public WorldHost Host { get; }
    public string SimData { get; }
    public CancellationToken Lifetime { get; }
    public int WorldGeneration { get; }
}

}
