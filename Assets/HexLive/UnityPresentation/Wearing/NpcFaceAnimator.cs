using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Face life on the Genesis3 blend shapes: random blinking, eyes held shut
// while sleeping, and an EXPRESSION layer. §127.8: вручную составленные
// маски эмоций заменены каталогом выражений DAZ-пака «Cute & Fun»
// (FaceExpressionCatalog) — лицо всегда показывает ОДНО выражение-рецепт с
// плавным кроссфейдом; чего в паке нет (злость, плач, страх, боль), то
// добавлено кастомными рецептами В ТОМ ЖЕ формате. Presentation-only.
//
// Слои по приоритету (выше — сильнее): боль → плач → бой → эмоция реплики →
// поп отношений после разговора → тема текущего разговора → взгляд в камеру →
// фоновое настроение (wellbeing). Presentation-only randomness: вариант
// выражения внутри эмоции выбирается случайно на фронте события.
public sealed class NpcFaceAnimator : MonoBehaviour
{
    // Blend-shape weights are 0..100.
    private const float BlinkCloseSpeed = 900f;  // fast close
    private const float BlinkOpenSpeed = 450f;   // slower open
    private const float EmotionSpeed = 90f;      // expression level ramp, %/s
    private const float SleepEyeSpeed = 160f;
    private const float TopicLevel = 55f;        // ambient face while chatting
    private const float PopSeconds = 2.2f;       // relationship flash length
    private const float MouthMuteSpeed = 6f;     // §67.8: отдать/вернуть рот за ~0.17 с

    private static readonly Vector2 BlinkInterval = new(2.2f, 5.5f);

    // ------------------------------------------------------------- catalog

    // Один каталог на всех: пак + кастомные рецепты того, чего в паке нет
    // (веса подобраны под G3F-фигуры).
    private static FaceExpressionCatalog _shared;

    private static FaceExpressionCatalog SharedCatalog
    {
        get
        {
            if (_shared == null)
            {
                _shared = FaceExpressionCatalog.Load();
                _shared.AddCustom("x_sad", "Грусть", new (string, float)[]
                {
                    ("eCTRLMouthFrown", 65f),
                    ("eCTRLEyesSquintL", 15f), ("eCTRLEyesSquintR", 15f),
                });
                _shared.AddCustom("x_angry", "Злость", new (string, float)[]
                {
                    ("eCTRLAngry", 85f), ("eCTRLBrowSqueeze", 70f),
                    ("eCTRLNoseScrunch", 45f), ("eCTRLMouthFrown", 45f),
                });
                _shared.AddCustom("x_cry", "Плач", new (string, float)[]
                {
                    ("eCTRLMouthFrown", 80f), ("eCTRLBrowSqueeze", 65f),
                    ("eCTRLEyesSquintL", 55f), ("eCTRLEyesSquintR", 55f),
                });
                // §67.8: разложен на отдельные FACS-юниты по образцу пака
                // («03» Удивление) вместо монолитного eCTRLSurprised — у того
                // ЗАПЕЧЁН открытый рот, и погасить рот на время реплики можно
                // было бы только вместе с бровями. Так брови/глаза переживают
                // разговорный mouth-mute, а рот отдаётся липсинку целиком.
                _shared.AddCustom("x_fear", "Испуг", new (string, float)[]
                {
                    ("eCTRLBrowInnerUp-DownL", 70f), ("eCTRLBrowInnerUp-DownR", 70f),
                    ("eCTRLBrowUp-Down", 35f),
                    ("eCTRLEyelidsUpperDownUp", -25f),
                    ("eCTRLEyelidsLowerUpDown", -15f),
                    ("eCTRLEyesSquintL", -15f), ("eCTRLEyesSquintR", -15f),
                    ("eCTRLLipsPart", 40f), ("eCTRLMouthOpen", 45f),
                    ("eCTRLMouthFrown", 30f),
                });
                _shared.AddCustom("x_work", "Натуга", new (string, float)[]
                {
                    ("eCTRLBrowSqueeze", 55f),
                    ("eCTRLMouthCornerBackL", 45f), ("eCTRLMouthCornerBackR", 45f),
                    ("eCTRLNoseScrunch", 25f),
                });
                // Pain wince from FACS units (no single eCTRLPain shape exists).
                _shared.AddCustom("x_pain", "Боль", new (string, float)[]
                {
                    ("eCTRLBrowSqueeze", 80f),
                    ("eCTRLEyesSquintL", 75f), ("eCTRLEyesSquintR", 75f),
                    ("eCTRLNoseScrunch", 55f),
                    ("eCTRLCheekFlexL", 45f), ("eCTRLCheekFlexR", 45f),
                    ("eCTRLMouthCornerBackL", 55f), ("eCTRLMouthCornerBackR", 55f),
                    ("eCTRLLipsPart", 30f),
                    ("eCTRLMouthFrown", 45f),
                });
            }

            return _shared;
        }
    }

    // --------------------------------------------------- emotion → variants

    // §67.8/§67.10: эмоция голосовой реплики → варианты выражений (короткий
    // номер пака или имя кастома). Вариант выбирается случайно на реплику.
    private static readonly Dictionary<string, string[]> VoiceVariants = new()
    {
        ["happy"] = new[] { "01", "04", "05", "06", "15" },
        ["sad"] = new[] { "x_sad", "10", "17" },
        ["sleepy"] = new[] { "14" },
        ["angry"] = new[] { "x_angry" },
        ["cry"] = new[] { "x_cry" },
        ["fear"] = new[] { "x_fear" },
        ["work"] = new[] { "x_work" },
        ["call"] = new[] { "02", "03" },
    };

    // §28.15E: тема разговора красит лицо, пока пара болтает. Ключи — имена
    // TalkTopic из симуляции (enum.ToString() в снапшоте); неизвестная тема
    // просто не трогает лицо, чтобы новый член enum ничего не ломал.
    private static readonly Dictionary<string, string[]> TopicVariants = new()
    {
        ["SmallTalk"] = new[] { "05" },
        ["Escape"] = new[] { "12" },
        ["Dogs"] = new[] { "x_fear" },
        ["Weather"] = new[] { "17" },
        ["Food"] = new[] { "06" },
        ["Fire"] = new[] { "05" },
        ["Home"] = new[] { "17" },
        ["Gossip"] = new[] { "13", "16" },
        ["Flirt"] = new[] { "13", "20", "09", "18" },
        ["Joke"] = new[] { "01", "02", "08", "15" },
        ["Grumble"] = new[] { "16", "10" },
        ["Hunger"] = new[] { "10" },
        ["Thirst"] = new[] { "10" },
        ["Pain"] = new[] { "x_sad" },
        ["Tired"] = new[] { "14" },
        ["Cold"] = new[] { "17" },
        ["Stranger"] = new[] { "16", "x_angry" },
    };

    // Разговор кончился, отношения дрогнули (§28.15E "+/-"): вспышка на лице.
    private static readonly string[] PopWarm = { "06", "01", "15" };
    private static readonly string[] PopSour = { "16", "10" };
    private static readonly string[] PopSourStrong = { "x_angry" };

    // ---------------------------------------------------------------- state

    private FaceExpressionRig _rig;
    // §67.8: пока NpcVoiceLipSync озвучивает реплику, ротовая группа шейпов
    // выражения гасится (рот целиком у визем) — брови/щёки/глаза остаются,
    // так что боль и плач читаются на лице и во время реплики.
    private Audio.NpcVoiceLipSync _lipSync;
    private float _mouthScale = 1f;  // 1 рецепт как есть .. 0 рот у липсинка
    // Blink writes AFTER the rig so a closing lid always wins; the base under
    // the blink is whatever the current expression put on EyesClosedL/R.
    private readonly List<(SkinnedMeshRenderer skin, int index)> _eyeTargets = new();
    private float _blinkWeight;      // 0 open .. 100 shut
    private float _blinkTimer;
    private float _blinkPhase = -1f; // <0 idle, otherwise 0..1 close, 1..2 open
    private bool _eyesHold;          // §80: не моргать, идёт съёмка портрета
    private bool _sleeping;
    private bool _crying;            // §110: лежит и рыдает — гримаса держится
    private float _wellbeing = 0.6f; // 0 miserable .. 1 great
    private bool _fighting;
    private float _surprisePulse;
    private float _pain;             // 0 none .. 1 writhing (fresh bleeding wounds)
    private bool _cameraAttention;   // §130: смотрит в объектив — лёгкая улыбка
    private bool _romance;
    private bool _romanceDistressed;

    private int _talkIndex = -1;     // эмоция реплики (§67.8)
    private float _talkUntil;
    private int _popIndex = -1;      // вспышка после разговора
    private float _popUntil;
    private int _topicIndex = -1;    // тема текущего разговора
    private string _topic = "";

    // Crossfade: одно активное выражение; смена — быстрый спад до нуля, свап,
    // подъём. Rig сам зануляет каналы предыдущего выражения при свапе.
    private int _currentIndex = -1;
    private float _level;            // 0..100

    // Индексы часто нужных выражений, разрешённые один раз в Construct.
    private int _idxPain, _idxCry, _idxAngry, _idxSurprise, _idxCamera;
    private int _idxMoodSmile, _idxMoodSad, _idxRomanceHappy, _idxFear;

    public void Construct(SkinnedMeshRenderer[] bodySkins)
    {
        // The prefabs ship with stuck authored expressions (Jana: frown 42 +
        // eyes half-closed; Molly: smile 37) — zero every eCTRL* expression
        // shape so the face starts neutral. Character morphs (non-eCTRL)
        // define the actor's look and are left untouched.
        _eyeTargets.Clear();
        if (bodySkins != null)
        {
            foreach (var skin in bodySkins)
            {
                var mesh = skin != null ? skin.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    var name = mesh.GetBlendShapeName(i);
                    if (name.Contains("eCTRL"))
                    {
                        skin.SetBlendShapeWeight(i, 0f);
                    }

                    // Known asset bug: the combined eCTRLEyesClosed shape does
                    // nothing — blink drives the per-eye L/R shapes in sync.
                    if (name.EndsWith("eCTRLEyesClosedL") || name.EndsWith("eCTRLEyesClosedR"))
                    {
                        _eyeTargets.Add((skin, i));
                    }
                }
            }
        }

        _rig = new FaceExpressionRig(bodySkins, SharedCatalog, gameSafe: true);
        // Липсинк живёт на том же GameObject (NpcActorView вешает его раньше);
        // у примитивных капсул его нет — тогда рот всегда у эмоции.
        _lipSync = GetComponent<Audio.NpcVoiceLipSync>();
        _idxPain = SharedCatalog.FindIndex("x_pain");
        _idxCry = SharedCatalog.FindIndex("x_cry");
        _idxAngry = SharedCatalog.FindIndex("x_angry");
        _idxSurprise = SharedCatalog.FindIndex("03");
        _idxCamera = SharedCatalog.FindIndex("04");
        _idxMoodSmile = SharedCatalog.FindIndex("05");
        _idxMoodSad = SharedCatalog.FindIndex("x_sad");
        _idxRomanceHappy = SharedCatalog.FindIndex("06");
        _idxFear = SharedCatalog.FindIndex("x_fear");

        _blinkTimer = Random.Range(BlinkInterval.x, BlinkInterval.y);
        enabled = _eyeTargets.Count > 0 || _rig.Count > 0;
    }

    // wellbeing: 0..1 aggregate of the NPC's needs; fighting switches the
    // face to anger and, on the first frame, pops a surprised flash.
    public void SetMood(float wellbeing, bool fighting)
    {
        _wellbeing = Mathf.Clamp01(wellbeing);
        if (fighting && !_fighting)
        {
            _surprisePulse = 1f;
        }

        _fighting = fighting;
    }

    public void SetSleeping(bool sleeping)
    {
        _sleeping = sleeping;
    }

    // §80: пока идёт съёмка портрета — глаза не моргают. Снимок читается ОДИН
    // кадр, и попасть в моргание он может запросто: интервал 2.2-5.5 с, а само
    // моргание длится доли секунды. Спящей это не касается — у неё глаза
    // закрыты по делу.
    public void SetEyesHold(bool hold)
    {
        if (hold == _eyesHold)
        {
            return;
        }

        _eyesHold = hold;
        if (hold)
        {
            _blinkPhase = -1f;
            _blinkTimer = Random.Range(BlinkInterval.x, BlinkInterval.y);
        }
    }

    // §110: она лежит и рыдает — гримаса плача ДЕРЖИТСЯ всё это время, а не
    // вспыхивает на длину реплики.
    public void SetCrying(bool crying)
    {
        _crying = crying;
    }

    /// <summary>§127: held face for a paired scene. Consensual participants
    /// are visibly delighted; a forced victim keeps the authored fear face.</summary>
    public void SetRomance(bool active, bool distressed)
    {
        _romance = active;
        _romanceDistressed = active && distressed;
    }

    // §67.8: короткая эмоция на время голосовой реплики — оверлей поверх
    // фонового настроения. Вариант выражения выбирается здесь, на фронте.
    public void FlashTalkEmotion(string emotion, float seconds)
    {
        // §67.10: реплики каталога приходят как "<эмоция>_<повод>"
        // ("sad_thirst") — лицу нужна только эмоция, повод рисует бабл.
        if (!string.IsNullOrEmpty(emotion))
        {
            var cut = emotion.IndexOf('_');
            if (cut > 0)
            {
                emotion = emotion[..cut];
            }
        }

        if (emotion == null || !VoiceVariants.TryGetValue(emotion, out var variants))
        {
            // Раньше опечатка в каталоге молчаливо давала каменное лицо.
            Debug.LogWarning($"[NpcFaceAnimator] unknown talk emotion '{emotion}'");
            return;
        }

        _talkIndex = Pick(variants);
        _talkUntil = Time.time + Mathf.Clamp(seconds, 0.5f, 6f);
    }

    // §28.15E: тема разговора из снапшота ("" = не разговаривает). Лицо держит
    // тематическое выражение, пока пара болтает; вариант — один на разговор.
    public void SetTalkTopic(string topic)
    {
        topic ??= "";
        if (topic == _topic)
        {
            return;
        }

        _topic = topic;
        _topicIndex = topic.Length > 0 && TopicVariants.TryGetValue(topic, out var variants)
            ? Pick(variants)
            : -1;
    }

    // §28.15E: исход разговора — тёплая вспышка на плюс, кислая на минус
    // (сильный минус, |Δ|≥0.10 как у сдвоенного глифа пузыря, — злость).
    public void FlashRelationship(float delta)
    {
        if (Mathf.Abs(delta) < 0.001f)
        {
            return;
        }

        var variants = delta > 0f ? PopWarm
            : delta <= -0.10f ? PopSourStrong : PopSour;
        _popIndex = Pick(variants);
        _popUntil = Time.time + PopSeconds;
    }

    // §130: пока камера вплотную и NPC смотрит в объектив — лёгкая улыбка
    // поверх настроения; плач, боль и бой сильнее (выше по каскаду).
    public void SetCameraAttention(bool attention)
    {
        _cameraAttention = attention;
    }

    // Pain/wince level from fresh bleeding wounds (0 none .. 1 writhing), with
    // a slow throb so it reads as waves of pain.
    public void SetPain(float pain01)
    {
        _pain = Mathf.Clamp01(pain01);
    }

    private int Pick(string[] variants)
    {
        // Presentation-only randomness (как у моргания) — сим не читает лицо.
        var name = variants[Random.Range(0, variants.Length)];
        var index = SharedCatalog.FindIndex(name);
        if (index < 0)
        {
            // Выражения 11-20 пака существуют только как -Left/-Right (взгляд
            // в сторону; сам сдвиг глаз риг игры пропускает) — сторона
            // случайна, остаётся лёгкая асимметрия бровей/век.
            var side = Random.value < 0.5f ? "-Left" : "-Right";
            index = SharedCatalog.FindIndex(name + side);
        }

        return index;
    }

    private void LateUpdate()
    {
        var dt = Time.deltaTime;
        _surprisePulse = Mathf.MoveTowards(_surprisePulse, 0f, dt * 1.6f);

        // --- Каскад приоритетов: одно желаемое выражение на кадр. ---
        var index = -1;
        var level = 0f;
        if (_pain > 0.05f)
        {
            index = _idxPain;
            level = 100f * _pain * (0.85f + 0.15f * Mathf.Sin(Time.time * 6f));
        }
        else if (_crying)
        {
            index = _idxCry;
            level = 100f;
        }
        else if (_fighting)
        {
            // На первом кадре боя — изумлённый вскид, дальше злость.
            if (_surprisePulse > 0.35f)
            {
                index = _idxSurprise;
                level = _surprisePulse * 90f;
            }
            else
            {
                index = _idxAngry;
                level = 80f;
            }
        }
        else if (_romance)
        {
            index = _romanceDistressed ? _idxFear : _idxRomanceHappy;
            level = _romanceDistressed ? 92f : 88f;
        }
        else if (_talkIndex >= 0 && Time.time < _talkUntil)
        {
            index = _talkIndex;
            level = 100f;
        }
        else if (_popIndex >= 0 && Time.time < _popUntil)
        {
            index = _popIndex;
            level = 90f;
        }
        else if (_topicIndex >= 0)
        {
            index = _topicIndex;
            level = TopicLevel;
        }
        else if (_cameraAttention)
        {
            index = _idxCamera;
            level = 50f;
        }
        else if (!_sleeping)
        {
            // Content: an easy resting smile. Miserable: a visible sad face.
            var happy = Mathf.InverseLerp(0.55f, 0.9f, _wellbeing);
            var sad = Mathf.InverseLerp(0.45f, 0.15f, _wellbeing);
            if (sad > 0.05f)
            {
                index = _idxMoodSad;
                level = 50f * sad;
            }
            else
            {
                index = _idxMoodSmile;
                level = 12f + 38f * happy;   // never fully dead-faced
            }
        }

        // --- Кроссфейд: спад до нуля (2× скорость), свап, подъём. ---
        if (index != _currentIndex)
        {
            _level = Mathf.MoveTowards(_level, 0f, EmotionSpeed * 2f * dt);
            if (_level <= 1f)
            {
                _currentIndex = index;
            }
        }
        else
        {
            _level = Mathf.MoveTowards(_level, level, EmotionSpeed * dt);
        }

        // §67.8: звучит реплика → ротовая группа выражения плавно отпускается
        // (ртом рулят виземы §67.7), после реплики так же плавно возвращается.
        var mouthTarget = _lipSync != null && _lipSync.IsSpeaking ? 0f : 1f;
        _mouthScale = Mathf.MoveTowards(_mouthScale, mouthTarget, MouthMuteSpeed * dt);

        _rig?.Apply(_currentIndex, _level / 100f, _mouthScale);

        // --- Blink, ПОВЕРХ выражения (закрывающееся веко всегда побеждает).
        // База — то, что текущее выражение положило в EyesClosedL/R
        // (расширенные глаза = отрицательная база).
        var blinkOpenSpeed = _romance ? 55f : BlinkOpenSpeed;
        var blinkRest = _romance ? 28f : 0f;
        if (_sleeping)
        {
            _blinkWeight = Mathf.MoveTowards(_blinkWeight, 100f, SleepEyeSpeed * dt);
            _blinkPhase = -1f;
        }
        else if (_eyesHold)
        {
            _blinkWeight = Mathf.MoveTowards(_blinkWeight, blinkRest, blinkOpenSpeed * dt);
            _blinkPhase = -1f;
        }
        else if (_blinkPhase < 0f)
        {
            _blinkTimer -= dt;
            if (_blinkTimer <= 0f)
            {
                _blinkTimer = Random.Range(BlinkInterval.x, BlinkInterval.y);
                _blinkPhase = 0f;
            }

            _blinkWeight = Mathf.MoveTowards(_blinkWeight, blinkRest, blinkOpenSpeed * dt);
        }
        else if (_blinkPhase < 1f)
        {
            _blinkWeight = Mathf.MoveTowards(_blinkWeight, 100f, BlinkCloseSpeed * dt);
            if (_blinkWeight >= 99f)
            {
                _blinkPhase = 1f;
            }
        }
        else
        {
            _blinkWeight = Mathf.MoveTowards(_blinkWeight, blinkRest, blinkOpenSpeed * dt);
            if (_blinkWeight <= blinkRest + 0.5f)
            {
                _blinkPhase = -1f;
            }
        }

        var eyelid = Mathf.Min(100f, (_rig?.AppliedEyesClosed ?? 0f) + _blinkWeight);
        foreach (var (skin, i) in _eyeTargets)
        {
            if (skin != null)
            {
                skin.SetBlendShapeWeight(i, eyelid);
            }
        }
    }
}

}
