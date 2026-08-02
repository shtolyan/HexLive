using System;
using System.Collections.Generic;
using System.Globalization;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Soak
{

/// <summary>
/// По-тиковая раскадровка боя: замах, попадание, готовность, такт сцены.
///
/// <para>
/// Нужна потому, что НИ ОДНО событие этого не показывает. «Удар лёг» видно по
/// урону, а вот сколько тиков висело окно анимации, успел ли клип доиграть и
/// когда открылся следующий замах — только по состоянию, тик за тиком. Ровно
/// того же не хватало в §102, только там молчал застой, а тут молчит бой.
/// </para>
/// </summary>
public static class CombatFrames
{
    private sealed class Row
    {
        public int Tick;
        public int Npc;
        public bool Fighting;
        public bool Swinging;
        public int Beat;
        public int Blows;
        public int LandsAt;
        public int ReadyAt;
        public int AnimUntil;
        public string Goal;
        public string Weapon;
        public int SwingStamp;
    }

    private static readonly List<Row> Rows = new List<Row>();
    private static readonly Dictionary<int, string> Signature = new Dictionary<int, string>();

    /// <summary>
    /// Забыть предыдущий прогон. Обязателен между сидами: раскадровка
    /// накапливалась в статике, и отчёт второго сида включал строки первого —
    /// то есть показания одного мира читались как показания другого.
    /// </summary>
    public static void Reset()
    {
        Rows.Clear();
        Signature.Clear();
    }

    public static void Sample(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var interesting = npc.IsFighting ||
                npc.Mind.CurrentGoal == GoalType.Abuse ||
                npc.AttackAnimUntilTick > world.Tick ||
                npc.StrikeLandsAtTick > 0;
            if (!interesting)
            {
                continue;
            }

            var row = new Row
            {
                Tick = world.Tick,
                Npc = npc.Id.Value,
                Fighting = npc.IsFighting,
                Swinging = world.Tick < npc.AttackAnimUntilTick,
                Beat = npc.Mind.AbuseBeat,
                Blows = npc.Mind.AbuseBlows,
                LandsAt = npc.StrikeLandsAtTick,
                ReadyAt = npc.StrikeReadyAtTick,
                AnimUntil = npc.AttackAnimUntilTick,
                Goal = npc.Mind.CurrentGoal.ToString(),
                SwingStamp = npc.SwingStartTick,
                Weapon = string.IsNullOrEmpty(npc.Mind.ForcedMeleeWeaponId)
                    ? (npc.Mind.ForcedMeleeWeaponId == null ? "(как обычно)" : "кулаки")
                    : npc.Mind.ForcedMeleeWeaponId,
            };

            // Пишем только КАДРЫ ИЗМЕНЕНИЯ: подряд идущие одинаковые состояния
            // ничего не добавляют, а раскадровку топят.
            var sig = row.Fighting + "|" + row.Swinging + "|" + row.Beat + "|" +
                      row.Blows + "|" + row.LandsAt + "|" + row.ReadyAt + "|" + row.Goal + "|" + row.Weapon + "|" + row.SwingStamp;
            if (Signature.TryGetValue(npc.Id.Value, out var prev) && prev == sig)
            {
                continue;
            }

            Signature[npc.Id.Value] = sig;
            Rows.Add(row);
        }
    }

    public static void Report()
    {
        if (Rows.Count == 0)
        {
            Console.WriteLine("  боёв не было");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  ── раскадровка боя (только кадры изменения) ──");
        Console.WriteLine("   тик   сек  NPC  бой замах такт удары  ляжет готов аним  цель        штамп  оружие");
        foreach (var r in Rows)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,5} {1,5:F2}  {2,3}  {3,3} {4,5} {5,4} {6,5}  {7,5} {8,5} {9,4}  {10,-10}  {11,5}  {12}",
                r.Tick, r.Tick * 0.25f, r.Npc,
                r.Fighting ? "да" : "—", r.Swinging ? "ДА" : "—",
                r.Beat, r.Blows, r.LandsAt, r.ReadyAt, r.AnimUntil, r.Goal, r.SwingStamp, r.Weapon));
        }

        Console.WriteLine();
    }
}

}
