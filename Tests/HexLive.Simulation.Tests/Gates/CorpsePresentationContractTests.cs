using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Bug #354: a corpse may be assembled over several presentation frames, but
/// no incomplete standing/bind pose or clean replacement body may become
/// visible. These source contracts protect the Unity-side ownership seams that
/// pure simulation tests cannot execute.
/// </summary>
public sealed class CorpsePresentationContractTests
{
    private static string Wearing(string file) => File.ReadAllText(Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing", file));

    private static string Rendering(string file) => File.ReadAllText(Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering", file));

    private static string Views(string file) => File.ReadAllText(Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Views", file));

    private static string Slice(string source, string startToken, string endToken)
    {
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        var end = source.IndexOf(endToken, start + startToken.Length,
            StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"missing {startToken}");
        Assert.That(end, Is.GreaterThan(start), $"missing {endToken}");
        return source[start..end];
    }

    [Test]
    public void GroundCorpseRequiresConfirmedSharedFallenIdleAndHasNoPrivateOffset()
    {
        var actor = Wearing("NpcActorView.cs");
        var retry = Slice(actor, "private bool TryApplyLyingDeathPose()",
            "private void RefreshCorpseVisibility()");
        var setDead = Slice(actor, "public void SetDead(",
            "private bool AnimatorReadyForDeathPose()");

        Assert.Multiple(() =>
        {
            Assert.That(retry, Does.Contain("GetCurrentAnimatorStateInfo(0).shortNameHash"));
            Assert.That(retry, Does.Contain("FallenIdleStateHash"));
            Assert.That(retry, Does.Not.Contain("if (_laying)"),
                "requested lying is not proof that Animator wrote FallenIdle");
            Assert.That(setDead, Does.Contain("ResetTransientPresentationForDeath();"));
            Assert.That(setDead, Does.Contain("BeginStableCorpsePose();"));
            Assert.That(actor, Does.Contain("SetFallen(true, sleepAfter: false);"));
            Assert.That(actor, Does.Not.Contain("PlantDeadBodyOnSurface"));
            Assert.That(actor, Does.Not.Contain("SetDeathSurfaceY"));
            Assert.That(actor, Does.Not.Contain("DeathBaseClip"));
            Assert.That(actor, Does.Not.Contain("DeathStateHash"));
        });
    }

    [Test]
    public void CorpseSnapshotRestoresTheWholeLastAppearanceBeforeReveal()
    {
        var renderer = Rendering("HexWorldRenderer.cs");
        var sync = Slice(renderer, "private void SyncCorpseViews(",
            "private void SyncRomancePairs(");

        var weather = sync.IndexOf("actor.SetSkinWeathering(", StringComparison.Ordinal);
        var condition = sync.IndexOf("actor.SetBodyCondition(", StringComparison.Ordinal);
        var ready = sync.IndexOf("actor.IsCorpsePresentationReady(", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(sync, Does.Contain("actor.SyncWorn(body.WornItems);"));
            Assert.That(sync, Does.Contain("actor.SyncHolster(body.HolsteredItems, string.Empty);"));
            Assert.That(sync, Does.Contain("SyncGarmentWear(actor, body.WornDurability);"));
            Assert.That(sync, Does.Contain("body.WornDirtiness, body.WornBloodiness"));
            Assert.That(sync, Does.Contain("body.Wounds, body.BandagedZones, body.SeveredParts"));
            Assert.That(weather, Is.GreaterThanOrEqualTo(0));
            Assert.That(condition, Is.GreaterThan(weather),
                "weather/material baselines must exist before historical body paint");
            Assert.That(ready, Is.GreaterThan(condition),
                "visibility readiness must be sampled after full condition sync");
        });
    }

    [Test]
    public void RenderGateOrsImpostorPoseAndAppearanceAndRescansAsyncChildren()
    {
        var gate = Wearing("ActorRenderGate.cs");
        var impostor = Views("ObjectImpostor.cs");

        Assert.Multiple(() =>
        {
            Assert.That(gate, Does.Contain("_reasons | reason"));
            Assert.That(gate, Does.Contain("_reasons & ~reason"));
            Assert.That(gate, Does.Contain("NpcImpostor = 1 << 0"));
            Assert.That(gate, Does.Contain("CorpsePosePending = 1 << 1"));
            Assert.That(gate, Does.Contain("CorpseAppearancePending = 1 << 2"));
            Assert.That(gate, Does.Contain("private void LateUpdate()"));
            Assert.That(gate, Does.Contain("CorpsePendingReasons"));
            Assert.That(gate, Does.Contain("_rendererScratch"));
            Assert.That(gate, Does.Contain("_projectorScratch"));
            Assert.That(gate, Does.Not.Contain("GetComponentsInChildren<Renderer>(true)"));
            Assert.That(gate, Does.Not.Contain("GetComponentsInChildren<DecalProjector>(true)"));
            Assert.That(gate, Does.Contain("GetComponent<ObjectImpostorVisual>()"),
                "the gate must never hide the replacement impostor quad");
            Assert.That(impostor, Does.Contain(
                "SetHidden(ActorRenderHideReason.NpcImpostor, true)"));
            Assert.That(impostor, Does.Contain(
                "SetHidden(ActorRenderHideReason.NpcImpostor, false)"));
        });
    }

    [Test]
    public void HistoricalPaintUsesABoundedPriorityPassAndAReadinessAck()
    {
        var actor = Wearing("NpcActorView.cs");
        var scheduler = Wearing("SkinPaintScheduler.cs");
        var skin = Wearing("SkinTexturePainter.cs");
        var garment = Wearing("GarmentWearPainter.cs");
        var bodyBones = Wearing("BodyBones.cs");
        var wear = Wearing("Wear.cs");
        var blood = Slice(garment, "private bool AccumulateBloodStains(",
            "private void RememberPendingBlood(");
        var asyncMiss = blood.IndexOf("Availability.Loading", StringComparison.Ordinal);
        var commit = blood.LastIndexOf("_lastBloodInputHash = inputHash;",
            StringComparison.Ordinal);
        var skinPriority = Slice(skin, "public bool TryPaintPresentation()",
            "private bool HasPendingSeamUpgrade()");
        var woundLoop = Slice(skin,
            "foreach (var (zone, seed, heal) in wounds)", "if (wounds.Count > 0)");
        var bandageLoop = Slice(skin,
            "foreach (var zone in bandaged)",
            "// The simulation keeps herbal and medkit dressings distinct");

        Assert.Multiple(() =>
        {
            Assert.That(actor, Does.Contain("IsCorpsePresentationReady("));
            Assert.That(actor, Does.Contain("SkinPaintScheduler.RequestPresentationPass"));
            Assert.That(scheduler, Does.Contain("PresentationQueue"));
            Assert.That(scheduler, Does.Contain(
                "return; // never more than one presentation repaint per frame"));
            Assert.That(skin, Does.Contain("public bool PresentationReady"));
            Assert.That(skin, Does.Contain("_pendingPlacements"));
            Assert.That(skin, Does.Contain("MapDependentPaintPending"));
            Assert.That(skin, Does.Contain(
                "_mapAvailability == AtomicResources.Availability.Loading"));
            Assert.That(skinPriority,
                Does.Contain("RefreshPointMap(bypassRetryDelay: true)"),
                "corpse paint must poll an in-flight skin map without the ambient retry delay");
            Assert.That(skinPriority, Does.Contain("Availability.Loading"));
            Assert.That(skinPriority, Does.Contain("HasUnresolvedMandatoryWraps"),
                "clean/clotted corpses still wait for historical bandage and gauze wraps");
            Assert.That(skinPriority, Does.Contain("TryPlaceMandatoryWraps"));
            Assert.That(skinPriority, Does.Contain("AcknowledgeMandatoryWrapFallback"),
                "terminal missing map/art must not hold the corpse gate forever");
            Assert.That(woundLoop, Does.Not.Contain("_mandatoryWrapPending"),
                "a bare wound must never be promoted into a full authored wrap");
            Assert.That(bandageLoop, Does.Contain("var key = $\"b{zone}\""));
            Assert.That(bandageLoop,
                Does.Contain("_mandatoryWrapPending[key] = (zone, false)"));
            Assert.That(skin, Does.Contain("_mandatoryWrapPending[key] = (zone, true)"));
            Assert.That(skin, Does.Contain("PaintPointMap.Request("));
            Assert.That(skin, Does.Not.Contain("PaintPointMap.Load("),
                "null from the old loader conflates Loading with terminal art-less fallback");
            Assert.That(garment, Does.Contain("IPresentationPaintTarget"));
            Assert.That(garment, Does.Contain("!_bloodMapPending"));
            Assert.That(garment, Does.Contain("PaintPointMap.Request"));
            Assert.That(garment, Does.Contain("RememberPendingBlood"));
            Assert.That(asyncMiss, Is.GreaterThanOrEqualTo(0));
            Assert.That(commit, Is.GreaterThan(asyncMiss),
                "an async map miss must not acknowledge the blood input hash");
            Assert.That(blood, Does.Not.Contain("count == 0 ||"),
                "stored WornBloodiness must survive after all body zones heal");
            Assert.That(blood, Does.Contain("AccumulateAggregateBlood(blood)"));
            Assert.That(garment, Does.Contain("private bool AccumulateAggregateBlood("));
            Assert.That(garment, Does.Contain("(uint)_seed * 2654435761u"),
                "aggregate restored blood needs stable item-identity placement");
            Assert.That(bodyBones, Does.Contain("public int StablePaintOwnerId"));
            Assert.That(actor, Does.Contain("_bodyBones.Construct(_actorMesh, _npcId)"));
            Assert.That(wear, Does.Contain("bodyBones.StablePaintOwnerId"));
            Assert.That(wear, Does.Not.Contain("bodyBones.GetInstanceID()"),
                "Unity instance ids change on every load/stream-in");
            Assert.That(garment, Does.Contain("_paintFailed = true"));
            Assert.That(garment, Does.Contain("_repaintDirty = true"),
                "a failed GPU composite must remain non-ready for retry");
        });
    }

    [Test]
    public void CarryRevealRequiresLiveCarrierBothHandsAndHips()
    {
        var follower = Wearing("CarriedPoseFollower.cs");
        var evaluate = follower.IndexOf("CarryPoseVisuals.EvaluateAt(", StringComparison.Ordinal);
        var anchors = follower.IndexOf(
            "_hips == null || _leftHand == null || _rightHand == null",
            StringComparison.Ordinal);
        var confirm = follower.IndexOf("_self.ConfirmCorpseCarriedPose();",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(follower, Does.Contain(
                "!NpcActorView.TryGetLive(_carrierNpcId, out var carrierView)"));
            Assert.That(follower, Does.Contain("_self.DeferCorpseCarriedPose();"));
            Assert.That(follower, Does.Contain("_handsCarrierAnimator != carrierAnimator"),
                "same-id streamed carrier replacement must invalidate old hand transforms");
            Assert.That(evaluate, Is.GreaterThanOrEqualTo(0));
            Assert.That(anchors, Is.GreaterThan(evaluate));
            Assert.That(confirm, Is.GreaterThan(anchors));
            Assert.That(follower, Does.Not.Contain(": basis.position"),
                "a root fallback is proximity, not a confirmed hands attachment");
        });
    }

    [Test]
    public void DeathClearsEveryLiveOnlyTransientBeforeHolsterSync()
    {
        var actor = Wearing("NpcActorView.cs");
        var cleanup = Slice(actor, "private void ResetTransientPresentationForDeath()",
            "private bool AnimatorReadyForDeathPose()");

        Assert.Multiple(() =>
        {
            Assert.That(cleanup, Does.Contain("SetCombat(false"));
            Assert.That(cleanup, Does.Contain("SetInteraction(string.Empty"));
            Assert.That(cleanup, Does.Contain("SetWardrobeAction(string.Empty"));
            Assert.That(cleanup, Does.Contain("SetWorldProgress(0f, false)"));
            Assert.That(cleanup, Does.Contain("_speechBubble?.HideIcon()"));
            Assert.That(cleanup, Does.Contain("SetHandProp(null)"));
            Assert.That(cleanup, Does.Contain("SetHandGarment(null)"));
            Assert.That(cleanup, Does.Contain("ClearActionTarget()"));
            Assert.That(cleanup, Does.Contain("ClearGaze()"));
            Assert.That(cleanup, Does.Contain("EndPortraitGaze()"));
            Assert.That(cleanup, Does.Contain("EndCameraGaze()"));
            Assert.That(cleanup, Does.Contain("_gazeWeight = 0f"));
            Assert.That(cleanup, Does.Contain("_lookAtIK.enabled = false"),
                "FinalIK must not rewrite head/spine after FallenIdle freezes");
            Assert.That(cleanup, Does.Contain("_carryWeight = 0f"));
            Assert.That(cleanup, Does.Contain("_animator.SetLayerWeight(carryLayer, 0f)"),
                "an ArmedCarry overlay must not be frozen over FallenIdle");
            Assert.That(actor, Does.Contain("if (!_dead && !_ragdollActive && !_romanceVisual)"),
                "procedural bone writers are live-only after corpse freeze");
        });
    }
}
