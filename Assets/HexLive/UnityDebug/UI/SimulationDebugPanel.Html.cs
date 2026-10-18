using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;
using NpcSelection = HexLive.UnityPresentation.Input.NpcSelection;

namespace HexLive.UnityDebug.UI
{
    /// <summary>
    /// §174.7: the Tab overlay as the page's «simDebug». Tab still toggles it
    /// here; the page gets the same values the UI Toolkit panel shows — the
    /// simulation's own names for goals, statuses and events, and I2 only for
    /// the captions. Rebuilt four times a second at most and only on a new tick
    /// or a change of the clock: the reserved/occupied counts walk every
    /// junction, which is fine for a developer overlay, not for every frame.
    /// </summary>
    public sealed partial class SimulationDebugPanel
    {
        private const string HtmlScreen = "simDebug";
        private bool _htmlHandled, _htmlShown;
        private float _htmlNext;
        private (int, bool, float, int, bool)? _htmlKey;

        private void UpdateHtml()
        {
            if (!_htmlHandled) { HtmlUi.Handle(HtmlScreen, OnHtmlCommand); _htmlHandled = true; }
            if (!_visible)
            {
                if (_htmlShown) { HtmlUi.Hide(HtmlScreen); _htmlShown = false; _htmlKey = null; }
                return;
            }

            if (Time.unscaledTime < _htmlNext) return;
            _htmlNext = Time.unscaledTime + 0.25f;
            var tick = _runner != null ? _runner.CurrentTick : 0;
            var key = (tick, _runner != null && _runner.IsPaused, _runner != null ? _runner.SpeedMultiplier : 0f,
                NpcSelection.HasSelection ? NpcSelection.SelectedId : -1, _runner != null && _runner.IsReady);
            if (_htmlShown && _htmlKey == key) return;
            _htmlKey = key;
            HtmlUi.Show(HtmlScreen, HtmlModel());
            _htmlShown = true;
        }

        private void OnDestroy()
        {
            if (_htmlHandled) HtmlUi.Unhandle(HtmlScreen, OnHtmlCommand);
        }

        private void OnHtmlCommand(string command, JObject args)
        {
            switch (command)
            {
                case "close":
                    _visible = false;
                    WorldSnapshotExporter.IncludeDebugDetails = false;
                    break;
                case "pause":
                    OnTogglePause();
                    break;
                case "step":
                    OnStepTick();
                    break;
                case "speed":
                    OnSetSpeed((float?)args["value"] ?? 1f);
                    break;
            }
            _htmlNext = 0f; // the pressed state answers at once
            _htmlKey = null;
        }

        private JObject HtmlModel()
        {
            var model = new JObject
            {
                ["ready"] = _runner != null && _runner.IsReady,
                ["tick"] = _runner != null ? _runner.CurrentTick : 0,
                ["paused"] = _runner != null && _runner.IsPaused,
                ["speed"] = _runner != null ? Mathf.Round(_runner.SpeedMultiplier * 100f) / 100f : 0f,
                ["remote"] = _runner != null && _runner.Link.IsRemote,
            };
            var snapshot = _runner != null ? _runner.CreateSnapshot() : null;
            if (snapshot == null || snapshot.Npcs.Count == 0)
            {
                model["trace"] = new JObject { ["recorder"] = false, ["rows"] = new JArray() };
                return model;
            }

            var npc = PickNpc(snapshot);
            model["npc"] = new JObject
            {
                ["id"] = npc.Id.Value,
                // §74: DisplayName is a name ID; the player sees the localized term.
                ["name"] = string.IsNullOrEmpty(npc.DisplayName) ? "NPC" : Loc.NpcName(npc.DisplayName),
                ["tile"] = new JArray(npc.Tile.Q, npc.Tile.R),
                ["goal"] = npc.CurrentGoal,
                ["interaction"] = npc.CurrentInteraction,
                ["inventory"] = new JArray(npc.InventoryItems),
                ["capacity"] = npc.InventoryCapacity,
                ["worn"] = new JArray(npc.WornItems),
                ["body"] = npc.WorstBodyPart,
                ["starving"] = npc.IsStarving,
                ["fighting"] = npc.IsFighting,
                ["needs"] = new JObject
                {
                    ["health"] = npc.Health, ["hunger"] = npc.Hunger, ["thirst"] = npc.Thirst, ["energy"] = npc.Energy,
                    ["comfort"] = npc.Comfort, ["social"] = npc.Social, ["thermal"] = npc.ThermalDiscomfort,
                },
                ["relations"] = new JArray(npc.Relationships),
            };
            model["sim"] = new JObject
            {
                ["plan"] = npc.PlanStatus,
                ["goalLock"] = npc.GoalLockEndTick,
                ["cooldowns"] = new JArray(npc.CooldownGoals),
                ["known"] = npc.KnownObjectCount,
                ["target"] = npc.TargetTile is { } target ? new JArray(target.Q, target.R) : null,
                ["movement"] = npc.MovementStatus,
                ["execution"] = npc.ExecutionStatus,
                ["path"] = npc.Path.Count == 0 ? null : FormatPath(npc.Path),
                ["clock"] = snapshot.Clock,
                ["phase"] = snapshot.DayPhase,
                ["uv"] = snapshot.UvIndex,
                ["raining"] = snapshot.IsRaining,
                ["temperature"] = snapshot.Temperature,
                ["reserved"] = CountReserved(snapshot),
                ["occupied"] = CountOccupied(snapshot),
            };
            var scores = new JArray();
            foreach (var score in npc.GoalScores)
                scores.Add(new JObject { ["goal"] = score.Goal, ["score"] = score.FinalScore });
            model["scores"] = scores;
            model["trace"] = HtmlTrace(snapshot, npc);
            return model;
        }

        // UpdateTrace's choice: her own flight-recorder ring (§30.14) where the
        // engine is in this process, else the colony ring filtered to her id.
        private JObject HtmlTrace(WorldSnapshot snapshot, NpcSnapshot npc)
        {
            var rows = new List<(int Tick, string Type, string Message)>();
            var recorder = _runner != null ? _runner.Engine?.World?.FlightRecorder : null;
            var fromRecorder = false;
            if (recorder != null)
            {
                foreach (var entry in recorder.Tail(npc.Id.Value, 30)) rows.Add((entry.Tick, entry.Type, entry.Message));
                fromRecorder = rows.Count > 0;
            }
            if (rows.Count == 0)
            {
                foreach (var trace in snapshot.TraceEvents)
                    if (trace.EntityId == npc.Id.Value) rows.Add((trace.Tick, trace.Type, trace.Message));
                if (rows.Count > 30) rows.RemoveRange(0, rows.Count - 30);
            }
            var json = new JArray();
            foreach (var row in rows) json.Add(new JArray(row.Tick, row.Type, row.Message));
            return new JObject { ["recorder"] = fromRecorder, ["rows"] = json };
        }
    }
}
