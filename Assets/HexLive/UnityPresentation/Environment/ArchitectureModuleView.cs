#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §120: ONE architecture module of a building, drawn where the simulation
    /// put it.
    ///
    /// <para>
    /// A building is raised as N independent world objects. Each carries a
    /// single <see cref="ArchitectureElementSnapshot"/> with its own delivery
    /// bill, so the house goes up piece by piece instead of fading in as one
    /// monolith. This view owns exactly one of those pieces:
    /// </para>
    /// <list type="bullet">
    /// <item>the model comes from <see cref="BlueprintArchitectureFactory"/> —
    /// the same loader the §120 constructor preview draws with, so a module
    /// looks in the world exactly as it did on the drawing board;</item>
    /// <item>the placement root stays identity: the BUILDING frame (the owner
    /// object's view) carries the world anchor and the six-way footprint yaw,
    /// and the module adds only its own local offset and rotation;</item>
    /// <item>revealing pieces is
    /// <see cref="BlueprintArchitectureFactory.ApplyStageProgress"/>, so a
    /// half-delivered wall shows exactly the sticks/boards/rope the colony
    /// actually hauled in.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArchitectureModuleView : MonoBehaviour
    {
        private static readonly Renderer[] NoRenderers = Array.Empty<Renderer>();

        // One warning per missing definition id, not one per module per world:
        // a plan with ten roof panels would otherwise print ten identical lines.
        private static readonly HashSet<string> MissingModels = new();

        private string _definitionId = string.Empty;
        private GameObject? _model;
        private Renderer[] _renderers = NoRenderers;
        private BlueprintDoorVisual? _door;
        private int _sticks = -1;
        private int _stageTwo = -1;
        private int _rope = -1;

        /// <summary>
        /// §121: the module's own renderers, for the marker object that carries
        /// its simulation id. Cached when the model is built — hover picking
        /// asks for this every frame and re-walking the hierarchy per tick was
        /// the shape of the old per-piece garbage.
        /// </summary>
        public Renderer[] Renderers => _renderers;

        /// <summary>
        /// Creates an empty module root inside a building's frame. The frame is
        /// the owner object's view: already at the site anchor and already
        /// turned to the building's footprint yaw, so the module needs no second
        /// converter of its own.
        /// </summary>
        public static ArchitectureModuleView Create(Transform buildingFrame, int objectId)
        {
            var root = new GameObject($"Architecture module #{objectId}");
            root.transform.SetParent(buildingFrame, false);
            return root.AddComponent<ArchitectureModuleView>();
        }

        /// <summary>
        /// Whether this view still hangs in the given building's frame. A raised
        /// building is a NEW owner object (the site despawns), so the module has
        /// to move house with it rather than keep drawing under a dead view.
        /// </summary>
        public bool StandsIn(Transform buildingFrame) => transform.parent == buildingFrame;

        public void Sync(ArchitectureElementSnapshot element)
        {
            if (element == null) return;
            EnsureModel(element);

            // §120.1: a roof panel whose posts are not up yet does not exist. It
            // is not an empty frame waiting for leaves — it is nothing at all,
            // exactly like the §52 build-site with nothing hauled in yet. Same
            // answer when the model is missing: never a placeholder primitive.
            var visible = element.Buildable && _model != null;
            if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
            if (_model == null) return;

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

        /// <summary>§129: the door leaf follows the simulation's door state.</summary>
        public void SetDoorOpen(bool open)
        {
            if (_door != null && _door.IsOpen != open) _door.SetOpen(open);
        }

        private void EnsureModel(ArchitectureElementSnapshot element)
        {
            if (_model != null && _definitionId == element.DefinitionId) return;
            if (_model != null) Destroy(_model);
            _model = null;
            _renderers = NoRenderers;
            _door = null;
            _definitionId = element.DefinitionId;
            _sticks = -1;
            _stageTwo = -1;
            _rope = -1;

            // Slot geometry is blueprint data: it is written once, when the
            // module is staked, and never moves afterwards. So it is read here,
            // when the model is built, and not re-applied every tick.
            //
            // A parentless transform's position IS its local position, so
            // handing the LOCAL pose to the factory and then re-parenting with
            // worldPositionStays:false keeps exactly those numbers as locals.
            var localPosition = new Vector3(
                element.LocalX,
                BlueprintArchitectureFactory.ModuleLift(_definitionId),
                element.LocalZ);
            var wrapper = BlueprintArchitectureFactory.InstantiateModel(
                _definitionId,
                localPosition,
                BlueprintArchitectureFactory.ModuleRotation(_definitionId, element.LocalYaw));
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
            _renderers = wrapper.GetComponentsInChildren<Renderer>(true);
            ConfigureDoor(wrapper);
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
