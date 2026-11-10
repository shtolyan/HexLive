#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.UnityPresentation.HutTest;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class BlueprintFloorDeleteRuntimeTests
{
    [Test]
    public void DeleteButtonRemovesSelectedFloorSectorInsteadOfItsWholeRoom()
    {
        var host = new GameObject("662 isolated actual constructor"); host.SetActive(false);
        var designer = host.AddComponent<HutLayoutDesigner>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var draft = new BuildingBlueprintDraft();
        BlueprintEditorCommands.CreateRoom(draft, Enumerable.Range(0, 6).Select(s => new FloorSectorKey(TileCoord.Zero, s)));
        var spare = draft.Elements.Single(e => e.Kind == BlueprintElementKind.FloorSector && e.FloorSector.Sector == 3);
        var wall = draft.Elements.First(e => e.Kind == BlueprintElementKind.Wall);
        wall.Origin = BlueprintElementOrigin.Manual;
        try
        {
            void Set(string name, object value) => typeof(HutLayoutDesigner).GetField(name, flags).SetValue(designer, value);
            Set("_draft", draft); Set("_selectedId", spare.Id); Set("_selectedRoomId", spare.RoomId);
            ((HashSet<string>)typeof(HutLayoutDesigner).GetField("_selectedIds", flags).GetValue(designer)).Add(spare.Id);
            typeof(HutLayoutDesigner).GetMethod("DeleteSelected", flags).Invoke(designer, null);
            Assert.That(draft.Elements.Count(e => e.Kind == BlueprintElementKind.FloorSector), Is.EqualTo(5));
            Assert.That(draft.Elements.Any(e => e.Id == spare.Id), Is.False);
            Assert.That(draft.Elements.Any(e => e.Id == wall.Id), Is.True);
            Assert.That(((BlueprintCommandHistory)typeof(HutLayoutDesigner).GetField("_history", flags).GetValue(designer)).CanUndo, Is.True);
        }
        finally { Object.DestroyImmediate(host); }
    }
}
}
#endif
