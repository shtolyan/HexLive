#nullable enable

namespace HexLive.UnityPresentation.Bootstrap.Remote
{

/// <summary>
/// The remote playhead's vital signs, published once per Update by
/// <see cref="RemoteSocketBackend"/> for anything that wants to watch the link
/// without touching the backend — today the §71 LocomotionLagRecorder, so a
/// smoothness take records WHY a frame stuttered (thin buffer, jitter spike,
/// slew correction) next to WHAT the player saw. Static and dumb on purpose:
/// plumbing these four floats through ISimulationBackend would tax the local
/// backend with fields that are meaningless for it.
/// </summary>
public static class RemoteLinkDiagnostics
{
    /// <summary>True once a remote backend has published at least once this session.</summary>
    public static bool Active { get; private set; }

    /// <summary>How far ahead of the playhead the newest arrived data is, in ticks.</summary>
    public static float BufferedTicks { get; private set; }

    /// <summary>The interpolation delay in force, in ticks.</summary>
    public static float DelayTicks { get; private set; }

    /// <summary>Signed playhead-to-target distance, in ticks.</summary>
    public static float TargetErrorTicks { get; private set; }

    /// <summary>P95 arrival lateness over the fastest observed path, ms.</summary>
    public static float JitterP95Milliseconds { get; private set; }

    /// <summary>Undecoded frames sitting in the inbox queue.</summary>
    public static int QueuedFrames { get; private set; }

    /// <summary>Whether -hexlive-netsim is shaping this session's traffic.</summary>
    public static bool NetSimEnabled { get; private set; }

    public static void Publish(
        float bufferedTicks, float delayTicks, float targetErrorTicks,
        float jitterP95Milliseconds, int queuedFrames, bool netSimEnabled)
    {
        Active = true;
        BufferedTicks = bufferedTicks;
        DelayTicks = delayTicks;
        TargetErrorTicks = targetErrorTicks;
        JitterP95Milliseconds = jitterP95Milliseconds;
        QueuedFrames = queuedFrames;
        NetSimEnabled = netSimEnabled;
    }
}

}
