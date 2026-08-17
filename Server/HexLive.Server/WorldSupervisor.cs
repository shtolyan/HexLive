using System;
using System.IO;
using System.Threading;
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
    private readonly string _simDataPath;
    private readonly bool _verboseTrace;
    private readonly bool _includeDebugDetails;
    private readonly LlmHostOptions _llmOptions;
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

    public WorldSupervisor(int seed, string savePath, string simDataPath, bool verboseTrace,
        bool includeDebugDetails, LlmHostOptions llmOptions, CancellationToken appShutdown)
    {
        _savePath = savePath;
        _simDataPath = simDataPath;
        _verboseTrace = verboseTrace;
        _includeDebugDetails = includeDebugDetails;
        _llmOptions = llmOptions;
        _appShutdown = appShutdown;

        _host = new WorldHost(seed, savePath, simDataPath, verboseTrace, includeDebugDetails, llmOptions);
        _hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(appShutdown);
        _viewerLifetime = CancellationTokenSource.CreateLinkedTokenSource(appShutdown);
        _thread = StartThread(_host, _hostLifetime.Token);
        _simData = File.ReadAllText(simDataPath);
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
    /// Throws the current colony away and starts a fresh one.
    /// <para>
    /// Destructive and irreversible, which is why the caller (the admin panel)
    /// asks for confirmation and why the old save is MOVED aside rather than
    /// deleted — "new world" is one misclick away from "the colony I watched
    /// for a month", and a backup file costs nothing.
    /// </para>
    /// </summary>
    public void StartNewWorld(int seed)
    {
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

            _host = new WorldHost(seed, _savePath, _simDataPath, _verboseTrace, _includeDebugDetails, _llmOptions);
            _hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(_appShutdown);
            _thread = StartThread(_host, _hostLifetime.Token);
            _simData = File.ReadAllText(_simDataPath);
            Console.WriteLine($"[world] NEW WORLD started, seed {seed}");
        }

        WorldSwapped?.Invoke();
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

}
