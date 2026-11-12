using HexLive.Simulation.Agents;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// Bug #733 / §67.16: the character's race — her authored background
    /// (<see cref="NpcSpeechOrigin"/>, already on the snapshot and the wire) —
    /// as a small badge right after the name. Island → «Хексы», Raider (the
    /// hexkufa-speaking wild people) → «Куфы», Modern (portal residents) →
    /// «Горожане». The pictures are Player Resources like the other identity
    /// icons, so the card needs no content bundle to draw them.
    /// </summary>
    public sealed partial class CharacterPanel
    {
        private VisualElement _raceBadge;
        private NpcSpeechOrigin? _raceBadgeOrigin;
        private Language _raceBadgeLanguage;

        /// <summary>The term slug of a race: <c>race.&lt;slug&gt;.name</c> / <c>.desc</c>.</summary>
        internal static string RaceSlug(NpcSpeechOrigin origin) => origin switch
        {
            NpcSpeechOrigin.Modern => "modern",
            NpcSpeechOrigin.Raider => "raider",
            _ => "island",
        };

        internal static string RaceIconResource(NpcSpeechOrigin origin) => origin switch
        {
            NpcSpeechOrigin.Modern => "HexLive/UI/RaceModernIcon",
            NpcSpeechOrigin.Raider => "HexLive/UI/RaceRaiderIcon",
            _ => "HexLive/UI/RaceIslandIcon",
        };

        private VisualElement BuildRaceBadge()
        {
            _raceBadge = new VisualElement();
            _raceBadge.style.width = 22f;
            _raceBadge.style.height = 22f;
            _raceBadge.style.flexShrink = 0f;
            _raceBadge.style.marginLeft = 6f;
            _raceBadge.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            _raceBadge.style.display = DisplayStyle.None;
            // Position, not Ignore: the badge is the one place its tooltip is read.
            _raceBadge.pickingMode = PickingMode.Position;
            return _raceBadge;
        }

        private void RefreshRaceBadge(NpcSpeechOrigin origin)
        {
            if (_raceBadge == null) return;
            if (_raceBadgeOrigin == origin && _raceBadgeLanguage == Loc.Current) return;
            _raceBadgeOrigin = origin;
            _raceBadgeLanguage = Loc.Current;

            // A missing picture hides the badge rather than showing a glyph in its place.
            var texture = Resources.Load<Texture2D>(RaceIconResource(origin));
            if (texture != null)
            {
                _raceBadge.style.backgroundImage = new StyleBackground(texture);
                _raceBadge.style.display = DisplayStyle.Flex;
            }
            else
            {
                _raceBadge.style.display = DisplayStyle.None;
            }

            var slug = RaceSlug(origin);
            _raceBadge.tooltip = string.Format(Loc.Get("race.tooltip"), Loc.Get($"race.{slug}.name")) +
                                 "\n" + Loc.Get($"race.{slug}.desc");
        }

        /// <summary>The HTML card's race: its slug for the terms and the page asset of its picture.</summary>
        private JObject HtmlRace(NpcSpeechOrigin origin)
        {
            var key = "race:" + RaceSlug(origin);
            if (!HexLive.UnityPresentation.Platform.HtmlUi.HasAsset(key))
            {
                var texture = Resources.Load<Texture2D>(RaceIconResource(origin));
                if (texture == null) return new JObject { ["id"] = RaceSlug(origin), ["icon"] = null };
                HexLive.UnityPresentation.Platform.HtmlUi.Asset(key,
                    TextureDataUrl(texture, new Rect(0f, 0f, texture.width, texture.height)));
            }

            return new JObject { ["id"] = RaceSlug(origin), ["icon"] = key };
        }
    }
}
