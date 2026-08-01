using System;
using System.Collections.Generic;
using System.Reflection;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Content
{

/// <summary>
/// The single canonical list of static balance-knob classes and a tiny
/// reflection surface over their tunable fields. Everything that treats
/// "the balance numbers" as data goes through here so the set can never
/// fork: the editor coverage gate (every static must be mapped to some
/// config asset), the SimData JSON balance section (export/import for
/// headless parity, spec §59.3) and the Capture menu all enumerate the
/// SAME fields by construction.
///
/// A "tunable field" is a public static field of a primitive tuning type
/// (float/int/bool/long). Properties and methods are derived values, not
/// knobs, and are skipped.
/// </summary>
public static class BalanceReflection
{
    public static readonly Type[] BalanceClasses =
    {
        typeof(SimBalance),
        typeof(Spec49),
        typeof(Spec50),
        typeof(Spec53),
        typeof(Spec57),
        typeof(Spec62),
        typeof(Spec72),
        typeof(Spec76),
        typeof(AiBalance),
        typeof(SocialBalance),
        typeof(WorldBalance),
        typeof(WildlifeBalance),
        typeof(HexHopTuning),
    };

    public static bool IsTunable(FieldInfo field)
    {
        if (field == null || !field.IsStatic || !field.IsPublic ||
            field.IsInitOnly || field.IsLiteral)
        {
            return false;
        }

        var t = field.FieldType;
        return t == typeof(float) || t == typeof(int) ||
               t == typeof(bool) || t == typeof(long);
    }

    /// <summary>All tunable fields, keyed "ClassName.FieldName", in a stable
    /// (class-list, then field-name) order.</summary>
    public static IEnumerable<KeyValuePair<string, FieldInfo>> EnumerateFields()
    {
        foreach (var type in BalanceClasses)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var field in fields)
            {
                if (IsTunable(field))
                {
                    yield return new KeyValuePair<string, FieldInfo>(
                        type.Name + "." + field.Name, field);
                }
            }
        }
    }

    public static FieldInfo Find(string key)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0)
        {
            return null;
        }

        var className = key.Substring(0, dot);
        var fieldName = key.Substring(dot + 1);
        foreach (var type in BalanceClasses)
        {
            if (type.Name != className)
            {
                continue;
            }

            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            return IsTunable(field) ? field : null;
        }

        return null;
    }
}

}
