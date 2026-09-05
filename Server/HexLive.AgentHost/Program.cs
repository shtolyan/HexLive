using HexLive.AgentHost;
using System.Runtime.InteropServices;

var doctor = args.Any(value => string.Equals(value, "--doctor", StringComparison.Ordinal));
try
{
    // Doctor validates that provider keys are configured, but never calls the
    // paid provider endpoints.
    var options = AgentHostOptions.Load(requireProviders: true);
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
    var runtime = new AgentHostRuntime(options);
    await runtime.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    // Provider bodies, transcripts, audio, authorization and keys are never logged.
    Console.Error.WriteLine($"AgentHost stopped: {ex.GetType().Name}; run masha doctor to check configuration.");
    Environment.ExitCode = 1;
}
