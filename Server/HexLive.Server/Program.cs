using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HexLive.Server.Llm;

namespace HexLive.Server
{

/// <summary>
/// HexLive with no Unity: the colony lives here, and viewers come and go.
///
///     dotnet run --project Server/HexLive.Server -- --seed 12345 --port 5123
///
/// Options (all optional):
///   --seed N        world seed; ignored if a save for another seed exists
///   --port N        listen port (default 5123)
///   --save PATH     save file (default ./hexlive-server.sav)
///   --simdata PATH  exported catalogs (default &lt;repo&gt;/SimData/simdata.json)
///   --autosave N    seconds between saves (default 60)
///   --debug-details include the per-NPC debug dumps in every frame
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ServerOptions? options;
        try
        {
            options = ServerOptions.Parse(args);
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is FormatException ||
            ex is OverflowException ||
            ex is InvalidOperationException)
        {
            // Configuration errors must be concise and must never echo the
            // environment value that may contain the provider secret.
            Console.Error.WriteLine($"[config] {ex.Message}");
            return 1;
        }

        if (options is null)
        {
            return 1;
        }

        using var lifetime = new CancellationTokenSource();

        WorldSupervisor worlds;
        try
        {
            // The supervisor owns the world AND its tick thread, so the admin
            // panel can start a fresh colony without restarting the process.
            worlds = new WorldSupervisor(options.Seed, options.SavePath, options.SimDataPath,
                options.VerboseTrace, options.IncludeDebugDetails, options.Llm, lifetime.Token);
        }
        catch (Exception ex)
        {
            // Almost always the §59.3 gate: no exported catalogs, so the world
            // would have run on code defaults. Refusing is the point.
            Console.Error.WriteLine($"[fatal] {ex.Message}");
            return 1;
        }

        using var _ = worlds;
        var census = worlds.Host.Census();
        Console.WriteLine(
            $"[world] seed {worlds.Host.Seed}, tick {worlds.Host.Tick}, {census.alive}/{census.total} colonists, " +
            $"{census.objects} objects, topology 0x{worlds.Host.TopologyChecksum:X8}");

        var account = Admin.AdminAccount.LoadOrCreate(options.AdminAccountPath);
        var sessions = new Admin.AdminSessions();
        var mailer = new Admin.AdminMailer();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("[server] stopping…");
            lifetime.Cancel();
        };

        var autosave = Task.Run(() => AutosaveAsync(worlds, options.AutosaveSeconds, lifetime.Token));
        var status = Task.Run(() => StatusAsync(worlds, lifetime.Token));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
        var app = builder.Build();
        app.UseWebSockets();

        app.Map("/watch", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("This endpoint speaks WebSocket.");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            Console.WriteLine($"[viewer] connected from {context.Connection.RemoteIpAddress}");
            // Host, simdata AND lifetime all come from the supervisor at accept
            // time: an admin "new world" swaps the host, refreshes the simdata
            // and cancels this token, closing the connection so the client
            // reconnects into the new world instead of pinging a frozen one.
            var viewer = new ViewerConnection(worlds.Host, socket, worlds.SimData, options.IncludeDebugDetails);
            try
            {
                await viewer.RunAsync(worlds.ViewerLifetime);
            }
            catch (OperationCanceledException)
            {
                // World swap or shutdown — expected.
            }
            finally
            {
                Console.WriteLine("[viewer] disconnected");
            }
        });

        Admin.AdminEndpoints.Map(app, worlds, account, sessions, mailer, lifetime);

        // A plain GET for eyeballing that the thing is alive.
        app.MapGet("/", () =>
        {
            var host = worlds.Host;
            var c = host.Census();
            return Results.Text(
                $"HexLive server\ntick {host.Tick}\ncolonists {c.alive}/{c.total}\nobjects {c.objects}\n" +
                $"seed {host.Seed}\ntopology 0x{host.TopologyChecksum:X8}\n" +
                $"avg {host.AverageTickMs:0.00} ms/tick\nrate {host.MeasuredTicksPerSecond:0.00} ticks/s\n" +
                "admin /admin\n");
        });

        Console.WriteLine($"[server] watching on ws://localhost:{options.Port}/watch");
        Console.WriteLine($"[server] admin panel  http://localhost:{options.Port}/admin");

        try
        {
            await app.RunAsync(lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }

        lifetime.Cancel();
        await Task.WhenAll(autosave, status).ConfigureAwait(false);

        // Last write wins: whatever happens, the colony that was alive a second
        // ago is on disk when this process ends.
        worlds.Host.Save();
        Console.WriteLine($"[world] saved at tick {worlds.Host.Tick}");
        return 0;
    }

    private static async Task AutosaveAsync(WorldSupervisor worlds, int seconds, CancellationToken cancel)
    {
        if (seconds <= 0)
        {
            return;
        }

        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancel).ConfigureAwait(false);
                worlds.Host.Save();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task StatusAsync(WorldSupervisor worlds, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancel).ConfigureAwait(false);
                var host = worlds.Host;
                var c = host.Census();
                Console.WriteLine(
                    $"[world] tick {host.Tick} · {c.alive}/{c.total} colonists · {c.objects} objects · " +
                    $"{host.AverageTickMs:0.00} ms/tick");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed class ServerOptions
{
    public int Seed { get; private set; } = 12345;

    public int Port { get; private set; } = 5123;

    public string SavePath { get; private set; } = "hexlive-server.sav";

    private string? _simDataPath;

    /// <summary>
    /// Exported catalogs (spec §59.3). Defaults to <c>SimData/simdata.json</c>
    /// found by walking UP from the working directory: `dotnet run --project
    /// Server/HexLive.Server` sets the cwd to the project folder, so a plain
    /// relative default would miss the file that is sitting at the repo root and
    /// the §59.3 gate would refuse to start for the wrong reason.
    /// </summary>
    public string SimDataPath
    {
        get => _simDataPath ??= FindSimData() ?? "SimData/simdata.json";
        private set => _simDataPath = value;
    }

    private static string? FindSimData()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "SimData", "simdata.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public int AutosaveSeconds { get; private set; } = 60;

    /// <summary>
    /// Admin credentials, kept beside the save. Not in the repo, not in the
    /// build output — it holds a password hash and a recovery address.
    /// </summary>
    public string AdminAccountPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath)) ?? ".", "hexlive-admin.txt");

    public bool IncludeDebugDetails { get; private set; }

    /// <summary>
    /// Per-tick trace chatter. Off by default, like a player build — though
    /// measured at only ~2% of the event volume, since most Trace.Emit calls do
    /// not consult the flag. See the note in <see cref="WorldHost"/>.
    /// </summary>
    public bool VerboseTrace { get; private set; }

    public LlmHostOptions Llm { get; } = new();

    public static ServerOptions? Parse(
        string[] args,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        var options = new ServerOptions();
        options.Llm.ApplyEnvironment(
            readEnvironmentVariable ?? Environment.GetEnvironmentVariable,
            CommandLineOptsIntoLlm(args));
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed" when i + 1 < args.Length:
                    options.Seed = int.Parse(args[++i]);
                    break;
                case "--port" when i + 1 < args.Length:
                    options.Port = int.Parse(args[++i]);
                    break;
                case "--save" when i + 1 < args.Length:
                    options.SavePath = args[++i];
                    break;
                case "--simdata" when i + 1 < args.Length:
                    options.SimDataPath = args[++i];
                    break;
                case "--autosave" when i + 1 < args.Length:
                    options.AutosaveSeconds = int.Parse(args[++i]);
                    break;
                case "--debug-details":
                    options.IncludeDebugDetails = true;
                    break;
                case "--verbose-trace":
                    options.VerboseTrace = true;
                    break;
                case "--llm-endpoint" when i + 1 < args.Length:
                    options.Llm.SetEndpoint(args[++i]);
                    break;
                case "--llm-model" when i + 1 < args.Length:
                    options.Llm.SetModel(args[++i]);
                    break;
                case "--llm-npcs" when i + 1 < args.Length:
                    options.Llm.SetSelectedNpcIds(args[++i]);
                    break;
                case "--llm-timeout" when i + 1 < args.Length:
                    options.Llm.SetRequestTimeoutSeconds(args[++i]);
                    break;
                case "--llm-backoff" when i + 1 < args.Length:
                    options.Llm.SetBackoffSeconds(args[++i]);
                    break;
                case "--llm-max-queued" when i + 1 < args.Length:
                    options.Llm.SetMaxQueuedRequests(args[++i]);
                    break;
                case "--llm-max-concurrent" when i + 1 < args.Length:
                    options.Llm.SetMaxConcurrentRequests(args[++i]);
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(
                        "HexLive server\n" +
                        "  --seed N         world seed (default 12345)\n" +
                        "  --port N         listen port (default 5123)\n" +
                        "  --save PATH      save file (default hexlive-server.sav)\n" +
                        "  --simdata PATH   exported catalogs (default SimData/simdata.json)\n" +
                        "  --autosave N     seconds between saves, 0 to disable (default 60)\n" +
                        "  --debug-details  include per-NPC debug dumps in every frame\n" +
                        "  --verbose-trace  match the editor's trace verbosity (only ~2% more events)\n" +
                        "  --llm-endpoint URL  enable host-side HTTP LLM provider endpoint\n" +
                        "  --llm-npcs IDS      comma-separated selected NPC ids for LLM control\n" +
                        "  --llm-model NAME    optional provider model hint\n" +
                        "  --llm-timeout N     HTTP request timeout seconds (default 12)\n" +
                        "  --llm-backoff N     delay after provider failures (default 8)\n" +
                        "  --llm-max-queued N  queued provider requests (default 2)\n" +
                        "  --llm-max-concurrent N  concurrent HTTP requests (default 2)\n" +
                        "  HEXLIVE_LLM_* environment variables provide the same settings;\n" +
                        "  HEXLIVE_LLM_API_KEY is the only accepted source for the bearer secret.\n");
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}' — try --help.");
                    return null;
            }
        }

        options.Llm.Validate();
        return options;
    }

    private static bool CommandLineOptsIntoLlm(string[] args)
    {
        foreach (var argument in args)
        {
            if (argument is "--llm-endpoint" or "--llm-npcs")
            {
                return true;
            }
        }

        return false;
    }
}

}
