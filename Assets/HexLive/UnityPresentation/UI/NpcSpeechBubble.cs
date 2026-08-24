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
[DefaultExecutionOrder(1000)]
public sealed class NpcSpeechBubble : MonoBehaviour
{
    private const float HeightOffset = 0.34f;    // world units above the head bone
    private const float BubbleUnitsTall = 0.70f; // on-screen bubble height (bigger white bg)
    private const float IconBoxUnitsWide = 0.32f;
    private const float IconBoxUnitsTall = 0.22f;
    // Centre of the white oval body in speech_bubble.png, measured in sprite
    // texture space from the bottom-left. The tail/outline make the full image's
    // geometric centre wrong for the emoji.
    private static readonly Vector2 BubbleBodyCenterFrac = new(0.515f, 0.580f);
    // §107.5: значок тревоги — МАЛЕНЬКИЙ, в углу пузыря. Он метит срочность
    // того, что и так показано (лицо чужака), и никогда не дублирует картинку:
    // если в пузыре уже сам треугольник, второго не будет.
    private const float BadgeUnitsWide = 0.13f;
    private const float BadgeUnitsTall = 0.13f;
    private const string AlarmBadgeIcon = "Warning";
    private static readonly Vector2 BadgeCornerFrac = new(0.80f, 0.86f);
    private const float PopUnitsWide = 0.28f;
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

    private SpriteRenderer _badge;

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

        // §107.5: значок тревоги в углу ОДНОГО пузыря. Второго пузыря нет и не
        // будет: всё, что она хочет сказать или показать, встаёт в очередь по
        // приоритету (SpeechCatalog.Rank) и занимает это единственное место.
        var badgeGo = new GameObject("AlarmBadge");
        badgeGo.transform.SetParent(transform, false);
        _badge = badgeGo.AddComponent<SpriteRenderer>();
        _badge.sortingOrder = sortingBase + 3;
        _badge.enabled = false;
        _badgeLocalPosition = BodyPointLocal(BadgeCornerFrac, _badgeLocalPosition);
        _badge.transform.localPosition = _badgeLocalPosition;

        SetVisible(false);
    }

    // §67.10: the bubble no longer knows about talk topics — it shows whatever
    // icon the speech director hands it, for as long as it is told to. Every
    // utterance goes through here (that is the "no voice without a picture"
    // invariant), so a conversation topic and a scream at a wolf are the same
    // mechanism with a different hold time.
    //
    // seconds <= 0 = hold until told otherwise (a running conversation).
    public void ShowIcon(string iconKey, float seconds, bool alarm = false)
    {
        if (string.IsNullOrEmpty(iconKey))
        {
            HideIcon();
            return;
        }

        if (_emoji != null)
        {
            _emoji.sprite = ResolveEmoji(iconKey);
            _emoji.color = Color.white;
            FitSpriteInside(_emoji, IconBoxUnitsWide, IconBoxUnitsTall);
        }

        SetAlarmBadge(alarm, iconKey);
        _holdUntil = seconds > 0f ? Time.time + seconds : -1f;
        SetVisible(true);
    }

    // §108/§111.12: a concrete sprite (face or item) occupies the same slot as
    // an emoji. Portraits already carry their circular mask; item icons keep
    // their authored transparency. Both fit through the one sizing path.
    public void ShowIcon(Sprite image, float seconds, bool alarm = false)
    {
        if (image == null)
        {
            HideIcon();
            return;
        }

        if (_emoji != null)
        {
            _emoji.sprite = image;
            // Лицо уже несёт свой цвет — тонировка сделала бы из него пятно.
            _emoji.color = Color.white;
            FitSpriteInside(_emoji, IconBoxUnitsWide, IconBoxUnitsTall);
        }

        // Лицо в пузыре само не кричит «опасность» — вот здесь угловой значок и
        // нужен: видно И кого она встретила, И что это тревога.
        SetAlarmBadge(alarm, null);
        _holdUntil = seconds > 0f ? Time.time + seconds : -1f;
        SetVisible(true);
    }

    public void HideIcon()
    {
        _holdUntil = -1f;
        SetVisible(false);
    }

    // What is on screen right now — the director restores the conversation icon
    // after an interrupting alarm line has faded.
    public bool IsShowing => _shown;

    private float _holdUntil = -1f;

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
        FitSpriteInside(_popRenderer, PopUnitsWide, PopUnitsTall);
        _pop.gameObject.SetActive(true);
        _popTimer = 0f;
    }

    // §107.5: пометить содержимое пузыря как тревожное. Маленький треугольник
    // в углу — НЕ вторая картинка: если в пузыре уже сам треугольник, значок не
    // показывается вовсе, иначе получались два жёлтых треугольника, большой и
    // маленький, об одном и том же.
    private void SetAlarmBadge(bool on, string contentIcon)
    {
        if (_badge == null)
        {
            return;
        }

        var wanted = on && contentIcon != AlarmBadgeIcon;
        if (wanted && _badge.sprite == null)
        {
            _badge.sprite = ResolveEmoji(AlarmBadgeIcon);
            FitSpriteInside(_badge, BadgeUnitsWide, BadgeUnitsTall);
        }

        _badgeWanted = wanted;
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

        // Position at the head + fixed lift; billboard to the camera. This
        // component runs after ordinary LateUpdate users: RomancePairView
        // authors the male root there, so calculating the bubble first left
        // his icon rotated by the pair's final root correction for one frame
        // (and continuously while that correction was changing).
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

        // §67.10: a timed line retires itself — the director is free to leave
        // the bubble alone once it has handed one over.
        if (_holdUntil > 0f && Time.time >= _holdUntil)
        {
            _holdUntil = -1f;
            SetVisible(false);
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

        // Значок едет тем же разворотом и живёт ровно столько же, сколько
        // пузырь: он его метка, а не самостоятельная картинка.
        if (_badge != null)
        {
            _badge.enabled = s > 0.01f && _badgeWanted && _badge.sprite != null;
            _badge.transform.localScale = Vector3.one * (_badgeBaseScale * s);
            _badge.transform.localPosition = new Vector3(
                _badgeLocalPosition.x * s,
                _badgeLocalPosition.y * s,
                _badgeLocalPosition.z);
        }
    }

    // ---- sprite sizing (localScale that renders a sprite at a target world height) ----

    private float _bubbleBaseScale = 1f;
    private float _emojiBaseScale = 1f;
    private float _popBaseScale = 1f;
    private float _badgeBaseScale = 1f;
    private bool _badgeWanted;
    private Vector3 _emojiLocalPosition = new(0f, 0.24f, -0.01f);
    private Vector3 _badgeLocalPosition = new(0.16f, 0.34f, -0.02f);

    private Vector3 BubbleBodyCenterLocal()
    {
        return BodyPointLocal(BubbleBodyCenterFrac, _emojiLocalPosition);
    }

    // Точка внутри спрайта пузыря в его локальных координатах: хвост и обводка
    // делают геометрический центр картинки неверным местом и для эмодзи, и для
    // углового значка.
    private Vector3 BodyPointLocal(Vector2 frac, Vector3 fallback)
    {
        var sprite = _bubble != null ? _bubble.sprite : null;
        if (sprite == null || sprite.rect.width <= 0f || sprite.rect.height <= 0f ||
            sprite.pixelsPerUnit <= 0.0001f)
        {
            return fallback;
        }

        var centerPx = new Vector2(frac.x * sprite.rect.width, frac.y * sprite.rect.height);
        var local = (centerPx - sprite.pivot) / sprite.pixelsPerUnit * _bubbleBaseScale;
        return new Vector3(local.x, local.y, fallback.z);
    }

    private void FitSpriteInside(SpriteRenderer sr, float targetWide, float targetTall)
    {
        var k = 1f;
        if (sr.sprite != null)
        {
            var size = sr.sprite.bounds.size;
            if (size.x > 0.0001f && size.y > 0.0001f)
            {
                k = Mathf.Min(targetWide / size.x, targetTall / size.y);
            }
        }

        if (sr == _emoji)
        {
            _emojiBaseScale = k;
        }
        else if (sr == _popRenderer)
        {
            _popBaseScale = k;
        }
        else if (sr == _badge)
        {
            _badgeBaseScale = k;
        }

        sr.transform.localScale = Vector3.one * k;
    }

    private Sprite ResolveBubble()
    {
        var s = ResolveSprite("speech_bubble", "HexLive/UI/speech_bubble",
            new Vector2(0.5f, 0.16f), 820f, false);
        // Scale the (variable-size) bubble to a fixed on-screen height.
        _bubbleBaseScale = s != null && s.bounds.size.y > 0.0001f
            ? BubbleUnitsTall / s.bounds.size.y
            : 1f;
        return s;
    }

    private static Sprite ResolveEmoji(string name)
    {
        var sprite = ResolveSprite("emoji:" + name, EmojiDir + name, new Vector2(0.5f, 0.5f), 100f, true);
        if (sprite == null && name != SpeechCatalog.FallbackIcon)
        {
            sprite = ResolveSprite("emoji:" + SpeechCatalog.FallbackIcon,
                EmojiDir + SpeechCatalog.FallbackIcon, new Vector2(0.5f, 0.5f), 100f, true);
        }

        return sprite;
    }

    // Load a texture from Resources and wrap it in a runtime Sprite (cached).
    // We build the sprite from the Texture2D rather than HexLive.UnityPresentation.Content.AtomicResources.Load<Sprite>
    // so the PNG needs no Sprite importer settings.
    private static Sprite ResolveSprite(
        string cacheKey,
        string resourcePath,
        Vector2 pivot,
        float ppu,
        bool trimTransparent)
    {
        if (_spriteCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var tex = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>(resourcePath);
        Sprite sprite = null;
        if (tex != null)
        {
            var rect = new Rect(0f, 0f, tex.width, tex.height);
            if (trimTransparent && TryVisiblePixelRect(tex, out var visible))
            {
                rect = visible;
                pivot = new Vector2(0.5f, 0.5f);
            }

            sprite = Sprite.Create(tex, rect, pivot, ppu);
        }
        else
        {
            Debug.LogWarning($"[NpcSpeechBubble] Resources/{resourcePath} not found.");
        }

        _spriteCache[cacheKey] = sprite;
        return sprite;
    }

    private static bool TryVisiblePixelRect(Texture2D tex, out Rect rect)
    {
        rect = default;
        Color32[] pixels;
        try
        {
            pixels = tex.GetPixels32();
        }
        catch (UnityException)
        {
            return false;
        }

        var minX = tex.width;
        var minY = tex.height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < tex.height; y++)
        {
            var row = y * tex.width;
            for (var x = 0; x < tex.width; x++)
            {
                if (pixels[row + x].a <= 3)
                {
                    continue;
                }

                minX = Mathf.Min(minX, x);
                minY = Mathf.Min(minY, y);
                maxX = Mathf.Max(maxX, x);
                maxY = Mathf.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return false;
        }

        const int pad = 2;
        minX = Mathf.Max(0, minX - pad);
        minY = Mathf.Max(0, minY - pad);
        maxX = Mathf.Min(tex.width - 1, maxX + pad);
        maxY = Mathf.Min(tex.height - 1, maxY + pad);
        rect = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return rect.width > 0f && rect.height > 0f;
    }
}

}
