using System.Collections.Generic;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: in the web client the always-visible part of this panel — the
    /// colony list on the right and the selected colonist's card with its pills
    /// — is drawn by the page (ui/screens/roster.js, character.js). The UI
    /// Toolkit card keeps running underneath, hidden: its refresh is what fills
    /// the fields read here (control, pace, journal badge, effects), its
    /// geometry still publishes the camera's coverage and the pointer gate,
    /// and the inventory, health and journal windows stay UI Toolkit until they
    /// move too.
    /// </summary>
    public sealed partial class CharacterPanel
    {
        private const string HtmlRoster = "roster";
        private const string HtmlCharacter = "character";
        private bool _html;
        private int _journalUnread;
        private string _lastRosterJson, _lastCharacterJson;
        private Rect? _portraitViewport;
        private bool _portraitOnScreen;
        private float _nextHtmlAt;
        private bool _htmlDirty;
        private float _htmlCoverage;

        private void UpdateHtml()
        {
            if (!HtmlUi.Active)
            {
                return;
            }

            if (!_html)
            {
                _html = true;
                HtmlUi.Handle(HtmlRoster, OnHtmlCommand);
                HtmlUi.Handle(HtmlCharacter, OnHtmlCommand);
                HtmlUi.Handle(HtmlInventory, OnHtmlInventoryCommand);
                HtmlUi.Handle(HtmlHealth, OnHtmlHealthCommand);
                HtmlUi.Handle(HtmlJournal, OnHtmlJournalCommand);
            }

            HideUitkUnderHtml();
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null || LoadingScreen.IsActive)
            {
                HideHtml();
                return;
            }

            // Ten models a second is plenty for bars and rings; a click answers at once.
            if (!_htmlDirty && Time.unscaledTime < _nextHtmlAt) return;
            _htmlDirty = false;
            _nextHtmlAt = Time.unscaledTime + 0.1f;

            Push(HtmlRoster, RosterModel(snapshot), ref _lastRosterJson);
            var character = CharacterModel(snapshot);
            Push(HtmlCharacter, character, ref _lastCharacterJson);
            UpdateHtmlInventory(snapshot);
            UpdateHtmlHealth(snapshot);
            UpdateHtmlJournal(snapshot);
            UpdateHtmlDoll();

            // The portrait camera paints the screen only while the hole exists;
            // the backpack covers the card (§174.3), so its hole is gone then.
            var hole = (string)character["mode"] == "single" && !_collapsed && _portraitViewport.HasValue &&
                       !_inventoryOpen && !_healthOpen && !_journalOpen;
            if (_portraitStage != null && hole != _portraitOnScreen)
            {
                _portraitOnScreen = hole;
                if (hole) _portraitStage.ShowOnScreen(_portraitViewport.Value);
                else _portraitStage.ShowInTexture();
            }
        }

        // §168.11: the page draws all of this. visibility:Hidden kept the UI
        // Toolkit card in layout and in the panel's repaint (~4–7 ms a frame
        // in the browser with the other documents); display:None takes it out.
        // Selection and RuntimeWindow.Show switch these back to Flex, so the
        // gate runs every frame. The elements stay in the tree — their text
        // and state still feed the HTML models — and the windows stay
        // registered as open (Esc, BlocksWorldInput): only display changes.
        private void HideUitkUnderHtml()
        {
            // The whole document goes: its hidden subtree is rewritten every
            // frame (Refresh feeds the HTML models from it) and kept the panel
            // repainting. The balance and the language switch are the page's
            // HUD and game menu (§174.7), voice publishes its own screens.
            Gone(_root);
            Gone(_stage);
            Gone(_roster);
            Gone(_expandTab);
            Gone(_inventoryWindow);
            Gone(_inventoryModal?.Backdrop);
            Gone(_healthWindow);
            Gone(_healthModal?.Backdrop);
            Gone(_journalWindow);
            Gone(_journalModal?.Backdrop);
        }

        private static void Gone(VisualElement element)
        {
            if (element == null) return;
            var display = element.style.display;
            if (display.keyword == StyleKeyword.Undefined && display.value == DisplayStyle.None) return;
            element.style.display = DisplayStyle.None;
        }

        private static void Push(string screen, JObject model, ref string last)
        {
            var json = model.ToString(Newtonsoft.Json.Formatting.None);
            if (json == last) return;
            last = json;
            HtmlUi.Show(screen, model);
        }

        private void HideHtml()
        {
            if (_lastRosterJson == null && _lastCharacterJson == null && _lastInventoryJson == null &&
                _lastHealthJson == null && _lastJournalJson == null) return;
            HtmlUi.Hide(HtmlRoster);
            HtmlUi.Hide(HtmlCharacter);
            HtmlUi.Hide(HtmlInventory);
            HtmlUi.Hide(HtmlHealth);
            HtmlUi.Hide(HtmlJournal);
            _lastRosterJson = _lastCharacterJson = _lastInventoryJson = _lastHealthJson = _lastJournalJson = null;
            _journalModel = null;
            SetDollOnScreen(false);
            _portraitStage?.ShowInTexture();
            _portraitOnScreen = false;
        }

        private void OnDestroy()
        {
            if (!_html) return;
            HtmlUi.Unhandle(HtmlRoster, OnHtmlCommand);
            HtmlUi.Unhandle(HtmlCharacter, OnHtmlCommand);
            HtmlUi.Unhandle(HtmlInventory, OnHtmlInventoryCommand);
            HtmlUi.Unhandle(HtmlHealth, OnHtmlHealthCommand);
            HtmlUi.Unhandle(HtmlJournal, OnHtmlJournalCommand);
            HideHtml();
        }

        // ---- faces ------------------------------------------------------------

        /// <summary>A roster/relation face as a page asset; null until the cache has it.</summary>
        private string FaceKey(int npcId)
        {
            if (_portraitCache == null) return null;
            if (!_portraitCache.TryGet(npcId, out var texture) || texture == null)
            {
                _portraitCache.RequestNow(npcId);
                return null;
            }

            // A re-rendered face (new clothes, a wound) is a new picture.
            var key = $"face:{npcId}:{texture.GetInstanceID()}:{texture.updateCount}";
            if (!HtmlUi.HasAsset(key))
                HtmlUi.Asset(key, "data:image/jpeg;base64," + System.Convert.ToBase64String(texture.EncodeToJPG(85)));
            return key;
        }

        private static string Hex(Color colour) => "#" + ColorUtility.ToHtmlStringRGB(colour);

        private static string StatusTone(string key) => key switch
        {
            "state.dead" or "state.dying" or "state.coma" => "danger",
            "state.fainted" or "state.playdead" => "warn",
            "state.resting" => "idle",
            _ => "ok",
        };

        // ---- roster -------------------------------------------------------------

        private JObject RosterModel(WorldSnapshot snapshot)
        {
            var clan = new JArray();
            var outsiders = new JArray();
            var total = 0;
            var selective = false;
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Health > 0f && PlayerCampView.IsMine(_runner, snapshot, npc) &&
                    !(_runner != null && _runner.CanControlNpc(npc.Id)))
                {
                    selective = true;
                    break;
                }
            }

            var ordered = new List<NpcSnapshot>(snapshot.Npcs);
            ordered.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            foreach (var npc in ordered)
            {
                if (npc.Health <= 0f) continue;
                var owned = _runner != null && _runner.CanControlNpc(npc.Id);
                var mine = PlayerCampView.IsMine(_runner, snapshot, npc);
                if (mine) total++;
                // Same visibility rule as the UI Toolkit roster.
                if (!owned && (_worldRenderer == null || !_worldRenderer.IsNpcPickable(npc.Id.Value, npc.Tile, false)))
                    continue;
                ResolveStatus(npc, out var status, out _);
                var hp = Mathf.Clamp01(npc.DisplayHealth);
                var card = new JObject
                {
                    ["id"] = npc.Id.Value,
                    ["name"] = Loc.NpcName(npc.DisplayName),
                    ["face"] = FaceKey(npc.Id.Value),
                    ["health"] = Mathf.Round(hp * 100f) / 100f,
                    ["healthColor"] = Hex(CharacterDollStage.StatusColor(hp, false)),
                    ["status"] = status.Substring("state.".Length),
                    ["statusTone"] = StatusTone(status),
                    ["fighting"] = npc.IsFighting,
                    ["mine"] = selective && owned,
                    ["selected"] = NpcSelection.Contains(npc.Id.Value),
                };
                (mine ? clan : outsiders).Add(card);
            }

            return new JObject
            {
                ["clan"] = new JObject { ["count"] = clan.Count, ["total"] = total },
                ["cards"] = clan,
                ["outsiders"] = outsiders,
            };
        }

        // ---- character card -----------------------------------------------------

        private JObject CharacterModel(WorldSnapshot snapshot)
        {
            if (!NpcSelection.HasSelection) return new JObject { ["mode"] = "none" };
            if (NpcSelection.Count > 1)
            {
                var group = GroupModel(snapshot);
                if (group != null) return new JObject { ["mode"] = "group", ["group"] = group };
            }

            var npc = FindNpc(snapshot, NpcSelection.PrimaryId, out var isDead);
            if (npc == null) return new JObject { ["mode"] = "none" };
            return new JObject { ["mode"] = "single", ["collapsed"] = _collapsed, ["npc"] = NpcModel(snapshot, npc, isDead) };
        }

        private JObject NpcModel(WorldSnapshot snapshot, NpcSnapshot npc, bool isDead)
        {
            ResolveStatus(npc, isDead, out var status, out _);
            var hp = isDead ? 0f : Mathf.Clamp01(npc.DisplayHealth);
            var agentControlled = _runner != null && _runner.TryGetAgentState(npc.Id, out var agent) && agent.Attached;
            var call = ServerVoiceActive || _callPreparing || PhoneControls(npc.Id.Value);
            var uv = npc.EffectiveUv;

            var needs = new JArray();
            foreach (var config in Needs)
            {
                var raw = Mathf.Clamp01(RawNeed(npc, config.Key));
                // Goodness, exactly as the UI Toolkit bar colours it: full = good.
                var good = config.Direct ? 1f - raw : config.Pressure ? 1f - raw : raw;
                var need = new JObject { ["id"] = config.Key.Substring("need.".Length), ["value"] = Mathf.Round(good * 100f) / 100f };
                need["impacts"] = HtmlNeedImpacts(npc, (string)need["id"]); // the row's tooltip (§48.7)
                if (config.Key == "need.blood" && npc.BloodDeficit > 0f)
                {
                    need["value"] = 0f;
                    need["deficit"] = Mathf.Round(Mathf.Clamp01(npc.BloodDeficit) * 100f) / 100f;
                }
                needs.Add(need);
                // Temperature is the bipolar cell, placed where the UI Toolkit grid has it.
                if (config.Key == "need.social")
                    needs.Add(new JObject { ["id"] = "thermal", ["value"] = 0f, ["thermal"] = Mathf.Round(Mathf.Clamp(npc.ThermalComfort, -1f, 1f) * 100f) / 100f,
                        ["impacts"] = HtmlNeedImpacts(npc, "thermal") });
            }

            var effects = new JArray();
            foreach (var e in _effectParseScratch)
            {
                if (!EffectCatalog.TryGet(e.Kind, out var def)) continue;
                var slug = def.TitleKey.Substring("effect.".Length, def.TitleKey.Length - "effect.".Length - ".title".Length);
                effects.Add(new JObject { ["slug"] = slug, ["emoji"] = def.Emoji, ["buff"] = def.Polarity != EffectPolarity.Debuff,
                    ["desc"] = string.IsNullOrEmpty(e.DetailKey) ? def.DescKey : e.DetailKey }); // the chip's tooltip
            }

            var relations = new JArray();
            // §171: her bond with the player's own voice (the server keeps it per
            // call; CharacterPanel.ServerVoice reads it) — first in the row, with
            // what the last talk changed and why.
            if (_agentRelationReady && _agentAttachedNpcId == npc.Id.Value)
            {
                relations.Add(new JObject
                {
                    ["id"] = -159, ["voice"] = true, ["name"] = _agentVoiceName, ["face"] = null,
                    ["tag"] = RelationKindKey(_agentAffinity),
                    ["affinity"] = Mathf.Round(Mathf.Clamp(_agentAffinity, -1f, 1f) * 100f) / 100f,
                    ["familiarity"] = Mathf.Round(Mathf.Clamp01(_agentFamiliarity) * 100f) / 100f,
                    ["trust"] = Mathf.Round(Mathf.Clamp01(_agentTrust) * 100f) / 100f,
                    ["impact"] = new JObject
                    {
                        ["known"] = _agentRelationHasChange,
                        ["affinity"] = Mathf.Round(Mathf.Clamp(_agentAffinityDelta, -1f, 1f) * 100f) / 100f,
                        ["familiarity"] = Mathf.Round(Mathf.Clamp(_agentFamiliarityDelta, -1f, 1f) * 100f) / 100f,
                        ["trust"] = Mathf.Round(Mathf.Clamp(_agentTrustDelta, -1f, 1f) * 100f) / 100f,
                        ["reason"] = string.IsNullOrWhiteSpace(_agentRelationReason) ? null : _agentRelationReason,
                    },
                });
            }
            var rels = new List<RelationshipSnapshot>(npc.RelationshipDetails);
            rels.Sort((a, b) => b.LastInteractionTick.CompareTo(a.LastInteractionTick));
            foreach (var r in rels)
            {
                relations.Add(new JObject
                {
                    ["id"] = r.OtherId,
                    ["name"] = Loc.NpcName(r.OtherName),
                    ["face"] = r.OtherId >= 0 ? FaceKey(r.OtherId) : null,
                    ["tag"] = RelationKindKey(r.Affinity),
                    ["affinity"] = Mathf.Round(Mathf.Clamp(r.Affinity, -1f, 1f) * 100f) / 100f,
                    ["familiarity"] = Mathf.Round(Mathf.Clamp01(r.Familiarity) * 100f) / 100f,
                    ["trust"] = Mathf.Round(Mathf.Clamp01(r.Trust) * 100f) / 100f,
                });
            }

            JObject directive = null;
            if (!isDead && !string.IsNullOrEmpty(npc.DirectiveKind))
            {
                string from = null;
                if (npc.DirectiveFromNpcId is { } fromId)
                    foreach (var other in snapshot.Npcs)
                        if (other.Id.Value == fromId) { from = Loc.NpcName(other.DisplayName); break; }
                directive = new JObject { ["kind"] = npc.DirectiveKind, ["from"] = from };
            }

            return new JObject
            {
                ["id"] = npc.Id.Value,
                ["name"] = string.IsNullOrEmpty(npc.DisplayName) ? $"NPC #{npc.Id.Value}" : Loc.NpcName(npc.DisplayName),
                ["control"] = call ? "call" : _manualControlNow ? "manual" : "ai",
                ["controlLocked"] = _controlAvailable || call ? null : agentControlled ? "panel.control.agent" : "panel.control.readonly",
                ["canCall"] = ServerVoiceEligible || call,
                ["pace"] = _runByDefaultNow ? "run" : "walk",
                ["canStop"] = _controlAvailable && _manualControlNow && !call,
                ["health"] = Mathf.Round(hp * 100f) / 100f,
                ["healthColor"] = Hex(CharacterDollStage.StatusColor(hp, false)),
                ["status"] = status.Substring("state.".Length),
                ["statusTone"] = StatusTone(status),
                ["uv"] = uv < 0.02f ? "none" : uv < 0.3f ? "low" : uv < 0.6f ? "mid" : "high",
                ["shade"] = npc.IsShaded && uv >= 0.02f,
                ["badge"] = npc.IsFighting ? "fighting" : npc.IsStarving ? "starving" : null,
                ["journalUnread"] = _journalUnread,
                // §40.6 r14: washing runs under the Bathe goal; the card says what she is doing.
                ["goal"] = npc.CurrentInteraction == "WashClothes" && npc.ExecutionStatus == "InProgress" ? "WashClothes" : npc.CurrentGoal,
                ["dream"] = !isDead && !string.IsNullOrEmpty(npc.CurrentDream) && npc.CurrentDream != "None" ? npc.CurrentDream : null,
                ["directive"] = directive,
                ["effects"] = effects,
                ["needs"] = needs,
                ["nature"] = SheetModel(AttributeRows, npc.Attributes),
                ["skills"] = SheetModel(SkillRows, npc.Skills),
                ["traits"] = new JArray(npc.Traits),
                ["relations"] = relations,
            };
        }

        private static JArray SheetModel(SheetConfig[] rows, List<string> values)
        {
            var result = new JArray();
            foreach (var row in rows)
            {
                if (row.Percent) continue; // compassion is a share, not a 0–10 rating
                var value = 0f;
                foreach (var line in values)
                {
                    var tab = line.IndexOf('\t');
                    if (tab == row.Id.Length && string.CompareOrdinal(line, 0, row.Id, 0, tab) == 0)
                    {
                        float.TryParse(line.Substring(tab + 1), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
                        break;
                    }
                }
                result.Add(new JObject { ["id"] = row.Key.Substring(row.Key.IndexOf('.') + 1), ["value"] = Mathf.RoundToInt(Mathf.Max(0f, value) * 10f) });
            }
            return result;
        }

        private JObject GroupModel(WorldSnapshot snapshot)
        {
            var faces = new JArray();
            int count = 0, manual = 0, blocked = 0, fighting = 0;
            foreach (var npc in snapshot.Npcs)
            {
                if (!NpcSelection.Contains(npc.Id.Value) || _runner == null || !_runner.CanControlNpc(npc.Id) || npc.Health <= 0f) continue;
                count++;
                if (npc.IsManualControl) manual++;
                if (npc.IsDying || npc.IsUnconscious || npc.IsFainted) blocked++;
                if (npc.IsFighting) fighting++;
                faces.Add(new JObject { ["id"] = npc.Id.Value, ["name"] = Loc.NpcName(npc.DisplayName), ["face"] = FaceKey(npc.Id.Value) });
            }
            if (count <= 1) return null;
            return new JObject
            {
                ["count"] = count, ["manual"] = manual, ["total"] = count, ["blocked"] = blocked, ["fighting"] = fighting,
                ["mode"] = manual == 0 ? "ai" : manual == count ? "manual" : "mixed",
                ["faces"] = faces,
                ["feedback"] = GroupOrderFeedback.IsFresh ? GroupOrderFeedback.LocalizedSummary() : null,
            };
        }

        // ---- commands -----------------------------------------------------------

        private void OnHtmlCommand(string command, JObject args)
        {
            _htmlDirty = true;
            switch (command)
            {
                case "select":
                    var id = (int?)args["id"] ?? -1;
                    if (id < 0) return;
                    if ((bool?)args["add"] == true && _runner != null && _runner.CanControlNpc(new HexLive.Simulation.Common.EntityId(id)))
                        NpcSelection.Toggle(id, requestFrame: false);
                    else NpcSelection.Activate(id);
                    return;
                case "selectClan": SelectWholeClan(); return;
                case "control":
                    var mode = (string)args["mode"];
                    if (mode == "call") SelectCharacterControlMode(ServerVoiceActive || _callPreparing ? CharacterControlMode.Ai : CharacterControlMode.Call);
                    else SelectCharacterControlMode(mode == "manual" ? CharacterControlMode.Manual : CharacterControlMode.Ai);
                    return;
                case "pace":
                    if (((string)args["value"] == "run") != _runByDefaultNow) TogglePace();
                    return;
                case "stop": StopSingle(); return;
                case "open":
                    switch ((string)args["window"])
                    {
                        case "inventory": ToggleInventory(); break;
                        case "journal": ToggleJournal(); break;
                        case "health": ToggleHealth(); break;
                    }
                    return;
                case "collapse": _collapsed = true; return;
                case "expand": _collapsed = false; return;
                case "groupMode": ToggleGroupManual(); return;
                case "groupStop": StopGroup(); return;
                case "coverage":
                    // How much of the screen height the card covers from the bottom (0–1).
                    _htmlCoverage = Mathf.Clamp01((float?)args["value"] ?? 0f);
                    return;
                case "portraitRect":
                    var vw = (float?)args["vw"] ?? 0; var vh = (float?)args["vh"] ?? 0;
                    var w = (float?)args["w"] ?? 0; var h = (float?)args["h"] ?? 0;
                    if (vw < 1 || vh < 1 || w < 1 || h < 1) return;
                    var x = (float?)args["x"] ?? 0; var y = (float?)args["y"] ?? 0;
                    _portraitViewport = new Rect(x / vw, 1 - (y + h) / vh, w / vw, h / vh); // page y grows down
                    _portraitOnScreen = false; // re-apply on the next update
                    return;
            }
        }
    }
}
