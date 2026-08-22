using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HexLive.Simulation.Bootstrap;
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

        try
        {
            options.ContinueExistingSaveIfPresent(Console.WriteLine);
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is InvalidDataException ||
            ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[fatal] {ex.Message}");
            return 1;
        }

        using var lifetime = new CancellationTokenSource();

        WorldSupervisor worlds;
        try
        {
            // The supervisor owns the world AND its tick thread, so the admin
            // panel can start a fresh colony without restarting the process.
            worlds = new WorldSupervisor(options.Seed, options.Mode, options.SavePath, options.SimDataPath,
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

        // Пути — АБСОЛЮТНЫМИ и всегда. Сейв задаётся относительным путём, и
        // запуск из другой папки молча заводит ДРУГОЙ мир и ДРУГОЙ админ-файл
        // рядом с ним: «я менял пароль, а он снова временный» — это не потеря
        // данных, это два файла в двух местах. Одна строка лога закрывает
        // целый вечер такой археологии.
        Console.WriteLine($"[server] save file     {Path.GetFullPath(options.SavePath)}");
        Console.WriteLine($"[server] admin account {Path.GetFullPath(options.AdminAccountPath)}");

        // §145.4: ОДИН реестр лиз на процесс — MCP-агенты и сетевые игроки
        // делят его, различаясь префиксом owner'а (mcp:/ws:). Создаётся, как
        // только открыта хоть одна дверь управления.
        ControlLeases? controlLeases = null;
        AccessTokenFile? playerToken = null;
        if (options.ControlEnabled || options.McpEnabled)
        {
            controlLeases = new ControlLeases(options.McpLeaseSeconds);
        }

        if (options.ControlEnabled)
        {
            playerToken = AccessTokenFile.LoadOrCreate(
                options.PlayerTokenPath, "PLAYER CONTROL — first run", "hexplay_");
        }

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

            // §145.3: токен игрока — заголовками HTTP-upgrade, ДО принятия
            // сокета. Неверный или отсутствующий токен не рвёт соединение:
            // это прежний анонимный зритель, ControlEnabled=false в рукопожатии.
            string? controlOwner = null;
            if (playerToken is not null && controlLeases is not null)
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                const string bearerPrefix = "Bearer ";
                if (authorization.StartsWith(bearerPrefix, StringComparison.Ordinal) &&
                    playerToken.Matches(authorization.Substring(bearerPrefix.Length).Trim()))
                {
                    var clientId = context.Request.Headers["X-HexLive-Client-Id"].ToString().Trim();
                    controlOwner = "ws:" + (string.IsNullOrEmpty(clientId)
                        ? context.Connection.Id
                        : clientId);
                }
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            Console.WriteLine(
                $"[viewer] connected from {context.Connection.RemoteIpAddress}" +
                (controlOwner is null ? string.Empty : $" as {controlOwner}"));
            // Host, simdata AND lifetime all come from the supervisor at accept
            // time: an admin "new world" swaps the host, refreshes the simdata
            // and cancels this token, closing the connection so the client
            // reconnects into the new world instead of pinging a frozen one.
            var viewer = new ViewerConnection(worlds.Host, socket, worlds.SimData,
                options.IncludeDebugDetails, controlOwner,
                controlOwner is null ? null : controlLeases);
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

        if (options.McpEnabled)
        {
            // MCP endpoint живёт весь процесс, поэтому передаём supervisor, а
            // не текущий host: каждый tools/call должен увидеть мир, который
            // существует ПОСЛЕ возможной «новой колонии» в админке.
            var mcpToken = Mcp.McpAccessToken.LoadOrCreate(options.McpTokenPath);
            var leases = controlLeases!;

            // §144.6: зеркало хроники заводится вместе с дверью и только с ней.
            worlds.Host.EnableMcpEventLog();

            // §144.9: агент за сетью не имеет ни репозитория, ни файлов рядом —
            // спека едет к нему тем же швом, что и всё остальное.
            var spec = Mcp.SpecLibrary.Discover(options.SpecDir);

            Mcp.McpEndpoint.Map(app, worlds, mcpToken, leases, spec);
            Console.WriteLine(
                $"[server] mcp control    http://localhost:{options.Port}/mcp " +
                $"(токен в {options.McpTokenPath}, лиз {leases.TimeoutSeconds} с)");
            Console.WriteLine(spec.Available
                ? $"[server] mcp spec       {spec.Sections().Count} разделов доступно агенту"
                : "[server] mcp spec       НЕ НАЙДЕНА — агент будет знать о мире только " +
                  "из описаний инструментов; путь задаётся --spec-dir");
        }

        if (options.ControlEnabled)
        {
            Console.WriteLine(
                $"[server] player control ws://localhost:{options.Port}/watch " +
                $"(токен в {options.PlayerTokenPath}, лиз {controlLeases!.TimeoutSeconds} с; " +
                "клиенту: -hexlive-token <токен|путь>)");
        }

        // §145.4: world-swap — внешние контуры забывают старый мир: реестр лиз
        // чистится, зеркало хроники MCP заводится на НОВОМ хосте (иначе
        // read_events после свапа молча пустеет).
        worlds.WorldSwapped += () =>
        {
            controlLeases?.Clear();
            if (options.McpEnabled)
            {
                worlds.Host.EnableMcpEventLog();
            }
        };

        // §145.4: истёкший лиз больше не молчит — брошенная колонистка
        // возвращается под ИИ тем же переходом, что тумблер 🎮→🧠. Всегда через
        // СВЕЖИЙ worlds.Host: после свапа команда не должна уйти мёртвому миру.
        var leaseSweep = controlLeases is null
            ? Task.CompletedTask
            : Task.Run(() => LeaseSweepAsync(worlds, controlLeases, lifetime.Token));

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
        await Task.WhenAll(autosave, status, leaseSweep).ConfigureAwait(false);

        // Last write wins: whatever happens, the colony that was alive a second
        // ago is on disk when this process ends.
        worlds.Host.Save();
        Console.WriteLine($"[world] saved at tick {worlds.Host.Tick}");
        return 0;
    }

    private static async Task LeaseSweepAsync(
        WorldSupervisor worlds, ControlLeases leases, CancellationToken cancel)
    {
        var expired = new System.Collections.Generic.List<(int NpcId, string Owner)>();
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancel).ConfigureAwait(false);
                expired.Clear();
                leases.CollectExpired(expired);
                foreach (var (npcId, owner) in expired)
                {
                    var admission = worlds.Host.SubmitManualCommand(
                        new HexLive.Simulation.Runtime.SetManualControlCommand(
                            new HexLive.Simulation.Common.EntityId(npcId), false));
                    Console.WriteLine(
                        $"[control] lease of NPC{npcId} by {owner} expired — " +
                        $"returned to AI ({admission.Status})");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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
                var llmFailures = host.DrainLlmProviderFailureSummary();
                if (llmFailures is not null)
                {
                    Console.Error.WriteLine(llmFailures);
                }
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

    // §146: which scenario a FRESH world is generated as. If --save points at
    // an existing file, its header overrides both --seed and --mode at startup:
    // resuming a colony must be the safe default, even when the operator forgets
    // that yesterday's world was BigIsland and today's shell still says Feud.
    public GameMode Mode { get; private set; } = GameMode.Feud;

    // Shared by --mode and the admin panel's new-world form.
    public static bool TryParseMode(string? value, out GameMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "feud":
            case "0":
                mode = GameMode.Feud;
                return true;
            case "bigisland":
            case "big-island":
            case "1":
                mode = GameMode.BigIsland;
                return true;
            case "hugeisland":
            case "huge-island":
            case "2":
                mode = GameMode.HugeIsland;
                return true;
            default:
                mode = GameMode.Feud;
                return false;
        }
    }

    public void ContinueExistingSaveIfPresent(Action<string>? log = null)
    {
        var header = ServerSaveHeader.ReadIfPresent(SavePath);
        if (header is null)
        {
            return;
        }

        if (Seed != header.Value.Seed || Mode != header.Value.Mode)
        {
            log?.Invoke(
                $"[server] continuing existing save: seed {header.Value.Seed}, " +
                $"mode {header.Value.Mode}, tick {header.Value.Tick} " +
                $"(startup requested seed {Seed}, mode {Mode})");
        }

        Seed = header.Value.Seed;
        Mode = header.Value.Mode;
    }

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

    /// <summary>
    /// §144: MCP выключен по умолчанию, и это не осторожность ради
    /// осторожности. Зритель по вебсокету может только смотреть, админка
    /// прячется за паролем и правит мир целиком, а MCP — единственная дверь,
    /// через которую посторонний процесс отдаёт приказы конкретным людям.
    /// Дверь, которой не просили, должна быть закрыта.
    /// </summary>
    public bool McpEnabled { get; private set; }

    /// <summary>
    /// §145.3: сетевое управление ИГРОКА выключено по умолчанию по той же
    /// логике, что MCP: дверь, которой не просили, должна быть закрыта.
    /// Включение генерирует токен игрока (hexlive-player.txt рядом с сейвом).
    /// </summary>
    public bool ControlEnabled { get; private set; }

    public string PlayerTokenPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath)) ?? ".", "hexlive-player.txt");

    public int McpLeaseSeconds { get; private set; } = ControlLeases.DefaultTimeoutSeconds;

    /// <summary>
    /// §144.9: где лежит спека. Пусто — берётся каталог Spec рядом со сборкой,
    /// куда его кладёт csproj. Флаг нужен запуску из чужого места.
    /// </summary>
    public string? SpecDir { get; private set; }

    public string McpTokenPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath)) ?? ".", "hexlive-mcp.txt");

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
                case "--mode" when i + 1 < args.Length:
                    if (!TryParseMode(args[++i], out var mode))
                    {
                        Console.Error.WriteLine(
                            $"Unknown mode '{args[i]}' — feud, bigisland or hugeisland.");
                        return null;
                    }
                    options.Mode = mode;
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
                case "--mcp":
                    options.McpEnabled = true;
                    break;
                case "--control":
                    options.ControlEnabled = true;
                    break;
                case "--mcp-lease" when i + 1 < args.Length:
                    options.McpLeaseSeconds = int.Parse(args[++i]);
                    break;
                case "--spec-dir" when i + 1 < args.Length:
                    options.SpecDir = args[++i];
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
                        "  --mode NAME      fresh world mode: feud | bigisland | hugeisland (default feud)\n" +
                        "  --port N         listen port (default 5123)\n" +
                        "  --save PATH      save file (default hexlive-server.sav)\n" +
                        "  --simdata PATH   exported catalogs (default SimData/simdata.json)\n" +
                        "  --autosave N     seconds between saves, 0 to disable (default 60)\n" +
                        "  --debug-details  include per-NPC debug dumps in every frame\n" +
                        "  --verbose-trace  match the editor's trace verbosity (only ~2% more events)\n" +
                        "  --mcp            expose MCP control at /mcp (off by default)\n" +
                        "  --control        allow player NPC control over /watch (token in hexlive-player.txt)\n" +
                        "  --mcp-lease N    seconds a control lease survives without commands (default 120)\n" +
                        "  --spec-dir PATH  spec served to MCP agents (default: Spec/ beside the binary)\n" +
                        "  --llm-endpoint URL  enable host-side HTTP LLM provider endpoint\n" +
                        "  --llm-npcs IDS      comma-separated selected NPC ids for LLM control\n" +
                        "  --llm-model NAME    optional provider model hint\n" +
                        "  --llm-timeout N     HTTP request timeout seconds (default 12)\n" +
                        "  --llm-backoff N     delay after provider failures (default 3)\n" +
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

internal readonly struct ServerSaveHeader
{
    private const int SaveMagic = unchecked((int)0x48584C53); // "HXLS"
    private const int CurrentSaveVersion = 2;

    private ServerSaveHeader(int seed, GameMode mode, int tick)
    {
        Seed = seed;
        Mode = mode;
        Tick = tick;
    }

    public int Seed { get; }

    public GameMode Mode { get; }

    public int Tick { get; }

    public static ServerSaveHeader? ReadIfPresent(string savePath)
    {
        var fullPath = Path.GetFullPath(savePath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        using var file = File.OpenRead(fullPath);
        using var reader = new BinaryReader(file);
        if (file.Length < sizeof(int) * 5)
        {
            throw new InvalidDataException(
                $"Save file '{fullPath}' is too short to identify safely; refusing to start over it.");
        }

        if (reader.ReadInt32() != SaveMagic)
        {
            throw new InvalidDataException(
                $"Save file '{fullPath}' has an unknown header; refusing to start over it.");
        }

        var version = reader.ReadInt32();
        if (version != 1 && version != CurrentSaveVersion)
        {
            throw new InvalidDataException(
                $"Save file '{fullPath}' has unsupported version {version}; refusing to start over it.");
        }

        var seed = reader.ReadInt32();
        var mode = version >= 2 ? (GameMode)reader.ReadInt32() : GameMode.Feud;
        if (!Enum.IsDefined(typeof(GameMode), mode))
        {
            throw new InvalidDataException(
                $"Save file '{fullPath}' names unsupported world mode {(int)mode}; refusing to start over it.");
        }

        var tick = reader.ReadInt32();
        return new ServerSaveHeader(seed, mode, tick);
    }
}

}
