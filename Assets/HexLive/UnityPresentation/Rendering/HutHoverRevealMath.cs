using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
namespace HexLive.UnityPresentation.Rendering;

// Presentation permission only; never changes simulation sight or building access.
public static class HutHoverRevealMath
{
    public static bool CanReveal(WorldSnapshot snapshot,ObjectSnapshot house,Faction playerCamp,bool hasLivingOwnedSelection)
    {
        if(!hasLivingOwnedSelection||snapshot==null||house==null||house.Durability<=0f||
           house.DefinitionId is not (ContentIds.HutPlan or ContentIds.Hut1Hex)||
           !string.IsNullOrEmpty(house.BuildProduct))return false;
        bool explicitOwner=false;
        if(house.PropertyOwnerFaction is {} faction)
        {if(faction!=playerCamp)return false;explicitOwner=true;}
        if(house.PropertyOwnerNpcId is {} ownerId)
        {
            NpcSnapshot owner=null;
            foreach(var npc in snapshot.Npcs)if(npc.Id.Value==ownerId){owner=npc;break;}
            if(owner==null)foreach(var npc in snapshot.Corpses)if(npc.Id.Value==ownerId){owner=npc;break;}
            if(owner==null||owner.Faction!=playerCamp)return false;
            explicitOwner=true;
        }
        if(explicitOwner)return true;
        // Legacy houses have no property stamp. They belong to our camp only
        // inside its existing private-ground disk, never a foreign overlap.
        bool ourGround=false;
        foreach(var home in snapshot.CampHomes)
        {
            if(HexSpatialMath.HexDistance(house.Tile,home.Tile)>TheftMath.PrivateGroundRadiusTiles)continue;
            if(home.Faction!=playerCamp)return false;
            ourGround=true;
        }
        return ourGround;
    }
}
