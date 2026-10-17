using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{
/// <summary>
/// §138.7: the player's craft queue — «press Create five times, she makes five».
/// A client-side planner, NOT world state: it reads the same <see cref="CraftRecipeOption"/>
/// read model the craft window shows and issues ordinary <see cref="CraftByOutputCommand"/>s,
/// one at a time. It lives with the presentation because crafting needs manual control,
/// and manual control already ends when the player leaves (§121.5): a world-side queue
/// would stop at the same moment and cost a save/wire field for nothing.
///
/// One job per output and NPC, worked head first. A job counts the item being made
/// now: «×3» = this one plus two more. A finished item is «the recipe stopped being
/// active and no project of it is left»; an interrupted one still has its project
/// (<see cref="CraftRecipeOption.IsResume"/>) and is continued without counting.
/// </summary>
public sealed class CraftQueue
{
    /// <summary>Ticks a sent order may take to show as active before it counts as refused.</summary>
    public const int StartTimeoutTicks = 40;

    private enum Phase { Idle, Starting, Running }

    private sealed class Job
    {
        public string Output = "";
        public int Remaining;
        public Phase Phase;
        public int SentTick;
    }

    private readonly Dictionary<int, List<Job>> _jobs = new();
    private readonly Dictionary<int, (string Output, CraftBlockReason Reason)> _stopped = new();
    private readonly List<CraftRecipeOption> _scratch = new();
    private int _lastTick = int.MinValue;

    /// <summary>Items still to make (the current one included) for this output.</summary>
    public int Remaining(int npc, string output)
    {
        if (!_jobs.TryGetValue(npc, out var jobs)) return 0;
        foreach (var job in jobs) if (job.Output == output) return job.Remaining;
        return 0;
    }

    /// <summary>Why the queue of this NPC last stopped on its own, if it did.</summary>
    public bool TryGetStop(int npc, out string output, out CraftBlockReason reason)
    {
        if (_stopped.TryGetValue(npc, out var stop)) { output = stop.Output; reason = stop.Reason; return true; }
        output = ""; reason = CraftBlockReason.None;
        return false;
    }

    /// <summary>
    /// One more of <paramref name="output"/>. <paramref name="option"/> is what the window
    /// shows now: an item already in the making (started before the queue knew) counts too.
    /// </summary>
    public int Add(int npc, string output, CraftRecipeOption option)
    {
        if (string.IsNullOrEmpty(output)) return 0;
        _stopped.Remove(npc);
        if (!_jobs.TryGetValue(npc, out var jobs)) _jobs[npc] = jobs = new List<Job>();
        foreach (var job in jobs)
        {
            if (job.Output != output) continue;
            return ++job.Remaining;
        }

        var running = option != null && option.IsActive;
        var added = new Job { Output = output, Remaining = running ? 2 : 1, Phase = running ? Phase.Running : Phase.Idle };
        jobs.Add(added);
        return added.Remaining;
    }

    /// <summary>Drop the queued items of this output; the one in the making is finished.</summary>
    public void Cancel(int npc, string output)
    {
        if (!_jobs.TryGetValue(npc, out var jobs)) return;
        for (var i = jobs.Count - 1; i >= 0; i--)
        {
            if (jobs[i].Output != output) continue;
            if (jobs[i].Phase == Phase.Running && jobs[i].Remaining > 1) jobs[i].Remaining = 1;
            else if (jobs[i].Phase != Phase.Running) jobs.RemoveAt(i);
        }
        if (jobs.Count == 0) _jobs.Remove(npc);
    }

    public void Clear(int npc)
    {
        _jobs.Remove(npc);
        _stopped.Remove(npc);
    }

    /// <summary>
    /// Advance every queue once per simulation tick. <paramref name="eligible"/> is «may the
    /// player still order this NPC to craft» (alive, assigned, manual); <paramref name="options"/>
    /// fills the read model; <paramref name="send"/> enqueues a command.
    /// </summary>
    public void Tick(int tick, Func<int, bool> eligible, Func<int, List<CraftRecipeOption>, bool> options,
        Action<CraftByOutputCommand> send)
    {
        if (tick == _lastTick || _jobs.Count == 0) { _lastTick = tick; return; }
        _lastTick = tick;
        var npcs = new List<int>(_jobs.Keys);
        foreach (var npc in npcs)
        {
            var jobs = _jobs[npc];
            if (!eligible(npc))
            {
                // Manual control ended (the lease, death, a hand-back): the queue goes with it.
                _jobs.Remove(npc);
                continue;
            }

            if (jobs.Count == 0) { _jobs.Remove(npc); continue; }
            _scratch.Clear();
            if (!options(npc, _scratch)) continue; // no read model this tick (remote lag)
            var job = jobs[0];
            var option = _scratch.Find(o => o.OutputDefinitionId == job.Output);
            if (option == null) { Stop(npc, jobs, job, CraftBlockReason.InvalidRecipe); continue; }
            Advance(npc, jobs, job, option, tick, send);
        }
    }

    private void Advance(int npc, List<Job> jobs, Job job, CraftRecipeOption option, int tick, Action<CraftByOutputCommand> send)
    {
        switch (job.Phase)
        {
            case Phase.Starting:
                if (option.IsActive) { job.Phase = Phase.Running; return; }
                if (tick - job.SentTick < StartTimeoutTicks) return;
                // Never became active: the order was refused, or nothing could start.
                if (option.CanCraft) { job.Phase = Phase.Idle; break; }
                Stop(npc, jobs, job, option.BlockReason);
                return;
            case Phase.Running:
                if (option.IsActive) return;
                if (!option.IsResume) job.Remaining--; // made; an interrupted project resumes uncounted
                job.Phase = Phase.Idle;
                break;
        }

        if (job.Remaining <= 0)
        {
            jobs.RemoveAt(0);
            if (jobs.Count == 0) _jobs.Remove(npc);
            return;
        }

        if (!option.CanCraft)
        {
            // Another recipe of hers is running: wait for it instead of stopping.
            if (option.BlockReason == CraftBlockReason.Active) return;
            Stop(npc, jobs, job, option.BlockReason);
            return;
        }

        send(new CraftByOutputCommand(new EntityId(npc), job.Output));
        job.Phase = Phase.Starting;
        job.SentTick = tick;
    }

    private void Stop(int npc, List<Job> jobs, Job job, CraftBlockReason reason)
    {
        jobs.Remove(job);
        if (jobs.Count == 0) _jobs.Remove(npc);
        _stopped[npc] = (job.Output, reason);
    }
}
}
