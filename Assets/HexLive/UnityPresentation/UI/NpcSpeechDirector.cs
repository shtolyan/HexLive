using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{

// §67.10: what a body must provide for its mouth to be driven by the director.
// NpcActorView implements it; the director never touches Unity objects itself,
// which keeps it testable and keeps the actor view free of speech policy.
public interface ISpeechStage
{
    // Play the voice bank for this speech id. Returns the clip length in
    // seconds, or 0 when nothing audible exists yet (unrecorded line) — the
    // bubble is shown either way, so the visual half works before the audio.
    float PlayVoiceLine(string speechId);

    // Cut the line that is sounding (an alarm interrupting chatter).
    void StopVoiceLine();

    // seconds <= 0 = hold indefinitely (a running conversation).
    void ShowSpeechIcon(string iconKey, float seconds);

    // §107: то же место в пузыре, но занятое ЛИЦОМ — когда тема разговора это
    // человек. Отдельный вызов, а не «iconKey может быть портретом»: ключ
    // ищется в Resources, а лицо печёт NpcPortraitCache, и общего пути у них нет.
    void ShowSpeechPortrait(Sprite portrait, float seconds);

    void HideSpeechIcon();

    // False when she physically cannot speak (dead, fast-forward, view not
    // ready). An alarm line — the death cry itself — passes the dead gate.
    bool CanSpeak(bool alarm);
}

// §67.10: THE arbiter of one colonist's mouth. Every utterance in the game goes
// through Say(), and Say() always does both halves — voice AND bubble — so the
// design invariant ("speech is always accompanied by a bubble with a matching
// picture") is structural, not something each call site has to remember.
//
// Four layers, in Rank order (SpeechCatalog.Rank):
//   Alarm  — wound / wolf / help cry / death. Cuts through anything.
//   Action — a verb beat: chopping, done, fire lit, aid given, failure.
//   Talk   — a conversation turn. Owns the mouth for the length of the chat.
//   Ambient— her own body: hungry, parched, freezing, filthy, lonely.
//
// The layers deliberately do not fight:
//   * while a conversation runs, the ambient layer is SILENT — the sim has
//     already turned her pressing state into her personal talk topic (§67.10
//     PickSpeakerTopic), so instead of muttering "I'm starving" to herself she
//     says it TO her housemate, and the bubble shows the same picture;
//   * an alarm borrows the bubble and hands it back to the conversation topic
//     when it fades, so an interrupted chat visibly resumes its subject;
//   * one mouth = one line: a global gap plus a per-line cooldown, so a busy
//     tick cannot turn the colony into a market.
public sealed class NpcSpeechDirector
{
    // No two lines from the same mouth closer than this (alarms excepted).
    private const float GlobalGap = 6f;

    // Self-talk is the rarest layer — a readable beat, not a running commentary.
    private const float AmbientGap = 25f;
    private const float AmbientFirstDelay = 10f;
    private const float AmbientPollGap = 5f;

    private readonly ISpeechStage _stage;
    private readonly Dictionary<string, float> _lastSaid = new();

    private float _activeUntil = -1f;
    private SpeechCatalog.Rank _activeRank;
    private float _lastAny = -999f;
    private float _lastAmbient = -999f;

    private string _conversationLine;
    private string _heldIcon;

    // §107: лицо того, о ком разговор, и то, что реально висит в пузыре.
    // Держим оба: значок отвечает на «сменилась ли тема», лицо — на «сменился
    // ли тот, о ком речь», и по одному из них это не восстановить.
    private Sprite _conversationFace;
    private Sprite _heldFace;

    private SpeechCatalog.BodyState _state;
    private bool _stateKnown;
    private string _lastInteraction = string.Empty;
    private float _ambientDue;

    public NpcSpeechDirector(ISpeechStage stage)
    {
        _stage = stage;
        _ambientDue = Time.time + AmbientFirstDelay;
    }

    public bool IsConversing => _conversationLine != null;

    /// <summary>The subject the sim says she is talking about right now (a
    /// TalkTopic name, "" when not talking). Pushed every snapshot.</summary>
    public void SetConversationTopic(string topicName) => SetConversationTopic(topicName, null);

    /// <summary>§107: та же тема, но с ЛИЦОМ того, о ком речь (сегодня это
    /// только чужак). Портрет живёт рядом с темой, а не вместо неё: реплика,
    /// голос и уход пузыря остаются прежними, меняется одна картинка.</summary>
    public void SetConversationTopic(string topicName, Sprite subjectFace)
    {
        var line = TalkTopicVisuals.IsKnown(topicName) ? SpeechCatalog.ForTopic(topicName) : null;
        var faceChanged = !ReferenceEquals(subjectFace, _conversationFace);
        _conversationFace = subjectFace;
        if (line == _conversationLine && !faceChanged)
        {
            return;
        }

        _conversationLine = line;

        if (line == null)
        {
            // Chat over: drop the bubble unless a timed line still owns it.
            if (Time.time >= _activeUntil)
            {
                _heldIcon = null;
                _stage.HideSpeechIcon();
            }

            return;
        }

        // The bubble adopts the new subject at once — the voice follows on her
        // next turn, so the picture is never behind the conversation.
        if (Time.time >= _activeUntil)
        {
            HoldConversationIcon();
        }
    }

    /// <summary>Her turn to speak in a conversation (view-side turn-taking).
    /// The line matches the bubble already on screen.</summary>
    public void OnTalkTurn()
    {
        Say(_conversationLine ?? "happy_topic_smalltalk");
    }

    /// <summary>A one-shot social cue from the sim (help cry, danger, aid…).</summary>
    public void OnCue(string cueKind)
    {
        Say(SpeechCatalog.ForCue(cueKind));
    }

    /// <summary>The verb she is performing, pushed every snapshot; only the
    /// rising edge speaks.</summary>
    public void OnInteraction(string interaction)
    {
        interaction ??= string.Empty;
        if (interaction == _lastInteraction)
        {
            return;
        }

        _lastInteraction = interaction;
        Say(SpeechCatalog.ForInteraction(interaction));
    }

    /// <summary>Her body, pushed every snapshot — the ambient layer's input.</summary>
    public void SetState(in SpeechCatalog.BodyState state)
    {
        _state = state;
        _stateKnown = true;
    }

    /// <summary>The one place an utterance is produced: voice + bubble + face,
    /// or nothing at all. Returns whether she actually spoke.</summary>
    public bool Say(string speechId)
    {
        if (string.IsNullOrEmpty(speechId))
        {
            return false;
        }

        var line = SpeechCatalog.Get(speechId);
        var alarm = line.Rank == SpeechCatalog.Rank.Alarm;
        if (!_stage.CanSpeak(alarm))
        {
            return false;
        }

        var now = Time.time;
        var busy = now < _activeUntil;

        // Equal rank does not interrupt — whoever started, finishes.
        if (busy && line.Rank <= _activeRank)
        {
            return false;
        }

        if (!alarm && now - _lastAny < GlobalGap)
        {
            return false;
        }

        if (line.MinGap > 0f &&
            _lastSaid.TryGetValue(speechId, out var last) && now - last < line.MinGap)
        {
            return false;
        }

        if (line.Rank == SpeechCatalog.Rank.Ambient)
        {
            // In company she does not mutter to herself — the sim gave her a
            // personal talk topic for exactly this.
            if (IsConversing || now - _lastAmbient < AmbientGap)
            {
                return false;
            }
        }

        if (busy)
        {
            _stage.StopVoiceLine();
        }

        var length = _stage.PlayVoiceLine(speechId);
        var hold = Mathf.Max(SpeechCatalog.MinBubbleSeconds, length) + SpeechCatalog.BubbleTailSeconds;
        _heldIcon = line.Icon;
        // Реплика перебивает лицо: она говорит СВОЮ фразу, и пузырь на это
        // время принадлежит ей. Тема с лицом вернётся сама — следующий снапшот
        // снова толкнёт её через SetConversationTopic.
        _heldFace = null;
        _stage.ShowSpeechIcon(line.Icon, hold);

        _activeUntil = now + hold;
        _activeRank = line.Rank;
        _lastAny = now;
        _lastSaid[speechId] = now;
        if (line.Rank == SpeechCatalog.Rank.Ambient)
        {
            _lastAmbient = now;
        }

        return true;
    }

    /// <summary>Per-frame: hand the bubble back to a running conversation once a
    /// timed line has faded, and schedule the ambient layer.</summary>
    public void Tick()
    {
        var now = Time.time;
        if (now < _activeUntil)
        {
            return;
        }

        if (IsConversing)
        {
            HoldConversationIcon();
            return;
        }

        if (!_stateKnown || now < _ambientDue)
        {
            return;
        }

        _ambientDue = now + AmbientPollGap;
        if (_state.Asleep || _state.Fainted)
        {
            return;
        }

        Say(SpeechCatalog.ForState(_state));
    }

    private void HoldConversationIcon()
    {
        var icon = SpeechCatalog.Get(_conversationLine).Icon;
        // §107: лицо старше значка. Значок при этом всё равно вычисляется и
        // запоминается — по нему сравнивается «сменилась ли тема», и голос на
        // её ход берётся из той же строки каталога.
        if (_conversationFace != null)
        {
            if (_heldIcon == icon && _heldFace == _conversationFace)
            {
                return;
            }

            _heldIcon = icon;
            _heldFace = _conversationFace;
            _stage.ShowSpeechPortrait(_conversationFace, 0f);
            return;
        }

        if (_heldIcon == icon && _heldFace == null)
        {
            return;
        }

        _heldIcon = icon;
        _heldFace = null;
        _stage.ShowSpeechIcon(icon, 0f);
    }
}

}
