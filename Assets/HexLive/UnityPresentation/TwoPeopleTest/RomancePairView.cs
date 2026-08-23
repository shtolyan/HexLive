#nullable enable
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace HexLive.UnityPresentation.TwoPeopleTest
{

/// <summary>§127: one runtime PlayableGraph drives both humanoids. The
/// female root is the authored anchor; the male root follows the saved offset.</summary>
public sealed class RomancePairView : MonoBehaviour
{
    private NpcActorView? _female;
    private NpcActorView? _male;
    private RomancePoseCatalog.Entry? _entry;
    private PlayableGraph _graph;
    private AnimationClipPlayable _femalePlayable;
    private AnimationClipPlayable _malePlayable;
    private bool _climax;
    private Vector3 _anchor;
    private Quaternion _rotation;
    private float _simSpeed = 1f;
    private int _nextFemaleVoiceTick = int.MaxValue;
    private int _nextMaleVoiceTick = int.MaxValue;
    private int _voiceSequence;
    private bool _voiceForced;

    public int FemaleNpcId { get; private set; }
    public int MaleNpcId { get; private set; }

    public void Sync(NpcActorView female, NpcActorView male,
        RomancePoseCatalog.Entry entry, int femaleNpcId, int maleNpcId,
        int tick, int startTick, int endTick, float tickDeltaTime,
        Vector3 anchor, Quaternion rotation, float simSpeed, bool forced)
    {
        var climax = tick >= endTick - Spec127.ClimaxTicks;
        var pairChanged = _female != female || _male != male || _entry != entry;
        if (pairChanged || _voiceForced != forced)
        {
            ResetVoiceSchedule(tick, femaleNpcId, maleNpcId, forced);
        }
        if (pairChanged || !_graph.IsValid() || _climax != climax)
        {
            StopGraphOnly();
            _female = female;
            _male = male;
            _entry = entry;
            _climax = climax;
            FemaleNpcId = femaleNpcId;
            MaleNpcId = maleNpcId;
            BuildGraph(climax);
            ApplyGenitalShapes(male, entry);
        }

        _anchor = anchor;
        _rotation = rotation;
        _simSpeed = Mathf.Max(0f, simSpeed);
        female.SetRomanceVisual(true, forced);
        male.SetRomanceVisual(true, false);

        if (!_graph.IsValid()) return;
        SyncVoices(tick, forced);
        if (climax)
        {
            var climaxStart = endTick - Spec127.ClimaxTicks;
            var elapsedSeconds = Mathf.Max(0, tick - climaxStart) * tickDeltaTime;
            var climaxTime = elapsedSeconds * Spec127.ClimaxPlaybackSpeed;
            var femaleClip = _entry!.femaleClimax != null
                ? _entry.femaleClimax : _entry.femaleLoop;
            var maleClip = _entry.maleClimax != null
                ? _entry.maleClimax : _entry.maleLoop;
            SetTime(_femalePlayable, Mathf.Min(climaxTime, ClipLength(femaleClip)));
            SetTime(_malePlayable, Mathf.Min(climaxTime, ClipLength(maleClip)));
            _femalePlayable.SetSpeed(Spec127.ClimaxPlaybackSpeed * _simSpeed);
            _malePlayable.SetSpeed(Spec127.ClimaxPlaybackSpeed * _simSpeed);
        }
        else
        {
            var loopTicks = Mathf.Max(1, endTick - startTick - Spec127.ClimaxTicks);
            var p = Mathf.Clamp01((tick - startTick) / (float)loopTicks);
            var smooth = p * p * (3f - 2f * p);
            var speed = Mathf.Lerp(Spec127.MinPlaybackSpeed,
                Spec127.MaxPlaybackSpeed, smooth);
            // Integral of min + (max-min)*(3p²-2p³), so reconnect and
            // fast-forward resume the same loop phase instead of re-rolling it.
            var speedRange = Spec127.MaxPlaybackSpeed - Spec127.MinPlaybackSpeed;
            var seconds = loopTicks * tickDeltaTime *
                (Spec127.MinPlaybackSpeed * p + speedRange *
                    (p * p * p - 0.5f * p * p * p * p));
            SetTime(_femalePlayable,
                Repeat(seconds, ClipLength(_entry!.femaleLoop)));
            SetTime(_malePlayable,
                Repeat(seconds, ClipLength(_entry.maleLoop)));
            _femalePlayable.SetSpeed(speed * _simSpeed);
            _malePlayable.SetSpeed(speed * _simSpeed);
        }
    }

    private void LateUpdate()
    {
        if (_female == null || _male == null || _entry == null) return;
        _female.transform.SetPositionAndRotation(_anchor, _rotation);
        var malePosition = _anchor + _rotation * _entry.malePosition;
        var maleRotation = _rotation * Quaternion.Euler(_entry.maleEuler);
        _male.transform.SetPositionAndRotation(malePosition, maleRotation);
    }

    public void Stop()
    {
        StopGraphOnly();
        _female?.SetRomanceVisual(false, false);
        _male?.SetRomanceVisual(false, false);
        _female = null;
        _male = null;
        _entry = null;
        _nextFemaleVoiceTick = int.MaxValue;
        _nextMaleVoiceTick = int.MaxValue;
    }

    // §127.11/§67.13: the scene asks the ordinary speech director to speak,
    // never the audio layer directly. That keeps voice, visemes, expression
    // and bubble one indivisible act and also obeys the >4x simulation mute.
    private void ResetVoiceSchedule(int tick, int femaleNpcId, int maleNpcId,
        bool forced)
    {
        _voiceSequence = 0;
        _voiceForced = forced;
        _nextFemaleVoiceTick = tick + (forced ? 8 : 12) +
            PositiveModulo(femaleNpcId * 3 + maleNpcId, 7);
        _nextMaleVoiceTick = tick + (forced ? 28 : 20) +
            PositiveModulo(maleNpcId * 5 + femaleNpcId, 9);
    }

    private void SyncVoices(int tick, bool forced)
    {
        if (_female == null || _male == null)
        {
            return;
        }

        if (tick >= _nextFemaleVoiceTick)
        {
            _female.Say(forced ? "cry_romance_forced" : "happy_romance");
            _nextFemaleVoiceTick = tick + (forced ? 30 : 28) +
                PositiveModulo(FemaleNpcId * 7 + MaleNpcId * 3 + _voiceSequence, 11);
            _voiceSequence++;
        }

        if (tick >= _nextMaleVoiceTick)
        {
            _male.Say("happy_romance");
            _nextMaleVoiceTick = tick + (forced ? 44 : 32) +
                PositiveModulo(MaleNpcId * 7 + FemaleNpcId * 3 + _voiceSequence, 13);
            _voiceSequence++;
        }
    }

    private static int PositiveModulo(int value, int modulo)
    {
        var remainder = value % modulo;
        return remainder < 0 ? remainder + modulo : remainder;
    }

    private void BuildGraph(bool climax)
    {
        if (_female?.BodyAnimator == null || _male?.BodyAnimator == null || _entry == null)
        {
            return;
        }

        var femaleClip = climax && _entry.femaleClimax != null
            ? _entry.femaleClimax : _entry.femaleLoop;
        var maleClip = climax && _entry.maleClimax != null
            ? _entry.maleClimax : _entry.maleLoop;
        if (femaleClip == null || maleClip == null) return;

        _graph = PlayableGraph.Create($"RomancePair.{FemaleNpcId}.{MaleNpcId}");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
        _femalePlayable = AnimationClipPlayable.Create(_graph, femaleClip);
        _malePlayable = AnimationClipPlayable.Create(_graph, maleClip);
        _femalePlayable.SetApplyFootIK(false);
        _malePlayable.SetApplyFootIK(false);
        _femalePlayable.SetDuration(femaleClip.length);
        _malePlayable.SetDuration(maleClip.length);
        var femaleOutput = AnimationPlayableOutput.Create(
            _graph, "Female", _female.BodyAnimator);
        femaleOutput.SetSourcePlayable(_femalePlayable);
        var maleOutput = AnimationPlayableOutput.Create(
            _graph, "Male", _male.BodyAnimator);
        maleOutput.SetSourcePlayable(_malePlayable);
        _graph.Play();
    }

    private void StopGraphOnly()
    {
        if (_graph.IsValid()) _graph.Destroy();
    }

    private static void ApplyGenitalShapes(NpcActorView male,
        RomancePoseCatalog.Entry entry)
    {
        foreach (var skin in male.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = skin.sharedMesh;
            if (mesh == null ||
                skin.name.IndexOf("Genital", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                var shapeName = mesh.GetBlendShapeName(i);
                if (shapeName.IndexOf("Preset", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    skin.SetBlendShapeWeight(i, 0f);
                }
                foreach (var authored in entry.genitalShapes)
                {
                    if (!string.IsNullOrEmpty(authored.shape) &&
                        shapeName.IndexOf(authored.shape,
                            System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        skin.SetBlendShapeWeight(i, authored.weight);
                        break;
                    }
                }
            }
        }
    }

    private static float ClipLength(AnimationClip? clip) =>
        clip != null ? Mathf.Max(0.001f, clip.length) : 0.001f;

    private static double Repeat(float value, float length) =>
        length > 0.001f ? value - Mathf.Floor(value / length) * length : 0d;

    private static void SetTime(AnimationClipPlayable playable, double time)
    {
        if (playable.IsValid()) playable.SetTime(time);
    }

    private void OnDisable() => Stop();
    private void OnDestroy() => Stop();
}

}
