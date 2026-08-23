using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Headless source/asset gate for the presentation contracts that cannot be
/// instantiated by the simulation-only test assembly.
/// </summary>
public sealed class CharacterDollAndInventoryUiContractTests
{
    private static string Presentation(params string[] parts) =>
        Path.Combine(new[]
        {
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation"
        }.Concat(parts).ToArray());

    [Test]
    public void CharacterPanelUsesOneLargeFlatGridAndContainsNoScroller()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));

        Assert.Multiple(() =>
        {
            // The portrait pane is as wide as the portrait and the item list
            // takes the rest. Half the window each left tall dead strips beside
            // a doll whose aspect is far narrower than half the window.
            Assert.That(source, Does.Contain("_invItemsPane.style.flexGrow = 1f"));
            Assert.That(source, Does.Not.Contain("_invItemsPane.style.width = Length.Percent(50f)"));
            Assert.That(source, Does.Not.Contain("_invDollPane.style.width = Length.Percent(50f)"));
            Assert.That(source, Does.Not.Contain("new ScrollView"));
            Assert.That(source, Does.Not.Contain("Scroller"));
            Assert.That(source, Does.Not.Contain("_invLeftColumn"));
            Assert.That(source, Does.Not.Contain("_invCenterColumn"));
            Assert.That(source, Does.Not.Contain("_invRightColumn"));
            Assert.That(source, Does.Contain("106f, 93f, 80f, 70f, 61f"));
            Assert.That(source, Does.Contain("BuildInventorySlotCell("));
            Assert.That(source, Does.Contain("name = \"inventory-flat-slot\""));
            Assert.That(source, Does.Contain("foreach (var container in npc.InventoryContainers)"));
            Assert.That(source, Does.Contain("foreach (var slot in container.Slots)"));
            Assert.That(source, Does.Not.Contain("BuildUnifiedInventoryCard"));
            Assert.That(source, Does.Not.Contain("BuildGarmentInventoryCard"));
            Assert.That(source, Does.Not.Contain("inventory-garment-grid"));
        });
    }

    [Test]
    public void ManualCraftingTabUsesAuthoritativeOptionsAndHasNoIngredientDragging()
    {
        var panel = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var sourceApi = File.ReadAllText(Presentation("Bootstrap", "ISimulationSource.cs"));
        var local = File.ReadAllText(Presentation("Bootstrap", "LocalEngineBackend.cs"));
        var loopback = File.ReadAllText(Presentation("Bootstrap", "LoopbackBackend.cs"));
        var remote = File.ReadAllText(Presentation(
            "Bootstrap", "Remote", "RemoteSocketBackend.cs"));
        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));
        var craftStart = panel.IndexOf(
            "private void BuildCraftingView", StringComparison.Ordinal);
        var craftEnd = panel.IndexOf(
            "private void BuildInventoryWearLayerButtons", craftStart,
            StringComparison.Ordinal);
        Assert.That(craftStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(craftEnd, Is.GreaterThan(craftStart));
        var craft = panel[craftStart..craftEnd];

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("inventory-tab-backpack"));
            Assert.That(panel, Does.Contain("inventory-tab-craft"));
            Assert.That(panel, Does.Contain("craft-recipe-grid"));
            Assert.That(panel, Does.Contain("craft-ingredient-row"));
            Assert.That(panel, Does.Contain("ingredient.Available >= ingredient.Required"));
            Assert.That(panel, Does.Contain("LoadItemIcon(option.OutputDefinitionId)"));
            Assert.That(panel, Does.Contain("Wearing.Garments.ItemIcons.Load(id)"));
            Assert.That(craft, Does.Not.Contain("Drag"));
            Assert.That(craft, Does.Not.Contain("PointerMoveEvent"));
            Assert.That(craft, Does.Not.Contain("PointerUpEvent"));
            Assert.That(panel, Does.Contain("NpcSelection.Count == 1"));
            Assert.That(panel, Does.Contain("npc.IsManualControl"));
            Assert.That(panel, Does.Contain("_runner.SupportsNpcCommands"));
            Assert.That(sourceApi, Does.Contain("TryGetCraftingOptions"));
            Assert.That(local, Does.Contain("CraftingOptions.TryFill"));
            // §145.2: loopback с кадром команд репетирует УПРАВЛЯЕМЫЙ мир —
            // вкладка крафта живёт, список рецептов делегируется внутрь
            // (правду о доступности всё равно решает ManualCommandExecutor).
            Assert.That(loopback, Does.Contain("_inner.TryGetCraftingOptions"));
            Assert.That(remote, Does.Contain("FrameKind.CraftingOptions"));
            Assert.That(remote, Does.Contain("_craftingOptions.TryGetValue"));
            Assert.That(remote, Does.Contain("into.AddRange(options)"));
            Assert.That(localization, Does.Contain("Term: craft.tab.craft"));
            Assert.That(localization, Does.Contain("- Craft"));
            Assert.That(localization, Does.Contain("\\u041A\\u0440\\u0430\\u0444\\u0442"));
            Assert.That(localization, Does.Contain("Term: craft.create"));
            Assert.That(localization, Does.Contain("Term: craft.continue"));
            Assert.That(localization, Does.Contain("Term: craft.working"));
        });
    }

    [Test]
    public void InventoryDetailRemainsSingleUnscrolledCard()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        Assert.That(Regex.Matches(source, @"void\s+BuildInventoryDetail\s*\(").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(source, @"void\s+ShowItemDetail\s*\(").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(source, @"void\s+BuildItemStats\s*\(").Count, Is.EqualTo(1));
        Assert.That(source, Does.Contain("ScheduleInventoryHoverDetailHide"));
        Assert.That(source, Does.Contain("RegisterInventoryItemInteraction"));
        Assert.That(source, Does.Contain("FinishInventoryClick"));
        Assert.That(source, Does.Contain("_root.Add(_invDetailView)"));
        Assert.That(source, Does.Not.Contain("_inventoryWindow.Add(_invDetailView)"));
        Assert.That(source, Does.Contain(
            "_inventoryOpen && _invSelectedId != null"),
            "The root-level card must still block world picking while visible.");
    }

    [Test]
    public void InventoryDetailDismissesOnEmptyClickButNotOnDragOrInsideClick()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var start = source.IndexOf(
            "private void KeepInventoryDetailOpen", StringComparison.Ordinal);
        var end = source.IndexOf(
            "private void BuildInventoryDetail", start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));
        var dismissal = source[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "_inventoryWindow.RegisterCallback<PointerUpEvent>(\n" +
                "                DismissInventoryDetailOnBackgroundClick)"));
            Assert.That(source, Does.Contain(
                "_invDetailView.RegisterCallback<PointerUpEvent>(KeepInventoryDetailOpen)"));
            Assert.That(dismissal, Does.Contain("evt.button != 0"));
            Assert.That(dismissal, Does.Contain("_invSelectedId == null"));
            Assert.That(dismissal, Does.Contain("_invDraggedId != null"));
            Assert.That(dismissal, Does.Contain("_invPointerMoved"));
            Assert.That(dismissal, Does.Contain("_invPreviewRotated"));
            Assert.That(dismissal, Does.Contain("HideItemDetail()"));
            Assert.That(dismissal, Does.Contain("evt.StopPropagation()"),
                "A normal click inside the detail card must not reach window dismissal.");
            Assert.That(source, Does.Contain("if (!_invPreviewRotated)"));
            Assert.That(source, Does.Contain("TryPickWorn("));
        });
    }

    [Test]
    public void InventorySeparatesCarriedSlotsAndAuthoritativeWornItems()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var rebuildStart = source.IndexOf(
            "private void RebuildItemList", StringComparison.Ordinal);
        var signatureStart = source.IndexOf(
            "private static string InventoryLayoutSignature", rebuildStart,
            StringComparison.Ordinal);
        var slotStart = source.IndexOf(
            "private VisualElement BuildInventorySlotCell", signatureStart,
            StringComparison.Ordinal);
        var anchorStart = source.IndexOf(
            "private void RememberItemAnchor", slotStart, StringComparison.Ordinal);
        Assert.That(rebuildStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(signatureStart, Is.GreaterThan(rebuildStart));
        Assert.That(slotStart, Is.GreaterThan(signatureStart));
        Assert.That(anchorStart, Is.GreaterThan(slotStart));
        var rebuild = source[rebuildStart..signatureStart];
        var slot = source[slotStart..anchorStart];

        Assert.Multiple(() =>
        {
            Assert.That(rebuild, Does.Contain("foreach (var container in npc.InventoryContainers)"));
            Assert.That(rebuild, Does.Contain("foreach (var slot in container.Slots)"));
            Assert.That(rebuild, Does.Contain("_invItemsContent.Add(cell)"));
            Assert.That(rebuild, Does.Not.Contain("OwnerItemDefinitionId"));
            Assert.That(rebuild, Does.Contain("if (_clothesPage)"));
            Assert.That(rebuild, Does.Contain("npc.WornItems.Count"));
            Assert.That(rebuild, Does.Contain("BuildWornItemCell("));
            Assert.That(slot, Does.Not.Contain("AcceptedItemDefinitionId"),
                "Typed empty cells must not leak holster/container semantics into the flat grid.");
            Assert.That(slot, Does.Not.Contain("InventoryContainerTitle"));
            Assert.That(source, Does.Not.Contain("inventory-card-hero"));
            Assert.That(source, Does.Not.Contain("inventory-unified-card"));
        });
    }

    [Test]
    public void ClothesTabUsesIconsAndPreservesTheWornSourceIndex()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("inventory-tab-clothes"));
            Assert.That(source, Does.Contain("Loc.Get(\"inv.tab.clothes\")"));
            Assert.That(source, Does.Contain("name = \"inventory-worn-item\""));
            Assert.That(source, Does.Contain("LoadItemIcon(itemId)"));
            Assert.That(source, Does.Contain(
                "cell, itemId, true, durability, water, stacks, wetness, dirtiness"));
            Assert.That(source, Does.Contain("_invSelectedSourceIndex = sourceIndex"));
            Assert.That(localization, Does.Contain("Term: inv.tab.clothes"));
            Assert.That(localization, Does.Contain("- Clothes"));
            Assert.That(localization, Does.Contain("\\u041E\\u0434\\u0435\\u0436\\u0434\\u0430"));
        });
    }

    [Test]
    public void ClothingCardShowsThePhysicalItemsLocalizedOwner()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("npc.InventoryOwnerIds"));
            Assert.That(source, Does.Contain("npc.WornOwnerIds"));
            Assert.That(source, Does.Contain("ResolveInventoryOwnerId(id, worn, sourceIndex)"));
            Assert.That(source, Does.Contain("Loc.Get(\"inv.owner\")"));
            Assert.That(source, Does.Contain("Loc.NpcName(nameId)"));
            Assert.That(localization, Does.Contain("Term: inv.owner"));
            Assert.That(localization, Does.Contain("Term: inv.owner.none"));
            Assert.That(localization, Does.Contain("Term: inv.owner.unknown"));
        });
    }

    [Test]
    public void DroppingIsAnExplicitDetailActionWithoutBottomDragZone()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("EnqueueInventoryAction(InventoryAction.Drop)"));
            Assert.That(source, Does.Contain("_invDropActionLabel"));
            Assert.That(source, Does.Not.Contain("_invDropZone"));
            Assert.That(source, Does.Not.Contain("Loc.Get(\"inv.drop_zone\")"));
        });
    }

    [Test]
    public void DollHoverOpensTheExistingDetailAndClickIsSeparatedFromRotation()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var hoverStart = source.IndexOf(
            "private void HoverInventoryPreview", StringComparison.Ordinal);
        var toggleStart = source.IndexOf(
            "private void ToggleInventory", hoverStart, StringComparison.Ordinal);
        var previewHover = source[hoverStart..toggleStart];

        Assert.Multiple(() =>
        {
            Assert.That(previewHover, Does.Contain("SetHoveredWorn(wornId)"));
            Assert.That(previewHover, Does.Contain("ShowItemDetail("));
            Assert.That(source, Does.Not.Contain(
                "RegisterCallback<MouseEnterEvent>(_ => ShowItemDetail"));
            Assert.That(previewHover, Does.Contain("ScheduleInventoryHoverDetailHide"));
            Assert.That(previewHover, Does.Contain("StartingIn(80)"));
            Assert.That(source, Does.Contain("window.xMax - root.xMin + 12f"));
            Assert.That(source, Does.Contain("doll.center.y - root.yMin"));
            Assert.That(source, Does.Contain("anchor.xMax - root.xMin + 10f"));
            Assert.That(source, Does.Contain("anchor.xMin - root.xMin - detailWidth - 10f"));
            Assert.That(source, Does.Contain("InventoryDetailPlacement.Doll"));
            Assert.That(source, Does.Contain("InventoryDetailPlacement.Item"));
            Assert.That(source, Does.Contain("if (_invSelectedWorn)"));
            Assert.That(source, Does.Contain("private const float InventoryDragThreshold = 6f"));
            Assert.That(source, Does.Contain("UpdateInventoryPointerGesture"));
            Assert.That(source, Does.Contain("FinishInventoryClick"));
            Assert.That(source, Does.Contain("_invPreviewRotated"));
            Assert.That(source, Does.Contain("if (!_invPreviewRotated)"));
            Assert.That(source, Does.Contain("_root.RegisterCallback<PointerMoveEvent>"),
                "The threshold must still be observed after the pointer leaves a card.");
        });
    }

    [Test]
    public void DollPickingUsesFrozenMeshSurfacesBeforeBoundsFallback()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));
        var pickStart = source.IndexOf(
            "public bool TryPickWorn", StringComparison.Ordinal);
        var zonesStart = source.IndexOf(
            "public void SetZones", pickStart, StringComparison.Ordinal);
        Assert.That(pickStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(zonesStart, Is.GreaterThan(pickStart));
        var picking = source[pickStart..zonesStart];

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("BuildWornPickColliders()"));
            Assert.That(source, Does.Contain("skin.BakeMesh(mesh, false)"));
            Assert.That(picking, Does.Contain("collider.Raycast"));
            Assert.That(picking, Does.Contain("_wornPickColliders.ContainsKey(renderer)"));
            Assert.That(picking.IndexOf("collider.Raycast", StringComparison.Ordinal),
                Is.LessThan(picking.IndexOf("bounds.IntersectRay", StringComparison.Ordinal)),
                "Exact visible triangles must be considered before a legacy bounds fallback.");
            Assert.That(source, Does.Contain("ReleaseWornPickMeshes()"));
        });
    }

    [Test]
    public void InventoryLayerTabsFilterThePersistentCloneWithoutRebuildingIt()
    {
        var panel = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var stage = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));
        var icons = File.ReadAllText(Presentation("UI", "VectorIcon.cs"));
        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));
        var selectStart = stage.IndexOf(
            "public void SetVisibleWearLayer", StringComparison.Ordinal);
        var targetStart = stage.IndexOf(
            "public void SetTarget", selectStart, StringComparison.Ordinal);
        var selection = stage[selectStart..targetStart];
        var filterStart = stage.IndexOf(
            "private void ApplyWearLayerVisibility", StringComparison.Ordinal);
        var enableStart = stage.IndexOf(
            "private void SetStageEnabled", filterStart, StringComparison.Ordinal);
        var filtering = stage[filterStart..enableStart];

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("BuildInventoryWearLayerButtons"));
            Assert.That(panel, Does.Contain("VisualWearLayer.Underwear"));
            Assert.That(panel, Does.Contain("VisualWearLayer.Wear"));
            Assert.That(panel, Does.Contain("VisualWearLayer.Outerwear"));
            Assert.That(panel, Does.Contain("VisualWearLayer.Bags"));
            Assert.That(panel, Does.Contain("VectorIcon.Kind.Underwear"));
            Assert.That(panel, Does.Contain("VectorIcon.Kind.Shirt"));
            Assert.That(panel, Does.Contain("VectorIcon.Kind.Coat"));
            Assert.That(panel, Does.Contain("VectorIcon.Kind.Bag"));
            Assert.That(selection, Does.Contain("ApplyWearLayerVisibility()"));
            Assert.That(selection, Does.Not.Contain("Instantiate("));
            Assert.That(selection, Does.Not.Contain("DestroyClone("));
            Assert.That(filtering, Does.Contain("_cloneWears"));
            Assert.That(filtering, Does.Contain("_equippedCloneWears"),
                "Clone-only runtime ids are not serialized; equipped Wears need an explicit cache.");
            Assert.That(filtering, Does.Not.Contain("GetComponentsInChildren"),
                "Layer browsing must reuse the clone-time Wear cache.");
            Assert.That(stage, Does.Contain("(int)wear.Layer <= (int)visibleLayer"));
            Assert.That(stage, Does.Contain("outer.HeedHideUnderwearSlot(slot)"));
            Assert.That(stage, Does.Contain("_mode == CharacterDollMode.Inventory"));
            Assert.That(icons, Does.Contain("private static void DrawUnderwear"));
            Assert.That(icons, Does.Contain("private static void DrawShirt"));
            Assert.That(icons, Does.Contain("private static void DrawCoat"));
            Assert.That(icons, Does.Contain("private static void DrawBag"));
            Assert.That(localization, Does.Contain("Term: 'layer.bags'"));
            Assert.That(localization, Does.Contain("'Рюкзак'"));
        });
    }

    [Test]
    public void InventoryAndHealthUseOnePersistentStage()
    {
        var stagePath = Presentation("UI", "CharacterDollStage.cs");
        Assert.That(File.Exists(stagePath), Is.True);
        Assert.That(File.Exists(Presentation("UI", "InventoryPreviewStage.cs")), Is.False);
        Assert.That(File.Exists(Presentation("UI", "HealthDollStage.cs")), Is.False);

        var stage = File.ReadAllText(stagePath);
        var setModeStart = stage.IndexOf("public void SetMode", StringComparison.Ordinal);
        var setTargetStart = stage.IndexOf("public void SetTarget", setModeStart, StringComparison.Ordinal);
        var setMode = stage[setModeStart..setTargetStart];
        Assert.Multiple(() =>
        {
            Assert.That(stage, Does.Contain("Inventory,"));
            Assert.That(stage, Does.Contain("CharacterDollMode.Health"));
            Assert.That(stage, Does.Contain("_normalBodyRenderer.enabled"));
            Assert.That(stage, Does.Contain("_healthBodyRenderer.enabled"));
            Assert.That(stage, Does.Contain("ActorBodyResolver.TryResolve"));
            Assert.That(stage, Does.Contain("FittedProstheticPoseFollower"));
            Assert.That(stage, Does.Contain("new GameObject(\"CharacterDollHealthBody\")"),
                "Unity forbids adding a second Renderer to the body GameObject.");
            Assert.That(stage, Does.Not.Contain(
                    "_normalBodyRenderer.gameObject.AddComponent<SkinnedMeshRenderer>()"),
                "The HP renderer must live on an identity child of the body renderer.");
            Assert.That(stage, Does.Contain("catch (Exception exception)"),
                "An HP overlay failure must not restart the persistent clone every frame.");
            Assert.That(stage, Does.Contain("!shader.isSupported"),
                "An unsupported player shader must fall back to the ordinary body, not magenta.");
            Assert.That(setMode, Does.Not.Contain("Instantiate("),
                "Mode switching must retain the existing clone.");
        });

        foreach (var bootstrap in new[]
                 {
                     Presentation("Bootstrap", "PrototypeRuntimeBootstrap.cs"),
                     Presentation("AbuseTest", "AbuseTestBootstrap.cs"),
                     Presentation("WolfFightTest", "WolfFightTestBootstrap.cs")
                 })
        {
            var source = File.ReadAllText(bootstrap);
            Assert.That(Regex.Matches(source, @"AddComponent<(?:UI\.)?CharacterDollStage>").Count,
                Is.EqualTo(1), bootstrap);
            Assert.That(source, Does.Not.Contain("HealthDollStage"), bootstrap);
            Assert.That(source, Does.Not.Contain("InventoryPreviewStage"), bootstrap);
        }
    }

    [Test]
    public void DollCameraBakesTheIdleCloneInsteadOfUsingSourcePoseOrCullingMargins()
    {
        var stage = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));
        var frameStart = stage.IndexOf("private void FrameClone()", StringComparison.Ordinal);
        var signatureStart = stage.IndexOf(
            "private int SourceVisualSignature", frameStart, StringComparison.Ordinal);
        var framing = stage[frameStart..signatureStart];

        Assert.Multiple(() =>
        {
            Assert.That(framing, Does.Contain("TryGetStableVisualWorldBounds(renderer"));
            Assert.That(framing, Does.Contain("skin.BakeMesh(bakedMesh, false)"));
            Assert.That(framing, Does.Contain("renderer.localToWorldMatrix"));
            Assert.That(framing, Does.Contain("bounds.max.y + height * HeadroomFraction"),
                "The head is kept in frame by explicit headroom, not by luck.");
            Assert.That(stage, Does.Contain("HeadroomFraction"));
            Assert.That(stage, Does.Contain("FootroomFraction"));
            Assert.That(framing, Does.Not.Contain("horizontalDistance"),
                "Fitting width let one wide silhouette pull the camera in for good.");
            Assert.That(framing, Does.Contain("bounds.extents.z +"),
                "Perspective fit must include the geometry nearest to the camera.");
            Assert.That(stage, Does.Contain("_animator.speed = 0f"),
                "The cloned source must not transition back into its sitting or lying pose.");
            Assert.That(stage, Does.Contain("AnimationPlayableUtilities.PlayClip"),
                "The studio pose must bypass the actor's controller and its cloned parameters.");
            var buildStart = stage.IndexOf(
                "private void BuildClone(", StringComparison.Ordinal);
            var build = stage[buildStart..stage.IndexOf(
                "AnimationPlayableUtilities.PlayClip", buildStart, StringComparison.Ordinal)];
            Assert.That(build, Does.Contain("DisableCloneBehaviour();"));
            Assert.That(build.IndexOf("_modelPivot.gameObject.SetActive(true)", StringComparison.Ordinal),
                Is.GreaterThan(build.IndexOf("DisableCloneBehaviour();", StringComparison.Ordinal)),
                "An Animator on an inactive hierarchy cannot be evaluated: the clone must " +
                "be woken up after its live solvers are dead but before it is posed, or the " +
                "doll silently keeps the pose Instantiate copied off the live colonist.");
            Assert.That(stage, Does.Contain("StudioPosePath = \"HexLive/Poses/Female Standing Pose\""));
            Assert.That(stage, Does.Contain("!_studioPose.humanMotion"),
                "A Generic import of the pose flattens the humanoid rig into a sheet.");
            Assert.That(framing, Does.Not.Match(@"bounds\s*=\s*renderer\.bounds"));
            Assert.That(framing, Does.Not.Contain("Encapsulate(renderer.bounds)"),
                "The evaluated source pose makes sitting and lying dolls change camera scale.");
        });
    }

    [Test]
    public void DollFrameIsAPhotoPointMeasuredOncePerActor()
    {
        var stage = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));
        var frameStart = stage.IndexOf("private void FrameClone()", StringComparison.Ordinal);
        var signatureStart = stage.IndexOf(
            "private int SourceVisualSignature", frameStart, StringComparison.Ordinal);
        var framing = stage[frameStart..signatureStart];

        Assert.Multiple(() =>
        {
            Assert.That(framing, Does.Contain("_framingByActor.TryGetValue(_actorMesh"),
                "A rebuilt clone must reuse the actor's frame, never re-aim the camera.");
            Assert.That(framing, Does.Contain("_framingByActor[_actorMesh] = framing"),
                "The frame is measured on the actor's first clone and cached.");
            Assert.That(framing, Does.Contain("_modelPivot.localRotation = Quaternion.identity"),
                "Measuring through the player's yaw would frame a rotated doll.");
            Assert.That(framing, Does.Contain("RestoreDistalBones()"),
                "An amputation must not zoom the portrait in.");
            Assert.That(framing, Does.Contain("_normalBodyRenderer"),
                "The photo point belongs to the body, not to clothes, hair or props.");
            Assert.That(stage, Does.Contain("transform.InverseTransformPoint(focus)"),
                "The frame is stored in stage-local space so the stage may move.");
        });
    }

    [Test]
    public void DollStageRendersOnDemandAndFreezesItsClone()
    {
        var stage = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(stage, Does.Contain("_camera.enabled = _renderDirty"),
                "A still portrait must not re-render every frame the window is open.");
            Assert.That(stage, Does.Contain("private void FreezeClone()"));
            Assert.That(stage, Does.Contain("_animator.enabled = false"),
                "An Animator at speed 0 still retargets the whole rig every frame.");
            Assert.That(stage, Does.Contain("skin.updateWhenOffscreen = false"),
                "The frozen clone must not be skinned on frames nobody renders.");
            Assert.That(stage, Does.Contain("!_texture.IsCreated()"),
                "A texture that renders once can also lose its contents once.");
            Assert.That(stage, Does.Contain("renderer is ParticleSystemRenderer"),
                "Transient blood VFX must not rebuild the doll twice per wound.");
            Assert.That(stage, Does.Contain("renderer.GetSharedMaterials(_materialScratch)"),
                "The sharedMaterials getter allocates an array per renderer.");
            Assert.That(stage, Does.Not.Contain("foreach (var material in renderer.sharedMaterials)"));
            Assert.That(stage, Does.Contain("SignatureIntervalSeconds"),
                "Walking the dressed hierarchy every frame is the stage's biggest cost.");
        });
    }

    [Test]
    public void DollSynchronizesPaintedSurfaceWithoutRebuildingItsClone()
    {
        var stage = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));
        var painter = File.ReadAllText(Presentation("Wearing", "SkinTexturePainter.cs"));
        var garmentPainter = File.ReadAllText(
            Presentation("Wearing", "GarmentWearPainter.cs"));
        var syncStart = stage.IndexOf(
            "private void SynchronizeSurfaceState", StringComparison.Ordinal);
        var signatureStart = stage.IndexOf(
            "private int SourceSurfaceSignature", syncStart, StringComparison.Ordinal);
        var healthStart = stage.IndexOf(
            "private void BuildHealthRenderer", signatureStart, StringComparison.Ordinal);
        Assert.That(syncStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(signatureStart, Is.GreaterThan(syncStart));
        Assert.That(healthStart, Is.GreaterThan(signatureStart));
        var sync = stage[syncStart..signatureStart];
        var surfaceSignature = stage[signatureStart..healthStart];

        Assert.Multiple(() =>
        {
            Assert.That(stage, Does.Contain("BindSurfaceRenderers(source, _clone.transform)"),
                "Source and clone renderers need a stable per-slot binding.");
            Assert.That(stage, Does.Contain("SynchronizeSurfaceState(force: true)"),
                "The first portrait must copy property blocks that Instantiate omits.");
            Assert.That(stage, Does.Contain("SynchronizeSurfaceState(force: false)"),
                "A frozen open portrait must notice later skin-painter passes.");
            Assert.That(sync, Does.Contain(
                "binding.Source.GetPropertyBlock(_surfaceBlock, slot)"));
            Assert.That(sync, Does.Contain("binding.Clone.SetPropertyBlock("));
            Assert.That(sync, Does.Not.Contain("Instantiate("));
            Assert.That(sync, Does.Not.Contain("BuildClone("),
                "Tan, wetness or paint revisions must not replace the persistent doll.");
            Assert.That(surfaceSignature, Does.Contain("BaseColorId"));
            Assert.That(surfaceSignature, Does.Contain("SmoothnessId"));
            Assert.That(surfaceSignature, Does.Contain("BaseMapId"));
            Assert.That(surfaceSignature, Does.Contain("BumpMapId"));
            Assert.That(surfaceSignature, Does.Contain("MetallicGlossMapId"));
            Assert.That(surfaceSignature, Does.Contain("texture.updateCount"),
                "Repainting an existing RenderTexture must invalidate the still photo.");
            Assert.That(Regex.Matches(painter, @"\.IncrementUpdateCount\(\)").Count,
                Is.GreaterThanOrEqualTo(3),
                "GPU albedo, gloss and normal writes must publish their texture revision.");
            Assert.That(Regex.Matches(garmentPainter, @"\.IncrementUpdateCount\(\)").Count,
                Is.GreaterThanOrEqualTo(2),
                "GPU garment mask and albedo writes must publish their texture revision.");
        });
    }

    [Test]
    public void BothDollWindowsContainThePortraitInsteadOfCroppingIt()
    {
        var panel = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
        var stage = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));
        var width = int.Parse(Regex.Match(stage, @"TextureWidth = (\d+)").Groups[1].Value);
        var height = int.Parse(Regex.Match(stage, @"TextureHeight = (\d+)").Groups[1].Value);

        Assert.Multiple(() =>
        {
            Assert.That(Regex.Matches(
                    panel, @"BackgroundSizeType\.Contain").Count,
                Is.GreaterThanOrEqualTo(2),
                "Inventory and health must both fit the complete portrait.");
            // Contain only stops letterboxing when the viewport carries the
            // texture's own aspect. Both windows derive their box from it.
            Assert.That(panel, Does.Contain(
                    $"DollAspectHeight = {height}f / {width}f"),
                "The viewport aspect must be derived from the stage texture, not retyped.");
            Assert.That(panel, Does.Contain(
                "_healthDollImage.style.height = DollViewportWidth * DollAspectHeight"));
            Assert.That(panel, Does.Contain("private void FitInventoryDollViewport()"),
                "The elastic inventory pane needs an explicit 2:3 lock.");
            Assert.That(panel, Does.Contain("previewRoomHeight / DollAspectHeight"),
                "The portrait is sized by the row's height; its pane's width follows it.");
            Assert.That(panel, Does.Not.Contain("_healthDollImage.style.height = 260f"),
                "195x260 is 3:4 and pillarboxes the 2:3 portrait.");
        });
    }

    [Test]
    public void ActorBodyFlowHasNoNamedDonorOrLegacySelectionFallback()
    {
        var resolver = File.ReadAllText(Presentation("Wearing", "ActorBodyResolver.cs"));
        var limbFactory = File.ReadAllText(Presentation("Wearing", "SeveredLimbFactory.cs"));
        var actorView = File.ReadAllText(Presentation("Wearing", "NpcActorView.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(resolver, Does.Not.Contain("Molly"));
            Assert.That(resolver, Does.Not.Contain("Marta"));
            Assert.That(resolver, Does.Not.Contain("Genesis"));
            Assert.That(resolver, Does.Not.Contain("vertexCount"));
            Assert.That(limbFactory, Does.Not.Contain("BuildFromMarta"));
            Assert.That(limbFactory, Does.Not.Contain("ActorName.Molly"));
            Assert.That(actorView, Does.Not.Contain("defaulting to Marta"));
        });
    }

    [Test]
    public void SeveredLimbUsesEvaluatedPoseCloneAtPersistedHexAnchor()
    {
        var factory = File.ReadAllText(Presentation("Wearing", "SeveredLimbFactory.cs"));
        var dropView = File.ReadAllText(Presentation("Wearing", "SeveredLimbDropView.cs"));
        var actorView = File.ReadAllText(Presentation("Wearing", "NpcActorView.cs"));
        var renderer = File.ReadAllText(Presentation("Rendering", "HexWorldRenderer.cs"));
        var simulation = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "Simulation", "Runtime", "Helpers",
            "AmputateSystemHelpers.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(factory, Does.Contain("BuildFromCurrentPose"));
            Assert.That(factory, Does.Contain("BakeMesh(posedMesh, false)"));
            Assert.That(factory, Does.Contain("CloneChildren"));
            Assert.That(factory, Does.Contain("DestroyTransient(poseClone)"));
            Assert.That(factory, Does.Not.Contain("BuildFromMarta"));
            Assert.That(actorView, Does.Contain("TryGetSeveredLimbOriginalScale"));
            Assert.That(dropView, Does.Contain("new WaitForEndOfFrame()"));
            Assert.That(dropView, Does.Contain("SetParent(transform, true)"));
            Assert.That(renderer, Does.Contain("FreshLimbPoseWindowTicks"));
            Assert.That(renderer, Does.Contain("SeveredLimbDropView"));
            Assert.That(simulation, Does.Contain(
                "limb.RotationDegrees = NormalizeObjectFacing(npc.RotationDegrees)"));
        });
    }

    [Test]
    public void ActorPrefabsDoNotReferenceLegacyNativeBodiesAndBodyFbxAreReadable()
    {
        var assets = Path.Combine(RepoPaths.Root, "Assets");
        var actors = Path.Combine(assets, "Resources", "HexLive", "Actors");
        var metaByGuid = Directory.EnumerateFiles(assets, "*.fbx.meta", SearchOption.AllDirectories)
            .Select(path => (path, match: Regex.Match(File.ReadAllText(path), @"(?m)^guid: (\w{32})$")))
            .Where(entry => entry.match.Success)
            .ToDictionary(entry => entry.match.Groups[1].Value, entry => entry.path);

        var legacyGuids = new HashSet<string>(StringComparer.Ordinal)
        {
            "6cdf2be7231d7bb45b39833186608d02",
            "380879164ab69ba489d4a458601c83be",
            "b42d10d8a93b8924f9ef2f79dbf878f0",
            "35015e74319864d4db6671513d7da417"
        };
        var problems = new List<string>();
        foreach (var prefab in Directory.EnumerateFiles(actors, "*.prefab"))
        {
            var yaml = File.ReadAllText(prefab);
            foreach (var legacy in legacyGuids)
            {
                if (yaml.Contains(legacy, StringComparison.Ordinal))
                {
                    problems.Add($"{Path.GetFileName(prefab)} references legacy asset {legacy}");
                }
            }

            var fbxMetas = Regex.Matches(yaml, @"guid: (\w{32}), type: 3").Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Distinct()
                .Where(metaByGuid.ContainsKey)
                .Select(guid => metaByGuid[guid])
                .Where(path => path.EndsWith(".fbx.meta", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (fbxMetas.Count == 0)
            {
                problems.Add($"{Path.GetFileName(prefab)} has no FBX dependency");
                continue;
            }
            if (!fbxMetas.Any(path => Regex.IsMatch(
                    File.ReadAllText(path), @"(?m)^\s+isReadable: 1$")))
            {
                problems.Add($"{Path.GetFileName(prefab)} has no readable FBX dependency");
            }

            var avatarMatch = Regex.Match(
                yaml, @"m_Avatar: \{fileID: \d+, guid: (\w{32}), type: 3\}");
            if (!avatarMatch.Success || !metaByGuid.TryGetValue(
                    avatarMatch.Groups[1].Value, out var avatarMeta))
            {
                problems.Add($"{Path.GetFileName(prefab)} Avatar is not embedded in an FBX");
                continue;
            }

            var avatarYaml = File.ReadAllText(avatarMeta);
            if (!Regex.IsMatch(avatarYaml, @"(?m)^\s+animationType: 3$") ||
                !Regex.IsMatch(avatarYaml, @"(?m)^\s+avatarSetup: 1$"))
            {
                problems.Add(
                    $"{Path.GetFileName(prefab)} Avatar FBX is not configured as CreateFromThisModel Humanoid");
            }
        }

        Assert.That(problems, Is.Empty, string.Join("\n", problems));
    }
}

}
