using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{

// Spec 28.15E: a Sims-style overhead chat bubble. While an NPC is talking it
// floats a bubble sprite (Resources/HexLive/UI/speech_bubble) with the
// conversation's emoji over the head; when a talk resolves it pops a green "+"
// / red "-" relationship change. Entirely world-space (SpriteRenderers) so it
// needs no Canvas/TMP setup — it billboards to the camera each frame. All
// lifetime is owned by NpcActorView.
//
// Both the emoji and the "+/-" are pre-rendered PNG sprites under
// Resources/HexLive/UI/Emoji (real colour emoji, rasterised from the system
// font). We deliberately do NOT use a legacy TextMesh glyph: Unity's dynamic
// font path renders colour-bitmap emoji fonts as empty/white "tofu", which is
// why the old glyph bubble came out blank.
public sealed class NpcSpeechBubble : MonoBehaviour
{
    private const float HeightOffset = 0.34f;    // world units above the head bone
    private const float BubbleUnitsTall = 0.70f; // on-screen bubble height (bigger white bg)
    private const float EmojiUnitsTall = 0.26f;  // emoji height inside the body (smaller, well inside)
    // Centre of the white oval body in speech_bubble.png, measured in sprite
    // texture space from the bottom-left. The tail/outline make the full image's
    // geometric centre wrong for the emoji.
    private static readonly Vector2 BubbleBodyCenterFrac = new(0.5205f, 0.5918f);
    private const float PopUnitsTall = 0.19f;    // "+/-" height (≈half the old size)
    private const float PopRiseSpeed = 0.42f;    // units/sec the "+/-" floats up
    private const float PopLifetime = 1.25f;

    private const string EmojiDir = "HexLive/UI/Emoji/";

    private static readonly Dictionary<string, Sprite> _spriteCache = new();

    private static readonly Color PosColor = new(0.36f, 0.86f, 0.42f);
    private static readonly Color NegColor = new(0.95f, 0.38f, 0.34f);

    private Transform _anchor;
    private Camera _cam;

    private SpriteRenderer _bubble;
    private SpriteRenderer _emoji;

    private Transform _pop;
    private SpriteRenderer _popRenderer;
    private float _popTimer = -1f;

    private Transform _cue;
    private SpriteRenderer _cueRenderer;
    private float _cueTimer = -1f;
    private Color _cueColor = Color.white;

    private bool _shown;
    private float _shownAmount; // eased 0..1 for the pop-in scale/fade

    public void Initialize(Transform anchor, int sortingBase)
    {
        _anchor = anchor;
        _cam = Camera.main;

        // Bubble body.
        var bubbleGo = new GameObject("Bubble");
        bubbleGo.transform.SetParent(transform, false);
        _bubble = bubbleGo.AddComponent<SpriteRenderer>();
        _bubble.sprite = ResolveBubble();
        _bubble.sortingOrder = sortingBase;

        // Emoji sprite, centred in the bubble body (bubble pivot is near the
        // tail, so the body centre sits above/right of the pivot).
        var emojiGo = new GameObject("Emoji");
        emojiGo.transform.SetParent(transform, false);
        _emoji = emojiGo.AddComponent<SpriteRenderer>();
        _emoji.sortingOrder = sortingBase + 1;
        _emojiLocalPosition = BubbleBodyCenterLocal();
        _emoji.transform.localPosition = _emojiLocalPosition;

        // Relationship "+/-" pop (independent of the bubble; can fire alone).
        var popGo = new GameObject("RelationshipPop");
        popGo.transform.SetParent(transform, false);
        _popRenderer = popGo.AddComponent<SpriteRenderer>();
        _popRenderer.sortingOrder = sortingBase + 2;
        _pop = popGo.transform;
        _pop.gameObject.SetActive(false);

        // Short social event pop: request/refusal/aid/quarrel/etc. It is
        // separate from the relationship "+/-", so an insult can show both.
        var cueGo = new GameObject("SocialCuePop");
        cueGo.transform.SetParent(transform, false);
        _cueRenderer = cueGo.AddComponent<SpriteRenderer>();
        _cueRenderer.sortingOrder = sortingBase + 3;
        _cue = cueGo.transform;
        _cue.gameObject.SetActive(false);

        SetVisible(false);
    }

    // Show the bubble with this topic's emoji, or hide it when the sim reports
    // no active talk topic.
    public void SetTopic(string topicName)
    {
        if (!TalkTopicVisuals.IsKnown(topicName))
        {
            SetVisible(false);
            return;
        }

        var sprite = ResolveEmoji(topicName);
        if (_emoji != null)
        {
            _emoji.sprite = sprite;
            FitSpriteHeight(_emoji, EmojiUnitsTall);
        }

        SetVisible(true);
    }

    // Fire the Sims-style relationship change. delta > 0 = warmed (+/++ green),
    // delta < 0 = soured (-/-- red). No-op for a negligible change.
    public void PopRelationship(float delta)
    {
        if (_popRenderer == null)
        {
            return;
        }

        var mag = Mathf.Abs(delta);
        if (mag < 0.001f)
        {
            return;
        }

        var doubled = mag >= 0.10f;   // a bigger swing shows a doubled glyph
        var positive = delta > 0f;
        var key = (positive ? "pop_plus" : "pop_minus") + (doubled ? "2" : string.Empty);

        _popRenderer.sprite = ResolveEmoji(key);
        _popRenderer.color = positive ? PosColor : NegColor;
        FitSpriteHeight(_popRenderer, PopUnitsTall);
        _pop.gameObject.SetActive(true);
        _popTimer = 0f;
    }

    public void PopSocialCue(string cueKind)
    {
        if (_cueRenderer == null || string.IsNullOrEmpty(cueKind))
        {
            return;
        }

        _cueRenderer.sprite = ResolveEmoji(SocialCueSprite(cueKind));
        _cueColor = SocialCueColor(cueKind);
        _cueRenderer.color = _cueColor;
        FitSpriteHeight(_cueRenderer, EmojiUnitsTall * 0.92f);
        _cue.gameObject.SetActive(true);
        _cueTimer = 0f;
    }

    private void SetVisible(bool visible)
    {
        _shown = visible;
        if (!visible && _bubble != null)
        {
            // Snap hidden immediately when the talk ends (the ease is only for
            // the appearance); avoids a lingering ghost while walking off.
            _shownAmount = 0f;
            ApplyShownAmount();
        }
    }

    private void LateUpdate()
    {
        if (_anchor == null)
        {
            return;
        }

        if (_cam == null)
        {
            _cam = Camera.main;
        }

        // Position at the head + fixed lift; billboard to the camera.
        var basePos = _anchor.position + Vector3.up * HeightOffset;
        transform.position = basePos;
        if (_cam != null)
        {
            transform.rotation = _cam.transform.rotation;
        }

        // Neutralise the parent actor's normalisation scale so the bubble is a
        // consistent world size regardless of how the NPC body was scaled.
        var parent = transform.parent;
        if (parent != null)
        {
            var ls = parent.lossyScale;
            transform.localScale = new Vector3(
                Mathf.Approximately(ls.x, 0f) ? 1f : 1f / ls.x,
                Mathf.Approximately(ls.y, 0f) ? 1f : 1f / ls.y,
                Mathf.Approximately(ls.z, 0f) ? 1f : 1f / ls.z);
        }

        // Ease the bubble in (pop) and out.
        var target = _shown ? 1f : 0f;
        _shownAmount = Mathf.MoveTowards(_shownAmount, target, Time.deltaTime * 6f);
        ApplyShownAmount();

        // Animate the relationship pop: float up + fade, then retire.
        if (_popTimer >= 0f && _pop != null)
        {
            _popTimer += Time.deltaTime;
            var t = _popTimer / PopLifetime;
            if (t >= 1f)
            {
                _popTimer = -1f;
                _pop.gameObject.SetActive(false);
            }
            else
            {
                // Rise from just above the head, independent of bubble scale.
                var rise = HeightOffset + 0.15f +
                           _popTimer * PopRiseSpeed / Mathf.Max(0.01f, transform.lossyScale.y);
                _pop.localPosition = new Vector3(0f, rise, -0.02f);
                var punch = 1f + Mathf.Clamp01(1f - t * 5f) * 0.5f; // quick overshoot
                _pop.localScale = Vector3.one * (_popBaseScale * punch);
                if (_popRenderer != null)
                {
                    var c = _popRenderer.color;
                    c.a = 1f - Mathf.SmoothStep(0.5f, 1f, t); // hold, then fade
                    _popRenderer.color = c;
                }
            }
        }

        if (_cueTimer >= 0f && _cue != null)
        {
            _cueTimer += Time.deltaTime;
            var t = _cueTimer / PopLifetime;
            if (t >= 1f)
            {
                _cueTimer = -1f;
                _cue.gameObject.SetActive(false);
            }
            else
            {
                var rise = HeightOffset + 0.02f +
                           _cueTimer * (PopRiseSpeed * 0.55f) / Mathf.Max(0.01f, transform.lossyScale.y);
                _cue.localPosition = new Vector3(-0.18f, rise, -0.03f);
                var punch = 1f + Mathf.Clamp01(1f - t * 4f) * 0.35f;
                _cue.localScale = Vector3.one * (_cueBaseScale * punch);
                if (_cueRenderer != null)
                {
                    var c = _cueColor;
                    c.a = 1f - Mathf.SmoothStep(0.45f, 1f, t);
                    _cueRenderer.color = c;
                }
            }
        }
    }

    private void ApplyShownAmount()
    {
        var s = Mathf.SmoothStep(0f, 1f, _shownAmount);
        if (_bubble != null)
        {
            _bubble.enabled = s > 0.01f;
            _bubble.transform.localScale = Vector3.one * (_bubbleBaseScale * s);
        }

        if (_emoji != null)
        {
            _emoji.enabled = s > 0.01f && _emoji.sprite != null;
            _emoji.transform.localScale = Vector3.one * (_emojiBaseScale * s);
            _emoji.transform.localPosition = new Vector3(
                _emojiLocalPosition.x * s,
                _emojiLocalPosition.y * s,
                _emojiLocalPosition.z);
        }
    }

    // ---- sprite sizing (localScale that renders a sprite at a target world height) ----

    private float _bubbleBaseScale = 1f;
    private float _emojiBaseScale = 1f;
    private float _popBaseScale = 1f;
    private float _cueBaseScale = 1f;
    private Vector3 _emojiLocalPosition = new(0f, 0.24f, -0.01f);

    private Vector3 BubbleBodyCenterLocal()
    {
        var sprite = _bubble != null ? _bubble.sprite : null;
        if (sprite == null || sprite.rect.width <= 0f || sprite.rect.height <= 0f ||
            sprite.pixelsPerUnit <= 0.0001f)
        {
            return _emojiLocalPosition;
        }

        var centerPx = new Vector2(
            BubbleBodyCenterFrac.x * sprite.rect.width,
            BubbleBodyCenterFrac.y * sprite.rect.height);
        var local = (centerPx - sprite.pivot) / sprite.pixelsPerUnit * _bubbleBaseScale;
        return new Vector3(local.x, local.y, -0.01f);
    }

    private void FitSpriteHeight(SpriteRenderer sr, float targetTall)
    {
        var k = 1f;
        if (sr.sprite != null && sr.sprite.bounds.size.y > 0.0001f)
        {
            k = targetTall / sr.sprite.bounds.size.y;
        }

        if (sr == _emoji)
        {
            _emojiBaseScale = k;
        }
        else if (sr == _popRenderer)
        {
            _popBaseScale = k;
        }
        else if (sr == _cueRenderer)
        {
            _cueBaseScale = k;
        }

        sr.transform.localScale = Vector3.one * k;
    }

    private static string SocialCueSprite(string cueKind)
    {
        return cueKind switch
        {
            "TalkRejected" or "TalkRefused" or "TalkQuarrel" or "Resentment" => "Grumble",
            "AidRequest" or "AidIncoming" or "AidStarted" or "AidCompleted" => "Food",
            "WitnessedMurder" => "Sharks",
            "TalkSuccess" => "Joke",
            _ => "SmallTalk"
        };
    }

    private static Color SocialCueColor(string cueKind)
    {
        return cueKind switch
        {
            "TalkRejected" or "TalkRefused" or "TalkQuarrel" or "Resentment" or "WitnessedMurder" => NegColor,
            "AidRequest" or "AidIncoming" or "AidStarted" or "AidCompleted" or "TalkSuccess" => PosColor,
            _ => Color.white
        };
    }

    private Sprite ResolveBubble()
    {
        var s = ResolveSprite("speech_bubble", "HexLive/UI/speech_bubble",
            new Vector2(0.5f, 0.16f), 820f);
        // Scale the (variable-size) bubble to a fixed on-screen height.
        _bubbleBaseScale = s != null && s.bounds.size.y > 0.0001f
            ? BubbleUnitsTall / s.bounds.size.y
            : 1f;
        return s;
    }

    private static Sprite ResolveEmoji(string name)
        => ResolveSprite("emoji:" + name, EmojiDir + name, new Vector2(0.5f, 0.5f), 100f);

    // Load a texture from Resources and wrap it in a runtime Sprite (cached).
    // We build the sprite from the Texture2D rather than Resources.Load<Sprite>
    // so the PNG needs no Sprite importer settings.
    private static Sprite ResolveSprite(string cacheKey, string resourcePath, Vector2 pivot, float ppu)
    {
        if (_spriteCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var tex = Resources.Load<Texture2D>(resourcePath);
        Sprite sprite = null;
        if (tex != null)
        {
            sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), pivot, ppu);
        }
        else
        {
            Debug.LogWarning($"[NpcSpeechBubble] Resources/{resourcePath} not found.");
        }

        _spriteCache[cacheKey] = sprite;
        return sprite;
    }
}

}
