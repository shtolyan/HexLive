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

    float PlayExternalVoiceLine(string wavPath, string visemePath, string emotion,
        bool listenerRelative);

    // Cut the line that is sounding (an alarm interrupting chatter).
    void StopVoiceLine();
    bool CanSpeakExternal(bool playerReply);

    // seconds <= 0 = hold indefinitely (a running conversation).
    // alarm = пометить пузырь маленьким значком тревоги в углу (§107.5).
    void ShowSpeechIcon(string iconKey, float seconds, bool alarm = false);

    // The same bubble slot occupied by a concrete Sprite: a baked face (§108)
    // or an owner-bundle item icon (§111.12). String keys follow another loader.
    void ShowSpeechImage(Sprite image, float seconds, bool alarm = false);

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
    private string _activeCueKind;
    private bool _activeCueAlarm;

    // §108: лицо того, о ком разговор, и то, что реально висит в пузыре.
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

    /// <summary>§108: та же тема, но с ЛИЦОМ того, о ком речь (сегодня это
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

    /// <summary>§107.5: одноразовая кьюшка из симуляции (крик о помощи, чужак,
    /// помощь…). Идёт в ТОТ ЖЕ единственный пузырь и по тем же правилам
    /// старшинства: «хочу пить» (Ambient) молчит, если рядом чужак (Alarm).
    /// Второго пузыря нет — раньше кьюшка рисовала свой, и над головой висели
    /// две картинки об одном.
    ///
    /// peerFace — лицо того, о ком кьюшка: оно занимает место значка, а сам
    /// значок тревоги уезжает маленьким в угол пузыря.</summary>
    public void OnCue(string cueKind, Sprite peerFace = null)
    {
        var cue = SpeechCatalog.ForCue(cueKind);
        if (!string.IsNullOrEmpty(cue.SpeechId))
        {
            Say(cue.SpeechId, peerFace);
            return;
        }

        // Молчаливая кьюшка (просьба поговорить, удар в сцене абьюза): голоса у
        // неё нет, но показать её надо — тем же пузырём и той же очередью.
        ShowSilent(cueKind, cue, peerFace);
    }

    // Пузырь без реплики. Отдельный путь от Say(), потому что Say обязан
    // ЗВУЧАТЬ: «речь не бывает немой» — инвариант §67.10, и ослаблять его
    // ради кьюшек нельзя. Правила старшинства при этом общие.
    private void ShowSilent(string cueKind, SpeechCatalog.CueVisual cue, Sprite peerFace)
    {
        if (string.IsNullOrEmpty(cue.PopIcon))
        {
            return;
        }

        var alarm = cue.Rank == SpeechCatalog.Rank.Alarm;
        if (!_stage.CanSpeak(alarm))
        {
            return;
        }

        var now = Time.time;
        if (now < _activeUntil && cue.Rank <= _activeRank)
        {
            return;
        }

        var hold = SpeechCatalog.MinBubbleSeconds + SpeechCatalog.BubbleTailSeconds;
        _heldIcon = cue.PopIcon;
        _heldFace = peerFace;
        if (peerFace != null)
        {
            _stage.ShowSpeechImage(peerFace, hold, alarm);
        }
        else
        {
            _stage.ShowSpeechIcon(cue.PopIcon, hold, alarm);
        }

        _activeUntil = now + hold;
        _activeRank = cue.Rank;
        _activeCueKind = cueKind;
        _activeCueAlarm = alarm;
    }

    /// <summary>Replace an async fallback only while this exact silent cue
    /// still owns the bubble. A newer alarm/conversation makes this a no-op.</summary>
    public bool TryRefreshCuePicture(string cueKind, Sprite picture)
    {
        var now = Time.time;
        if (picture == null || cueKind != _activeCueKind || now >= _activeUntil)
        {
            return false;
        }

        _heldFace = picture;
        _stage.ShowSpeechImage(picture, _activeUntil - now, _activeCueAlarm);
        return true;
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
    public bool Say(string speechId) => Say(speechId, null);

    /// <summary>§107.5: та же реплика, но в пузыре ЛИЦО того, о ком она — а не
    /// значок. Значок при этом не пропадает: если картинка тревожная, он метит
    /// угол пузыря маленьким треугольником.</summary>
    public bool Say(string speechId, Sprite subjectFace)
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

        _activeCueKind = null;
        var length = _stage.PlayVoiceLine(speechId);
        var hold = Mathf.Max(SpeechCatalog.MinBubbleSeconds, length) + SpeechCatalog.BubbleTailSeconds;
        _heldIcon = line.Icon;
        // Лицо держится, только если его дали вместе с репликой (кьюшка про
        // чужака). Иначе она говорит СВОЮ фразу, и пузырь на это время её —
        // тема с лицом вернётся сама, следующим SetConversationTopic.
        _heldFace = subjectFace;
        if (subjectFace != null)
        {
            _stage.ShowSpeechImage(subjectFace, hold, alarm);
        }
        else
        {
            _stage.ShowSpeechIcon(line.Icon, hold, alarm);
        }

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

    /// <summary>§160: external agent speech uses the same one-mouth arbiter.</summary>
    public bool SayExternal(
        string wavPath, string visemePath, string emotion, SpeechCatalog.Rank rank)
    {
        if (string.IsNullOrEmpty(wavPath)) return false;
        var alarm = rank == SpeechCatalog.Rank.Alarm;
        if (!_stage.CanSpeakExternal(rank == SpeechCatalog.Rank.Talk)) return false;

        var now = Time.time;
        var busy = now < _activeUntil;
        if (busy && rank <= _activeRank) return false;
        if (rank != SpeechCatalog.Rank.Talk && !alarm && now - _lastAny < GlobalGap) return false;
        if (rank == SpeechCatalog.Rank.Ambient &&
            (IsConversing || now - _lastAmbient < AmbientGap)) return false;
        if (busy) _stage.StopVoiceLine();

        var visualId = emotion?.ToLowerInvariant() switch
        {
            "sad" or "tired" => "sad_topic_weather",
            "angry" or "tense" => "angry_topic_grumble",
            "afraid" => "sad_topic_escape",
            _ => "happy_agree"
        };
        var line = SpeechCatalog.Get(visualId);
        var length = _stage.PlayExternalVoiceLine(
            wavPath, visemePath, emotion ?? "neutral", rank == SpeechCatalog.Rank.Talk);
        if (length <= 0f) return false;
        var hold = Mathf.Max(SpeechCatalog.MinBubbleSeconds, length) + SpeechCatalog.BubbleTailSeconds;
        _stage.ShowSpeechIcon(line.Icon, hold, alarm);
        _heldIcon = line.Icon;
        _heldFace = null;
        _activeUntil = now + hold;
        _activeRank = rank;
        _lastAny = now;
        if (rank == SpeechCatalog.Rank.Ambient) _lastAmbient = now;
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
        _activeCueKind = null;
        var icon = SpeechCatalog.Get(_conversationLine).Icon;
        // §108: лицо старше значка. Значок при этом всё равно вычисляется и
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
            _stage.ShowSpeechImage(_conversationFace, 0f);
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
