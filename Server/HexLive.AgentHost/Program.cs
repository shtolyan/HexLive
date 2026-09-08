using HexLive.AgentHost;
using System.Runtime.InteropServices;

var doctor = args.Any(value => string.Equals(value, "--doctor", StringComparison.Ordinal));
try
{
    // Doctor validates that provider keys are configured, but never calls the
    // paid provider endpoints.
    var options = AgentHostOptions.Load(requireProviders: true);
    if (args.Contains("--check-model"))
    {
        using var probe = new AgentProviders(options.ProviderOptions);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(130));
        var result = await probe.DecideAsync("heartbeat", "{\"diagnostic\":true}",
            "Проверка соединения, не игровой ход. Не выбирай действие и не обновляй память. Молчи.",
            "", Array.Empty<string>(), deadline.Token);
        if (result.Action != null) throw new InvalidDataException("Diagnostic must not propose an action.");
        Console.WriteLine($"Model check passed: backend={options.LlmBackend}; validated decision; no MCP actions, memory writes or TTS.");
        return;
    }
    if (doctor)
    {
        await AgentHostRuntime.DoctorAsync(options, CancellationToken.None);
        return;
    }

    using var shutdown = new CancellationTokenSource();
    using var termination = OperatingSystem.IsWindows() ? null :
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
    // §163: CLI and Studio share a non-deleted exclusive workspace lock.
    using var workspaceLease = new FileStream(Path.Combine(options.MemoryDirectory, ".agent-studio.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var providers = new KnowledgeAwareAgentProviders(new AgentProviders(options.ProviderOptions),
        new McpClient(options.ProviderOptions));
    var runtime = new AgentHostRuntime(options, providers);
    await runtime.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    // Provider bodies, transcripts, audio, authorization and keys are never logged.
    Console.Error.WriteLine($"AgentHost stopped: {ex.GetType().Name}; run masha doctor to check configuration.");
    Environment.ExitCode = 1;
}
