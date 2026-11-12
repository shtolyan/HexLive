#nullable enable
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Views;
using UnityEngine;
using EntityId=HexLive.Simulation.Common.EntityId;
namespace HexLive.UnityPresentation.Input
{
public sealed partial class SimulationInputAdapter
{
    private readonly PointerTargetSet _pointerTargets=new();
    private readonly List<WorldObjectPicker.Hit> _objectHits=new();
    private readonly Dictionary<int,WorldObjectView> _pointerObjects=new();
    private bool TryOpenTargetChooser(WorldSnapshot? snapshot,Vector2 mousePos)
    {
        if(snapshot==null||_camera==null||_runner==null) return false;
        _pointerTargets.Clear();_pointerObjects.Clear();
        var ray=_camera.ScreenPointToRay(mousePos);
        if(_worldRenderer==null) _worldRenderer=FindAnyObjectByType<HexWorldRenderer>();
        foreach(var person in People(snapshot))
        {
            if(!CanTargetPerson(person)) continue;
            if(_worldRenderer!=null&&_worldRenderer.TryGetActorView(person.Id.Value,out var view))
            {
                if(NpcBodyPicker.TryIntersect(view,ray,_camera.farClipPlane,out var distance))
                    _pointerTargets.Add(PointerTargetKind.Person,person.Id.Value,distance);
            }
        }
        // The same eligibility/cull predicates as ordinary hover picking.
        _pickEligible??=view=>view.ObjectId>=0&&HasContextActions(view)&&!IsBeyondSmallPropCull(view);
        _pickDefers??=DefersToContents;
        WorldObjectPicker.Collect(ray,_pickEligible,_pickDefers,_objectHits);
        foreach(var hit in _objectHits)
        {
            if(hit.Distance>_camera.farClipPlane) continue;
            var id=hit.View.ContextObjectId;
            _pointerTargets.Add(PointerTargetKind.Object,id,hit.Distance);
            if(!_pointerObjects.ContainsKey(id)) _pointerObjects[id]=hit.View;
        }
        if(_worldRenderer!=null&&_worldRenderer.TryPickWildlife(ray,out var wildlifeId))
            _pointerTargets.Add(PointerTargetKind.Wildlife,wildlifeId,0f);
        if(_pointerTargets.Items.Count<2) return false;
        _pointerTargets.Sort();
        var entries=new List<ContextMenuEntry>();
        foreach(var target in _pointerTargets.Items)
        {
            var id=target.Id;
            switch(target.Kind)
            {
                case PointerTargetKind.Person:
                    entries.Add(new ContextMenuEntry(NpcTitle(id),()=>{
                        var current=_runner!=null&&_runner.IsReady?_runner.CreateSnapshot():null;
                        if(current==null||!TryFindPerson(current,id,out var person,out _)||!CanTargetPerson(person)) return;
                        if(id==OrderNpcId) OpenSelfMenu(mousePos,id); else OpenNpcMenu(mousePos,id);
                    }));break;
                case PointerTargetKind.Object:
                    var item=_pointerObjects[id];
                    var title=_runner.TryGetObjectDefinition(item.ContextDefinitionId,out var definition)?
                        ObjectTitle(definition,item.ContextDefinitionId):item.ContextDefinitionId;
                    entries.Add(new ContextMenuEntry(title,()=>{
                        if(item!=null&&HasContextActions(item)&&!IsBeyondSmallPropCull(item)) OpenObjectMenu(mousePos,item);
                    }));break;
                case PointerTargetKind.Wildlife:
                    entries.Add(new ContextMenuEntry(Loc.Get("menu.target.beast"),()=>OpenMobMenu(mousePos,id)));break;
            }
        }
        if(TryPickGroundPoint(mousePos,out var ground))
        {
            var actors=SelectedActors().ToArray();
            entries.Add(new ContextMenuEntry(Loc.Get("menu.walk_here"),()=>EnqueueChooserWalk(ground,actors),actors.Length>0));
        }
        ContextMenuPanel.Open(mousePos,Loc.Get("menu.choose_target"),entries);
        return true;
    }
    private void EnqueueChooserWalk(Float2 point,EntityId[] actors)
    {
        if(_runner==null||!_runner.SupportsNpcCommands) return;
        var controlled=actors.Where(id=>_runner.CanControlNpc(id)).ToArray();
        if(controlled.Length==0) return;
        if(controlled.Length==1) EnqueueOrder(controlled[0].Value,new MoveToCommand(controlled[0],point));
        else
        {
            foreach(var actor in controlled) EnsureManual(actor.Value);
            _runner.EnqueueCommand(new GroupMoveCommand(controlled,point));
        }
        DestinationMarker.Show(SimulationUnityMapper.ToUnityPosition(point,GroundMarkerY(point)));
    }
}
}
