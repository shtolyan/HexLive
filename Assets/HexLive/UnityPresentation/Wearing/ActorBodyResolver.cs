using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// The single body-renderer contract used by live actors and derived views.
/// A body must be a skinned FBX mesh bound to the actor's complete humanoid
/// skeleton. Actor names, renderer names and mesh size are deliberately not
/// part of the contract.
/// </summary>
public static class ActorBodyResolver
{
    private static readonly string[] RequiredBones =
    {
        "head", "pelvis", "lHand", "rHand", "lFoot", "rFoot"
    };

    public static SkinnedMeshRenderer ResolveOrNull(Component actorRoot) =>
        actorRoot != null && TryResolve(actorRoot.gameObject, out var renderer, out _)
            ? renderer
            : null;

    public static bool TryResolve(
        GameObject actorRoot,
        out SkinnedMeshRenderer renderer,
        out string error)
    {
        renderer = null;
        if (actorRoot == null)
        {
            error = "Actor root is missing.";
            return false;
        }

        var candidates = new List<SkinnedMeshRenderer>();
        foreach (var skin in actorRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skin == null || skin.sharedMesh == null || skin.bones == null ||
                skin.bones.Length == 0 || skin.GetComponentInParent<Wear>(true) != null)
            {
                continue;
            }

            if (HasCompleteBodySkeleton(skin.bones))
            {
                candidates.Add(skin);
            }
        }

        if (candidates.Count == 1)
        {
            renderer = candidates[0];
            error = string.Empty;
            return true;
        }

        error = candidates.Count == 0
            ? $"Actor '{actorRoot.name}' has no skinned FBX renderer bound to the complete body skeleton."
            : $"Actor '{actorRoot.name}' has {candidates.Count} complete body renderers; the contract requires exactly one.";
        return false;
    }

    private static bool HasCompleteBodySkeleton(IReadOnlyList<Transform> bones)
    {
        var found = new bool[RequiredBones.Length];
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            if (bone == null)
            {
                continue;
            }

            for (var required = 0; required < RequiredBones.Length; required++)
            {
                if (!found[required] && string.Equals(
                        bone.name, RequiredBones[required], StringComparison.OrdinalIgnoreCase))
                {
                    found[required] = true;
                }
            }
        }

        for (var i = 0; i < found.Length; i++)
        {
            if (!found[i])
            {
                return false;
            }
        }

        return true;
    }

}

}
