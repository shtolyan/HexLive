using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the health window (§57) as the page's «health» screen. As with the
    /// backpack, the UI Toolkit window keeps its open state and feeds the doll
    /// stage (RefreshHealth), only hidden; the page gets the zone numbers that
    /// RefreshHealth turns into its rows, and Unity draws the doll into the hole.
    /// </summary>
    public sealed partial class CharacterPanel
    {
        private const string HtmlHealth = "health";
        private string _lastHealthJson;
        private bool _healthHtmlHidden;

        private void UpdateHtmlHealth(WorldSnapshot snapshot)
        {
            if (!_healthOpen)
            {
                if (_lastHealthJson != null) { HtmlUi.Hide(HtmlHealth); _lastHealthJson = null; }
                _healthHtmlHidden = false;
                return;
            }

            if (!_healthHtmlHidden)
            {
                _healthHtmlHidden = true;
                _healthWindow.style.visibility = Visibility.Hidden;
                if (_healthModal?.Backdrop != null) _healthModal.Backdrop.style.visibility = Visibility.Hidden;
            }

            var npc = FindNpc(snapshot, NpcSelection.PrimaryId, out _);
            if (npc == null) return;
            Push(HtmlHealth, HealthModel(npc), ref _lastHealthJson);
        }

        private JObject HealthModel(NpcSnapshot npc)
        {
            var zones = new JArray();
            foreach (var zone in CharacterDollStage.ZoneOrder)
            {
                BodyPartConditionSnapshot condition = null;
                foreach (var typed in npc.BodyPartConditions)
                {
                    if (typed.Part.ToString() == zone) { condition = typed; break; }
                }

                // Open wounds of this zone, as RefreshHealth counts them.
                var wounds = 0;
                var cut = 0f;
                var bleeding = false;
                foreach (var wound in npc.OpenWounds)
                {
                    if (wound.Part.ToString() != zone || wound.Heal01 >= 0.999f) continue;
                    wounds++;
                    cut += wound.Severity * (1f - wound.Heal01);
                    bleeding |= !wound.Stabilized && wound.Clot01 < 0.999f;
                }

                var severed = condition?.Severed ?? npc.SeveredParts.Contains(zone);
                var bandaged = !string.IsNullOrEmpty(condition?.BandageKind);
                foreach (var entry in npc.BandagedZones)
                {
                    var bar = entry.IndexOf('|');
                    if ((bar > 0 ? entry[..bar] : entry) == zone) { bandaged = true; break; }
                }

                JObject prosthetic = null;
                if (condition?.Prosthetic is { } p)
                {
                    var def = ResolveDef(p.DefinitionId);
                    prosthetic = new JObject
                    {
                        ["name"] = ItemName(def, ResolveItemInfo(p.DefinitionId, def)),
                        ["hp"] = p.Condition, ["max"] = p.MaxCondition, ["function"] = Mathf.Clamp01(p.Function),
                    };
                }

                zones.Add(new JObject
                {
                    ["id"] = zone,
                    ["hp"] = Mathf.Clamp01(condition?.Health ?? 1f),
                    ["critical"] = Mathf.Clamp01(condition?.CriticalTrauma ?? 0f),
                    ["severed"] = severed,
                    ["bleeding"] = bleeding,
                    ["bandaged"] = bandaged,
                    ["splint"] = condition?.SplintSupport ?? 0f,
                    ["armor"] = severed ? 0f : condition?.Armor ?? 0f,
                    ["wounds"] = wounds,
                    ["cut"] = Mathf.Clamp01(cut),
                    ["blunt"] = condition?.BluntDamage ?? 0f,
                    ["prosthetic"] = prosthetic,
                });
            }

            var stage = _characterDollStage;
            return new JObject
            {
                ["name"] = Loc.NpcName(npc.DisplayName),
                ["dollAvailable"] = stage == null || stage.HealthOverlayAvailable || string.IsNullOrEmpty(stage.BuildError),
                ["zones"] = zones,
            };
        }

        private void OnHtmlHealthCommand(string command, JObject args)
        {
            _htmlDirty = true;
            if (OnHtmlDollCommand(command, args)) return;
            if (command == "close") CloseHealth();
        }
    }
}
