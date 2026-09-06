#nullable enable
using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.Views;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HexLive.UnityPresentation.Wearing
{

[Flags]
internal enum ActorRenderHideReason
{
    None = 0,
    NpcImpostor = 1 << 0,
    CorpsePosePending = 1 << 1,
    CorpseAppearancePending = 1 << 2
}

/// <summary>
/// One composable owner for an actor's render suppression. The overview
/// impostor and corpse assembly used to save/restore <c>forceRenderingOff</c>
/// independently; clearing either owner could reveal a body that the other
/// still needed hidden. Reasons are OR-ed here and every asynchronously added
/// garment, hairstyle, prosthesis and skin projector joins the active gate.
/// Transform geometry and behaviours that assemble the actor stay untouched.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(32000)]
public sealed class ActorRenderGate : MonoBehaviour
{
    private const ActorRenderHideReason CorpsePendingReasons =
        ActorRenderHideReason.CorpsePosePending |
        ActorRenderHideReason.CorpseAppearancePending;

    private ActorRenderHideReason _reasons;
    private readonly Dictionary<Renderer, bool> _rendererStates = new();
    private readonly Dictionary<DecalProjector, bool> _projectorStates = new();
    private readonly List<Renderer> _rendererScratch = new();
    private readonly List<DecalProjector> _projectorScratch = new();

    internal ActorRenderHideReason Reasons => _reasons;

    internal void SetHidden(ActorRenderHideReason reason, bool hidden)
    {
        var next = hidden ? _reasons | reason : _reasons & ~reason;
        if (next == _reasons)
        {
            return;
        }

        _reasons = next;
        if (_reasons == ActorRenderHideReason.None)
        {
            RestorePresentation();
        }
        else
        {
            HideCurrentPresentation();
        }
    }

    private void LateUpdate()
    {
        if ((_reasons & CorpsePendingReasons) != 0)
        {
            // Wardrobe/hair/prosthetic loads complete asynchronously. Scan at
            // the end of the frame so no new renderer gets one visible bind-
            // pose frame before the next snapshot. A distant *living* NPC is
            // refreshed by EnsureActorLayer after snapshot assembly instead;
            // keeping every impostor on this path would scan the whole actor
            // hierarchy every frame for the full overview session.
            HideCurrentPresentation();
        }
    }

    /// <summary>
    /// Incorporates renderers added since the last actor snapshot. Lists are
    /// reused so a hidden crowd does not allocate arrays every frame.
    /// </summary>
    internal void RefreshHiddenPresentation()
    {
        if (_reasons != ActorRenderHideReason.None)
        {
            HideCurrentPresentation();
        }
    }

    private void HideCurrentPresentation()
    {
        _rendererScratch.Clear();
        GetComponentsInChildren(true, _rendererScratch);
        foreach (var renderer in _rendererScratch)
        {
            if (renderer == null || renderer.GetComponent<ObjectImpostorVisual>() != null)
            {
                continue;
            }

            if (!_rendererStates.ContainsKey(renderer))
            {
                _rendererStates[renderer] = renderer.forceRenderingOff;
            }
            renderer.forceRenderingOff = true;
        }
        _rendererScratch.Clear();

        // Skin wounds/soil are URP projectors rather than Renderers. Keep the
        // painter/loader components alive, but prevent their projectors from
        // leaking onto terrain while all receiving meshes are gated.
        _projectorScratch.Clear();
        GetComponentsInChildren(true, _projectorScratch);
        foreach (var projector in _projectorScratch)
        {
            if (projector == null)
            {
                continue;
            }

            if (!_projectorStates.ContainsKey(projector))
            {
                _projectorStates[projector] = projector.enabled;
            }
            projector.enabled = false;
        }
        _projectorScratch.Clear();
    }

    private void RestorePresentation()
    {
        foreach (var pair in _rendererStates)
        {
            if (pair.Key != null)
            {
                pair.Key.forceRenderingOff = pair.Value;
            }
        }
        _rendererStates.Clear();

        foreach (var pair in _projectorStates)
        {
            if (pair.Key != null)
            {
                pair.Key.enabled = pair.Value;
            }
        }
        _projectorStates.Clear();
    }

    private void OnDestroy() => RestorePresentation();
}

}
