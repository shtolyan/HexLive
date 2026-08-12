using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.LocomotionTest
{

/// <summary>
/// §71 lag recorder: while armed, captures one JSONL line per RENDERED frame —
/// sim tick, per-NPC model position/rotation/status straight from the world,
/// the interpolated view transform the player actually sees, and the animator
/// state driving the legs. Stop writes the take to Captures/ at the repo root
/// so the smoothing work can replay the exact frames the player flagged as
/// suspicious. The execution order puts LateUpdate after the renderer's
/// interpolation and every pose layer, i.e. it samples the final frame.
/// </summary>
[DefaultExecutionOrder(32000)]
public sealed class LocomotionLagRecorder : MonoBehaviour
{
    private SimulationRunnerBehaviour _runner;
    private HexWorldRenderer _renderer;
    private readonly List<string> _lines = new(4096);
    private readonly StringBuilder _sb = new(1024);
    private float _startedRealtime;
    private string _lastSavedPath = string.Empty;

    public bool IsRecording { get; private set; }

    public int FrameCount => _lines.Count;

    public float Seconds => IsRecording ? Time.realtimeSinceStartup - _startedRealtime : 0f;

    public string LastSavedPath => _lastSavedPath;

    public void Configure(SimulationRunnerBehaviour runner, HexWorldRenderer renderer)
    {
        _runner = runner;
        _renderer = renderer;
    }

    public void StartRecording(string metaJson)
    {
        _lines.Clear();
        _lines.Add(metaJson);
        _startedRealtime = Time.realtimeSinceStartup;
        IsRecording = true;
    }

    public string StopRecording()
    {
        IsRecording = false;
        if (_lines.Count <= 1)
        {
            _lines.Clear();
            return string.Empty;
        }

        var dir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(Application.dataPath, "..", "Captures"));
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(
            dir, "locomotion_lag_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jsonl");
        System.IO.File.WriteAllLines(path, _lines);
        _lines.Clear();
        _lastSavedPath = path;
        Debug.Log("[LocomotionLagRecorder] Запись сохранена: " + path);
        return path;
    }

    private void LateUpdate()
    {
        if (!IsRecording || _runner == null || _runner.Engine == null || _renderer == null)
        {
            return;
        }

        var world = _runner.Engine.World;
        var sb = _sb;
        sb.Length = 0;
        sb.Append("{\"t\":").Append(F(Time.realtimeSinceStartup - _startedRealtime))
          .Append(",\"dt\":").Append(F(Time.unscaledDeltaTime))
          .Append(",\"tick\":").Append(world.Tick)
          .Append(",\"simSpeed\":").Append(F(_runner.SpeedMultiplier))
          .Append(",\"paused\":").Append(_runner.IsPaused ? "true" : "false")
          .Append(",\"npcs\":[");

        var first = true;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!_renderer.TryGetNpcViewPosition(npc.Id.Value, out var viewPos))
            {
                continue;
            }

            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append("{\"id\":").Append(npc.Id.Value)
              .Append(",\"name\":\"").Append(npc.DisplayName).Append('"')
              .Append(",\"simX\":").Append(F(npc.Position.X))
              .Append(",\"simY\":").Append(F(npc.Position.Y))
              .Append(",\"viewX\":").Append(F(viewPos.x))
              .Append(",\"viewY\":").Append(F(viewPos.y))
              .Append(",\"viewZ\":").Append(F(viewPos.z))
              .Append(",\"rot\":").Append(F(npc.RotationDegrees))
              .Append(",\"rotWant\":").Append(F(npc.Movement.DesiredRotationDegrees))
              .Append(",\"status\":\"").Append(npc.Movement.Status).Append('"')
              .Append(",\"moving\":").Append(npc.Movement.IsMoving ? "true" : "false")
              .Append(",\"path\":").Append(npc.Movement.PathIndex)
              .Append('/').Append(npc.Movement.JunctionPath.Count)
              .Append(",\"running\":").Append(npc.Mind.IsRunning ? "true" : "false")
              .Append(",\"goal\":\"").Append(npc.Mind.CurrentGoal).Append('"');

            if (_renderer.TryGetActorView(npc.Id.Value, out var view) && view != null)
            {
                var viewYaw = view.transform.rotation.eulerAngles.y;
                sb.Append(",\"viewYaw\":").Append(F(viewYaw));
                var animator = view.GetComponentInChildren<Animator>();
                if (animator != null && animator.isActiveAndEnabled)
                {
                    AppendAnimator(sb, animator);
                }
            }

            sb.Append('}');
        }

        sb.Append("]}");
        _lines.Add(sb.ToString());
    }

    private static void AppendAnimator(StringBuilder sb, Animator animator)
    {
        sb.Append(",\"animSpeed\":").Append(F(animator.speed))
          .Append(",\"pSpeed\":").Append(F(animator.GetFloat(SpeedHash)))
          .Append(",\"pGait\":").Append(F(animator.GetFloat(GaitHash)));

        var state = animator.GetCurrentAnimatorStateInfo(0);
        sb.Append(",\"stateTime\":").Append(F(state.normalizedTime))
          .Append(",\"inTransition\":").Append(animator.IsInTransition(0) ? "true" : "false");

        // Clip names, not state hashes: the take must be readable without the
        // controller asset at hand. Allocates — recording-only, acceptable.
        var clips = animator.GetCurrentAnimatorClipInfo(0);
        sb.Append(",\"clips\":[");
        for (var i = 0; i < clips.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"clip\":\"").Append(clips[i].clip != null ? clips[i].clip.name : "?")
              .Append("\",\"w\":").Append(F(clips[i].weight)).Append('}');
        }

        sb.Append(']');
    }

    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int GaitHash = Animator.StringToHash("Gait");

    private static string F(float value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}

}
