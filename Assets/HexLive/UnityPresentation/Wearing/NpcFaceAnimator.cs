using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Face life on the Genesis3 blend shapes (ported from molly_copy's
// BlendShapeBlinking idea, coroutine-free): random blinking, eyes held shut
// while sleeping, and a mood layer — the face mirrors the NPC's overall
// wellbeing (smile when content, frown when miserable, angry in a fight,
// a surprised pop when combat starts). Presentation-only randomness.
public sealed class NpcFaceAnimator : MonoBehaviour
{
    // Blend-shape weights are 0..100.
    private const float BlinkCloseSpeed = 900f;  // fast close
    private const float BlinkOpenSpeed = 450f;   // slower open
    private const float EmotionSpeed = 90f;      // mood fades in/out
    private const float SleepEyeSpeed = 160f;

    private static readonly Vector2 BlinkInterval = new(2.2f, 5.5f);

    // One resolved shape channel across every renderer that has it.
    private sealed class Channel
    {
        public readonly List<(SkinnedMeshRenderer skin, int index)> Targets = new();
        public float Current;

        public void Apply(float target, float speed, float dt)
        {
            Current = Mathf.MoveTowards(Current, target, speed * dt);
            foreach (var (skin, index) in Targets)
            {
                if (skin != null)
                {
                    skin.SetBlendShapeWeight(index, Current);
                }
            }
        }
    }

    private Channel _eyesClosed;
    private Channel _smile;       // eCTRLMouthSmileSimple — mouth only, subtle
    private Channel _smileFull;   // eCTRLSmile — whole-face, used sparingly
    private Channel _frown;       // eCTRLMouthFrown
    private Channel _angry;
    private Channel _surprised;
    // Pain wince (no single eCTRLPain shape exists — composited from FACS units).
    private Channel _browSqueeze;   // brows knit together+down — the signature
    private Channel _eyesSquint;    // eyes screwed up (lower lids + orbital)
    private Channel _noseScrunch;   // nose wrinkled (levator — pain/disgust)
    private Channel _cheekFlex;     // cheeks pushed up under the squint
    private Channel _mouthGrimace;  // corners pulled back — a teeth-bared wince
    private Channel _lipsPart;      // lips part in a pained gasp

    private float _blinkTimer;
    private float _blinkPhase = -1f; // <0 idle, otherwise 0..1 close, 1..2 open
    private bool _eyesHold;          // §80: не моргать, идёт съёмка портрета
    private bool _sleeping;
    private float _wellbeing = 0.6f; // 0 miserable .. 1 great
    private bool _fighting;
    private float _surprisePulse;
    private float _pain;             // 0 none .. 1 writhing (fresh bleeding wounds)

    public void Construct(SkinnedMeshRenderer[] bodySkins)
    {
        // The prefabs ship with stuck authored expressions (Jana: frown 42 +
        // eyes half-closed; Molly: smile 37) — zero every eCTRL* expression
        // shape so the face starts neutral. Character morphs (non-eCTRL)
        // define the actor's look and are left untouched.
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
                    if (mesh.GetBlendShapeName(i).Contains("eCTRL"))
                    {
                        skin.SetBlendShapeWeight(i, 0f);
                    }
                }
            }
        }

        // Known asset bug: the combined eCTRLEyesClosed shape does nothing —
        // blink by driving the per-eye L/R shapes together, in sync.
        _eyesClosed = Resolve(bodySkins, "eCTRLEyesClosedL", "eCTRLEyesClosedR");
        _smile = Resolve(bodySkins, "eCTRLMouthSmileSimple");
        _smileFull = Resolve(bodySkins, "eCTRLSmile");
        _frown = Resolve(bodySkins, "eCTRLMouthFrown");
        _angry = Resolve(bodySkins, "eCTRLAngry");
        _surprised = Resolve(bodySkins, "eCTRLSurprised");
        // Pain wince, built from FACS primitives (drive L/R shapes together, as
        // the combined controllers are unreliable on these figures).
        _browSqueeze = Resolve(bodySkins, "eCTRLBrowSqueeze");
        _eyesSquint = Resolve(bodySkins, "eCTRLEyesSquintL", "eCTRLEyesSquintR");
        _noseScrunch = Resolve(bodySkins, "eCTRLNoseScrunch", "eCTRLNoseWrinkle");
        _cheekFlex = Resolve(bodySkins, "eCTRLCheekFlexL", "eCTRLCheekFlexR");
        _mouthGrimace = Resolve(bodySkins, "eCTRLMouthCornerBackL", "eCTRLMouthCornerBackR");
        _lipsPart = Resolve(bodySkins, "eCTRLLipsPart");
        _blinkTimer = Random.Range(BlinkInterval.x, BlinkInterval.y);
        enabled = _eyesClosed.Targets.Count > 0 || _smile.Targets.Count > 0;
    }

    // exact-name or suffix match, so both "eCTRLSmile" and
    // "Genesis3Female__eCTRLSmile" resolve; exclude longer variants
    // (eCTRLEyesClosed must not grab eCTRLEyesClosedL/R).
    private static Channel Resolve(SkinnedMeshRenderer[] skins, params string[] shapes)
    {
        var channel = new Channel();
        if (skins == null)
        {
            return channel;
        }

        foreach (var skin in skins)
        {
            var mesh = skin != null ? skin.sharedMesh : null;
            if (mesh == null)
            {
                continue;
            }

            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                var name = mesh.GetBlendShapeName(i);
                foreach (var shape in shapes)
                {
                    if (name == shape || name.EndsWith("__" + shape) || name.EndsWith("." + shape))
                    {
                        channel.Targets.Add((skin, i));
                        break;
                    }
                }
            }
        }

        return channel;
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

    // §67.8: короткая эмоция на время голосовой реплики — оверлей поверх
    // фонового настроения (говорит радостно/грустно/зло, потом лицо само
    // возвращается к настроению). Боль (SetPain) всё равно сильнее.
    private string _talkEmotion;
    private float _talkEmotionUntil;

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

        _talkEmotion = emotion;
        _talkEmotionUntil = Time.time + Mathf.Clamp(seconds, 0.5f, 6f);
    }

    // Pain/wince level from fresh bleeding wounds (0 none .. 1 writhing). There
    // is no single Genesis3 "pain" morph — LateUpdate composites the grimace
    // from FACS units and gives it a slow throb so it reads as waves of pain.
    public void SetPain(float pain01)
    {
        _pain = Mathf.Clamp01(pain01);
    }

    private void LateUpdate()
    {
        var dt = Time.deltaTime;

        // --- Eyes ---
        if (_sleeping)
        {
            _eyesClosed.Apply(100f, SleepEyeSpeed, dt);
            _blinkPhase = -1f;
        }
        else if (_eyesHold)
        {
            _eyesClosed.Apply(0f, BlinkOpenSpeed, dt);
            _blinkPhase = -1f;
        }
        else
        {
            if (_blinkPhase < 0f)
            {
                _blinkTimer -= dt;
                if (_blinkTimer <= 0f)
                {
                    _blinkTimer = Random.Range(BlinkInterval.x, BlinkInterval.y);
                    _blinkPhase = 0f;
                }

                _eyesClosed.Apply(0f, BlinkOpenSpeed, dt);
            }
            else if (_blinkPhase < 1f)
            {
                _blinkPhase = Mathf.Min(1f, _blinkPhase + dt * (BlinkCloseSpeed / 100f));
                _eyesClosed.Apply(100f, BlinkCloseSpeed, dt);
                if (_eyesClosed.Current >= 99f)
                {
                    _blinkPhase = 1f;
                }
            }
            else
            {
                _eyesClosed.Apply(0f, BlinkOpenSpeed, dt);
                if (_eyesClosed.Current <= 0.5f)
                {
                    _blinkPhase = -1f;
                }
            }
        }

        // --- Mood ---
        _surprisePulse = Mathf.MoveTowards(_surprisePulse, 0f, dt * 1.6f);

        float smile = 0f, smileFull = 0f, frown = 0f, angry = 0f;
        if (_fighting)
        {
            angry = 70f;
        }
        else
        {
            // Content: an easy resting smile. Miserable: a visible frown.
            var happy = Mathf.InverseLerp(0.55f, 0.9f, _wellbeing);
            var sad = Mathf.InverseLerp(0.45f, 0.15f, _wellbeing);
            smile = 10f + happy * 45f;      // never fully dead-faced
            smileFull = happy * 18f;        // touch of eyes at real happiness
            frown = sad * 50f;
            if (sad > 0.05f)
            {
                smile = Mathf.Min(smile, (1f - sad) * 10f);
                smileFull = 0f;
            }
        }

        // --- §67.8: talk-emotion overlay — реплика окрашивает лицо целиком.
        float overlayBrow = 0f, overlaySquint = 0f, overlayNose = 0f, overlayCheek = 0f;
        // §67.10: каналы для fear/work/call (испуг, натуга, оклик).
        float overlaySurprise = 0f, overlayLipsPart = 0f, overlayGrimace = 0f;
        if (_talkEmotion != null && Time.time >= _talkEmotionUntil)
        {
            _talkEmotion = null;
        }

        if (_talkEmotion != null)
        {
            // Последние полсекунды реплики эмоция плавно отпускает лицо.
            var k = Mathf.Clamp01((_talkEmotionUntil - Time.time) / 0.5f);
            switch (_talkEmotion)
            {
                case "happy": // улыбка/смех: всё лицо, щёки вверх
                    smile = Mathf.Max(smile, 55f * k);
                    smileFull = Mathf.Max(smileFull, 80f * k);
                    overlayCheek = 45f * k;
                    frown = 0f;
                    break;
                case "sad":
                    frown = Mathf.Max(frown, 65f * k);
                    overlaySquint = 15f * k;
                    smile = Mathf.Min(smile, 8f);
                    smileFull = 0f;
                    break;
                case "sleepy": // полуприкрытые глаза, вялый рот
                    frown = Mathf.Max(frown, 25f * k);
                    overlaySquint = 45f * k;
                    smile = Mathf.Min(smile, 8f);
                    smileFull = 0f;
                    break;
                case "angry":
                    angry = Mathf.Max(angry, 85f * k);
                    overlayBrow = 70f * k;
                    overlayNose = 45f * k;
                    frown = Mathf.Max(frown, 45f * k);
                    smile = 0f;
                    smileFull = 0f;
                    break;
                case "cry":
                    frown = Mathf.Max(frown, 80f * k);
                    overlayBrow = 65f * k;
                    overlaySquint = 55f * k;
                    smile = 0f;
                    smileFull = 0f;
                    break;
                // §67.10: три новых канала — испуг, усилие и оклик вдаль.
                case "fear": // распахнутые глаза, приоткрытый рот, брови вверх
                    overlaySurprise = 85f * k;
                    overlayLipsPart = 55f * k;
                    frown = Mathf.Max(frown, 30f * k);
                    smile = 0f;
                    smileFull = 0f;
                    break;
                case "work": // натуга: сведённые брови, сжатый рот
                    overlayBrow = 55f * k;
                    overlayGrimace = 45f * k;
                    overlayNose = 25f * k;
                    smile = Mathf.Min(smile, 10f);
                    smileFull = 0f;
                    break;
                case "call": // зовёт: рот широко, брови вверх
                    overlayLipsPart = 80f * k;
                    overlaySurprise = 45f * k;
                    smileFull = 0f;
                    break;
            }
        }

        // --- Pain (fresh bleeding wounds): a wince composited from FACS morphs,
        // with a slow throb so it reads as writhing. It overrides the resting
        // smile — you don't smile while in pain — and reinforces the frown.
        if (_pain > 0.05f)
        {
            smile = Mathf.Min(smile, (1f - _pain) * 6f);
            smileFull = 0f;
            frown = Mathf.Max(frown, _pain * 45f);
        }

        var painThrob = _pain * (0.85f + 0.15f * Mathf.Sin(Time.time * 6f));

        _smile.Apply(smile, EmotionSpeed, dt);
        _smileFull.Apply(smileFull, EmotionSpeed, dt);
        _frown.Apply(frown, EmotionSpeed, dt);
        _angry.Apply(angry, EmotionSpeed, dt);
        _surprised.Apply(Mathf.Max(_surprisePulse * 80f, overlaySurprise), 400f, dt);

        _browSqueeze.Apply(Mathf.Max(painThrob * 80f, overlayBrow), EmotionSpeed, dt);
        _eyesSquint.Apply(Mathf.Max(painThrob * 75f, overlaySquint), EmotionSpeed, dt);
        _noseScrunch.Apply(Mathf.Max(painThrob * 55f, overlayNose), EmotionSpeed, dt);
        _cheekFlex.Apply(Mathf.Max(painThrob * 45f, overlayCheek), EmotionSpeed, dt);
        _mouthGrimace.Apply(Mathf.Max(painThrob * 55f, overlayGrimace), EmotionSpeed, dt);
        _lipsPart.Apply(Mathf.Max(painThrob * 30f, overlayLipsPart), EmotionSpeed, dt);
    }
}

}
