using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime.Blueprints;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Blueprints;

public sealed class BlueprintFloorDeletionTests
{
    [Test]
    public void SpareSectorCanBeRemovedWithoutDeletingRoomItsManualWallOrHistory()
    {
        var draft = new BuildingBlueprintDraft();
        var sectors = Enumerable.Range(0, 6).Select(s => new FloorSectorKey(TileCoord.Zero, s)).ToArray();
        Assert.That(BlueprintEditorCommands.CreateRoom(draft, sectors).Succeeded, Is.True);
        var room = draft.Elements.First(e => e.Kind == BlueprintElementKind.FloorSector).RoomId;
        var edge = BuiltInBuildingBlueprints.EdgeSegments(TileCoord.Zero, 0);
        var wall = draft.Elements.First(e => e.Kind == BlueprintElementKind.Wall && e.Segment == edge[0]);
        wall.Origin = BlueprintElementOrigin.Manual;
        var spare = draft.Elements.Single(e => e.Kind == BlueprintElementKind.FloorSector && e.FloorSector.Sector == 3);
        var initial = BuildingBlueprintJson.Serialize(draft, false);
        var history = new BlueprintCommandHistory();
        Assert.That(BlueprintEditorCommands.DeleteRoom(draft.Clone(), room).Succeeded, Is.False,
            "Whole-room deletion is forbidden by the manual wall; this must not prevent selecting a spare floor piece.");
        var result = history.Execute(draft, d => BlueprintEditorCommands.Delete(d, spare.Id));
        Assert.That(result.Succeeded, Is.True, result.Message);
        Assert.That(draft.Elements.Count(e => e.Kind == BlueprintElementKind.FloorSector), Is.EqualTo(5));
        Assert.That(draft.Elements.Any(e => e.Id == wall.Id), Is.True);
        Assert.That(BlueprintValidator.Validate(draft).IsValid, Is.True);
        Assert.That(history.Undo(draft), Is.True);
        Assert.That(BuildingBlueprintJson.Serialize(draft, false), Is.EqualTo(initial));
        Assert.That(history.Redo(draft), Is.True);
        Assert.That(draft.Elements.Any(e => e.Id == spare.Id), Is.False);
    }
}
