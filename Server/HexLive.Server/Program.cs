using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HexLive.Simulation.Bootstrap;
using HexLive.Server.Assets;
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
///   --asset-root PATH persistent atomic content (default /var/lib/hexlive/assets)
///   --admin-icon-root PATH direct PNG previews for the authenticated catalog
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

        // §152.3: the installed server binary is also the SSH-only promotion
        // command. It never starts a world or opens an upload endpoint in this
        // mode; all bytes must already be under asset-root/staging.
        if (options.PublishCandidatePath is not null || options.PublishCandidatesDirectory is not null)
        {
            try
            {
                var registry = new AssetRegistryStore(options.AssetRoot);
                var paths = options.PublishCandidatePath is not null
                    ? new[] { options.PublishCandidatePath }
                    : Directory.GetFiles(
                            Path.GetFullPath(options.PublishCandidatesDirectory!),
                            "*.json", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray();
                if (paths.Length == 0)
                {
                    throw new InvalidDataException("candidate directory contains no JSON files");
                }

                var changed = 0;
                foreach (var path in paths)
                {
                    var candidate = AssetRegistryStore.ReadCandidateFile(path);
                    var published = await registry.PublishAsync(
                        candidate, options.RetainCurrentAssetVariants).ConfigureAwait(false);
                    if (published.Changed)
                    {
                        changed++;
                    }
                    Console.WriteLine(
                        $"[assets] {(published.Changed ? "published" : "no-op")} " +
                        $"{published.Record.Type}/{published.Record.Id} revision " +
                        $"{published.Record.Revision}, registry {published.RegistryRevision}");
                }
                Console.WriteLine($"[assets] batch complete: {paths.Length} objects, {changed} changed");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or InvalidDataException)
            {
                Console.Error.WriteLine($"[assets] publish refused: {ex.Message}");
                return 1;
            }
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

        AssetRegistryStore assetRegistry;
        AssetGarmentCatalog assetCatalog;
        try
        {
            assetRegistry = new AssetRegistryStore(options.AssetRoot);
            assetCatalog = new AssetGarmentCatalog(assetRegistry, options.SimDataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"[fatal] asset registry: {ex.Message}");
            return 1;
        }

        WorldSupervisor worlds;
        try
        {
            // The supervisor owns the world AND its tick thread, so the admin
            // panel can start a fresh colony without restarting the process.
            worlds = new WorldSupervisor(options.Seed, options.Mode, options.SavePath, assetCatalog,
                options.VerboseTrace, options.IncludeDebugDetails, options.Llm,
                options.CompanionProfile, lifetime.Token, options.PlayerAssignmentsPath);
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
        var bugToken = AccessTokenFile.LoadOrCreate(
            options.BugTokenPath, "BUG TRACKER API — agent access", "hexbug_");
        var bugs = new Bugs.BugDatabase(options.BugDatabasePath);
        bugs.ImportJsonOnce(options.BugImportPath);

        // Пути — АБСОЛЮТНЫМИ и всегда. Сейв задаётся относительным путём, и
        // запуск из другой папки молча заводит ДРУГОЙ мир и ДРУГОЙ админ-файл
        // рядом с ним: «я менял пароль, а он снова временный» — это не потеря
        // данных, это два файла в двух местах. Одна строка лога закрывает
        // целый вечер такой археологии.
        Console.WriteLine($"[server] save file     {Path.GetFullPath(options.SavePath)}");
        Console.WriteLine($"[server] admin account {Path.GetFullPath(options.AdminAccountPath)}");
        Console.WriteLine($"[server] bug database  {Path.GetFullPath(options.BugDatabasePath)}");

        Console.WriteLine($"[server] asset root    {assetRegistry.RootPath}");
        Console.WriteLine($"[server] admin icons  {options.AdminIconRoot ?? "disabled"}");
        LogAssetCoverage(assetRegistry);

        // §145.4: ОДИН реестр лиз на процесс — MCP-агенты и сетевые игроки
        // делят его, различаясь префиксом owner'а (mcp:/ws:). Создаётся, как
        // только открыта хоть одна дверь управления.
        ControlLeases? controlLeases = null;
        AccessTokenFile? playerToken = null;
        var agentSessions = new AgentSessionRegistry();
        using var deepgram = new DeepgramTokenBroker();
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

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRouting();
        builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
        builder.WebHost.UseKestrel();
        // Kestrel's socket transport disables Nagle by default; stated
        // explicitly because a 4 Hz frame stream must never be coalesced —
        // rediscovering that through a 200 ms send hiccup would cost a day.
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions>(
            o => o.NoDelay = true);
        builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
        var app = builder.Build();
        app.UseResponseCompression();
        app.UseWebSockets();

        var adminAccess = new GodMode.AdminAccess(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.SavePath))!, "hexlive-admin-clients.json"));
        var adminBus = new GodMode.AdminCommandBus(worlds, adminAccess,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.SavePath))!, "admin-receipts")) { Leases = controlLeases, Agents = agentSessions };
        var adminHub = new GodMode.AdminAgentHub(adminAccess, adminBus, worlds);
        var adminViewer = new GodMode.AdminViewerProtocol(adminAccess, adminBus, adminHub, deepgram);
        var adminAgentToken = Environment.GetEnvironmentVariable("HEXLIVE_ADMIN_AGENT_TOKEN") ?? "";
        if (adminAgentToken.Length >= 32)
        {
            worlds.Host.EnableMcpEventLog();
            worlds.WorldSwapped += () => worlds.Host.EnableMcpEventLog();
            app.Use(async (context, next) =>
            {
                var supplied = context.Request.Headers.Authorization.ToString();
                if (context.Request.Path == "/mcp" && supplied.StartsWith("Bearer ", StringComparison.Ordinal) &&
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(supplied.Substring(7)), System.Text.Encoding.UTF8.GetBytes(adminAgentToken)))
                    await GodMode.AdminMcpEndpoint.Handle(context, adminHub);
                else await next(context);
            });
        }
        GodMode.AdminAccessEndpoints.Map(app, adminAccess, sessions, adminBus);

        app.Map("/watch", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("This endpoint speaks WebSocket.");
                return;
            }

            // Host, simdata and cancellation belong to one atomic world
            // generation (§149); a world-swap must not splice old assignments
            // into the new host between three separate property reads.
            var viewerSession = worlds.CaptureViewerSession();

            // §145.3: токен игрока — заголовками HTTP-upgrade, ДО принятия
            // сокета. Неверный или отсутствующий токен не рвёт соединение:
            // это прежний анонимный зритель, ControlEnabled=false в рукопожатии.
            string? controlOwner = null;
            IReadOnlyList<int>? assignedNpcIds = null;
            if (playerToken is not null && controlLeases is not null)
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                const string bearerPrefix = "Bearer ";
                if (authorization.StartsWith(bearerPrefix, StringComparison.Ordinal) &&
                    playerToken.Matches(authorization.Substring(bearerPrefix.Length).Trim()))
                {
                    var clientId = context.Request.Headers["X-HexLive-Client-Id"].ToString().Trim();
                    if (PlayerCharacterAssignments.TryNormalizePlayerId(
                            clientId, out var playerId))
                    {
                        controlOwner = "ws:" + playerId;
                        assignedNpcIds = viewerSession.Assignments!.Reconcile(
                            viewerSession.Host, playerId,
                            options.PlayerCharacterLimit,
                            viewerSession.Lifetime,
                            viewerSession.WorldGeneration);
                    }
                }
            }

            // §83.4: сжатие больших кадров — только клиенту, который его
            // объявил. Отсутствие заголовка = старый клиент = прежние сырые
            // кадры; ProtocolVersion поэтому не бампался.
            var acceptsGzip = context.Request.Headers["X-HexLive-Accepts"]
                .ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            Console.WriteLine(
                $"[viewer] connected from {context.Connection.RemoteIpAddress}" +
                (controlOwner is null ? string.Empty : $" as {controlOwner}") +
                (acceptsGzip ? " (gzip)" : string.Empty));
            // Host, simdata AND lifetime all come from the supervisor at accept
            // time: an admin "new world" swaps the host, refreshes the simdata
            // and cancels this token, closing the connection so the client
            // reconnects into the new world instead of pinging a frozen one.
            var viewer = new ViewerConnection(viewerSession.Host, socket, viewerSession.SimData,
                options.IncludeDebugDetails, controlOwner,
                controlOwner is null ? null : controlLeases,
                assignedNpcIds, acceptsGzip,
                options.McpEnabled ? agentSessions : null,
                viewerSession.WorldGeneration,
                controlOwner is null ? null : deepgram,
                Guid.NewGuid().ToString("N"), adminViewer, viewerSession.Assignments);
            try
            {
                await viewer.RunAsync(viewerSession.Lifetime);
            }
            catch (OperationCanceledException)
            {
                // World swap or shutdown — expected.
            }
            finally
            {
                viewer.Disconnect();
                Console.WriteLine("[viewer] disconnected");
            }
        });

        Admin.AdminEndpoints.Map(
            app, worlds, account, sessions, mailer, lifetime, assetRegistry, assetCatalog,
            options.AdminIconRoot, bugs);
        WorldCreationEndpoints.Map(app, worlds, account, sessions, assetRegistry, options.CompanionProfile, playerToken);
        Bugs.BugApiEndpoints.Map(app, bugs, bugToken, playerToken, sessions);

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

            Mcp.McpEndpoint.Map(app, worlds, mcpToken, leases, agentSessions, spec);
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
                $"назначения в {options.PlayerAssignmentsPath}; " +
                "клиенту: -hexlive-token <токен|путь>)");
        }

        // §145.4: world-swap — внешние контуры забывают старый мир: реестр лиз
        // чистится, зеркало хроники MCP заводится на НОВОМ хосте (иначе
        // read_events после свапа молча пустеет).
        worlds.WorldSwapped += () =>
        {
            controlLeases?.Clear();
            agentSessions.Clear();
            var viewerSession = worlds.CaptureViewerSession();
            // Assignments are owned by the active world library entry.
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
        var agentSweep = options.McpEnabled
            ? Task.Run(() => AgentSweepAsync(worlds, agentSessions, lifetime.Token))
            : Task.CompletedTask;

        // A plain GET for eyeballing that the thing is alive.
        AssetEndpoints.Map(app, assetRegistry);

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
        await Task.WhenAll(autosave, status, leaseSweep, agentSweep).ConfigureAwait(false);

        // Last write wins: whatever happens, the colony that was alive a second
        // ago is on disk when this process ends.
        worlds.Save();
        Console.WriteLine($"[world] saved at tick {worlds.Host.Tick}");
        return 0;
    }

    /// <summary>
    /// §152.4: say out loud which platforms this registry can actually serve.
    /// A Windows Player asking for content nobody published gets an index that
    /// is short, not broken, so the gap has to be visible from the server side.
    /// </summary>
    private static void LogAssetCoverage(AssetRegistryStore registry)
    {
        AssetCoverageReport report;
        try
        {
            report = registry.AuditCoverage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException or JsonException)
        {
            Console.Error.WriteLine($"[assets] coverage audit failed: {ex.Message}");
            return;
        }

        Console.WriteLine(
            $"[assets] registry revision {report.RegistryRevision}, " +
            $"{report.ActiveObjects} active, {report.RetiredObjects} retired, " +
            $"{report.Platforms.Count} platform/profile pair(s)");
        foreach (var platform in report.Platforms)
        {
            var line =
                $"[assets] coverage {platform.Platform}/{platform.RuntimeProfile}: " +
                $"{platform.Covered}/{report.ActiveObjects}";
            if (platform.Missing.Count > 0)
            {
                var named = platform.Missing.Take(8)
                    .Select(key => key.Type + "/" + key.Id);
                line += $" — missing {platform.Missing.Count}: " +
                    string.Join(", ", named) +
                    (platform.Missing.Count > 8 ? ", …" : string.Empty);
            }

            Console.WriteLine(line);
        }
    }

    private static async Task LeaseSweepAsync(
        WorldSupervisor worlds, ControlLeases leases, CancellationToken cancel)
    {
        var expired = new System.Collections.Generic.List<(int NpcId, string Owner)>();
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                worlds.RefreshCustomRoster();
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

    private static async Task AgentSweepAsync(
        WorldSupervisor worlds, AgentSessionRegistry sessions, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                var viewer = worlds.CaptureViewerSession();
                sessions.Sweep(viewer.Host, viewer.WorldGeneration);
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
                worlds.Save();
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
            case "maniac":
            case "3":
                mode = GameMode.Maniac;
                return true;
            case "islands":
            case "4":
                mode = GameMode.Islands;
                return true;
            default:
                mode = GameMode.Feud;
                return false;
        }
    }

    public void ContinueExistingSaveIfPresent(Action<string>? log = null)
    {
        var header = ServerSaveHeader.ReadIfPresent(WorldLibrary.StartupSavePath(SavePath));
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

    /// <summary>§152: persistent objects are independent of this server build.</summary>
    public string AssetRoot { get; private set; } = "/var/lib/hexlive/assets";

    private string? _adminIconRoot;

    /// <summary>
    /// §154.3 optional direct PNG previews for the authenticated admin. These
    /// are source/export textures, never files decoded from an AssetBundle.
    /// </summary>
    public string? AdminIconRoot
    {
        get => _adminIconRoot ??= FindAdminIconRoot();
        private set => _adminIconRoot = string.IsNullOrWhiteSpace(value)
            ? null
            : Path.GetFullPath(value);
    }

    /// <summary>Administrative one-shot mode, reachable only from the process CLI.</summary>
    public string? PublishCandidatePath { get; private set; }

    /// <summary>SSH-only bootstrap/resume mode for many independent candidates.</summary>
    public string? PublishCandidatesDirectory { get; private set; }

    /// <summary>
    /// Administrative platform bootstrap: keep verified variants absent from
    /// the incoming candidate. Metadata must remain byte-for-byte equivalent.
    /// </summary>
    public bool RetainCurrentAssetVariants { get; private set; }

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

    private static string? FindAdminIconRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            var candidate = Path.Combine(
                directory.FullName, "Assets", "HexLiveContent", "Icons");
            if (Directory.Exists(candidate)) return candidate;
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

    /// <summary>§160: explicitly enabled authored character preset.</summary>
    public string? CompanionProfile { get; private set; }
    public int PlayerCharacterLimit { get; private set; } = PlayerCharacterAssignments.DefaultCharacterLimit;

    /// <summary>
    /// §145.3: сетевое управление ИГРОКА выключено по умолчанию по той же
    /// логике, что MCP: дверь, которой не просили, должна быть закрыта.
    /// Включение генерирует токен игрока (hexlive-player.txt рядом с сейвом).
    /// </summary>
    public bool ControlEnabled { get; private set; }

    public string PlayerTokenPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath)) ?? ".", "hexlive-player.txt");

    /// <summary>§114: central SQLite bug tracker, colocated with the save.</summary>
    public string BugDatabasePath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath)) ?? ".", "hexlive-bugs.sqlite3");

    public string BugTokenPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath)) ?? ".", "hexlive-bugs-token.txt");

    /// <summary>One-shot legacy import. The database records completion.</summary>
    public string BugImportPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath)) ?? ".", "BUGS.json");

    /// <summary>§149: постоянные назначения playerId → npcIds живут рядом с
    /// тем же сейвом и переносятся вместе с серверным runtime-каталогом.</summary>
    public string PlayerAssignmentsPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath)) ?? ".", "hexlive-players.json");

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
                            $"Unknown mode '{args[i]}' — feud, bigisland, hugeisland, maniac or islands.");
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
                case "--asset-root" when i + 1 < args.Length:
                    options.AssetRoot = args[++i];
                    break;
                case "--admin-icon-root" when i + 1 < args.Length:
                    options.AdminIconRoot = args[++i];
                    break;
                case "--publish-candidate" when i + 1 < args.Length:
                    options.PublishCandidatePath = args[++i];
                    break;
                case "--publish-candidates" when i + 1 < args.Length:
                    options.PublishCandidatesDirectory = args[++i];
                    break;
                case "--retain-current-asset-variants":
                    options.RetainCurrentAssetVariants = true;
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
                case "--companion" when i + 1 < args.Length:
                    options.CompanionProfile = args[++i].Trim().ToLowerInvariant();
                    break;
                case "--character-preset" when i + 1 < args.Length:
                    options.CompanionProfile = args[++i].Trim().ToLowerInvariant();
                    break;
                case "--player-characters" when i + 1 < args.Length:
                    options.PlayerCharacterLimit = int.Parse(args[++i]);
                    if (options.PlayerCharacterLimit is < 1 or > 8)
                        throw new ArgumentException("--player-characters must be 1..8");
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
                        "  --mode NAME      fresh world mode: feud | bigisland | hugeisland | maniac | islands (default feud)\n" +
                        "  --port N         listen port (default 5123)\n" +
                        "  --save PATH      save file (default hexlive-server.sav)\n" +
                        "  --simdata PATH   exported catalogs (default SimData/simdata.json)\n" +
                        "  --asset-root PATH persistent atomic content (default /var/lib/hexlive/assets)\n" +
                        "  --admin-icon-root PATH direct PNG previews for authenticated catalog\n" +
                        "  --publish-candidate PATH  validate/promote one staged object, then exit\n" +
                        "  --publish-candidates DIR  promote sorted candidate JSON files, then exit\n" +
                        "  --retain-current-asset-variants  add a platform without dropping verified existing variants\n" +
                        "  --autosave N     seconds between saves, 0 to disable (default 60)\n" +
                        "  --debug-details  include per-NPC debug dumps in every frame\n" +
                        "  --verbose-trace  match the editor's trace verbosity (only ~2% more events)\n" +
                        "  --mcp            expose MCP control at /mcp (off by default)\n" +
                        "  --character-preset masha  ensure the authored Masha body preset exists\n" +
                        "  --companion masha  deprecated alias for --character-preset masha\n" +
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

        if (options.CompanionProfile is { Length: > 0 } profile)
        {
            foreach (var entry in profile.Split(','))
                if (!HexLive.Simulation.Runtime.CharacterPresetRegistry.ProfileIds.Contains(entry))
                    throw new ArgumentException($"unknown character preset '{entry}'");
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
