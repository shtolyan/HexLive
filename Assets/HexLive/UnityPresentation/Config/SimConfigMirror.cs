using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Declares which static balance class(es) a config asset mirrors into.
    /// Repeatable: one asset may feed several small Spec bags.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class MirrorTargetAttribute : Attribute
    {
        public MirrorTargetAttribute(Type target) => Target = target;

        public Type Target { get; }
    }

    /// <summary>Per-field escape hatch when the convention (camelCase field →
    /// PascalCase static of the same name) can't hold — renames and the
    /// colliding `Enabled` toggles.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class MirrorFieldAttribute : Attribute
    {
        public MirrorFieldAttribute(Type target, string staticName)
        {
            Target = target;
            StaticName = staticName;
        }

        public Type Target { get; }

        public string StaticName { get; }
    }

    /// <summary>Marks a config field the mirror must skip — values that feed
    /// presentation statics through hand-written code (swim/water feel), not
    /// a balance class.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class MirrorIgnoreAttribute : Attribute
    {
    }

    /// <summary>
    /// Reflection bridge between a config ScriptableObject and the static
    /// balance classes it declares via <see cref="MirrorTargetAttribute"/>.
    /// Replaces the hand-written Apply/Capture mirror that drifted (§59):
    /// adding a knob is now one SO field + one static — the mapping is by
    /// name convention, and the editor coverage gate guarantees no static
    /// escapes the asset layer.
    ///
    /// Fails LOUDLY (InvalidOperationException) on an unmapped field, an
    /// ambiguous name or a type mismatch — silent drift is the failure mode
    /// this whole mechanism exists to kill.
    /// </summary>
    public static class SimConfigMirror
    {
        /// <summary>Push the asset's serialized fields into the statics.</summary>
        public static void Apply(ScriptableObject config)
        {
            foreach (var (soField, staticField) in ResolveMappings(config.GetType()))
            {
                staticField.SetValue(null, soField.GetValue(config));
            }
        }

        /// <summary>Read the live statics back into the asset (save button /
        /// Capture menu direction).</summary>
        public static void Capture(ScriptableObject config)
        {
            foreach (var (soField, staticField) in ResolveMappings(config.GetType()))
            {
                soField.SetValue(config, staticField.GetValue(null));
            }
        }

        public static bool IsMirrorConfig(Type type) =>
            type != null && type.IsDefined(typeof(MirrorTargetAttribute), inherit: true);

        /// <summary>Resolve every serialized field of the config type to its
        /// target static field. Public so the editor coverage gate can build
        /// the global covered-set from the same resolution logic.</summary>
        public static List<(FieldInfo Config, FieldInfo Static)> ResolveMappings(Type configType)
        {
            var targets = new List<Type>();
            foreach (var attr in configType.GetCustomAttributes<MirrorTargetAttribute>(inherit: true))
            {
                targets.Add(attr.Target);
            }

            if (targets.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{configType.Name} has no [MirrorTarget] — nothing to mirror into.");
            }

            var mappings = new List<(FieldInfo, FieldInfo)>();
            foreach (var soField in configType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (soField.IsDefined(typeof(MirrorIgnoreAttribute)))
                {
                    continue;
                }

                var explicitMap = soField.GetCustomAttribute<MirrorFieldAttribute>();
                FieldInfo staticField;
                if (explicitMap != null)
                {
                    staticField = explicitMap.Target.GetField(
                        explicitMap.StaticName, BindingFlags.Public | BindingFlags.Static);
                    if (staticField == null)
                    {
                        throw new InvalidOperationException(
                            $"{configType.Name}.{soField.Name}: [MirrorField] names " +
                            $"{explicitMap.Target.Name}.{explicitMap.StaticName}, which does not exist.");
                    }
                }
                else
                {
                    staticField = ResolveByConvention(configType, soField, targets);
                }

                if (staticField.FieldType != soField.FieldType)
                {
                    throw new InvalidOperationException(
                        $"{configType.Name}.{soField.Name} is {soField.FieldType.Name} but " +
                        $"{staticField.DeclaringType?.Name}.{staticField.Name} is {staticField.FieldType.Name}.");
                }

                mappings.Add((soField, staticField));
            }

            return mappings;
        }

        private static FieldInfo ResolveByConvention(
            Type configType, FieldInfo soField, List<Type> targets)
        {
            var staticName = char.ToUpperInvariant(soField.Name[0]) + soField.Name.Substring(1);
            FieldInfo found = null;
            foreach (var target in targets)
            {
                var candidate = target.GetField(staticName, BindingFlags.Public | BindingFlags.Static);
                if (candidate == null)
                {
                    continue;
                }

                if (found != null)
                {
                    throw new InvalidOperationException(
                        $"{configType.Name}.{soField.Name} is ambiguous: both " +
                        $"{found.DeclaringType?.Name} and {target.Name} declare {staticName}. " +
                        "Disambiguate with [MirrorField].");
                }

                found = candidate;
            }

            if (found == null)
            {
                throw new InvalidOperationException(
                    $"{configType.Name}.{soField.Name}: no target class declares {staticName}. " +
                    "Rename the field, add [MirrorField], or [MirrorIgnore] it.");
            }

            return found;
        }
    }
}
