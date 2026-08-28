#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.HutTest;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §120: ONE architecture module of a building, drawn where the simulation
    /// put it.
    ///
    /// <para>
    /// A building is raised as N independent world objects, each carrying a
    /// single <see cref="ArchitectureElementSnapshot"/> with its own delivery
    /// bill. So a module is drawn like any other world object: this component
    /// rides on that object's OWN view, which the ordinary render diff creates,
    /// positions at the object's anchor junction and destroys. There is no
    /// building frame, no parent and no lifecycle of its own — a piece is
    /// placed in the world and switched on.
    /// </para>
    /// <list type="bullet">
    /// <item>the model comes from <see cref="BlueprintArchitectureFactory"/> —
    /// the same loader the §120 constructor preview draws with, so a module
    /// looks in the world exactly as it did on the drawing board;</item>
    /// <item>every module of a building shares its anchor junction and its
    /// <c>RotationDegrees</c> (see <c>BuildingRules.EnsureHutElements</c>), so
    /// the building's six-way footprint yaw and the module's own local offset
    /// compose HERE, on this object's own model child;</item>
    /// <item>revealing pieces is
    /// <see cref="BlueprintArchitectureFactory.ApplyStageProgress"/>, so a
    /// half-delivered wall shows exactly the sticks/boards/rope the colony
    /// actually hauled in.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArchitectureModuleView : MonoBehaviour
    {
        // One warning per missing definition id, not one per module per world:
        // a plan with ten roof panels would otherwise print ten identical lines.
        private static readonly HashSet<string> MissingModels = new();

        private string _definitionId = string.Empty;
        private GameObject? _model;
        private BlueprintDoorVisual? _door;
        private Renderer[] _renderers = System.Array.Empty<Renderer>();
        private bool _cutawayHidden;
        private int _sticks = -1;
        private int _stageTwo = -1;
        private int _rope = -1;

        public void Sync(ObjectSnapshot piece)
        {
            if (piece == null || piece.ArchitectureElements.Count != 1) return;
            var element = piece.ArchitectureElements[0];
            if (element == null) return;
            EnsureModel(piece, element);
            if (_model == null) return;

            // While the real owner is being edited, the authoritative preview
            // occupies exactly the same coordinates. Hide only the live model
            // (the world object and its shadows/topology remain untouched) to
            // avoid z-fighting and doubled walls. The next snapshot after the
            // editor closes restores it through this same branch.
            var editorHidden =
                (piece.ArchitectureOwnerObjectId is { } ownerId &&
                 HutLayoutDesigner.EditingOwnerObjectId == ownerId) ||
                (HutLayoutDesigner.DirectWorldEditorOpen &&
                 piece.ArchitectureOwnerObjectId == piece.Id.Value);

            // §120.1: a roof panel whose posts are not up yet does not exist. It
            // is not an empty frame waiting for leaves — it is nothing at all,
            // exactly like the §52 build-site with nothing hauled in yet. Same
            // answer when the model is missing: never a placeholder primitive.
            // The MODEL is what hides, never this object's own view: the view is
            // the world object, and the render diff owns whether it is active.
            var shouldShow = element.Buildable && !editorHidden;
            if (_model.activeSelf != shouldShow) _model.SetActive(shouldShow);

            // §129: the door leaf follows the simulation's door state.
            if (_door != null && _door.IsOpen != piece.IsDoorOpen) _door.SetOpen(piece.IsDoorOpen);

            // Stage 2 is whatever THIS module actually bills for: boards on a
            // wall, window, door or floor sector — palm LEAVES on a roof panel.
            var stageTwo = element.RequiredLeaves > 0
                ? element.DeliveredLeaves
                : element.DeliveredBoards;
            if (_sticks == element.DeliveredSticks && _stageTwo == stageTwo &&
                _rope == element.DeliveredRope)
            {
                return;
            }

            _sticks = element.DeliveredSticks;
            _stageTwo = stageTwo;
            _rope = element.DeliveredRope;
            BlueprintArchitectureFactory.ApplyStageProgress(_model, _sticks, _stageTwo, _rope);
        }

        private void EnsureModel(ObjectSnapshot piece, ArchitectureElementSnapshot element)
        {
            if (_model != null && _definitionId == element.DefinitionId) return;
            if (_model != null) Destroy(_model);
            _model = null;
            _door = null;
            _renderers = System.Array.Empty<Renderer>();
            _definitionId = element.DefinitionId;
            _sticks = -1;
            _stageTwo = -1;
            _rope = -1;

            // Slot geometry is blueprint data: it is written once, when the
            // module is staked, and never moves afterwards. So it is read here,
            // when the model is built, and not re-applied every tick.
            //
            // This object's own transform stands at the building's anchor
            // junction (the render diff puts it there) with no rotation, so the
            // FOOTPRINT yaw is composed here instead of being carried by a
            // parent: the module's own RotationDegrees IS the building's.
            //
            // A parentless transform's position IS its local position, so
            // handing the composed pose to the factory and then re-parenting
            // with worldPositionStays:false keeps exactly those numbers as
            // locals.
            var frame = Quaternion.Euler(
                0f, SimulationUnityMapper.ToUnityFootprintYawDegrees(piece.RotationDegrees), 0f);
            var localPosition = frame * new Vector3(
                element.LocalX,
                BlueprintArchitectureFactory.ModuleLift(_definitionId),
                element.LocalZ);
            var wrapper = BlueprintArchitectureFactory.InstantiateModel(
                _definitionId,
                localPosition,
                frame * BlueprintArchitectureFactory.ModuleRotation(_definitionId, element.LocalYaw));
            if (wrapper == null)
            {
                if (MissingModels.Add(_definitionId))
                {
                    UnityEngine.Debug.LogWarning(
                        $"§120: no model for architecture module '{_definitionId}' " +
                        "(expected Resources/HexLive/Objects/<id>) — it will not be drawn.");
                }

                return;
            }

            wrapper.transform.SetParent(transform, worldPositionStays: false);
            _model = wrapper;
            // §120 cutaway: this module's own renderers, resolved once with the
            // model. A module that appears WHILE the near wall is already cut
            // away has to come up cut away too, or the room the player is
            // looking into grows a wall back one delivery at a time.
            _renderers = wrapper.GetComponentsInChildren<Renderer>(true);
            // Bug #282: §152 content bundles are asynchronous, so this view is
            // created (and WorldObjectView.Init caches its picking surface)
            // snapshots BEFORE the model exists. Without a refresh the plan
            // house keeps a forever-empty hit surface and cannot be hovered or
            // selected in manual mode — the site itself has no geometry of its
            // own (BuildSitePile skips HutPlan), so the modules ARE the house's
            // only clickable body. Null-safe: on the very first Sync the
            // WorldObjectView is added right after this call and its own Init
            // covers that case.
            GetComponent<HexLive.UnityPresentation.Views.WorldObjectView>()?.RefreshGeometry();
            if (_cutawayHidden) ApplyCutaway();
            ConfigureDoor(wrapper);
        }

        /// <summary>
        /// §120: takes this module out of the picture while the player is
        /// looking into the room it walls off, exactly the way the canonical
        /// hut's monolith does it — the renderers go to
        /// <c>ShadowsOnly</c>, so the house keeps its shadows and the piece keeps
        /// accepting deliveries. Never SetActive: the model's own active state is
        /// the §120.1 "this piece does not exist yet" answer and must not be
        /// overwritten by a camera angle.
        /// </summary>
        public void SetCutawayHidden(bool hidden)
        {
            if (_cutawayHidden == hidden) return;
            _cutawayHidden = hidden;
            ApplyCutaway();
        }

        private void ApplyCutaway()
        {
            for (var i = 0; i < _renderers.Length; i++)
                ArchitectureCutaway.SetVisible(_renderers[i], !_cutawayHidden);
        }

        /// <summary>
        /// Wires the authored door leaf, if this module has one. The exported
        /// HL_Door_State_Closed/Open empties carry the poses; the FBX conversion
        /// means the pivot's local Y is not world up (§120.3), so the swing has
        /// to interpolate between them rather than turn about an axis picked
        /// here. Which SIDE the leaf swings to is the constructor's DoorOutward
        /// decision, and that needs the room's floor sectors — data the world
        /// snapshot does not carry — so a module door opens to its authored side.
        /// </summary>
        private void ConfigureDoor(GameObject model)
        {
            Transform? pivot = null;
            Transform? open = null;
            Transform? closed = null;
            foreach (var child in model.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.StartsWith("HL_Door_Pivot", StringComparison.Ordinal)) pivot = child;
                else if (child.name.StartsWith("HL_Door_State_Open", StringComparison.Ordinal)) open = child;
                else if (child.name.StartsWith("HL_Door_State_Closed", StringComparison.Ordinal)) closed = child;
            }

            if (pivot == null) return;
            var visual = pivot.GetComponent<BlueprintDoorVisual>();
            if (visual == null) visual = pivot.gameObject.AddComponent<BlueprintDoorVisual>();
            if (open != null && closed != null)
            {
                visual.ConfigurePoses(closed.localRotation, open.localRotation, startOpen: true);
            }
            else
            {
                visual.Configure(72f, startOpen: true);
            }

            _door = visual;
        }
    }
}
