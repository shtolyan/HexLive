#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;

namespace HexLive.UnityPresentation.Bootstrap
{

public enum LinkState
{
    /// <summary>The world is in this process. Nothing can go wrong with the link.</summary>
    Local,

    /// <summary>Opening the connection / waiting for the handshake.</summary>
    Connecting,

    /// <summary>Frames are arriving.</summary>
    Live,

    /// <summary>
    /// Connected, but nothing has arrived for a while and the world is not
    /// paused. Distinct from <see cref="Reconnecting"/> because a stalled link
    /// often recovers on its own, and yanking the socket would make it worse.
    /// </summary>
    Stalled,

    /// <summary>The socket dropped; retrying with backoff.</summary>
    Reconnecting,

    /// <summary>
    /// Unrecoverable — a different build, a different world. Retrying cannot
    /// help, so we stop and say why instead of flapping forever.
    /// </summary>
    Failed,
}

/// <summary>
/// How presentation is getting its world, and how well. Always
/// <see cref="LinkState.Local"/> for a local game, so a HUD can read this
/// unconditionally without knowing which backend it has.
/// </summary>
public readonly struct SimulationLink
{
    public SimulationLink(LinkState state, int pingMilliseconds, string message)
    {
        State = state;
        PingMilliseconds = pingMilliseconds;
        Message = message;
    }

    public LinkState State { get; }

    /// <summary>Smoothed round-trip time; -1 when not measured (local, or not yet).</summary>
    public int PingMilliseconds { get; }

    /// <summary>Human-readable detail for the failed/stalled cases; may be null.</summary>
    public string Message { get; }

    public bool IsRemote => State != LinkState.Local;

    public static SimulationLink Local => new(LinkState.Local, -1, null);
}

/// <summary>
/// Everything the presentation layer is allowed to know about "the simulation".
/// <para>
/// The point of this interface is that a colonist walking across the screen does
/// not care whether the world is being stepped in this process or on a machine
/// somewhere else. Presentation reads a <see cref="WorldSnapshot"/>, an event
/// stream and a clock; nothing here hands out a <c>SimulationEngine</c> or a
/// <c>WorldState</c>, because those exist only in the local case.
/// </para>
/// <para>
/// Rule for new presentation code: depend on THIS, never on
/// <c>SimulationRunnerBehaviour.Engine</c>. That property still exists for the
/// dev/test scenes, which build throwaway worlds and mutate them directly, and
/// it returns null in any non-local mode.
/// </para>
/// </summary>
public interface ISimulationSource
{
    /// <summary>A world exists and can be read.</summary>
    bool IsReady { get; }

    /// <summary>Where the world is coming from and how healthy that is.</summary>
    SimulationLink Link { get; }

    bool IsCompleted { get; }

    /// <summary>
    /// Spec 29C.1 world seed. Available before the first snapshot arrives (the
    /// history log picks its file by it), which is why it is here and not only
    /// on the snapshot.
    /// </summary>
    int Seed { get; }

    /// <summary>The last tick presentation may consider real.</summary>
    int CurrentTick { get; }

    /// <summary>
    /// Fraction [0..1) of the way from <see cref="CurrentTick"/> to the next
    /// tick, for interpolating between discrete states.
    /// <para>
    /// MUST stay consistent with <see cref="SpeedMultiplier"/>: the renderer
    /// feeds that multiplier to the animators while alpha drives the root
    /// motion, so if the two disagree the feet slide and jump arcs overshoot.
    /// An implementation that estimates its own tick rate has to derive BOTH
    /// numbers from that one estimate.
    /// </para>
    /// </summary>
    float TickAlpha { get; }

    bool IsPaused { get; }

    float SpeedMultiplier { get; }

    /// <summary>
    /// The current frame's read model. Reused in place between ticks, so
    /// consumers must re-poll every frame and never hold the instance.
    /// </summary>
    WorldSnapshot? CreateSnapshot();

    // Clock. Advisory when the world is not ours: a remote implementation
    // forwards these as requests and reflects whatever the owner decides.
    void Pause();

    void Resume();

    void TogglePause();

    void SetSpeed(float speedMultiplier);

    void StepSingleTick();

    /// <summary>
    /// Static content lookup (item names, tags, icons). Definitions are built
    /// at world creation and never change, so this works the same whoever owns
    /// the world — callers used to reach into <c>World.Content</c> for it.
    /// </summary>
    bool TryGetObjectDefinition(string id, out ObjectDefinition? definition);

    /// <summary>
    /// Appends every event newer than <paramref name="sinceSeq"/> to
    /// <paramref name="into"/> and returns the new watermark.
    /// <para>
    /// Callers pass back the value they last received. If events were dropped
    /// (the ring trims at 2048), the gap is SKIPPED rather than replayed —
    /// replaying writes duplicate colony-history lines.
    /// </para>
    /// </summary>
    long DrainEvents(long sinceSeq, List<SimulationEvent> into);

    /// <summary>
    /// False when the world is not ours to poke. The debug controls panel
    /// edits NPC bodies in place; that is only meaningful locally.
    /// </summary>
    bool SupportsDirectWorldMutation { get; }

    /// <summary>
    /// False when someone else owns persistence. A client with no
    /// <c>WorldState</c> has nothing to hand the save serializer.
    /// </summary>
    bool SupportsClientSave { get; }

    /// <summary>
    /// §121: можно ли отдавать приказы конкретному NPC. Ложь на удалённом
    /// мире — там колония общая, и один зритель не вправе увести чужую
    /// колонистку; вид просто прячет тумблер.
    /// </summary>
    bool SupportsNpcCommands { get; }

    /// <summary>§138 local-only read model for the backpack crafting tab.
    /// Returns false when this client does not own authoritative command
    /// execution; remote and wire-rehearsal UIs hide the tab.</summary>
    bool TryGetCraftingOptions(EntityId npc, List<CraftRecipeOption> into);

    /// <summary>
    /// §121: единственная дорога от интерфейса к симуляции. Приказ кладётся в
    /// очередь и применяется в начале ближайшего тика — вид сам мир НЕ трогает.
    /// <para>
    /// Шов держится и без сети нарочно: когда приказы поедут по проводу, здесь
    /// появится ещё один кадр, и ни одна кнопка об этом не узнает.
    /// </para>
    /// </summary>
    void EnqueueCommand(ISimulationCommand command);
}

/// <summary>
/// A source that also has to be driven and torn down. Implemented by whatever
/// actually produces the world — a local engine today, a socket tomorrow — and
/// held by <see cref="SimulationRunnerBehaviour"/>, which stays the single
/// MonoBehaviour the scene wires up.
/// </summary>
public interface ISimulationBackend : ISimulationSource
{
    /// <summary>
    /// Advance by one rendered frame. A local backend steps the engine on a
    /// fixed-timestep accumulator; a remote one advances its interpolation
    /// clock and drains whatever arrived.
    /// </summary>
    void Tick(float unscaledDeltaTime);

    /// <summary>No-op unless <see cref="ISimulationSource.SupportsClientSave"/>.</summary>
    void WriteSaveNow();

    void Shutdown();
}

/// <summary>
/// Ambient access to the live source, for the handful of components that are
/// constructed without a reference to the runner (the audio manager among them,
/// which today has no fallback at all and simply goes silent if wiring order
/// changes). Prefer an explicit reference where one is already threaded through.
/// </summary>
public static class SimulationSource
{
    public static ISimulationSource? Current { get; internal set; }
}

}
