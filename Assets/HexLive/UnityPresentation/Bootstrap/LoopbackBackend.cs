#nullable enable
using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;

namespace HexLive.UnityPresentation.Bootstrap
{

/// <summary>
/// The world still runs locally, but everything the presentation reads has been
/// through the wire codec: snapshot and events are encoded and decoded again on
/// the way out.
/// <para>
/// This exists to answer one question cheaply — <i>did someone add a field to the
/// snapshot and forget the codec?</i> That failure is invisible in review and
/// silent at runtime: the game compiles, the colony walks, and one colonist is
/// just missing a wound or an item in her hand. Normally you would only find it
/// by standing up a server. With this mode you find it by pressing Play.
/// </para>
/// <para>
/// Not a performance mode — it deliberately pays the full encode/decode every
/// tick. Enable it in the editor or in CI, never in a shipped session.
/// </para>
/// <para>
/// It also reports no world of its own (<c>SupportsClientSave</c> and
/// <c>SupportsDirectWorldMutation</c> are both false, and the runner's
/// <c>Engine</c> is null), so Continue-from-save and the debug body controls go
/// quiet here exactly as they will over a network. That is the rehearsal working,
/// not a bug: it is how those paths get exercised before a server exists.
/// </para>
/// </summary>
public sealed class LoopbackBackend : ISimulationBackend
{
    private readonly LocalEngineBackend _inner;

    private readonly MemoryStream _buffer = new();
    private readonly WorldSnapshot _decoded = new();
    private int _decodedTick = -1;
    private bool _decodedDebug;
    private bool _topologyCopied;

    private readonly List<SimulationEvent> _encodedEvents = new();

    public LoopbackBackend(LocalEngineBackend inner)
    {
        _inner = inner;
    }

    public bool IsReady => _inner.IsReady;

    // The codec is in the way, but no network is — nothing can go wrong here
    // that a HUD could tell the player about.
    public SimulationLink Link => SimulationLink.Local;

    public bool IsCompleted => _inner.IsCompleted;

    public int Seed => _inner.Seed;

    public int CurrentTick => _inner.CurrentTick;

    public float TickAlpha => _inner.TickAlpha;

    public bool IsPaused => _inner.IsPaused;

    public float SpeedMultiplier => _inner.SpeedMultiplier;

    // Both false on purpose: this mode is a rehearsal for not owning the world,
    // so it must also exercise the code paths that have to cope without one.
    public bool SupportsDirectWorldMutation => false;

    public bool SupportsClientSave => false;

    // §118: и приказы тоже. Режим существует, чтобы РЕПЕТИРОВАТЬ жизнь без
    // своего мира, — значит и здесь тумблер ручного управления обязан быть
    // спрятан, как на настоящем удалённом подключении.
    public bool SupportsNpcCommands => false;

    public void EnqueueCommand(ISimulationCommand command)
    {
        // Ничего: SupportsNpcCommands ложь, звать сюда никто не должен.
    }

    public WorldSnapshot CreateSnapshot()
    {
        var source = _inner.CreateSnapshot();
        var detailed = WorldSnapshotExporter.IncludeDebugDetails;
        if (_decodedTick == source.Tick && _decodedDebug == detailed)
        {
            return _decoded;
        }

        _buffer.SetLength(0);
        _buffer.Position = 0;
        using (var writer = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
        {
            WorldSnapshotCodec.Write(source, writer, detailed);
            writer.Flush();
        }

        _buffer.Position = 0;
        using (var reader = new BinaryReader(_buffer, System.Text.Encoding.UTF8, true))
        {
            WorldSnapshotCodec.Read(reader, _decoded);
        }

        // Junctions never ride the wire — ~14 000 of them, pure worldgen output.
        // A real client rebuilds them from the seed at connect time; here the
        // world is in the same process, so take the one shortcut that changes
        // nothing about what the codec is being tested for. (That the rebuild is
        // deterministic is proven separately, by the topology-checksum probe.)
        if (!_topologyCopied)
        {
            _topologyCopied = true;
            foreach (var junction in source.Junctions)
            {
                _decoded.Junctions.Add(junction);
            }

            foreach (var tile in source.Tiles)
            {
                _decoded.Tiles.Add(tile);
            }
        }

        _decodedTick = source.Tick;
        _decodedDebug = detailed;
        return _decoded;
    }

    public void Tick(float unscaledDeltaTime) => _inner.Tick(unscaledDeltaTime);

    public void Pause() => _inner.Pause();

    public void Resume() => _inner.Resume();

    public void TogglePause() => _inner.TogglePause();

    public void SetSpeed(float speedMultiplier) => _inner.SetSpeed(speedMultiplier);

    public void StepSingleTick() => _inner.StepSingleTick();

    // Static content: a real client loads the same catalogs, so there is nothing
    // to encode. Straight through.
    public bool TryGetObjectDefinition(string id, out ObjectDefinition? definition) =>
        _inner.TryGetObjectDefinition(id, out definition);

    public long DrainEvents(long sinceSeq, List<SimulationEvent> into)
    {
        _encodedEvents.Clear();
        var watermark = _inner.DrainEvents(sinceSeq, _encodedEvents);
        if (_encodedEvents.Count == 0)
        {
            return watermark;
        }

        _buffer.SetLength(0);
        _buffer.Position = 0;
        using (var writer = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
        {
            SimulationEventCodec.Write(_encodedEvents, sinceSeq + 1, writer);
            writer.Flush();
        }

        _buffer.Position = 0;
        using (var reader = new BinaryReader(_buffer, System.Text.Encoding.UTF8, true))
        {
            SimulationEventCodec.Read(reader, into);
        }

        return watermark;
    }

    public void WriteSaveNow()
    {
        // Nothing: SupportsClientSave is false, so nobody should be calling this.
    }

    public void Shutdown()
    {
        _inner.Shutdown();
        _buffer.Dispose();
    }
}

}
