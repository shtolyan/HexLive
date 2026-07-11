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

    private float _blinkTimer;
    private float _blinkPhase = -1f; // <0 idle, otherwise 0..1 close, 1..2 open
    private bool _sleeping;
    private float _wellbeing = 0.6f; // 0 miserable .. 1 great
    private bool _fighting;
    private float _surprisePulse;

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

    private void LateUpdate()
    {
        var dt = Time.deltaTime;

        // --- Eyes ---
        if (_sleeping)
        {
            _eyesClosed.Apply(100f, SleepEyeSpeed, dt);
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

        _smile.Apply(smile, EmotionSpeed, dt);
        _smileFull.Apply(smileFull, EmotionSpeed, dt);
        _frown.Apply(frown, EmotionSpeed, dt);
        _angry.Apply(angry, EmotionSpeed, dt);
        _surprised.Apply(_surprisePulse * 80f, 400f, dt);
    }
}

}
