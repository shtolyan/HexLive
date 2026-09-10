using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Content
{
// §54: engine-free ground geometry shared by headless placement and Unity.
public readonly struct GroundPilePose
{
    public readonly float X, Y, Z, Yaw;
    public GroundPilePose(float x, float y, float z, float yaw = 0f) { X=x; Y=y; Z=z; Yaw=yaw; }
}

public readonly struct GroundPileBounds
{
    public readonly float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    public GroundPileBounds(float minX,float minY,float minZ,float maxX,float maxY,float maxZ)
    { MinX=minX;MinY=minY;MinZ=minZ;MaxX=maxX;MaxY=maxY;MaxZ=maxZ; }
    public GroundPileBounds At(GroundPilePose pose)
    {
        var radians=pose.Yaw*(float)Math.PI/180f;
        var c=(float)Math.Cos(radians); var s=(float)Math.Sin(radians);
        var cx=(MinX+MaxX)*.5f;var cz=(MinZ+MaxZ)*.5f;
        var hx=(MaxX-MinX)*.5f;var hz=(MaxZ-MinZ)*.5f;
        var x=c*cx+s*cz+pose.X;var z=-s*cx+c*cz+pose.Z;
        var ex=Math.Abs(c)*hx+Math.Abs(s)*hz;var ez=Math.Abs(s)*hx+Math.Abs(c)*hz;
        return new(x-ex,MinY+pose.Y,z-ez,x+ex,MaxY+pose.Y,z+ez);
    }
    public GroundPileBounds Union(GroundPileBounds other) => new(
        Math.Min(MinX,other.MinX),Math.Min(MinY,other.MinY),Math.Min(MinZ,other.MinZ),
        Math.Max(MaxX,other.MaxX),Math.Max(MaxY,other.MaxY),Math.Max(MaxZ,other.MaxZ));
    public bool OverlapsXZ(GroundPileBounds other,float gap=.01f) =>
        MaxX+gap>other.MinX && other.MaxX+gap>MinX && MaxZ+gap>other.MinZ && other.MaxZ+gap>MinZ;
    public float RadiusXZ => (float)Math.Sqrt(Math.Max(MinX*MinX,MaxX*MaxX)+Math.Max(MinZ*MinZ,MaxZ*MaxZ));
}

public sealed class GroundPileProfile
{
    public readonly string Id;
    public readonly GroundPileBounds Single, FullBounds;
    public readonly float TargetSize, RecenterX, RecenterZ;
    public readonly bool Garment, CenterVisualXZ;
    private readonly int _layout;
    public GroundPileProfile(string id,GroundPileBounds single,float targetSize,int layout=0,
        bool garment=false,float recenterX=0f,float recenterZ=0f,bool centerVisualXZ=false)
    {
        Id=id;Single=single;TargetSize=targetSize;_layout=layout;Garment=garment;RecenterX=recenterX;RecenterZ=recenterZ;CenterVisualXZ=centerVisualXZ;
        FullBounds=UnionBounds(garment?1:GroundPileCatalog.Capacity(id));
    }
    public GroundPilePose Slot(int index,int capacity,int objectId=0)
    {
        if (Garment) return new(0,0,0,(objectId*73L)%360);
        if (_layout==1) return new(0,index*.001f,0); // proven equal-XY leaf layers; no yaw/jitter
        if (_layout==2)
        {
            const float gap=.01f;
            var width=Single.MaxZ-Single.MinZ;var height=Single.MaxY-Single.MinY;
            var row=index/5;var column=index%5;
            return new(0,row*(height+gap),(column-2)*(width+gap)+(row%2)*.5f*(width+gap)-.25f*(width+gap));
        }
        var columns=_layout==3?4:(int)Math.Ceiling(Math.Pow(capacity,1d/3d));
        var rows=_layout==3?2:columns;
        var pitchX=Single.MaxX-Single.MinX+.01f;var pitchZ=Single.MaxZ-Single.MinZ+.01f;
        var layer=index/(columns*rows);var cell=index%(columns*rows);
        return new((cell%columns-(columns-1)*.5f)*pitchX,
            layer*(Single.MaxY-Single.MinY+.01f),(cell/columns-(rows-1)*.5f)*pitchZ);
    }
    public GroundPileBounds UnionBounds(int count,int objectId=0)
    {
        var result=Single.At(Slot(0,count,objectId));
        for(var i=1;i<count;i++) result=result.Union(Single.At(Slot(i,count,objectId)));
        return result;
    }
}

public static class GroundPileCatalog
{
    private static readonly Dictionary<string,GroundPileProfile> Profiles=new(StringComparer.Ordinal);
    private static readonly Dictionary<string,string> GarmentPrototypes=new(StringComparer.Ordinal);
    // Filled only from the safe GarmentDropFactory measurement fixture. Unknown
    // prototypes fail admission and preserve inventory; never guess their size.
    private static readonly Dictionary<string,GroundPileProfile> Garments=new(StringComparer.Ordinal);
    public static readonly float MaximumRadiusXZ;
    static GroundPileCatalog()
    {
        foreach (var garment in GarmentLibrary.Defaults) GarmentPrototypes[garment.Id] = garment.PrototypeId;
        // source-sha256: 78063675fbdb8c90e5250c3445fe74b0d5ee61619c931bf36092bcad2ba4162c Assets/HexLiveContent/RuntimeSource/Objects/resource.stick.fbx
        // source-sha256: 1df11d6dd5a7b9940baf0827272a49d8119c29b2b4e8b8d819d490fd2423d704 Assets/HexLiveContent/RuntimeSource/Objects/resource.stick.fbx.meta
        // source-sha256: 57d8a8109e482dedd43ae95a9f8176a959a0393e27bb12e1d69e304555294d1e Assets/HexLiveContent/RuntimeSource/Objects/palm_frond_native.fbx
        // source-sha256: cc40a17c5197d43cc662756630c3b52cbe7d25b99d55e97d630c23a22c3f99e9 Assets/HexLiveContent/RuntimeSource/Objects/palm_frond_native.fbx.meta
        // source-sha256: 56bea6a6b8cc56f42d4095c1c89f3f3a9e7c87f0b4d320b18986c4cf6ebe0e49 Assets/HexLiveContent/RuntimeSource/Objects/food.coconut_open.fbx
        // source-sha256: 7f411acc230a473f6feabd95c36eaa764c42cde322c54922358eb81edf1efbdf Assets/HexLiveContent/RuntimeSource/Objects/food.coconut_open.fbx.meta
        // source-sha256: 1a287bc99922baedff50adc3718e3945d29761945996c39ea6c8e845eb2f710a Assets/HexLiveContent/RuntimeSource/Objects/resource.palm_crown.fbx
        // source-sha256: 8b60f77ce5ea371145c0a0d15364d95659300741324f1b72221f424b68ba4959 Assets/HexLiveContent/RuntimeSource/Objects/resource.palm_crown.fbx.meta
        // source-sha256: f364eaafadd4da6b242f93c521de35b93d6002e615fc695947ae82280b416e44 Assets/HexLiveContent/RuntimeSource/Objects/resource.palm_crown_small.fbx
        // source-sha256: 89a655e6294bc9ac9d9a81e99fb8123f60c422836cdbee4621849aa8e8db2b6b Assets/HexLiveContent/RuntimeSource/Objects/resource.palm_crown_small.fbx.meta
        // source-sha256: d104f15f57b7c9e4bffe31401e7b53e357d040f13da980180a190af2266bdd61 Assets/HexLiveContent/Prosthetics/prosthetic_arm_mechanical_l.fbx
        // source-sha256: a819d3d600fa879cd1919a84c3ef575b837600f9fdbc6cfe6e96bfe07cf2412c Assets/HexLiveContent/Prosthetics/prosthetic_arm_mechanical_l.fbx.meta
        // source-sha256: 1844aff9894fa599f0d8e906d53c124bb25956c154d3954def39e6bafe43c2d6 Assets/HexLiveContent/Prosthetics/prosthetic_arm_mechanical_r.fbx
        // source-sha256: a5342b31469ad8acc984b623b3987e9702c6f0b1afda9240f5016bd39bad66dc Assets/HexLiveContent/Prosthetics/prosthetic_arm_mechanical_r.fbx.meta
        // source-sha256: 51ccfff32062424de3f816a629da50d67cf981d1c15077c05cfedc4068c1be1f Assets/HexLiveContent/Prosthetics/prosthetic_arm_wood_l.fbx
        // source-sha256: e72a5fc120db3cebf935d0e556cad62ed1b075e2543030845412a346b9d4ea23 Assets/HexLiveContent/Prosthetics/prosthetic_arm_wood_l.fbx.meta
        // source-sha256: 28de63af5b1d16e88f6cdd7831267d072d27d937baedcef09abec453738e9871 Assets/HexLiveContent/Prosthetics/prosthetic_arm_wood_r.fbx
        // source-sha256: ebcd0ac1aa42c88714661597db2a7969583ce710379d9bbb31fcf8f4a3f89ebf Assets/HexLiveContent/Prosthetics/prosthetic_arm_wood_r.fbx.meta
        // source-sha256: 4464d03206ec54793ce4a1e20135f5c477bc656cb711a20f9d8e605a3f72637a Assets/HexLiveContent/Prosthetics/prosthetic_leg_mechanical_l.fbx
        // source-sha256: 994ed2e92fbcba9a1c7af88c0779915b07784b725939aef587ff2f98e1479398 Assets/HexLiveContent/Prosthetics/prosthetic_leg_mechanical_l.fbx.meta
        // source-sha256: ffbeb92f79940be652c4511d40a58ff5d2d8c78870e8910e9091f06596dbe9ca Assets/HexLiveContent/Prosthetics/prosthetic_leg_mechanical_r.fbx
        // source-sha256: a4a0133da1fcee295e3425a869d2148bc25f8bf42b9d0d80aeb0d97da4c4f5f2 Assets/HexLiveContent/Prosthetics/prosthetic_leg_mechanical_r.fbx.meta
        // source-sha256: a69dcc59c4ed737c6353179210e47bcbf40466c0f7f9132b7669d726d4e2d787 Assets/HexLiveContent/Prosthetics/prosthetic_leg_wood_l.fbx
        // source-sha256: 5a9649dd75a0ec39eb52ba8c40e4b4cfa146ea09d0e53f758b2209ee0e99a55d Assets/HexLiveContent/Prosthetics/prosthetic_leg_wood_l.fbx.meta
        // source-sha256: c2e49bbe7367332fa91e4546bc3e032d2f5d71a7d5d98fa925b5759d701805a5 Assets/HexLiveContent/Prosthetics/prosthetic_leg_wood_r.fbx
        // source-sha256: 486303e07b262c63c2a1822eec8c8790b9166c149e38323b6ec97de3ab85b8d5 Assets/HexLiveContent/Prosthetics/prosthetic_leg_wood_r.fbx.meta
        // Explicit production generic-fit branches only. ObjectFit normalizes all
        // three local axis extents to <=T. Orthogonal base rotation therefore
        // bounds each world extent by sqrt(3)*T. Presentation recenters XZ and
        // seats Y before applying slots; source pivots cannot escape this cube.
        foreach(var id in new[] {"food.coconut_open","food.meat_raw","food.meat_cooked","item.bandage","item.pill","item.plaster","med.splint","resource.arrow","resource.board","resource.cloth","resource.fiber","resource.herb_leaf","resource.hide","resource.log","resource.mechanical_part","resource.rope","resource.stone","food.coconut","food.coconut_pierced","tool.axe_stone","tool.bottle","tool.bow","tool.hammer","tool.knife","tool.lighter","tool.machete","tool.pickaxe_stone","tool.saw","tool.spear"})
        {
            var target=TargetWorldSize(id);var extent=(float)Math.Sqrt(3d)*target;
            Profiles[id]=new(id,new(-extent*.5f,0,-extent*.5f,extent*.5f,extent,extent*.5f),target,centerVisualXZ:true);
        }
        // Measured coconut half: source .248302788 diameter, .129195194 height,
        // fitted diameter .18. Preserve source importer basis, no yaw jitter.
        Profiles[ContentIds.CoconutOpen]=new(ContentIds.CoconutOpen,new(-.09f,0,-.09f,.09f,.09365636f,.09f),.18f,3,centerVisualXZ:true);

        Profiles[ContentIds.Stick]=new(ContentIds.Stick,new(-.525f,0,-.038896749f,.525f,.079794095f,.038896749f),1.05f,2);
        Profiles[ContentIds.PalmLeaf]=new(ContentIds.PalmLeaf,new(-.4125f,0,-.211292874f,.4125f,.158479951f,.211292874f),.825f,1,centerVisualXZ:true);
        // Exact source-vertex distance to joint anchor, multiplied by production
        // actor/bone scales. This radius survives any importer/factory rotation
        // and both L/R sources. Keep the production off-centre joint anchor.
        Profiles["prosthetic.arm.wood"]=new("prosthetic.arm.wood",new(-0.341170174f,0,-0.341170174f,0.341170174f,0.692340348f,0.341170174f),0f);
        Profiles["prosthetic.arm.mechanical"]=new("prosthetic.arm.mechanical",new(-0.341139913f,0,-0.341139913f,0.341139913f,0.692279826f,0.341139913f),0f);
        Profiles["prosthetic.leg.wood"]=new("prosthetic.leg.wood",new(-0.420520689f,0,-0.420520689f,0.420520689f,0.851041378f,0.420520689f),0f);
        Profiles["prosthetic.leg.mechanical"]=new("prosthetic.leg.mechanical",new(-0.420338469f,0,-0.420338469f,0.420338469f,0.850676938f,0.420338469f),0f);
        // Real PalmCrownFactory V3 bounds; these branches bypass ObjectFit.
        Profiles["resource.palm_crown"]=new("resource.palm_crown",new(-1.611852f,0,-1.638496f,1.611852f,1.868458f,1.638496f),0f,centerVisualXZ:true);
        Profiles["resource.palm_crown_small"]=new("resource.palm_crown_small",new(-1.359147f,0,-1.49779f,1.359147f,1.868458f,1.49779f),0f,centerVisualXZ:true);
        // Generated by Tools/author_ground_piles.py from safe factory bounds.
        // source-sha256: 99ea2d3fc64766b9214736c8768621c202836ca557d6e5fce705fcae8097e49a Assets/Editor/AtomicContent/AtomicContentBatchBuild.cs
        // source-sha256: 6922b5b9e16b3a9d4c4aa279a49fcd69aa71071f4eebe87e2c95cf76d8847c6c Assets/Editor/AtomicContent/AtomicContentBatchBuild.cs.meta
        // source-sha256: 48fa61891f9257342dd9b1de87dc09f5d0cc336cdb31bca29432db0ab25fe8e0 Assets/HexLive/Simulation/Content/Garments/GarmentLibrary.cs
        // source-sha256: 3bff4d480a70260bf679e32af2376a376658812c58890a08f9dce14070750c63 Assets/HexLive/Simulation/Content/Garments/GarmentLibrary.cs.meta
        // source-sha256: 2982a1bca1920d27d8d6c2c8a9f167b22f698ce525ec8dd16e12da6b779d7083 Assets/HexLive/Simulation/Content/Garments/GarmentStorageCategories.cs
        // source-sha256: 3bb108dfa334e6e6d20af4773f94771ab3113c64a884fcc739bc3ee6f6c91c09 Assets/HexLive/Simulation/Content/Garments/GarmentStorageCategories.cs.meta
        // source-sha256: f8971b4d7ba297810bc1765412150088b0131da9f38c8b92faa9a501fba32003 Assets/HexLive/UnityPresentation/Rendering/HexWorldRenderer.cs
        // source-sha256: 71cb6fb932e425bf9b8c027ac2981d43acb1c2d7a445078c0a4acde9816166bf Assets/HexLive/UnityPresentation/Rendering/HexWorldRenderer.cs.meta
        // source-sha256: fc1b73c9cdd6f68add00444ddaa1cf42083609898ace18996714cffc03cbe577 Assets/HexLive/UnityPresentation/Wearing/ActorWardrobe.cs
        // source-sha256: 5f53bea8fd401de494e968cfde9f6736ed08aac3abf901c5341a64f876cf61c7 Assets/HexLive/UnityPresentation/Wearing/ActorWardrobe.cs.meta
        // source-sha256: fff013cf88c3c04e80c3dd370f897711fb9cbda9bee2838fc64305d56d6bf9c1 Assets/HexLive/UnityPresentation/Wearing/GarmentDropFactory.cs
        // source-sha256: 5a071b95fa6c7e9c9ee6af1dc8f2c9e8360142409fd6fe8743025bd98299e806 Assets/HexLive/UnityPresentation/Wearing/GarmentDropFactory.cs.meta
        // source-sha256: c62b02ba64abc3afa9530ce0e305a7f0e22cc3b5daa775aa3614638f34e4280a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_osiris.asset
        // source-sha256: c30a67299da337d36e2558620eeb245ed536f6baaf22e8a4ceb67130f2649641 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_osiris.asset.meta
        // source-sha256: 704ec1536713f1a342e84244d4133bf1b72fe7f37faf5c1865a742efcd8328e5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_riot.asset
        // source-sha256: d9a6631049d72bd1c978feb355d5ffcd1fee3c61ba056fe8511ff9d7d74b7511 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_riot.asset.meta
        // source-sha256: 7abc3bd42f346dcef24bb27a4a6d90d2bcbda2aa440262ebcef6655aa5a6f7cc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_riot_denim.asset
        // source-sha256: 0b4a96cb6cb0c8518161c9fb9729c642dc8878dd752cf67b574e06586b052e73 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_riot_denim.asset.meta
        // source-sha256: 1206b18a51bea7f27da458181b35c598456c66f2de6d7a1107842087f6eb4d32 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_riot_grey.asset
        // source-sha256: ff52669418efa4721e32202700cc813089f4f033b90b4aa19057790178946f42 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_riot_grey.asset.meta
        // source-sha256: 1f0cd1f469cce94725730526c5df6d5b9a3870bb9c0825fc4013f567d68c053f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_riot_leather.asset
        // source-sha256: 3bc0cc606305eae4e4db1874b2d9e23f18a73c603077e179024f8b7c0622fc66 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Bags/gear_backpack_riot_leather.asset.meta
        // source-sha256: 6d1a0e6e8fdbad83fa66d821d8cd8355924dac4796e19ce68a87d494712244d1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_ankleboots_amy.asset
        // source-sha256: df235991e9c700604bfde09b86e0f8634b4855a3acaa901b092297543d05b78a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_ankleboots_amy.asset.meta
        // source-sha256: ff4401612f0588d1bb59cc1490b204dba1306b79066fee0688513c892d930c7e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_anarchy.asset
        // source-sha256: 5fef672353d9a6cecda2cb0e5d16ad5b2a2a6c660ea94bc2bdb8fdfd237f3d69 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_anarchy.asset.meta
        // source-sha256: 2c0b20db05ce9e2922b1bb308e019cb7039c4441f72d225bc8061e81c3f5cde2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_cindy.asset
        // source-sha256: 124613120bdec3738e90ef08c6a08aae8d39fd4cdc7121a57605de4b827260a2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_cindy.asset.meta
        // source-sha256: 8550125277cbf01ffa34a25213b4e2cfbf8d3356aa1f8965d75a6dff7fcefc00 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_fighter.asset
        // source-sha256: fa557e03c1a4d6dc129fe147e28048252866cd2f748d31a02ca821cd03eee82d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_fighter.asset.meta
        // source-sha256: ab1384977207f4f2cfc3cde01ede6ea31b0ad5b0626b59ba849af355d755cb61 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_holster.asset
        // source-sha256: ca9501bc6333720f457bc8746a42c664918412a492b6098b39263b033ceaa709 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_holster.asset.meta
        // source-sha256: d623ad3a6c61a9b648a1274408599aea6775c3c3a08183cebf96e738ebda3911 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_stars.asset
        // source-sha256: 3f8844a07da55868e8c0875d68e213c34540d7f56f9f0c5f52a1fd20facd576f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_belt_stars.asset.meta
        // source-sha256: e5870c2195e82d8449ab3d07a3bd8a5ec1a46fbc5edcad4a465d68901c7096e9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_riot.asset
        // source-sha256: f1e7f38c166cdbbbe5d93d095233698001c94f7e8c349c675021794a321a74b7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_riot.asset.meta
        // source-sha256: 3ec785f7314e8f1d6d0173de6b755caafb17fd756c2753d0c46f5a116093f3c7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_riot_denim.asset
        // source-sha256: d029672f889d91f7d86056159c8e9574b82e2f8303d11d7e5ddacb0f7f0f75bc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_riot_denim.asset.meta
        // source-sha256: 9cf0d3601b60420bb978caf254f81636e04ddeef930ef6c28f30a41330138395 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_riot_green.asset
        // source-sha256: c911cdf36bb82419ce0e33958dfe080003fced55c0f3176790441c270b591645 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_riot_green.asset.meta
        // source-sha256: 28db24ba94576d8964ac2571dc193f442f12aa603e6d5b2e2de6ad2146a9fd4f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_riot_squaresclear.asset
        // source-sha256: 8114f3092787ce71a2b0f9ec862dcaa8076d521aebe817a162f90adac71dcdd5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_riot_squaresclear.asset.meta
        // source-sha256: aab42bf64b252933fcecb886ff8fcb14032500c08afba29e20b31113a53b23af Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_waist_riot.asset
        // source-sha256: b0a3f9c5819239084c8292d5059fa8277c0394929320b646ded9890d93da2f10 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_waist_riot.asset.meta
        // source-sha256: 0c2e086a8f32057e8dd4065c0605a85f88d7ab09d89eded9665cb42a354b356a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_waist_riot_denim.asset
        // source-sha256: 8892f9974d4d2be2de126fa1ec552371c678fa7b8ebf729a03f50b2c7d808c28 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_waist_riot_denim.asset.meta
        // source-sha256: c51f15407996007b1d62c6e53811bdef741719fcd3a660b87b52323dff5d38c1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_waist_riot_green.asset
        // source-sha256: 008f7106cc758e40218e4a3be05e4b5b0488a3584b535cc344a0c7136dbf9d1e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_waist_riot_green.asset.meta
        // source-sha256: e404f8b45b20f02f1c386c94c46105ee3f90f8b06c1741b6c9e1f580815e2b83 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_waist_riot_squaresclear.asset
        // source-sha256: df78ababac0995b2498ac21086ab9f422ee47e219497eb07457efc16373120ad Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_blouse_waist_riot_squaresclear.asset.meta
        // source-sha256: 063a965b77169a172fadae8dec67ccacbccc6fd0ac021c133a11637bab408428 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_alloy.asset
        // source-sha256: 8b861eff6db3aadd20bac8e53e8ad3b976487b977c5dc9c70d89af9a7d8eb67e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_alloy.asset.meta
        // source-sha256: 6df9f6db9e7da7e77a9881a3006aa8bd0e2b6b1df32d0daf794ded6c2c04ec2c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_anarchy.asset
        // source-sha256: 7a4739d1f02ee81d45c6622f5c3a2073b318efee069a15dd878170d2354e94df Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_anarchy.asset.meta
        // source-sha256: d1dc49862b7ce579dd11e1ef4705e17c3e2dbd0690a923cfb407f4f7d0a353c8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_anarchy_purple.asset
        // source-sha256: fa9fe412d7d39f9e787587b43f7857ad88b4acda85038a55494a0bd58b5033e1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_anarchy_purple.asset.meta
        // source-sha256: c482753b9b3dc1c31d1d10b5e10089dbff1ba0a4486a38ea6ba7529ee9d5c56a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_anarchy_red.asset
        // source-sha256: 76f528adbffbd3789cdf7036088bb5eb3b81d90a12dd8a0ea4454e9894061a87 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_anarchy_red.asset.meta
        // source-sha256: d69e57db1cd1ea2f719526db089303d827bd0099cba66fded5de4562dcd39fdf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm.asset
        // source-sha256: 32371d74d9d3858fb4df3305c6c14af2a3edd96c9b255907d76506f33befd24b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm.asset.meta
        // source-sha256: 71580f66ac9a7421a6111338c9eec611aaf210fcc1c72c0c8d02a59f3f78fe68 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_blue.asset
        // source-sha256: 9f108c01cbf6d38540717d2bdc0817d1dc956d9110fb54104565957c0a63eda7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_blue.asset.meta
        // source-sha256: 7bb4ecac2a5efe2410ab3367c6860e52cc0dcb701236dd42497ae55a6aab1705 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_grey.asset
        // source-sha256: ea0bb929d0a4f987ca9d9b52c0d37ca6de78fc60cb59892ee7144a66016a6668 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_grey.asset.meta
        // source-sha256: 6cdbd130dbd5e07ed211b1c479d1faa9962d2a10de0d514a46e6b78e6ece4529 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_pink.asset
        // source-sha256: f0ff06a745d8d4b8ddc49dd39f8a113ea2185e9c40194935465ccf052b83bc20 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_pink.asset.meta
        // source-sha256: c1dfe5cbb92d5fa845928ac0a3e504d4db97711abbea4bdb669193079bed408b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_red.asset
        // source-sha256: 111afd7b991cd7830c64b18077eed8393fc6a76a031194a1ea3b1f57b3ce4ed8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_red.asset.meta
        // source-sha256: 3c6ee157da7cc84458fe939848cb911b09003f2ab5422647884ab6fd0f8f23f4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_white.asset
        // source-sha256: cf20e963be3d2e3bb28f309e43a63618b8eb2d3f1dc4c22a1f419f8cd8bb4311 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_latex_white.asset.meta
        // source-sha256: b24456a07ec453285da0bd40d19b91ad8324298cb8379d351f0aead586c8aa5f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_beige.asset
        // source-sha256: 20dafb5ad18cf9e2e9b3784f5b1e654ca5d42d049ecb2a278c8ebaa38ea4f54a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_beige.asset.meta
        // source-sha256: 2170cb237d583ab0288ce3ae6156858eba4d24e5419071f3bd4cb2ab0e1e69f6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_black.asset
        // source-sha256: 0430be196ceb2ded48363d42c13b7a0e68026cecc2eeb319e3da98146ea5a304 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_black.asset.meta
        // source-sha256: b7d08fcae613c14c9d5a0edfc56208f139727dbd46c49ce6ce0e186d2112b3e2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_blue.asset
        // source-sha256: e7837ead461831403eda66933194d00a425156c0d1149ce353b251d4c716bcdd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_blue.asset.meta
        // source-sha256: 35928b4904ba38ab822c070cccdb01d2c2a142df495f59e346d89fb6f03fbaf4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_brown.asset
        // source-sha256: 427775386d3ae6c46826704d23535282d0c1092479a99c7b3e203045e19bbc81 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_brown.asset.meta
        // source-sha256: a2423523bd6d27dc790750c09c29b546aaf5863a7021a1c8362e04cb302b834f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_grey.asset
        // source-sha256: 2204d7567b17fe8d026b3421867848bdb78b9899b37e07d66e3f02024a8f64ff Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_grey.asset.meta
        // source-sha256: 069afa0fc33a3f32bb66d36d437957b2c45613bc775415bb65935f05cf0cc181 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_red.asset
        // source-sha256: bdd67630cd63f4ee322dc73770480a72927d7c51d1da067d670db31c41fe75fe Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_leather_red.asset.meta
        // source-sha256: b83753b51f3f02995bf04896d0a33506095ded294f39f8f9f2263bac9d5244ce Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_beige.asset
        // source-sha256: 0bd3e1cbf7a8249e5e85c605f9db2e1819da3dcc2eef72af8d002ebd14744a70 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_beige.asset.meta
        // source-sha256: 46146c93e00dc9b866dd7279aa3c006f383977ac69b6b3d7c540fc0f813bdc27 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_black.asset
        // source-sha256: 624ae270ac75cf443bd727f71b6597dab03a8e7689a218172e73663381480645 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_black.asset.meta
        // source-sha256: 43146cc6ff9b654d368d1ead5319a957d5d9f7666cbf52c5693425bf3726f904 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_blue.asset
        // source-sha256: aef09b9d2e621d6db3440ea5b5b980eb016e2b7c8ec104bf2d265c8f90633385 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_blue.asset.meta
        // source-sha256: f8e6a143da7f5bfb56fb6844d508613ed6ce692762e68756e6a681066b33b83b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_brown.asset
        // source-sha256: 2b956e576a7b3ee28bcfc0e8066467282a826101a36d6fd9f43ace56b59917d6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_brown.asset.meta
        // source-sha256: 05daea46a61210a34ce8031d604a90c827dfdf5360d7dc0e5135e9afdff4c051 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_purple.asset
        // source-sha256: cd29a3ee71e94eface722b82d73f21e13f7ddb93e19a3b12886af6213a705edc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_purple.asset.meta
        // source-sha256: 5b454e94dff277824566dc27c84d46302663c7d173ceaa81cd47f11ec0f97f5d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_red.asset
        // source-sha256: bd70710d694a29daefcf70bb131de69481348ab0c2ff23a5e300d1955a72f18f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_charm_velvet_red.asset.meta
        // source-sha256: 4cd92adaec651e17848f90648ae3af5a2d2bd126b56fa64feaf258b4b0d6857e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_cindy.asset
        // source-sha256: 988a0118c99738f206e622fff36d4eb4bd5def5f2141dbb0d482a7a15b212a98 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_cindy.asset.meta
        // source-sha256: 417e45ec7a0f3f43e9be7ed602bb4984986a5ef6b1453de4b7ce4463aa719448 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_classic.asset
        // source-sha256: 204cc3cd61774ddbbe98614b0674552717051a774a99969ea221138100dd860b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_classic.asset.meta
        // source-sha256: 922e624c4f8a9a4b1b98239d6146f840f72e2a5b829cae3e951ce188cc65b95e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather.asset
        // source-sha256: 2d432d64e8e6579e8d8ee499b20f2fdaba3b1c583df5c78d33e254d4b235fa50 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather.asset.meta
        // source-sha256: d1996b99331ab8e9ab9a5b5da4549941c95b35bdda1141dc2c6fd58c2aeab7b4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_black_red_leather.asset
        // source-sha256: 76e793ca446d6abdbc2dd206bb13b2a4875bcef3537f8546fbc6342b402842cb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_black_red_leather.asset.meta
        // source-sha256: 4fc60c83c9adc5cf73da96021ea6f400bead323d3d78266014c92c1361d7a926 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_black_white_leather.asset
        // source-sha256: a13a2d6e763b25f391ea4e79175b0ab138b2ad27d47f3fb91b7fdff56bf121be Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_black_white_leather.asset.meta
        // source-sha256: a04baa47dd3b12ad23079afcae7bcfcfb36662a8e53fb29ef18c1555c1702d44 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_black_yellow_leather.asset
        // source-sha256: 8a97fb7cd57b6ab23ba172e6b6f2645b80c197d49c4e42e81aafe37f95c804c3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_black_yellow_leather.asset.meta
        // source-sha256: 67a8e90668986e875f231381646b85bcdc2ea01c1b856632d79bdc93cbeb93e7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_red_black_leather.asset
        // source-sha256: bf675313763569659e30dee43412f42046f66c5003b90bee7369165377bbe446 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_red_black_leather.asset.meta
        // source-sha256: 5bcc3b51c8939ce3e74a1b84bf8d69547d3edcea4fd0a8e9acec0398e6158211 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_red_leather.asset
        // source-sha256: a2a7ab85c0b17f21fa7e90053c0a86e8ad6aa21589f44201e350ad638ec8f663 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_red_leather.asset.meta
        // source-sha256: 02dda965d8fd7559eaa7e52ca3c427c080a28d59ab5515a3ac06dbe30cebe334 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_red_white_leather.asset
        // source-sha256: a9fe7ff47969f2d937478eadf9ffe94a258deaa6d53cd84d287b6ccaf7c4c866 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_red_white_leather.asset.meta
        // source-sha256: 77e62bfb60a98e3e5f327362c17b5624867259e4e90c68481e9c05003cdf7e00 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_red_yellow_leather.asset
        // source-sha256: 2e9e2f9dd51af3c18e4cdc904f833f673a1d8eeed27d6607a67fb384a22d9aa8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_red_yellow_leather.asset.meta
        // source-sha256: 55eb20e5c3d69b83aeed6f42000ff443be016ab56c693da4308229ff5c2bd331 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_white_black_leather.asset
        // source-sha256: a4020da68df876d5ad31cd53dcb0150b7096be77ccfe544125262b6a7b8b22d6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_white_black_leather.asset.meta
        // source-sha256: 86d4d42c8aec29b7be52193df023a83ac008aa9a106e8be90aae698f3dc074f3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_white_leather.asset
        // source-sha256: 25a25df5a747f20ba885539103da61a6ae64679083ee4f7aae5089bc4a585b86 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_white_leather.asset.meta
        // source-sha256: 1948f5ff4b8147fa934c8432512b1ba57d4e42e93f04523f9577b9c013872c54 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_white_red_leather.asset
        // source-sha256: 9ab867f799d2591bc0b52e263279c4863e9c50cfe411cf7a8a0a30ebd524bf71 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_white_red_leather.asset.meta
        // source-sha256: 1c45ab36daaeddc4906e6f60d608a3c12f787e24b37e5cec0311a1228c4ee4c2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_white_yellow_leather.asset
        // source-sha256: a67a5c07f41a338ba7f1aedb56258d0d82dc94f2b7f59dbb0d2f5751285da358 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_white_yellow_leather.asset.meta
        // source-sha256: b227bdb95765537b7d42d218cce42eca1345cd5d8b95f46866dd8310e984ef57 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_yellow_black_leather.asset
        // source-sha256: 386e2526a73ece2355f0b126017c523537c7c9c398e9dcc787af2d6e2c93bacb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_yellow_black_leather.asset.meta
        // source-sha256: bb3999592b589037f76958b3924d726e42588cd52f0cee77c030e980d73c2522 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_yellow_leather.asset
        // source-sha256: 22a84f3a8e48481f017891938224485bf79beea5ec1edaf4097c7219128aa08a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_yellow_leather.asset.meta
        // source-sha256: e3a4fc3087e9b326d3e2fc9b696a7e1b36570759ffd24d74b4bbfab5d1ba6fcb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_yellow_red_leather.asset
        // source-sha256: 2c49aabf6b8ca25eae3af3c35dad6922a5c2b378eea4a4f4d6acef30b26d7b6f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_yellow_red_leather.asset.meta
        // source-sha256: 248d6dc9ca96fcd7c14ff9e91873ae3f8248a25a3dcaa1edc60291c85c34951d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_yellow_white_leather.asset
        // source-sha256: 3114499d94dd5fbe29e2ee1e8a1c491a63b825adcd805b2f7c427aa9a0cc5d39 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_leather_lb_yellow_white_leather.asset.meta
        // source-sha256: 1d97f86af73a7c45165ffdc046071a814af96a6d60718db9c66d440d04aa03de Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_osiris.asset
        // source-sha256: 7c8924170410da0ffe7d895f2cf5c25c39f96c7ed0722a2401700f2f6ca71b42 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_osiris.asset.meta
        // source-sha256: 81fcad2805ef60b60c56bbfba9259ab508e6fcca1fbd8f3881890da916a03efe Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_ranger.asset
        // source-sha256: 4332b9363c5f4fb6760be1a427f29f01b9323338ed2f5d954d091a84df804184 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_ranger.asset.meta
        // source-sha256: 0387d7aebb536a916993345ff4d9585ec58a53cbc9768e640ca13b955c81c070 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_riot.asset
        // source-sha256: eea93b63817b46bb08c1c0d8168be40dde416f489d90b99d2b75a39d9fbde013 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_boots_riot.asset.meta
        // source-sha256: e22d71fcef9f07608bdb2b09f650d1ed15f6824da553d4dec0ca4db27bfa3d21 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur.asset
        // source-sha256: 27dc00ccc0211ab89e5c2775c385ce28750697e642d92e894fb254069a5999fa Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur.asset.meta
        // source-sha256: b9126d3364cc69176b484d33b38df2c27cf1e37d5dcb5ccdaca072cc9ba8b5cd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color01.asset
        // source-sha256: ce780a8e49d56f72f1299b78ae2eb97c9479e3d5457671d824493be72042a46e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color01.asset.meta
        // source-sha256: 89eba2428dca5a33d29ec7958aae25220d5547e68fb044854af49d34041c64f3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color02.asset
        // source-sha256: 72b4a2b1f8e5f3e12e14ef376f3aa7a22911cba8dbc846edfd254d54c0a325aa Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color02.asset.meta
        // source-sha256: 94e91f791971ff84eedc5ff8bc2e4f075c864e7721fb78f2ae55fa4eb667f6fd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color03.asset
        // source-sha256: 41860759ecd98348e427e786f5eb80f6623ad659f25e94d3e83133d8986dcabf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color03.asset.meta
        // source-sha256: 1d6c306cc5e83be8f5e2441dd84fedc24ce2a8445f5f9cc95b8d4a44f7f29225 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color04.asset
        // source-sha256: dd3cee42668ec95218d77eb76e37a95e5a8af53756173f1b1d5f3c9adb725c7b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color04.asset.meta
        // source-sha256: 1d3e3ee68a6d28b61223f9d6d2150818815893c29cbccede3336477eb18cc9f1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color05.asset
        // source-sha256: 0fe9c93a2c62b72b592256ac2a52c80ad58c5f37f6c9d98121a24b0eefce65f0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color05.asset.meta
        // source-sha256: 0bf4ee3d9ea783ddb5bf9cd2ef46f9ac9996709264c6ff819985af77859ae77c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color06.asset
        // source-sha256: ce8cfc115b05ed2470ad1b76022c51728ed069e72477b3e0827f8ffc1dc60103 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_collar_fur_color06.asset.meta
        // source-sha256: af30a32aa032c872b6ca1b2e6b1ee2f2a6849b0a5352a6802722a89241accaa5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_footwear_fighter.asset
        // source-sha256: 845d504571859cf2df7680085b199d83beea0aa2dcd65826a25f5cd48a308bcf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_footwear_fighter.asset.meta
        // source-sha256: f9c3841fb6d9b08409c26bcd344bc82c8c509771ede43dbc9dab642cc45faf2d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_glasses_nerd.asset
        // source-sha256: 3ca2eeae4c1f40a946565a0a1507d89c8d664c5030d9cd2487fb95481fd15dde Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_glasses_nerd.asset.meta
        // source-sha256: 7002d85e2b03ca9f3438731e8d5d36a7446f1d3b76a95ea7ff871d53fe981abb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_greaves_tod.asset
        // source-sha256: 2cc19ac823a9a2a6ba704216c9fe086757d3b856b993e49342c0586cb60b8b13 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_greaves_tod.asset.meta
        // source-sha256: d68628a11bc067dd88db92b8d8a096c9319da08afae5a60cd74db86709297053 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury.asset
        // source-sha256: 1f0aa80c2787686f054f72b9fe5dcdf2dcff378d3f9efc0d0b203e0e90c413a0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury.asset.meta
        // source-sha256: db58d9fd804847c8c0d5b930a12c117536ab8ecf45ec14b7721ce4ab2de94dfd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury_golden_color.asset
        // source-sha256: 3c15e93d7a04e0ac8b051382732b54853326ba92ce809af91abf7814f444a769 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury_golden_color.asset.meta
        // source-sha256: b871b57605247363b6802355530e09fedde174aae0f7458daf9302f0ecf7f4f8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury_light_pink_color.asset
        // source-sha256: 84d4c098ec445540e2b656494eeb83a96e196c5406b8fba6b6b5ad9bfa1689e5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury_light_pink_color.asset.meta
        // source-sha256: a0a7df93852f60bd374aac611b46bd5edce7ed6e1cef3bd19f21099c2e024214 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury_silver_color.asset
        // source-sha256: 27a9f82b1f854ae35d33b853748584b20d81a1282075b50b5bc007facf32b7e1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury_silver_color.asset.meta
        // source-sha256: fa15ace81d6cd84e963c92229229f68ae4328f07a66adb0dea3c59f071ba2261 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury_white_color.asset
        // source-sha256: 020c79710654b5bd34bb148ffadcba4c4572a4f19858c3170977e370c58f112e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_heels_luxury_white_color.asset.meta
        // source-sha256: 62c206500dc6d90d7862ca3ad79a423833e3da3ce8623ad2bf450abc95ab9da1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_bull.asset
        // source-sha256: 472f5cecd29aa84fb971051f42351d158acad7a7a6cc76fc56a80e67f736df08 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_bull.asset.meta
        // source-sha256: 1c626ffe79dfd4881df40a84b15939b84bd17a97aa280b8bd089173fa7aa56f4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_carbon.asset
        // source-sha256: 157cef7f9e5035d086089a7c96da3ffbf66765ce0b9814d42bdb5e1dbbd541ab Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_carbon.asset.meta
        // source-sha256: 4d23c26805f606929285242729e7608793d77e8da7995c4e1b031069861f2a61 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_knight.asset
        // source-sha256: 5a1218a8a1d257980794fcd98d176d08da983d9210e5d6c0c7347352395a1169 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_knight.asset.meta
        // source-sha256: b22cf75d0c7e6d67237b518d993582470277ec368c22ff0fab068228ab56a8d7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_m1.asset
        // source-sha256: 0a789bb33879278f7d4b2d34961a6c487e8f998d56adebf9bdaa0a4c149970e5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_m1.asset.meta
        // source-sha256: 711b609c3652dcdc1c00879791f769ab18a78c45f4123f87bc7d399ac76bf6ff Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_moto.asset
        // source-sha256: 3129da50dee822b5b0740016e1bdcfdd5cdd682caf4dc26a3649ed7d72b6fe69 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_moto.asset.meta
        // source-sha256: 6fd47419d5cda6ddef94b424fefb867219c27f5c5f7cdb1c2d92fb92194fdee7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_racing.asset
        // source-sha256: 156fdd4b105ef4daae6334a05f6c2aefdc2a42d823ad1059f308e0dc8c0edc77 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_racing.asset.meta
        // source-sha256: 4cba3d8a4c2a2c5b12e7af0f06deb216f27b6475f6c87f36666b1192e0ba51fd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_retro.asset
        // source-sha256: 0255e0adc176807481f7823692f8c47238815a99bcf6eb9911a0a5df18ad7678 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_retro.asset.meta
        // source-sha256: e892b56c0245b7762f8f8225412ab98d46fe8abb13354b832c829ffdd849687c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_space.asset
        // source-sha256: a5959cc1674349130ae874c85b4ac51f66884880e88d94e1d9d20f01b2bcfc7f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_space.asset.meta
        // source-sha256: 9cae035968aea4c7103f0f275e3d351fade42f172045198bcb3dd90877003cb8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_t1.asset
        // source-sha256: 69fe4af773fda05ab41120c601c92b2768f46127a1bbc38e64c565a0fb5cfc45 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_t1.asset.meta
        // source-sha256: 4d8329abef611ef017110b88f564d9968a4bc95276c5618da7415bdd36a4570e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_tactical.asset
        // source-sha256: f0e7a45ba63db9a5219d55510a1d1d18bbb010154cba6957f91b35d9ec1f08e1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_tactical.asset.meta
        // source-sha256: 28354e4e953e70d205fde57c180d009074a758c131f1fb96092cfe9078b75e1c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_tactical_headset.asset
        // source-sha256: f074b8780e480033d8a55d88f37f8ea1fcfe336cb8e8ae794d47946896938ef9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_tactical_headset.asset.meta
        // source-sha256: f9cb4a28544b7cb5c84aacdbfa276dd62fb9b0858a0e417de507bcadf5eac991 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_vietnam.asset
        // source-sha256: 4f6fc6e30106afae33c07863a6625097b4c1d8b48ec05775aee92001fb753a4e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_vietnam.asset.meta
        // source-sha256: 8ba093bc26afe8502f8503b7c061b6e232173e51528048ce2566399198c427b8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_vintage.asset
        // source-sha256: 67ff229463b9cb466ffaeb266abd18bc816632ae419549e7a9ad927d08cf8e30 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_helmet_vintage.asset.meta
        // source-sha256: 8d7394d017d0d83f87fd80ceb831019960435fc7b656dc859717b232fcd19447 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_hipbelt_anarchy.asset
        // source-sha256: 0342fd1ff3a5110b6e1ed5eae6fb5fa628835e04dc0f7d24ac5bbc611806461c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_hipbelt_anarchy.asset.meta
        // source-sha256: 3bb1990a9e1507ac2d645ab5a1459a0d7ad55d4307d421fd00bd634d60476ebe Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn.asset
        // source-sha256: da24abc8f2ac1099fb6aec1ad7537b6c1eaef3f1e5789aede0132b5b6da9a240 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn.asset.meta
        // source-sha256: c56e7f6c036723ceade626f47923698963504154395e4e8edf87b0604e0a8dba Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material01.asset
        // source-sha256: 22e871652a8c8452cde5efdddd7cb76175bcfc8de0e87dc4f5f799a1c2078dd6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material01.asset.meta
        // source-sha256: 72c13e9217b6cc30ad0ab12b87c484b430c535335e6eaf71cdc230bb186afb57 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material02.asset
        // source-sha256: 84180d203801b0bd4879e4470d371609b63b3a74a0e535efb7897c7f7031d67b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material02.asset.meta
        // source-sha256: c201c58b7018e25e83650602192c36ba66c828a13faae74eda371587dbb29ed9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material03.asset
        // source-sha256: 46cbe1e55e42f764395a388af60c7f068f4d2cc96b689ad7579c5ed38c2e0291 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material03.asset.meta
        // source-sha256: 3853d6078db74bf1fd206181ae82b754b9bffbba00f776f717ff5374d1371884 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material04.asset
        // source-sha256: de8cf6454d4938dad78863e02e67b6b905eaa86da1f3c9763bd6651f4bbab483 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material04.asset.meta
        // source-sha256: 813c2d8de9fbcabdd8b037154451bf0b91855805e8e0e44ee1f34327e45a423b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material05.asset
        // source-sha256: 19b3731f1e15a66a214dc8282fef784cdc1b088927f40394a4874622474e0685 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material05.asset.meta
        // source-sha256: 47b896636b7ce5090f9ab9a5ed6e98794de284a5c4835ab5f7b87ac98e60a463 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material06.asset
        // source-sha256: 173e464dc9622d27a9644b1ff9db094f9fe29906878c4958311bc48855282a28 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material06.asset.meta
        // source-sha256: 434972b4c2e71187e3ba8b155ab153b0a2fcc6b7804b9f381c21a520cfab5ac2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material07.asset
        // source-sha256: 52d9562711885f7d00f562ec9c5e97ccd7f835ecef5c7285dcd709e1652c4fa5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material07.asset.meta
        // source-sha256: dd290ba0e50fa070eba4e7d82dc7b75b835074ed8f6711eba294186a6a233bfb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material08.asset
        // source-sha256: f0023b2f8d20ea79d9e5390a4c0f4dd2f0a9e87e4da52d106a1e59e7fb40565c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material08.asset.meta
        // source-sha256: 70d5d0489dce3f687f32eeb0c82e0458a60f3673041383e88c41a02eee8a9688 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material09.asset
        // source-sha256: 7eae695f58c72aae278d8e645d3a82c81befde006d0f524f10c3280e7d43eafc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material09.asset.meta
        // source-sha256: fb9e7dff94aeb3baaa2752e4ff500cdf138c981dc8671141b37ca5be6f872071 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material10.asset
        // source-sha256: e89b3bd82d896613c784258e4a9ceb7d9c54a28b8d0284365b26329ec37f9856 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material10.asset.meta
        // source-sha256: 563ed50e1b47405a8ebbc047cbba061ef3f036b3ba30702a22bcc1c508933a41 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material11.asset
        // source-sha256: 05cda6c606938191308fe4bc5b96a72fd7b14acb656fe9413d0c211bd76ea6ec Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material11.asset.meta
        // source-sha256: 12509b01b43f5dd584b59888a2ceeb56121087e5af94b91ebfa84e9cd3eec401 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material12.asset
        // source-sha256: e0af4a1791c5cf9c49a30f0b116378349337dd3e83b68350c81199e100c5a460 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material12.asset.meta
        // source-sha256: b17c776cdc3d8d803d70a2958ef3de899b1a78c723cc7041c997f1a863614c0a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material13.asset
        // source-sha256: 778ba088f5931f97160a2ba2d52263f58b0e9fc8539add7c4f5635beae3543f6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material13.asset.meta
        // source-sha256: cbcef2185d4ddd04c5ee8985d963c14c31f0465baf578fc1c4fb2b43fc7da49d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material14.asset
        // source-sha256: 14502c7e8554b74e4b40a76b2c9e004a41172f067de34ba47de8bb19fa5a59c8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material14.asset.meta
        // source-sha256: f3c5dee0652306b13335b4a50ff392f17b8cd9a389711ad4c9e23ce166ca4004 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material15.asset
        // source-sha256: 862c67a84b25e025f4e8a4378e935f7683ea2e61b3974dcbc76821b160c3c08b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material15.asset.meta
        // source-sha256: c47f6ac088d27a67ae9ebb7450e1a35b94a1f11079e4f61091aef49892bf3ba6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material16.asset
        // source-sha256: 0a928f69e26e685f73810a768d8cbc8b6e66330f8a74b63e08a2fe07785e90ea Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material16.asset.meta
        // source-sha256: 1ea92908adb485f2bcee4cac750a0618f7bc0fe32e15d308da975b81a4a283d2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material17.asset
        // source-sha256: cf45b3be46c65e8903aff899a53a750f571633ad88b190964e87ccd430d59f5b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material17.asset.meta
        // source-sha256: fb5edb8d339c86046d1aeed0a57d2044c4c1c52ee5a36310815a353c8a8f6b99 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material18.asset
        // source-sha256: d5b4d79f990a46157d3187e9b93001c40e937620c85e8ccbc0bcd42b88bc9c7f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_autumn_jacket_material18.asset.meta
        // source-sha256: 4fe3a37ba4fd06560aa17d56d295db4dfd1a8ad96699c577b0d0c6c8956a328b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_biker.asset
        // source-sha256: 807fceeac6994fa13b11af3bf0d0bec6e42651da0184b47dc037dd261faf697f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_biker.asset.meta
        // source-sha256: c69b59e254d9fb30f64461246cae56279ad09211ab89487204961e1bad0f2761 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_cindy.asset
        // source-sha256: a8b61caaac3eeeb5c1ae681e793ba466bfeaafe16ae6f10384edf9db388385eb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_cindy.asset.meta
        // source-sha256: ffa5603e0edbba48cd63140628c3dbd1a49a71e200ff8a8bbf569434530cc085 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_ranger.asset
        // source-sha256: 8b579befd93ca0962c8ea6175b4912e054c88040ff81cd1ce58b280216d69bb2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_jacket_ranger.asset.meta
        // source-sha256: 67612c726cc0d91599f32686260a56ad692f66dc50f64b433814f89831cf8889 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_pumps_flair.asset
        // source-sha256: b65987d59cfbffe65a282e182617481c835d77d74763dbe0ecc3c53edbd75b8b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_pumps_flair.asset.meta
        // source-sha256: b0849c5d50d64e02dcf59c8b651932c99f9eba0b115672f4b8090e2e72892631 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1.asset
        // source-sha256: 1eb96771e7d247c0271f5ce57621aa57258fc7eb831c5c9a534696cb4bf765d4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1.asset.meta
        // source-sha256: 6642f6676b78446082b306055e7e987d6ced359b62cf88a6414ffadb78a12d93 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_black.asset
        // source-sha256: ecc62dc8ffb6149fb927373689abcc00dd4539b48f924b9a12d4d4e1b1539f92 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_black.asset.meta
        // source-sha256: 1401fb2baa77e88aeeef3cc08bda077f0cf3c27c6f97871d4266d585eefef303 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_cherry.asset
        // source-sha256: 40695d23c46781aa4605b9e619cee882b0a365452e5dec8b634964bc5606c186 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_cherry.asset.meta
        // source-sha256: a0b3635182f2b8ec46bd7bb9734e4f2c2f40eb453ece8dee114cd9f14ed45b76 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_red.asset
        // source-sha256: 0d95e0a007b6481e0264b271543d008df86667f38c4ebc513ccd1d7252a206f2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_red.asset.meta
        // source-sha256: d8d3ffaeb036bf22a578c54e41c32a314433f9a3f1198eb855e7ef3b0656e04b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_salmon.asset
        // source-sha256: 8bfc352f533db38dac438e1e41a3b8cb944b8f431fc88d07a214ac564db8d93b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_salmon.asset.meta
        // source-sha256: 63fbe12acf85b18b90aa8739b6b632250f95235de6b73f01ad85cea4a3fd718c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_silver.asset
        // source-sha256: 6ba9eed6028ac25843b39df34e682432297d8f6349a7e2dc12c40ed0355e190f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_silver.asset.meta
        // source-sha256: 2341973177ee97248a682b835087429306b5f33e044099bc215c8926985b7aa3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_sky.asset
        // source-sha256: 17c6e8be19ec4fe2e866617096610c12acaad535c0c6a8776b9649ec6bf72ecb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_sky.asset.meta
        // source-sha256: 93ca738be4517f2505adedf6906b283201c9b9e9e2c1c619cf5fed735ad0c5b0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_tan.asset
        // source-sha256: 861c3568a348cb2533efd8a1debc53f958552a6f4cb43d712c4fddf6c4b61d22 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_tan.asset.meta
        // source-sha256: 0e665e9e57b60f49f503c132e1bba2f69da61ddce178477d289080401136e411 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_white.asset
        // source-sha256: 5d49e7216fbe1df9d38a532880be3d92f97232eba737752b09c1f7e72668424f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_white.asset.meta
        // source-sha256: 795dc62140a2645c31615aa16fbe2c44b67249b2527457f4dbaf855be984ec53 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_yellow.asset
        // source-sha256: 808e7557f468933c0f0c9ac70f8640ab1ea981e89b849aaf6dbc42e42186e689 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer1_style_01_yellow.asset.meta
        // source-sha256: a60dd85a9622db11f51f2716356c54ef38a216f95667f8b13d447efc159b16fb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2.asset
        // source-sha256: aa97ea5077d2327c9d0e1694879afd0e7e8e723e7b72dd123156db5057ac0a51 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2.asset.meta
        // source-sha256: 199093ff1bc7a805e3e852c872ac97e21670dc54db308bfa99fd5faa1a884ea4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_black.asset
        // source-sha256: d4c18dad2855bf22dc26f73b7c0d54d6b545a8a233896f952e347e00f20f0bfc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_black.asset.meta
        // source-sha256: 90fabf654034021000170ca2deed737071e1a4f47008e23c1dd5b43210a5a459 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_cherry.asset
        // source-sha256: 55381feece2d3b870163e83dcaa6b6b66e7f9b6c4758c260945152a68c2e6610 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_cherry.asset.meta
        // source-sha256: 281fbb9d58e15c40b927df2da6625e7859c70d3a023fcb52a1e9fe116a66bf6d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_red.asset
        // source-sha256: 556c8f772e607a5db50fd253d72103dbe179cd596694170700a81194abb4dd32 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_red.asset.meta
        // source-sha256: a956d13a4fe8c800f4c8a9f0525b7dd9a6f7c0bd43c78780c945d8ee0db7b9cc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_salmon.asset
        // source-sha256: 918d49747c8dd2edf89f6b891e8cdf536748edfcf00b0e153951928c3a448261 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_salmon.asset.meta
        // source-sha256: 13eedf4f86b31f77d76ebe0be0a3536cd1380c077633f6559c6ac53afa7def60 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_silver.asset
        // source-sha256: facc4ad13da33bdb597b6c50dc3eb08564b26c52ff125d8e1648df83c5deb99c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_silver.asset.meta
        // source-sha256: f797e77397b98c89515181e6fc3a22fda78f60b64f2a2fe6e27b08fd135ab207 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_sky.asset
        // source-sha256: 59e5b88a895163090cbb56602bacc15818069300cf12ecec8d1567dd39392585 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_sky.asset.meta
        // source-sha256: 816c26ec8571730de2485556934de40c521dac62989a81a22f63af2154d48225 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_tan.asset
        // source-sha256: 872a139766fcba900017eb8f0405c34e36a620c2e617240d9034bb3d6d7060d1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_tan.asset.meta
        // source-sha256: 6bf68d969999cd0ca48bffe9c29e1fd8e77ab79fe945a1c801c79003c11651f0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_white.asset
        // source-sha256: 0308060ae592c256dbff2d1aec22d77849addcab68dfad5e31ace6bfbb3aa02c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_white.asset.meta
        // source-sha256: a2bbefd359f47c5db43954fe03ad90b43c9182258dbff44fc3469afb7c839c7e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_yellow.asset
        // source-sha256: ddeed7677791ad19cba4c4c345707d13ed86d030a519712b3f2199f03758ca7a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer2_style_02_yellow.asset.meta
        // source-sha256: 0407af119f925a0abaf65852ce2437f46eb18a002fcfcced9b209053feb179e7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3.asset
        // source-sha256: 14f69a27e0b1f772f9dce767614a47836ac7a63110533291961ab5fba6396643 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3.asset.meta
        // source-sha256: 16f2301c0c9fed434a79bd94acfdb32f6dedb92e093b57f47973fd809048f414 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_black.asset
        // source-sha256: 6b58e2753ed3271f54faf2fbe42f0bcd3936a312ca3725622df4fa8f81b5286c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_black.asset.meta
        // source-sha256: 8750e484a296ca58aeb0517d64d613742697e9981b69be7b66ccf7b785013c33 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_cherry.asset
        // source-sha256: d37c61c14f1bcbc292a48fa8f2e68739b2561c6d117e8f56ef2a675732487928 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_cherry.asset.meta
        // source-sha256: 518e5808336dbf4767004af74f19b0e9915cb5796f185899be57389a100e65ed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_red.asset
        // source-sha256: 01edf1705c5eedaa81405010f04b92d0f6e5030380ff50d90d93ff7bf434f62e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_red.asset.meta
        // source-sha256: f1d20395d38a73d4b85a0c9c962e57b541d11fc359b9ba9f12ceed85e05ffb96 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_salmon.asset
        // source-sha256: 2cbb7d1b36b9fd0d0803cfcd606df3a7fba855b0a40803a398a2e7ae97e27d2a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_salmon.asset.meta
        // source-sha256: 3d52932c3d534df5799e4cb25b042475e92013a1db033a9a4f1e861397971268 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_silver.asset
        // source-sha256: d59d4c437601709e09c29f4bd43ec611811f1605e1fb8f552d7adbfb78df54cc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_silver.asset.meta
        // source-sha256: 6f45a1d4d2ec005c37e3f7e2c34348b7c700607b52ee5a6e66f2ef38737a28b6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_sky.asset
        // source-sha256: a0d6c4f9dabf20a27b57d17f3adaf9c7d98a43f09718d53a80b3f44e334dabab Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_sky.asset.meta
        // source-sha256: 8a56f06267f75f37d7caed075391e6f50c17d6841d630af5f51d8fe0a4850d45 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_tan.asset
        // source-sha256: ed4ef4381c47d13af86d4a50e0a7d767372fb0f209a6937766611854ee982d9f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_tan.asset.meta
        // source-sha256: 994d2f717163ab30200d9ea8dba2d9a28e37376cbe750a873757fd5259c20632 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_white.asset
        // source-sha256: a290bc7994bb158ba1a4f1b96f533929e14797d8e1758853ec1edae82f0433bd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_white.asset.meta
        // source-sha256: 35fe3e9904aee6fb15422e53f244856553a21c32b5f3f470cfebf471e17ccff8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_yellow.asset
        // source-sha256: 6e6e24a652639b3ab3377e8f8569974bb4ecb71dfcbee6dc70c164c3ade58350 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer3_style_03_yellow.asset.meta
        // source-sha256: 11456d63b4ab035ef3905a03ff39f3ab7334950e7d2d7e5be7841717670e524a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4.asset
        // source-sha256: 9043abc81c659cd9545c67cce9b480129aa9a7e6ee5232e906d4685e18fcf0f4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4.asset.meta
        // source-sha256: e83241d560f2e696440f20d626849cb051c5508e115350918680a7733f4736e9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_black.asset
        // source-sha256: 23716f90b1957a2c2ff54d6ebffad09604504915c7e7eb232a4711dcff116637 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_black.asset.meta
        // source-sha256: 73269ad0d8dfbfda4aea5ea8d34555170614de5f0d23a00a3cf4b9baebb63c53 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_cherry.asset
        // source-sha256: e931c39d59d1e025f69d8255954062199a65d4c78ef7e942f8f2e00aa7a80f2e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_cherry.asset.meta
        // source-sha256: 80fa44a24a6a3c5ae198bcd0f5e2528e5554e5eb98e8aa78b15a8bbed996f833 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_red.asset
        // source-sha256: 1c90dbe5aa650ca6df86132c45b36147f0ff389de9b51bfe0d0526926c02315d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_red.asset.meta
        // source-sha256: be846d2e2cf49f059e644332fa3b2b2f638a6a85767db66dca9b170324646c0d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_salmon.asset
        // source-sha256: 509bdd5e3da27b254d458d2804129d0ba6dfc34add50472ac91e89be3151f71c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_salmon.asset.meta
        // source-sha256: 59bd88b498f1803eb54e09edbd3e5b01d9a7f3f5aab75db418870ad04cd72380 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_silver.asset
        // source-sha256: 6acb93ddab33faa094dcbf0c25ac9870f9799da047a44c6e7f7309d90601b6ab Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_silver.asset.meta
        // source-sha256: e58fa9c1b22799343c0b2b4964c3165514206f116adf53d9d0181c3ffaea1501 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_sky.asset
        // source-sha256: 834f83955a1401d171013d4eb0dd970e77c0fe3250b2eb44c031694ab5135be6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_sky.asset.meta
        // source-sha256: 4b8fdfcea273d18ce7551fb4acf9981060eaab357d279944f82e73471afb99ed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_tan.asset
        // source-sha256: 6a21217a2008d25af4f65669bc601a33efc5569eca26865da59f0647d961c20d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_tan.asset.meta
        // source-sha256: d0314b9816b8cbbe273ee03c764f8caada34e1b893293f5fe462ddb75e4d366d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_white.asset
        // source-sha256: fad068c02ad06d031648f116fa67c347eaf9b18bd01908728b7e7dff016551c3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_white.asset.meta
        // source-sha256: bbc21fe613412b781fd0e878ccc21facc34409b85c156e36128a6c1cb998da8d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_yellow.asset
        // source-sha256: 794bbbeb08c620927cbc92f4ed86d050e3f5491acf6cc481f232fee188ff0f51 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sandals_summer4_style_04_yellow.asset.meta
        // source-sha256: 974cd1a00c068c49d27f935c3cbebadedbd63f8dd8f5c24318f02995b90ccd28 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_shoes_tod.asset
        // source-sha256: 6bea36ec47079988a26860c628294d6bbcffa9dc7ac269c200ae7ad47535bb1a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_shoes_tod.asset.meta
        // source-sha256: 21e4a0d16058e7747b29c205d715d1cc44b168853011cbf5de2f549656aac59d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads.asset
        // source-sha256: e53566fa06e4dd7249a5f3076f83f5e263649fdda71299e85dbe0f38a3ea58a9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads.asset.meta
        // source-sha256: e688af2b7fbee820876fd8b66dd43b274cfa42765535ebc1a1719cfdd12d4483 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_checks.asset
        // source-sha256: e08fa3117e6762d39193a9627afbcefc78f048d7d34cb8e86093e25a460b8e28 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_checks.asset.meta
        // source-sha256: 5ba202a2b63ce9c909777318a54c3c8f1bcc0dbc045854cc8e2c3c023b56704c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_graffiti.asset
        // source-sha256: d7ce21489118001f177acdbd465984dcc358634d7dccf328987a7e2bbb6261fd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_graffiti.asset.meta
        // source-sha256: ad97e833a467141b34b203367f1e206e70d9adc0e40c3e3f026d3ad79417bf88 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_ladybugs.asset
        // source-sha256: 97acfba97696dd872e0cdc28f353e0df16f1d05cfae5c81312e92dacf4df3e59 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_ladybugs.asset.meta
        // source-sha256: 9ff1ce4c5a3bf2f18f16ce679c73509f257b74f8a122de53667fabd7eddbe655 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_navy.asset
        // source-sha256: 6bf247f7688cea5a5924a7f4bf5c291da58e2b7c19b701240a7ff43fd1c18ebd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_navy.asset.meta
        // source-sha256: 20ebbd7d7360cd0ee0921aab79cda5ad0c8e591f2d76deec5465a7d5fb1c3a9e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_pink.asset
        // source-sha256: 61993e573d38c1c4ea609d201f3c7c3351c6345417242307c2a091e83379c912 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_pink.asset.meta
        // source-sha256: 29255dfd00bd87c0caaf943c46e0660c2d89fd42494d0ea9dd2f9bf4b32f901a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_plaid.asset
        // source-sha256: 8777da026dd6e4a715e960f7f30a158bedb90f3a585ed9fb959b0994100180f3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_plaid.asset.meta
        // source-sha256: d42883720a12ab8d27efb894f3abf09870a43dec24084f272fbb9a643d20f477 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_planets.asset
        // source-sha256: 4e15750a54c4ed74d8978b957dc5a8db601a843234d3cc5ba58e8c4a3ec23538 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_planets.asset.meta
        // source-sha256: 60e1fa0ba47c0d751c28649eb483c0bd4bd7c2b2d29b97a1f015dff63c5d0acb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_red.asset
        // source-sha256: e8e258dc77bd4e4a44dfaa97adac25b16eaf912071d015954db6602bfbd9f782 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_red.asset.meta
        // source-sha256: c11a03094840f5818a2640029e481ea43de6e866480669de1985d90c39341387 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_skulls.asset
        // source-sha256: 209b460f3854152905af402602639623f812e75434f1e78565d3f4b01b443d77 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_skulls.asset.meta
        // source-sha256: ebc360fc360d912a1d5b3c06b8739d7953c035cea104a71399cfd98a6bd8397b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_white.asset
        // source-sha256: c623c9b243bd5af48b6fcdbde4ebf99156c60eaa02b25b5c08cbf9bbf44e64db Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_slipons_fads_white.asset.meta
        // source-sha256: 4c020510e8c2e30fd4d2758aeab706a97ac0a669028d712b6f2e625a2d49f982 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_canvas.asset
        // source-sha256: 24a2c2e593e15f6671928a6871cfeb66433c8a3b3ac03ff0c795b9c3c58983ca Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_canvas.asset.meta
        // source-sha256: b0d1bbf52556bcdeb148e30e8b75863c80be7d67a87ed7c493f7c83d08360f6b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_canvas_rs5_mat02.asset
        // source-sha256: 96b84bebae1d7350273c6acf2c975f585fd3607b84f70cf0a4da73da14e85fed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_canvas_rs5_mat02.asset.meta
        // source-sha256: a262caed4af6961e314e15d29d6575531550b46d73d0b42539c42216a3174f3f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_canvas_rs5_mat03.asset
        // source-sha256: 57033ae89f9e79e333f2a509ada3e59badbe2dd09281c02c490c6dff3d308946 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_canvas_rs5_mat03.asset.meta
        // source-sha256: 5f885a8aac26904788cfce0d3c0ca72ccccbc31c9f9f8d4b5523d030ed6bc332 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_nerd.asset
        // source-sha256: d953bf0dfa6312bf797e1375b095627cdaec5f9d77904bc7b1a04afdfe3a5255 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_nerd.asset.meta
        // source-sha256: 138e03d181bcb9ae4077925bf8865dd0504289b134fa38b85da8654bb267254a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_nerd_nc_sneakers_pink.asset
        // source-sha256: 2ece8871f50d6091cc04aefe0b2a582a17fbbb1203b9611b09660d154b0b2c4e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_nerd_nc_sneakers_pink.asset.meta
        // source-sha256: 7859b7eb28f4a0d70443032d1e201217b3f2302b125b0b581a6d867d25fd1d1d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_nerd_nc_sneakers_purple.asset
        // source-sha256: 4f4474b940b2b37255634092b357da792a9ab2fb745d846bd2328ff0b86a3d79 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_nerd_nc_sneakers_purple.asset.meta
        // source-sha256: f30956b446c2388d9dbf6336e209e125b99b60775419a957272f37e45d7a11ed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_sport.asset
        // source-sha256: ee0d490174ec55aa2faf05c437d556161b1ead8cfb55bb44c2b9210225dc2e6c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_sport.asset.meta
        // source-sha256: bec06b75678b12c1122ba26d1fb44323f0f9e9fcbf1ca375a49d6c2238abbb94 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_sport_cream_white.asset
        // source-sha256: 1c899a018778c90b75119f2313e2d8c243e4538890c18aec420a5dc44d8fd984 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_sport_cream_white.asset.meta
        // source-sha256: 93c4752fe0793e277b12b94e8171a1c7974689b3b4c4f96181df40f1688040f9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_sport_lilac_purple.asset
        // source-sha256: e72634d5e83af79d2abdb517f3996286f5d6aa180550758a042e94fe12542c1c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_sneakers_sport_lilac_purple.asset.meta
        // source-sha256: 57654a9e24ecdcac15a52eb59404ee66c9dbe950ccf657ff12e86eb672af9843 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_thighboots_amy.asset
        // source-sha256: edd3941daa0f94a5bd4c5414c60752f4ecdb3bb24f9d580bf39541d7699d158f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_thighboots_amy.asset.meta
        // source-sha256: 326b3cef0ef50c44905ee0b39ee3d48a9d67becb7a526dee50eee1336b897f26 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_vest_ranger.asset
        // source-sha256: 3275ae3fedded97c1ff9f1e15c3ee41931728fd108dfdc10d64bf1dfad873d82 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_vest_ranger.asset.meta
        // source-sha256: fe2f828f6375050dd657788f2b96c5693ebcefee3197f993f0afe6b693e5b10d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_vest_ranger_green.asset
        // source-sha256: b7bde5324f6ee87c759133e7eafe016f3619c60b6cc093103433a2f579a1e53f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_vest_ranger_green.asset.meta
        // source-sha256: 80226cea470543da56e1b8a29fe849d62dbd961bc8233de4189074fd7d1f2d37 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_vest_ranger_packs.asset
        // source-sha256: b647936ccae40a53d3a8008b0fc52d87a563bb71dc1b57ab2c25846354204aae Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_vest_ranger_packs.asset.meta
        // source-sha256: b03d10909adbd4fcf28f000a1dd8d7f1690ef2c1b6580555637609ecf9950eb1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_vest_stars.asset
        // source-sha256: 98f96084aaf97ec7adb81fc553d4d8738a048b1ff98aa3a5fc35690132df2265 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_vest_stars.asset.meta
        // source-sha256: bf11b5282921a62a090dd94b34fe530055cc1cb51b9ba3da7f25d480d7bd5eca Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_wrapboots_primal.asset
        // source-sha256: 292716d578eff7340e729f5d4caad4b91bb53908148929207163b48eeab80045 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_wrapboots_primal.asset.meta
        // source-sha256: 8b5bb3c043c1410746cc68fa57d62d556c722eaf9741be6588e038c26ff09622 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_wrapboots_primal_1.asset
        // source-sha256: 127668c19d56e810cebf4a25c9e994817a2e3a0ed898094751b46a65c6f79e5e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_wrapboots_primal_1.asset.meta
        // source-sha256: 9c8cc5fd4eb598a57eede119540229eebe46e54fe7a930f1486cb14b149a8545 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_wrapboots_primal_2.asset
        // source-sha256: f33ede775727b86c5fb8d2c828f0c18b46be853269315b2b305ab5bbef439572 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_wrapboots_primal_2.asset.meta
        // source-sha256: c395e146335a12d3e5371107b9b81803e80d8ce45e6cac67d36f5634053ef32e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_wrapboots_primal_3.asset
        // source-sha256: 1ac93ec060199441b8c4aecf2c03db0960809c6dce5cf06520749a627b251e84 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/clothing_wrapboots_primal_3.asset.meta
        // source-sha256: 4acb4dcf8e552ab01f58a121c96073e01a715ed36e8ceb3791999074c9b8f850 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fao_harness_male.asset
        // source-sha256: 5d05841b2d16d93276bf87539df90bc6e5fe01f00cb15544c3ceeeaff9954649 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fao_harness_male.asset.meta
        // source-sha256: 8152c09aa3750f39e0588464ffb8d7c43132564513574e2e3abd4220aa663cdd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fco_boots_male.asset
        // source-sha256: 4cebdab62f3bfd4c1a7c4c0a75e806cbb9f19ab992fe8f793237a6d8c3cf157a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fco_boots_male.asset.meta
        // source-sha256: ebe3b94f0c7a23bc300a2ef938b68c5f9557a4d9a4ee952f4789c7bbe2305904 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fco_knee_straps_male.asset
        // source-sha256: b611f67a4062118df42f17f0950170ac87ae2b3016e7eb28755d9c9f33899a2d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fco_knee_straps_male.asset.meta
        // source-sha256: a1dad0ca7cf19d8b9750af5a6c122d886d3d401e09ef36863fdbe9d0cd950b58 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fco_legs_straps_male.asset
        // source-sha256: 336390ba47c8bed01b4fab26fef4ddb66124b4303610a04eaba0196f93b7ae4e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fco_legs_straps_male.asset.meta
        // source-sha256: 1b4633b7344b0624fb186832b065f88e696dbb131d54261dfe5a09b7fda64494 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fco_waist_strappy_male.asset
        // source-sha256: bbdf793fe0b52904ca0b0c941b6a0d575b2aaacb6224bc5d471e7c5ea6f2148d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/fco_waist_strappy_male.asset.meta
        // source-sha256: 8a1f90bf01321221016532b6831fe02e5c957772cbfc1b1b8eb12157ea5ca976 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/gear_beltpouch_ranger.asset
        // source-sha256: ce6c14ede2ee33f580ab97a7e3836f56d1e9ed1f44c6c4a0a0437ce358d7134e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Outerwear/gear_beltpouch_ranger.asset.meta
        // source-sha256: 4e58336e957c60b99becd43445c2bbfeebe227ef42425acfd27f2c815c245b80 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit.asset
        // source-sha256: 0b74ceed310e7ea3ac29a0cc4cda91eafca4655c247cb10ff3ea876a94e6045a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit.asset.meta
        // source-sha256: 65bf029bb991320003ea30bff92ea02451d74415a5fb017c6eef997e04ce9b71 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_1_blackblue.asset
        // source-sha256: f0fce5fff69e31b8fef61a5ff57f452880436aeeb7ba6347b9614f465e53c132 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_1_blackblue.asset.meta
        // source-sha256: ede110a202d2caef2214fa6b2927077e52ea18678cf10c7bc47491032eb6c2a2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_1_blacklime.asset
        // source-sha256: 7cfd5513ae6bf781904a7a1cd570fb6ad898e5fc9c7985b5c5e92dafcf9817ff Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_1_blacklime.asset.meta
        // source-sha256: 6cb16c4495b5d7fef343985d233e79e1943f9d948ddd467076849dbb82274936 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_1_blackpink.asset
        // source-sha256: eb562eeb52301a595452a29e38a4082cb6bed2940fd7841e6a7b70cfffca86c4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_1_blackpink.asset.meta
        // source-sha256: 73d4faf16e890b80da61da61e0ebe21d52088cd79f16dcd1899a653f4696b490 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_1_blackwhite.asset
        // source-sha256: 0789ba11dd2c286d263b9e3c519916f6b8217ef47f9201c3c03020bab1042041 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_1_blackwhite.asset.meta
        // source-sha256: 7619eaec7159b8c32c1bce4b0efc8ff15e4ecc692941734b3f14837ede926187 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_2_black.asset
        // source-sha256: 41b54cfc11df4ea21fe5167b03ca670afeca235e63489bc4477e0cf54b21e84b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_2_black.asset.meta
        // source-sha256: 153211ed7ab9fde4a9fa17c66a7ef67c6b439b9c611f01af2e0994169b89cbab Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_black.asset
        // source-sha256: 055ecc42aacf4bf935cb40f533ba27b8de12a2e8e9db05a5d5d99471d34cdc83 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_black.asset.meta
        // source-sha256: 5d74f1414383da8491376a6ff472ade79bb113774fc1a73727fd16b7578f71ba Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_blackgreen.asset
        // source-sha256: bfb0bd29b4760ddc3df0d681d6cb9eea1e14bb10974ae65afab58e97e38ba096 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_blackgreen.asset.meta
        // source-sha256: cb228f3875581094e619e0f3ebc5891b803555fa9492dc50bb5c5cb9bfdfda9e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_blackmagenta.asset
        // source-sha256: 66f34cdf75affff2981081126dbb6594155cd018c6709179516a8d3de9cb8ddf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_blackmagenta.asset.meta
        // source-sha256: 9e41a6918b86cf1cccca0c9385dacc065a184b5cddcb187247dd3c8849327d48 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_blackorange.asset
        // source-sha256: 08d2dfae00ff2d7d69aac94947a64b0dd2c474504d57320ecabaac8e9dad87e8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_blackorange.asset.meta
        // source-sha256: d54e200d69293aacbaaaab045cf565e6bb42ec9f27c1e6a136e46d0be182ab68 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_magenta.asset
        // source-sha256: 6acc341c86c4a789b855b3eec1ba3bc412f28712f66039ce95f6745ef5469808 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_magenta.asset.meta
        // source-sha256: 53251182a4f1be9d94b0c531f1ce52479d020212232d471fde8bdd3d53519d33 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_orange.asset
        // source-sha256: 6df4e1d349cc8f3de380919cf526a35b7bc409f42423d1a7204eddeac6be7429 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_orange.asset.meta
        // source-sha256: 563f5b0bee05d7b925bd7128a5c0dd24c12eda944deff5207af2d4f516d5f163 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_turquoise.asset
        // source-sha256: ffaa9fb11166daca89b7af2b491b8f911b0355477743ae1450e9ad54bb423c15 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_turquoise.asset.meta
        // source-sha256: c360e8db1b779baa7c7975fc59b09c785f0b8c8253f1bc8701a72725e410fb72 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_white.asset
        // source-sha256: 674e62250db9c1f5d8e250cbc1184ab58e91c7b6a31c6de243c3808e2a9c361b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_3_white.asset.meta
        // source-sha256: 8800e79f3829662f18c79c9326b27c14026d067b208639fdce0f6e15d2682939 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik1.asset
        // source-sha256: c3c37d48a5354c9e9fa938cfe8b6087eb2c1e1c796419aaff9b97e0ed38315b5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik1.asset.meta
        // source-sha256: fcbf6b9a369edefcb15663f2245646695ebdcca07fc13143111216cf2a3df868 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik2.asset
        // source-sha256: 80a72a154c41161fc03562189d273566a34bdb6851eadba0d4ff736da6b52066 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik2.asset.meta
        // source-sha256: 792ee53c054f0c1355afcc0b34426fbbecbbc885973f930fb43d11f8745da95f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik3.asset
        // source-sha256: b31ed11aea07365495b08aaa30bb199148db92b0e8629ee9de6241e50461e177 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik3.asset.meta
        // source-sha256: d5eb8c64754ed692c932d7c6a53ae32142c6c439a8c26cc3b74ad5719158a9ec Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik4.asset
        // source-sha256: 63c30f256c0cd6fbe9f9ad9a403d52a7b6af1142fd364dca36614006ff0f117f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik4.asset.meta
        // source-sha256: 1c15e4e879f60c3af42763d715851d233d96311ee4a53b854f19279b15f14644 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik5.asset
        // source-sha256: bf47ae2a286a96a4fca5fa6c54d77bc3fde3c1f05e5c66ecd5d63b48d95d332b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_batik5.asset.meta
        // source-sha256: 83f0e13d952528e05b62e500c7a47d2c8381b0be50b61ccf09d9f9f2f359e3f8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_camouflage.asset
        // source-sha256: 8ab1845bdebd9966c9bd2c3f17b2aa6421d661b492d9ca8fae784168126f3e19 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/clothing_shorts_fit_4_camouflage.asset.meta
        // source-sha256: 99a5e38a31f567df4566b5202553636abb52215513893ce8c0379a1d2e26d4db Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/tonnyflash.asset
        // source-sha256: 8e281dd0b8521d6856c1a3e1b070dbdffb9329e25a4ffee8ee30dd35ea4889a4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/tonnyflash.asset.meta
        // source-sha256: 592d1d4f639fb73d3d43f7e74b4a8f7b70491c5f851d8ca5c6b86ee093937c3b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs.asset
        // source-sha256: 7ab89feb1e05c8368ea05c34becd33244078d9bb7b0dd85c5989b7179f533949 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs.asset.meta
        // source-sha256: a17664af389698b4a72faffac5d040d02a462ae9432e80191989604f242a029a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat01.asset
        // source-sha256: 86c65fa19dbfa6d045e15938fe1ab98e95ca50aa24cf79f82acd86bbfbcbf02a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat01.asset.meta
        // source-sha256: 2a54dbb03b8ccccfd520478a0ae6b7c50ea43ef6654571ab6c34881f3fda4d08 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat02.asset
        // source-sha256: 9250d77be54edc7a4a720dbffee3f5795440a36cab41274f47d417a5a67ccf60 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat02.asset.meta
        // source-sha256: 3d73fca8b8c599125a85e9d67ed91d330288cf61acf3bd29d8a9f2b6012a249b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat03.asset
        // source-sha256: 3dae460745b8e7295b1262516a7813cf6598fa31a0357f921bdc8a07255358b1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat03.asset.meta
        // source-sha256: d947d7eb3778a7bdab2e0e88b0e8e9817ee30bcb669e7a6e2e8a9ca70e397cba Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat04.asset
        // source-sha256: 1ebdbc84edfd995e60129ca7b84462212a5a5ef6045ea49860f7940db9eb3033 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat04.asset.meta
        // source-sha256: e981095fa5cf7afa852dd0c4ebb775a021c0539f5bdc41bca6d5a5759e454a5b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat05.asset
        // source-sha256: 0a472dc582a808e1b95331893d062c440d494f0a7dcda8e5e24dc9a5ae00ea1a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_briefs_mat05.asset.meta
        // source-sha256: c35b40855355a2f67e6b14af987f64b4a54ba3021e461a2e1f0dfa506d52d6c3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top.asset
        // source-sha256: d17401ffcd845b40a4c64f77c67cda485e2a1a87801f2b50ae1875c3e8420c45 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top.asset.meta
        // source-sha256: b0228e4bc0c0d38f2e9e5cc31bd716b958fc008f70b48fbc765b3cfd6fda556c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat01.asset
        // source-sha256: 17a01a0292cf638a757448da55ce25831dfaeb21a055ac3f4a36fe2260b79073 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat01.asset.meta
        // source-sha256: c2a6298ee03829d2bcbbe9002b69f93ce84fd4803924cc907182b7dd3e06159e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat02.asset
        // source-sha256: 5fd6f3547a34f71eb2882b0be898662f853c121cb91c42edd9d6d1f0021baeab Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat02.asset.meta
        // source-sha256: d63e5f5d27a597a828218e475462c4f37df3e58e36338840664546786aa1adca Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat03.asset
        // source-sha256: 910e687ee7d11d901c950d242b0b1cb68ecb70a87e5cae8fae4a609070acf66b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat03.asset.meta
        // source-sha256: f420fe97fdb2ca301f174c9cb40c36222e785be7c2a96df3984511b91b3e4edc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat04.asset
        // source-sha256: 231df89b0324f710fef705cb5bd177cfbc19b273ac38ce8c3836df603d8cc2ff Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat04.asset.meta
        // source-sha256: 706632c4e56fe3f463017023ce27538ae2d2f598f19b1812786ca4cf239c9b8e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat05.asset
        // source-sha256: 70f449bcb0c4adbf26d9aad03600b4dd258ac5e40bc7b16cdd000bf05f3d6757 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bikini_top_mat05.asset.meta
        // source-sha256: 1c0a2f2d021133d99b052b62a8edf67f615ea19af02c63b897086109e9a00080 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_lace.asset
        // source-sha256: ee5c031fa66db415f6f8f7a8df5ffa19e28c559a258f9fe20bfeb1b8bdb5bc3f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_lace.asset.meta
        // source-sha256: 4f8b06e9e844d83462f3dddf9df15bd4a1721e53cc133bc3b8b58f0b2cb9acad Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_lace_dream_lace_3_bra_black.asset
        // source-sha256: 4a9dc8f10a7c663fd1c10e92bd043b0e43edf657e2e577b906051a2ae6a52914 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_lace_dream_lace_3_bra_black.asset.meta
        // source-sha256: bef8c5420372bc88811f128c9b5b457b7b0caa20add1e698cff5a3652a69e7ed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback.asset
        // source-sha256: d9b7caaca9d9bc67880208f308bdebe9041376831a116834e32538da794f7f64 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback.asset.meta
        // source-sha256: d73215a2c2bf7ccdf617762131d3a0e50f145c2e6997950899cff5ff9f96a451 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_2.asset
        // source-sha256: 3ccafb3b08d272e9ac4a59ffe432f8f352500ca85782a4421b49a01f8bc607d6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_2.asset.meta
        // source-sha256: e3c5f67f54cf82cebc882b5925fe7b07573baaeb96757e0e1918f16675dcb473 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_3.asset
        // source-sha256: dbcc904ef67333372e6f432a219db67f493e2c4957b3e302018cad4db23d6ea9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_3.asset.meta
        // source-sha256: a2ed69b2f0f0e14efd8549df03a132ecd31f7cc2af1d4f59faccb5157c00f9d2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_4.asset
        // source-sha256: 348e0f178d2834fce11dd54f2396e68fe89323d9e290a484974537e8efeec59d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_4.asset.meta
        // source-sha256: fb379ba7dac5838a5ccc53066b449904d880a0c68f493b50eb3045004363336e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_5.asset
        // source-sha256: 4210c4ed7a030bf25cc8e362450739dfbc0ef8ed101e579b37a6867f2c2f3df7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_5.asset.meta
        // source-sha256: 244212da525ebc1e80e9bb38220c72e5d89b1bbb8a2e83f9d8ea31a77155d5ce Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_6.asset
        // source-sha256: 1feb951bfc268aa4310959004f8a8e8df67198cc14fe92cefbc245f93ebff02a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_openback_bra_6.asset.meta
        // source-sha256: f80dcca8b09a20963fbae807c9cc265a6641add9058f3d4bec4701ad60bf8b53 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_riot.asset
        // source-sha256: 1bcaedc9b2d667796115ea5d67544a12b3928a3104a1dee3e72e38cb467c4596 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_riot.asset.meta
        // source-sha256: 81485ae6c205c5f3dba26a52f6aa06a53369e80a440d936df4bb6338079602c1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_riot_blue.asset
        // source-sha256: df822d18ab37476dcfc4988fe7f0db482d460d56d3824886a06e6e77b53eed1a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_riot_blue.asset.meta
        // source-sha256: e31b04fce1f18716a11f74129f30bb7d91dd08c26bc5c63f8d678edd63c1329e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_riot_pink.asset
        // source-sha256: 5670697fe08989c080b5d073fe428d363102efcfd5f264a0c4b47bccafa0724a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_riot_pink.asset.meta
        // source-sha256: 8ab4999480a85b15afa58d43fc08bda16f922147f3c1c5e5b7d9d5cf3112a332 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_riot_red.asset
        // source-sha256: 96ea3a28117fdee5bd7b5f18e2d117d7280bd392b967980b1532bceaad5edde0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_riot_red.asset.meta
        // source-sha256: ed078a230fa5eea6463296b294abadedc4929ef7d8957df8b9064804ffb1273d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy.asset
        // source-sha256: 1cf7a24a9c9ba7714bda60b8fc4f3286af05eb3879ec37d8f5e6f377bdec6f1a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy.asset.meta
        // source-sha256: cc754846cddfc0f2afe38a482ee36c0201e874530f106db19459d7aa707dd748 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy_mat01.asset
        // source-sha256: abcc9866a11c18101e86c440afa320211bb3bad3d8dd9884638529b6df2e9f63 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy_mat01.asset.meta
        // source-sha256: 71183adf6141edba29a8bfea6f3bbc13bdfa42679d7f6e1c90b9c15fabe38246 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy_mat02.asset
        // source-sha256: e1847b28faf429c39ea914ae450bc3d3b8a8a10804c8d82a5503beeef7b7dedc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy_mat02.asset.meta
        // source-sha256: bdab7a56114582e70478d4d967d1468267191f1a7abd7236f2b6b96fa722ad55 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy_mat03.asset
        // source-sha256: 268ffa976d47041a291b775dd4aeb9cca636a7e91efc0e81f35d6e74597a568a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy_mat03.asset.meta
        // source-sha256: 4588669a1b992c691517b92075ab0ec46c362d422f27d67d32b2ee46b4d07271 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy_mat04.asset
        // source-sha256: ba1b3b5562981adbc968e6d494d8bc040f70c700aa3f13b278529dbefaa48919 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_strappy_mat04.asset.meta
        // source-sha256: 6e88f710935cd14ecfdc5067e0d5de6b3cf5668148bca67d3929ebb491ba5ed1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_wild.asset
        // source-sha256: 5f5ab1625d889de1c7bac2c8e03b2cf6e822be7745ed1fe1e38a9a8b6bb91bdd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_bra_wild.asset.meta
        // source-sha256: fa0ae0fbd2c11c3411f954cd4532d03b2d01cfc6398c80d859dbed390275ceeb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_cindy.asset
        // source-sha256: 70b2c3d300b87663ea73331726c7936ea109b06b94532e803fd1f6cda4f8f908 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_cindy.asset.meta
        // source-sha256: 3219f2af5b299f1c7268397b642c4d61b9bbea22b34bb08893ecc32d2a1f40a4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_flair.asset
        // source-sha256: c4c2dc5273f469367894a64817020d00ee44547667c174f93ec6eee0abc891d3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_flair.asset.meta
        // source-sha256: 7d8c249b5ebcb7c087c308c8c8f731a7ba3885f33c665a6cf8904292d0a3f205 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_flair_panty_02.asset
        // source-sha256: 8618df78daae7480105cd6f0800a73c46fdf86a09f0cf6ef674d1cc8617c8a60 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_flair_panty_02.asset.meta
        // source-sha256: 4ae7bd37e28eecabbeac23b58317b0ea4bd1b0161edd7a31cc1f5da0d3590605 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_flair_panty_03.asset
        // source-sha256: 109fec720d6559d141bdb7bd68c426f85dfe0a0d63192768fe450a8e6b228afc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_flair_panty_03.asset.meta
        // source-sha256: 4415785b1c88d0c65629900d26c4fc512fc6791beffaf1a9d64ecee4365767d4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_lace.asset
        // source-sha256: c5216750d51bec15392e422ecac4cce91f9daad1afcbd14d54af51200906e444 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_lace.asset.meta
        // source-sha256: 27bb685ea2f635ec7e0819d5dfae6fad41c3c135f390cca41754cd67e61e72d2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_lace_dream_lace_3_panty_black.asset
        // source-sha256: c5bce06ca1c0c7b7249aee56bf68225ecb59d82427f99848c417344a565e34de Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_lace_dream_lace_3_panty_black.asset.meta
        // source-sha256: e42afa609932d41bed79d966338e317630888f594a553de68423c847711c23ef Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_lace_dream_lace_3_panty_blue1.asset
        // source-sha256: cbe1a94a72636d886083c3c868ef9f64286598d9bca9e6259bc5335a25866113 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_lace_dream_lace_3_panty_blue1.asset.meta
        // source-sha256: e9ba9b30f0b3a1fc44a3dca4f1e63f82cd612327c3dd7184ded9a458cc153ca6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_lace_dream_lace_3_panty_blue2.asset
        // source-sha256: cfe6fd1234d4d937fc3850e3ef90d396e21fa872b814a08cf2ce4bf47552bc49 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_lace_dream_lace_3_panty_blue2.asset.meta
        // source-sha256: abfa6fdf5824027a12856d13794d95e03f9bb2f54a44c576298f266fa59fd002 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury.asset
        // source-sha256: 882bde409b1a782126e0da901c91617877f35969a2c1d1898d4491a9760c70bb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury.asset.meta
        // source-sha256: c71ff3ad7cc1965e9d3d55bdf20a7f3b1627980f5d0b48954340c0c308963a90 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury_02_cherry_panties.asset
        // source-sha256: d77ca2e5191e39a044f46a8771b184dd0a92d802dd8a2a9847b1b6db7a9a74dd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury_02_cherry_panties.asset.meta
        // source-sha256: 9cc11f1aa2177ed3b578876a66ab03c8ba2415f8b6f5742987afc97f91a446c5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury_03_white_panties.asset
        // source-sha256: 3fe031dee20bc887cca393ee133c4150d510ade523b3d7c7e4c9db4ebba3b2ad Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury_03_white_panties.asset.meta
        // source-sha256: a61dac72aab74ae1265abdfc9f42c32b4aa3de758d7ba0fbff11e9614f6eadc9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury_04_black_panties.asset
        // source-sha256: f7d76c3e1abc140278589614740491901d60052191d11dd4cfebf8823e09024a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury_04_black_panties.asset.meta
        // source-sha256: c692c0d7cd2dc9b0e107c2925e844742864c4ae0773337a5fe2b55d407858f7d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury_05_golden_panties.asset
        // source-sha256: 0e58429e6a4940032a32c6c6bca493a9509bc5f31cce4fdc811d7bddf1f45181 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_luxury_05_golden_panties.asset.meta
        // source-sha256: 5e01bcb5b3b111a78b7e49d02e5ffa41e9f36f79c7a2a510fc9354f166882db8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback.asset
        // source-sha256: 9ec1e3d8d4fe1479b5a1fc1e93a0cedced6171aa4ce273ca7e4307dd57d3c252 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback.asset.meta
        // source-sha256: eaa4f6e35e179d31fbfeffc13526f45b7aa2f3cc564ed85f446dfb76ca0c2891 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_2.asset
        // source-sha256: 6371ba57f591775f8df4ff8d419e073c8b96cda343c076d7bc19f9bbedb2b377 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_2.asset.meta
        // source-sha256: a848407ee3b11980a302bf2a1b3c706979ad8a5809805743db6dcd4d791cdde1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_3.asset
        // source-sha256: 6b6c787e3b941b06dadf98a1d0d03449d4d48d25b608537de0145421a30d1719 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_3.asset.meta
        // source-sha256: 2d2a09e128967a3afe58ed46c6e47c65268872556cedc29b1877f2fc4d5dc3d2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_4.asset
        // source-sha256: 2c9a9e8eecf56adb485ccc1f07b1a92f7ecdb7f21014ce5a72d90a962559554c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_4.asset.meta
        // source-sha256: d55d547dfdab4629dbf9e69ca623945b1d4747e5c4b5cb5f3940ce7ef3dd1104 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_5.asset
        // source-sha256: 94b2312d274f9bd40ba6c7e3aa4bbb94f1e4983ee0df01fe59b78f1516b4ea16 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_5.asset.meta
        // source-sha256: 4584099cb499ee9901de026ce6f7de75d8028032f1e202382693c746daef3983 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_6.asset
        // source-sha256: 6a554270a3f46162d814d46a8eba99f2789019f3e026c99299b1ea8d5488c725 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_openback_panties_6.asset.meta
        // source-sha256: 230487792b3616f879fc1a84639aa7f958038e8c79732e7eb7887f98afb62f10 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain.asset
        // source-sha256: 5208b144f27e9042e556234b66079f3635cb535738857a3e727966405c32fbb2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain.asset.meta
        // source-sha256: 97134a313aebefeab1addad5013c4892daaf8633cff76d4dd4a5a4f7cd860d05 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_mat01.asset
        // source-sha256: f9fef3721e01d2c4d775f940f745801e11e0be996554d1be6feaa253998602fa Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_mat01.asset.meta
        // source-sha256: a14d02054aa68cd9c56e0fb0dbfa13c7a5dda95f506b3d56a535a8ef57b50938 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_mat02.asset
        // source-sha256: a98a4e1c94c914b442af47460578b24b2a9c590fc495e9fd642edc92aea83888 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_mat02.asset.meta
        // source-sha256: 6e6b281b8b65a4af0b3ff683a2031a51938261f80854e1131478e82e98db3322 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_mat03.asset
        // source-sha256: b4f74ca1f00dcdedcd3139d37c69206af4c0450bdf2e7e51bfc505efd9505b99 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_mat03.asset.meta
        // source-sha256: b08b9b1a0f251127a6dfeb2263887b5fcebb2523b2611cdebdb2422f5c0e8d8f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_mat04.asset
        // source-sha256: 7982dbbf5b67a09cd5055f52e3b5d54b567734b6c1aaa0e9e6276bbe20a7c45e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_mat04.asset.meta
        // source-sha256: 72347a9ee8c09a510913e7df71ece176884d0e20efff3f50c882a50954f9a88d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_shima01.asset
        // source-sha256: 65e69c4635f33c0e6109c764ad6f853b1abcda8d1c7c43fed3200fe30e072afc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_shima01.asset.meta
        // source-sha256: 0a4ad3e256a288fff1d99656b92bfe22453bbc45e6bc8405dbecaebda47c7eac Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_shima02.asset
        // source-sha256: 26b447541e8f768d0476cad3abb63207761ba0ffa7feb241608d47eff6c16717 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_shima02.asset.meta
        // source-sha256: fa752b000154d3ca30fc85c8c32dfd7f1fc9c9bdc9b6995fc1a40417c0e9b5e7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_shima03.asset
        // source-sha256: ff0346fe3272a8160144882b499725e693da796cc28fe1da1910312e344ea061 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_shima03.asset.meta
        // source-sha256: 7d6c0aeb5edfa5fc0eb8d7066fb926268a40be444d5835ba453e81e8ed19e026 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_shima04.asset
        // source-sha256: 18fe35ed6bcf253e49e7fe57479f4b3dfc7d36b1984199bb395192f3f7e86da6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_plain_shima04.asset.meta
        // source-sha256: 2216eae60f6c62af66d9bc9be07fabfd75b8f266674e38989e007a08542b4619 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_primal.asset
        // source-sha256: 4acf7ca2124000898003d03aff68af9f2298aaf2b1232186a245c8af39df4c5d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_primal.asset.meta
        // source-sha256: cc4ef76f78036f22bb92cca876c5e2f75c59e79587282872cc137b0fa4240dda Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_primal_panty1.asset
        // source-sha256: 6d9b2410ec18981a696fed7aacd7f705571e2b5ab1e874683f590a418d4c63af Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_primal_panty1.asset.meta
        // source-sha256: 086bd1d168dcc86b5c57bf4f83e5f3bc94425767838d9d8375d886c0f17a3450 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_primal_panty2.asset
        // source-sha256: 17160b29f2d363a327e97a1b66bde11f261ad4ed0c7b28f7a500db4a85fe9646 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_primal_panty2.asset.meta
        // source-sha256: 19d3c018548bb0733cb629e7c0549e9c4fc2263ca5151305783dac9f1a836d06 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_primal_panty3.asset
        // source-sha256: be73c8b683f5f6d488a6d862a47219667765a95485661cb96d2955d3a6a29b4c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_primal_panty3.asset.meta
        // source-sha256: 568e25cbef5b8d0ade5360624697c1195ac14b0f107d3fbd68fe4bd7ac18b251 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport.asset
        // source-sha256: 6e22a1f142cf355291476166c9b061732de940c5208557ab30775522de1d810b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport.asset.meta
        // source-sha256: 8d01afcc036db4538e6a899d6977c2de6bb7923b923c64e93488a9d893d2231a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_2.asset
        // source-sha256: 4d1e854de24e027626e678f7b92f4b609180b4982749380af5d5fa94e78cc32c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_2.asset.meta
        // source-sha256: 91fbcea38456f8b8931b34d76447b7f47d7e67474c2a9df2d664797e7b5b4608 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_3.asset
        // source-sha256: 8df68b56031ccce1b1760581b4d9b3e91133a4607474105d86c3173955bc67c6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_3.asset.meta
        // source-sha256: a3d3136f6ffd9b33e580be74f9c89437390838054f01bff90e3046a266c9bcf2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_4.asset
        // source-sha256: d82c4d63f30f38ec6199ef1afd41c0fac0090b5ce6e107a4c285d5d0a295ba16 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_4.asset.meta
        // source-sha256: f8084b621f3726ac80a275039f3aa8602166bece64c3457cc37e6190ce617486 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_5.asset
        // source-sha256: ba8e21b9f86610336cbab4bc1a9aed67df6fd1b1d8965685d795974ffb1b9a5b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_5.asset.meta
        // source-sha256: 0cb62e08cc4531d3502b4855aabfbb372bdd2cade1d5ac6d5e33abcfef373b28 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_6.asset
        // source-sha256: 32fc83548ac0ca23223cb3f2a2194e02fbecc06a6231910d484baa49efa7612e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sport_panties_6.asset.meta
        // source-sha256: 520a4e1d330f9d00cda7b9d9cd7cd80852d63f9a20c574f9952b9d47ffd8eb21 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy.asset
        // source-sha256: 752b0c47adf75657ff3cf4e2ac5d717779a91451eec0ee9c71d7095420a323fe Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy.asset.meta
        // source-sha256: 7b627d37896257f4def9959ddc08e6d2d1980a39c5be252e5ada508d56364820 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy_mat01.asset
        // source-sha256: a3f00f8be03fd94d6519e382d36ce0ba6e8e5f91d2d639cad69348315f8db636 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy_mat01.asset.meta
        // source-sha256: 1b8aa08fc2e7f20eae35504a17ef32b09a25daac436c03b46156219aed3744ed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy_mat02.asset
        // source-sha256: 4174c6dfca68d72aaf53f69fd2de78a4b7d10ff83ebdea0555c096f54bd5ab4b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy_mat02.asset.meta
        // source-sha256: d87b1c66efdf3fb0a5c8787fbb0c5ae060f1dd4cfc92b6307a0ba07102bb6eed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy_mat03.asset
        // source-sha256: c3a96989d9dd27a9e3824b736689e92481a7b93fa926d5c04f78971375a1db23 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy_mat03.asset.meta
        // source-sha256: 2883f554791160e02b021ecc59289ba52db871b71b4441f27e86e805b193ee73 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy_mat04.asset
        // source-sha256: 666b4898c9660d3059696c738307aae756ef607f7261b0c7b40414d463443ad9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_strappy_mat04.asset.meta
        // source-sha256: 044956e93d7f9f9816413311f6ec8143926987a2b2096a56a3ef2b7cb79641f2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety.asset
        // source-sha256: a794b24fb5897133bea94f288e4d6e8fc641a619da6b752f596d80490c12c573 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety.asset.meta
        // source-sha256: 71b92ea9d1c2ab73c3d72246346342fc6f0cc24f620b3db4286be71439907d1d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_01panty.asset
        // source-sha256: 0e3779daaeb014d6df6eec7f56eb1864699f0507564d25efd03fe924a8fb1606 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_01panty.asset.meta
        // source-sha256: 4be72d7b02d2b8d70e035d23d21407eae9e155284d87bced5cc0941afe4429fd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_02_panty.asset
        // source-sha256: 621c4ab049bb201e9cc31b7f4bc58088f48519d6ccad6901ea60a22568d6d829 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_02_panty.asset.meta
        // source-sha256: c63278e6efe5426e1e2a982e2c11d1d69ceeef62b339107dde8d79dbad681929 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_03_panty.asset
        // source-sha256: fb29127490086ba80cca5316d4626bc91ad5b3f0d6c1524f3256f130efc87948 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_03_panty.asset.meta
        // source-sha256: 1608ed9c8171f2bdd82ed2b3a9bc6b7cf3e66a1dda7342747dbf530e775214ad Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_04panty.asset
        // source-sha256: a53d6a2ae6d1cb58beb268fdaac0eec9a7ae9bab1d571bc07bd9f1dcf21eec55 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_04panty.asset.meta
        // source-sha256: 8348e3be763c43d7ba6620fc238a400a0c74deec657f2249e6048f6f482d0d1f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_05panty.asset
        // source-sha256: 83c922f3d3cc6e28131f046ab257fe2121cd3b3c71b7bb1a133b096a1c5119d1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_05panty.asset.meta
        // source-sha256: ff67f8371934aab1d0ddc5ce8a6a430105b4e2a01db80ec6d3ea86651147d5e4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_06panty.asset
        // source-sha256: 0be823216b3375e911cd028c6d8233ae4b3c0249f158b91ad855f768d4b0ef52 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_06panty.asset.meta
        // source-sha256: 3440d8a24879d8ae518d987278e6b7d1cbe5c32651940dfefe41d1346917033b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_07panty.asset
        // source-sha256: 306649ae06099a924610fb026f63cf64f1b4e0e594b31e77f950e8731cc57c67 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_07panty.asset.meta
        // source-sha256: ef9f2435d6b5e4241dce1dbff40de057419004b823e690c2986f0e4428373644 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_08panty.asset
        // source-sha256: 1a93334e9c3d5c7960ec4afe5a575e1531d9f068007d87d503036cdb7d1d65ba Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_sweety_08panty.asset.meta
        // source-sha256: f13fb0e81df38a33a93eb9004abd186101d099f7fe99bc5d4e472c85eb66cead Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez.asset
        // source-sha256: 7c4c7cec4fd42e631447c42f93e4cf43389a356dc46c9a30ca78b5ed0765812b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez.asset.meta
        // source-sha256: 8cede21b5d1b35031ffb251cb178b9b0d8dbcd47b9b2a44367ea5a6da1fd36d9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_charcoal.asset
        // source-sha256: 8abbbc02f9468577cebf7b2f1abb4ebe6b30863595442e7cdba0059b0db5e146 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_charcoal.asset.meta
        // source-sha256: acb192220a05f3ea2cf02994486511bb2757e6318941517f7611a3d282b36240 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_dark_grey.asset
        // source-sha256: 4377cf1e6914a41e485a1ceb97d7ce5dcd56fdd1f9bbc4cd2f91200493e9ecc7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_dark_grey.asset.meta
        // source-sha256: 6f7d509720e098dbc7f1ad4102bbdbdaae1357ec25fc95eb87fe8ae8c2751469 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_dusty_blue.asset
        // source-sha256: 341d841f4cd62bce61411918a484f4eb58e9a6409c1064ed224012624673e1f0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_dusty_blue.asset.meta
        // source-sha256: 6174bf0ad9a907873786961a2535841faf8fe34ec8ffc95368646973adb1936c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_dusty_pink.asset
        // source-sha256: 5d89dae3caeee248afcb722e4b118e6b3f095faa73d0873fe33f9936960ed63a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_dusty_pink.asset.meta
        // source-sha256: 6c23b553a652906f698da5c3a1dacd1c1139b61dbc5a7f823ddf23509d54ebdd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_dusty_purple.asset
        // source-sha256: 288ffbefa06c03633684d859b3674dc3786130ba3381938684e0e72a2a389fad Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_dusty_purple.asset.meta
        // source-sha256: 8fa7a6e14cc6821a2b374a28ad7a711c34f67ab2721dcff423d2694b3f6f15d1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_grey.asset
        // source-sha256: 96bdbcc0449e1efd100f893433004e92b1d9306449d41092938e89c6d8fae1a3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_grey.asset.meta
        // source-sha256: c2812c2bb67f3bc573fdc6349d03e6ece6b6f9da85eec73c4cdea9d445de081e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_red.asset
        // source-sha256: ea52705c918646fdcc9100c8f932b4c5ee1d9c086e0e25a6968f7bd5180305d4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_red.asset.meta
        // source-sha256: 2926d4045b62ba8c2006cd80c87168d095db7c947d9321e5cf95dc9cb05c6e8e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_sky_blue.asset
        // source-sha256: 6d1f9cc8686db3f9894e0182304571896e12a46ef48bc22ff25c0b917c647fb7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_sky_blue.asset.meta
        // source-sha256: 3c724e0eb14e118a38535be4a439d45d5c127d0bbfbbd49efb5d7bb32654df59 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_white.asset
        // source-sha256: f4f6fbe37b37bcc2066ab4b81849d6dc6cc2ff89d7f09522f79c1cd8546665f4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_teez_bt_panty_white.asset.meta
        // source-sha256: 9c6ec28be0ca89f0f47b69534742275e4d520e23d553753eb7928c1b64378838 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor.asset
        // source-sha256: 98d6a5c208280e415a7163eb58431b40e89950f45fa927c7100460fd9ccc60f1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor.asset.meta
        // source-sha256: 1cf4dbcb63c16216daccfbe472e960c906fb63ce9e7126bd2f8d22583ce2aa01 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot01.asset
        // source-sha256: 90aa66c23557f12bf87afc7ab9fb94097b6b778bb91766050d56b5c9eb9b6a01 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot01.asset.meta
        // source-sha256: 3855a2519f9ce4f62765601f2bda41fe451c63476eb66f1f39481773c9fbb8ac Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot02.asset
        // source-sha256: 951acc4e5811180528218848004e8b1c7a3c773967bc034743ae4c7e97e0ad1c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot02.asset.meta
        // source-sha256: 1c1e41a4ad4f0d94aa6e8cc146c1d6f390923a3fa406b358fb08f2ce25904525 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot03.asset
        // source-sha256: 4fe423a91403a242780530f1b7b1f1eba4dcc953634f9e26a987af279da592a5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot03.asset.meta
        // source-sha256: 35e90822a965c4792ff0a580a70cd2ad2d20a135c2d87aeddd55fe1371c9d956 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot04.asset
        // source-sha256: cb1b680e2efa4df4d807757fe03b9cc3ec9fe33cf2cb09a2d662ac4851dc30c4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot04.asset.meta
        // source-sha256: 2ca0f477dec711348f46335df754d662a093c7c45bcc566c2da8390cdf916491 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot05.asset
        // source-sha256: b7ff66fe4632b5d5a3d139861b781a4ec9c32a539713de368027d9844541f4ae Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot05.asset.meta
        // source-sha256: 697af5ecee297f9e2c39ffc83b0aa4beb7240f66350ff756490037f871d9f4c5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot06.asset
        // source-sha256: a9e5c6e1babce2a00c163ca5143eb6595d23586ba3d29ddba9007de4f54a1586 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot06.asset.meta
        // source-sha256: d7c5bbd7c43bf5943aab8339eeb6c00740a291b09d8775ee2835c911c99629ca Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot07.asset
        // source-sha256: 0773244acf4663d8e7e224482767219d2620f52df5c433d7130832464c9c872c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot07.asset.meta
        // source-sha256: 51d52f7860469a0b67c6c5b23f9f580bf1485cc7d97ed1199927238b9af5ead0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot08.asset
        // source-sha256: c3ce551fe7b34e922d07c3d546fd5fe4f886e613f345552e02a053d0e4fcbb2a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot08.asset.meta
        // source-sha256: 11d68bd493296d92837bab4e02d9059960af70ca123ef67165d030959479c517 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot09.asset
        // source-sha256: 9039c890dee8e1961ba23dfaf8f1015cfe8f8f8988b86d90afcd92695c0323f7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot09.asset.meta
        // source-sha256: 3a29e37421d60bbd8504c57cff8e831a79e33a351b50ed5c2b553b1bfb0afce2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot10.asset
        // source-sha256: 834584feffbcdff6fe439dcdcdc8193b2ba40fc61de47868e32d4e321b457cf5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot10.asset.meta
        // source-sha256: b97a17034238e79ce87fdc544663b5650e353de853775fdb2be221007b8f2920 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot11.asset
        // source-sha256: ea3c7b23e6e3dee59d4774f04e0926b77d48364850bb1dd834f635c4383000dd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot11.asset.meta
        // source-sha256: 87276a639ae390990a47c9ba33ddfa77f8356a0620558c373c2ccc0b75292b19 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot12.asset
        // source-sha256: b0293949a5342ee17ed85d0974840cdeb81ce1573985ecbd9860c5be2ee51f8d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_vapor_bot12.asset.meta
        // source-sha256: 5fb282043e57c4d698e36d64f89e497134816f1cdad2ad6b3b1b162421efce49 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_wild.asset
        // source-sha256: 21e05cd96e83ed7e2a0310b7b44cb2370ce69b3adfcc72012d406e5860bca2b7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_briefs_wild.asset.meta
        // source-sha256: a5c769bb4f5099b4eef201af4ff4ed20605d9693193af9088d43acf52ae3e6cc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd.asset
        // source-sha256: 0c6c2a581ee98debc96d32c4dd0f7d4563de0c513f887643bc97254173ac50f4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd.asset.meta
        // source-sha256: 4ec685ad1f2deb557193b78e91b38d96b570adbce93b281355097c860e1c465e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd_nc_sock_left_argyle.asset
        // source-sha256: 8ca3dd23c886d21a08225de410f86abf45ab38525f36391226cf786edc398ff3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd_nc_sock_left_argyle.asset.meta
        // source-sha256: bb8650e3099b66a34be38166d85dc7f6b7b354bf98a8768fa9d92605de58a6bc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd_nc_sock_left_polka_dots.asset
        // source-sha256: 4d1a8001cb7de2f3367150c5ee823a84b08467a25e2ab1c885f381e780262317 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd_nc_sock_left_polka_dots.asset.meta
        // source-sha256: d1426ccd8ed1b3bd064767aefcba151653363dc6f615a50ca5ccece53970c79a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd_nc_sock_right_hearts.asset
        // source-sha256: 8e4c2be4ccd14868a05c07ea897a0ec2bc15db3fe9f0826f12808fc3d2b04769 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd_nc_sock_right_hearts.asset.meta
        // source-sha256: 437d751df859bbf04b4aa057e089f415929bca212f8ebe779edab24dc24e0130 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd_nc_sock_right_polka_dots.asset
        // source-sha256: 72190c858f1bfd0be660669470a489a316eaf66673ad94fe5ab1a57146aa0abb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_kneesocks_nerd_nc_sock_right_polka_dots.asset.meta
        // source-sha256: 4dccb5c0bdf758783b01e4f1b8d670863cd45d5975ee3ea772504bd5e4e571ac Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_panty_tod.asset
        // source-sha256: 41eec0d099ed1d5d22bdbddd776330c9666831c4e4b0605c60b5ed6762293c40 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_panty_tod.asset.meta
        // source-sha256: 256fc1dc03999a3dcd42762977bbe6aa91ff6062c27e39186b226c56e1d938c2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_socks_fit.asset
        // source-sha256: 69a17586c8427fef9a82ff4785ca6c414b4400818c80033f7d08af8ec6a45311 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_socks_fit.asset.meta
        // source-sha256: 497c15f7bf4693ef0fa0abef70c92e9350a65713561928a3023bf79c4175f4eb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_socks_fit_blue.asset
        // source-sha256: fa51ca35e94dbf775d84456d067a174d091dd7391d8a2d6e762150774623038b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_socks_fit_blue.asset.meta
        // source-sha256: 1332f085fff29d1f0b07780ff93811a1d5fa845c500fc8b0b2346ecc40f40c1d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_socks_fit_blue2.asset
        // source-sha256: 358e70350e86666da3c5c2c3d57d1bb88a13562808a9f31d8e1f1b50c17105da Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_socks_fit_blue2.asset.meta
        // source-sha256: 1121ef05f8968ec894d30197a86265c0c1b45180ca00407980ada42d502a0385 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr.asset
        // source-sha256: 2fb9cb94adf003b8bed878825dee084f9ba72b4c866584009d70509ddb7430f6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr.asset.meta
        // source-sha256: 9314103a587df7cee3e537d2d97af75757e41d40cf86f26ce516442949b5afff Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_2.asset
        // source-sha256: f0614ba59d19a35ce86eb18069d4eb959d22a038b6fdeb6be4c7892415223682 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_2.asset.meta
        // source-sha256: 94ba14d4fd3e94d8864a88f8a3285d04b800ef3a4979dd38524e4d5f1c08860e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_3.asset
        // source-sha256: 32738a02f6260de5a187060688299393109da4550716a6e2a915d024dc26a32f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_3.asset.meta
        // source-sha256: 93472289ffc5424276fc33161bbbc13dabaf220095342752dd88b19807502e46 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_4.asset
        // source-sha256: a8654d6e9c65a15beab0299b550882b35fcde4e08cc0450f55f6a4a0779f9563 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_4.asset.meta
        // source-sha256: 548bbb82c567e7bb45d97f210ad51899846c45a686f4d26b50b74103d1e8d8d3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_5.asset
        // source-sha256: 23c2a0497d363047ca0a18738a32a630259847dbcff5684e5b9116cd8a349512 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_5.asset.meta
        // source-sha256: 13b1ee0d5fc98e47a0ce9c3a91031b3a529d321c55a34ae8cc1120f69a106124 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_6.asset
        // source-sha256: 62c210125fe5ec1e482587d861ba2a7e4e0154be76304f737ec284ad111b77c4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_jmr_bra_6.asset.meta
        // source-sha256: 5506b5edcbde2f4b29747b46fa8998357153519adb1ea5cdd820c667ef6cf5f8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek.asset
        // source-sha256: c665482941fab71730063f6fdf403515916845cee6bc1edbb4f14df50a3c9bae Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek.asset.meta
        // source-sha256: 9d755acafc7d37e6756175a19fa5feaebd602951fb1c923c58db929cc4f15832 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_01_apply_black_bottom_trim.asset
        // source-sha256: a5a6e2452881295695ce6ee3d1d5728d7b97f82d76980100bf04d422d301850d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_01_apply_black_bottom_trim.asset.meta
        // source-sha256: f0dffe67e36db02413a635cc9a52d07cbeacd5859e467188b590bafa2bc4ed42 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_01_apply_black_trim.asset
        // source-sha256: 86d54f18397d5fc070a8a9019c53f40667dc5e0334b07b48019578dbac6c5465 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_01_apply_black_trim.asset.meta
        // source-sha256: 49192a55fa4c329303c4fc83f1f23ce122c8d4f9a6d0cde8f726393fdf4d2cca Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_01_apply_white_bottom_trim.asset
        // source-sha256: ad813404883358bfc198cb69f824a08c5b53db11a21e5131e6b85fc9961f544a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_01_apply_white_bottom_trim.asset.meta
        // source-sha256: 03a302f91e5ab185231166cb052c4ef354b58899fe6db60ebaa4034af8999226 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_01_apply_white_trim.asset
        // source-sha256: db940c249c06b148252c6c5d97a7ae7a4204dab76720574e9f00f127998e3549 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_01_apply_white_trim.asset.meta
        // source-sha256: 07dd25601cb433b65f0cd00b45bcb438947f4ccd93f075abe5a510694cd86309 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_02_black.asset
        // source-sha256: 26822b213bf6798ebe90433b897925d1aa563cb81b4e55c22ca9919683532f69 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_02_black.asset.meta
        // source-sha256: 17a2e2b1419d28be163b67c0c76b9d026dbe5a1f6d920eb269545a8a69d821ae Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_03_orange_black.asset
        // source-sha256: f35211caf83e4b8a42269a903fd15cfe8bdd79cea35e3936febf59b7590854e8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_03_orange_black.asset.meta
        // source-sha256: d85f965d68b61f340de519132fc60de761d1016370ef1be621d52aaeb3c7952a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_04_red_black.asset
        // source-sha256: 85e1034c8f3b92db2c7ff266d18c024c352abc59a0614678d68b7600aebe077f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_04_red_black.asset.meta
        // source-sha256: 5c412c08f28ad4edbc85293d34b652e5624bb7951909cdca8a51c608f8909942 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_05_black_pink.asset
        // source-sha256: 694df20004f7777f88a5c731349c622671f50766ae3cdd3c2f9bd7bfbe5d3037 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_05_black_pink.asset.meta
        // source-sha256: 104726fda872487a7c24f511ff30412bee2ba8f3dfcd44bcabc854aaea4f6d37 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_06_blue_black.asset
        // source-sha256: de13c14bdc5d824b36a21719fbdf92acb0778b759ad4523c6bf2ca0030fd4cc4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_06_blue_black.asset.meta
        // source-sha256: 6c032df92d03b4a3275bff39230958a1530388b3522b6798314072f1f381fa48 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_07_blue_black_orange.asset
        // source-sha256: 843075bb8836c6a54dd916557aceb07fd1c017da3edefa46e440f00edb0631dc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_07_blue_black_orange.asset.meta
        // source-sha256: 18da7205a3ed167878a67c3c175670eb896b6cf5e24c05c475a8e9c332b29524 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_08_white.asset
        // source-sha256: 82fce77804befcb17c941cb72268c4c43333a8f3cc22a6794198517d6df38509 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_sportsbra_tek_bra_08_white.asset.meta
        // source-sha256: aea7c3dfe28cbe09730027b2fa3317b9f4bb6f8e0be084720183b2b7bc6b7a62 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee.asset
        // source-sha256: cd4500b6428ae66ec6e9c642e4d25a06a543b778f039639e9732d01a5baee6e7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee.asset.meta
        // source-sha256: 684f6bcb0ae1fc221e084aba0468f17b5022b1d042d864f308d401dcf294b46c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_2.asset
        // source-sha256: 86f3d8f1edd316d214de2e3441b1acffc3b68b599bf42d491646e1d868a4b3c6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_2.asset.meta
        // source-sha256: 4567b1d84baf55d232aabe3de7bcf7e9ff1d81941cb3a55659a48d8e4248067d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_3.asset
        // source-sha256: e702f625e2416b0d2f9de253a57150c9185eba225769736d707993b6fc3607c4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_3.asset.meta
        // source-sha256: d44049e8165325ad6e9d65ffc5112b9f547316f5b03c2c4da8eea7e373fe2f61 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_4.asset
        // source-sha256: a8367813efcb063bec1d4fcb7b95cf87a32680e148f7292f7d3dbe487bd90c88 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_4.asset.meta
        // source-sha256: 472f02eb3556ee35cbf2da181226c10b282e4721ec7954b00ded7768675db560 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_5.asset
        // source-sha256: b1c90740e034acde90dc52c462addc9fddb4b177227feecd80338c47435ecaa2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_5.asset.meta
        // source-sha256: 75d9f08191857a5b6612b2fd69cc56700cf8364e7cb20d43b595e168bcb3172b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_6.asset
        // source-sha256: 205802a1c265965aaedfa41079d75046bf3ed607db255774444e5e0412f75302 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_overknee_over_knee_6.asset.meta
        // source-sha256: 89a79436bc6c6246ebdbe6e877a3a38e3a906c8644f0f8096ed1ede491778faf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_riot.asset
        // source-sha256: 9fcf6e54ed0f12d578e19c7890fe2c07eb5bc363cfc9b5c11f256d1cd9617695 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_riot.asset.meta
        // source-sha256: 6f60af5d08c2a362cfcf29920eec7cfb41e0fce88ed6b0d0da2e287198c90e7f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_riot_clear.asset
        // source-sha256: f5fd351b23c5ad6e194b3ba75179c81b711d9ab5c9d1a654a4c1b1224e5a2a95 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_riot_clear.asset.meta
        // source-sha256: 510600212f617fbfa0c6f9003096c0b56c89944e8c6cee1099956447a146daa8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky.asset
        // source-sha256: 6468684a7e9187cced384a15addf8a9e10a527567a79b1af8107bbef8c2d500f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky.asset.meta
        // source-sha256: b7e57c4af67dd9516625f6e6f6dcdd8a068cbcd23669b339b8b341a05d5a4fde Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_01.asset
        // source-sha256: 0e481f13ab62e2ff71a33710c2c3ba5afe46ecdd3fb26125e1fc69fe46fd345f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_01.asset.meta
        // source-sha256: 5d1bdbd8b745ab19e8a46da9047e4f96f20f3562b3beddb29195dac0ac80c461 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_02.asset
        // source-sha256: 23f36dd8e0893b0e83045795e7fe3571ee5af3d21088428b235ea3b504d2af3f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_02.asset.meta
        // source-sha256: e3a38555e9679bc7b05f12b920953555716acce0013c55181ef20a38b3e26dec Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_03.asset
        // source-sha256: 58b225a7aea2d6522ca62bb71766a72dd9b7a7ad8d113c841a04cf4926fffbaa Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_03.asset.meta
        // source-sha256: 99dd843a7f684cbeb38e7a6fb6cdb20111285c72119469c4c377687870f1d090 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_04.asset
        // source-sha256: 70d7737908c88c6643ad5ec02be34c069f5da3835a7d015dc386f0f041e5e626 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_04.asset.meta
        // source-sha256: 5b389e107a8acac09ab8fef62d54334d40e482d9c369b78ec87f1817ba28bc6d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_05.asset
        // source-sha256: b60aa7451b1105ad3b682f5e1c790146883671934b1f8930b98649c4f3c6ea07 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_05.asset.meta
        // source-sha256: cf6bbc890d4b284b7f60f54f79c1609d1789de2aa28ce84760a98ccff128ac46 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_06.asset
        // source-sha256: a1f7c6b07e11c243ba15f80ce22dd2c39b8e1b8e8390facffc8c16ba4b6c6803 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_06.asset.meta
        // source-sha256: bc4a66c13b5ae781d08fea47912b3338f2c5addb4423bd734e7b79d43859ddb7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_07.asset
        // source-sha256: a7c94bbd7a7b9d37fe839434b11764099127042d9b82fbdc3e55a7c5d698308b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_07.asset.meta
        // source-sha256: 279d3efde5c4f5cf5091fb015986bcbe1d040808e79fe0e1f33fa6237fbfe3ca Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_08.asset
        // source-sha256: 50178eacfcf0d005d9aba005cb63050e509dfa93b6b331ff560c6a2b89f8750e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_08.asset.meta
        // source-sha256: 12093f5cce105c70a8fe2e4a1d5c07a3fa6404c9cf2c7175358e89379e29902f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_09.asset
        // source-sha256: 361b06d6798814317ed98656f6dd4918eb48a18b0e3040b960c41fa312313de6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_09.asset.meta
        // source-sha256: 4e609482970f9c8d2073cb6117c0348e6c194087e4c7bb5fd4bc0ccdf2abec68 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_10.asset
        // source-sha256: bd430f149d84ce5c8b7f9381301529dc5a3c0ba11033c443e0a4f26f65b2ba80 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_10.asset.meta
        // source-sha256: f2828dde89ee9a68b14b4d9a5105d6e1fc6b396f4800d8609ed3b5108169570d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_11.asset
        // source-sha256: e7d53f20e74f0faa9c6bfa8df24751140b4fa827c06a28cc970e4182b4a015f4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_11.asset.meta
        // source-sha256: c33bd236c25e0a1f40c4b59547622a95674c394d107ed4af76c6bf2a6e7d9e86 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_13.asset
        // source-sha256: fb4f4fcafe3995bd439130fe992cf29437a0594419349c7cc09f455b4f84e8d9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_13.asset.meta
        // source-sha256: 8e20a68fcd6829baacd3695f83abe8d53afb193bc25572b13a821d9eff74e6bd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_14.asset
        // source-sha256: 7601e885a50d8e938c13ebb2559f06130921a80b9613059c66b301b7e608fa8c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_14.asset.meta
        // source-sha256: 95f07400bba716bef331b420fab5161f384f47b230ad1ef7118986e24893af34 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_15.asset
        // source-sha256: 56039ad950504b0d5e19784c517d9e0a744999d29919a9731ea20a3a875f1cfa Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_15.asset.meta
        // source-sha256: 2c7a297548d3f74e251506ffd1533767d7513121385dd7f12b4bb7fcc8ee720f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_16.asset
        // source-sha256: c0add1dd9a7f289c84a643deae0bdf8b4f2f81a89761cee06379078e18d5d91b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_16.asset.meta
        // source-sha256: b1c276ddf2007df118ed6702b84d38f1f9e211cde6b22bc5ef08ec0facb69a60 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_17.asset
        // source-sha256: 12e02323ff327dbfe4cf1c608873af6398cf32f5aa71756e0394f9481f6b165c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_17.asset.meta
        // source-sha256: 76eeaf250d6e2216cc2f8511919dcecc49436d03da3c05be49321e0fdfcb4d2a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_18.asset
        // source-sha256: fe420a7697269d3782a8955073453dd6073ff669f7f68bc23d6515be4f8ccf77 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_spooky_18.asset.meta
        // source-sha256: 757ee4a803f9ad1d5e27d4c21abf1bbd63dbd5f0852b9f338c05945764bce170 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_tod.asset
        // source-sha256: 0aa8b0affde5bf7ab55dd0d82bdbee7f4a7fc72c8be999125506336e1c1e4b9c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_stockings_tod.asset.meta
        // source-sha256: c251bb6b4c41980dfa48c90ce7aaf2ed7493d376cb13d84f2790592f2b3bfce8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs.asset
        // source-sha256: bd51a819f9343f94ec5cf3aca9a9b7ece2c85091cbed6e8aecf7970fba12e69d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs.asset.meta
        // source-sha256: 57904725eaa10f1b1f0d10df084354ec2a37f4ff34350098f1d045f26f898d01 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_2.asset
        // source-sha256: 5892b169ca54390b9a1b191c06e37cc9d94a24d266a88cecf978d3931034325c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_2.asset.meta
        // source-sha256: 3779d66ebd01321f14855da2672cca196d15dcdfa506e61e016d38ee5c9a5b63 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_3.asset
        // source-sha256: 2eb5dcabc68f1bf03f5dbd18ae1cb3a3d47ca47bef7a48472f89780bf2e352a3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_3.asset.meta
        // source-sha256: 6686484a1c64be97e2a18a95d78d76d7a54f8cddcadaef8f60c301e21a90c1ad Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_4.asset
        // source-sha256: a01c2094a4c7999d42bdd54a05c6a78e757aff5576fcf31da04cced79b91ece5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_4.asset.meta
        // source-sha256: 7660df0be6cf941dc7153d38214784ae435c48c3aa58639b19fd99a523d7a560 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_5.asset
        // source-sha256: 7f3fc64895bb113a304154b1463e469e7331d108ee139c433820977ba1727544 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_5.asset.meta
        // source-sha256: 6d87a7c9c6ffb70549c83a64ff21e4b0bc85349ed16c9b4db9b66fe20690c765 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_6.asset
        // source-sha256: 8f356bd696ebf639cba24cc2ad79ebea63419c7a94c202893778a2757d600bd0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_6.asset.meta
        // source-sha256: cde465f89db7b666f9c0c870d54aa788ad88054b6e226108264e44083b7e7ebc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_black.asset
        // source-sha256: 5429c6f7f8f340fb28ebf13db9ba85d88b64d4970d854bf37ec25dc2c1ce413d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_black.asset.meta
        // source-sha256: 05e789a040cbe6f497f19e2df76b9b470c3409d5a1c59e1bd64511d35c6f3b7c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_blue.asset
        // source-sha256: 8ab28cd68a9c1181e16b795cf2640dd4207d5d8806ac40ab0cc1e98b30a55b1e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_blue.asset.meta
        // source-sha256: 6ca8bcb6996b39b0d5d582dc644c75c4d5e3ad9e057155ea47d1a966fddee814 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_green.asset
        // source-sha256: 4be9a0d063ef8626d828f294c55e5013223ab70205f2f64071d854475ff7b6a9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_green.asset.meta
        // source-sha256: d444d66356d8a86efd11ae120b4212012168f5d68883e8fc6b91b8e1cb553a34 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_pink.asset
        // source-sha256: 900f780a9d3b2c205e7eae5789a24597ad502031cf48b1f39448e69a1e77080a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_pink.asset.meta
        // source-sha256: b81330cc33023f1a6474fcae0c33dac8c60e121d4d083b2925224d6d02247b04 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_red.asset
        // source-sha256: 4e00a0b617b6217653c0f393010f59455d6508230db0d74da9efc427c2d96bb3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_red.asset.meta
        // source-sha256: 004294db50752a49a17ac44af15b13790f6c874c52e99599f2ae9fd26f57064b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_white.asset
        // source-sha256: 19e1e449f4399d66f346fbdf6fc7cdd69281a5b9bee8cbe1d6310296b3ff2c76 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_briefs_bottom_white.asset.meta
        // source-sha256: eb2f99e146da30f09d1ed154a2f057cb0e341c659ef7bd00a873f4afe3b966d3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top.asset
        // source-sha256: e7c6986285fae70c91c4863b1fbd580d4fdec9dd30a1d2e1a5da1d5e9ed8b55d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top.asset.meta
        // source-sha256: 0d45950d5b3a0410db6e87b06a3709cd020cd8d1fe503d1cb9bd3114072c15d9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_blue.asset
        // source-sha256: 46bde6e7f5f71ad97c72446f28a766f45a871bb49e63d50fd701d8b977e49191 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_blue.asset.meta
        // source-sha256: 0c0b494c5187cc69dc4e7f71620b544f5f8ef41a1d3982d7d108e37b3e6c8ed3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_green.asset
        // source-sha256: bf44c43f34f11e2ed68bf916b23f0744d36a6091cc485e7052c2f672f226de6a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_green.asset.meta
        // source-sha256: 63ea1a5868e7e894ada338a1d4fb47b8a13233ca12e2775349f23d6f1c51d171 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_pink.asset
        // source-sha256: 3e506a3038cad43bd6dee7c32f63f9f69d99b51e2c3229821200ccd8be43f03a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_pink.asset.meta
        // source-sha256: a27bcf3c7ed78072a0c38e3cb493ea65167860ed05f017f37de204280f4b89df Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_red.asset
        // source-sha256: 82df74c80d24e0ae2a52a4d4766eaa20188c158aed7304cbd5a21c68c6846675 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_red.asset.meta
        // source-sha256: 6d3baded31f530bca88b7a48677eb4de70fbe75b92ab565b70809897185ce0cd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_1.asset
        // source-sha256: 46095cb3130b5766562348ab67f013dfb20bc1596c2f24df4893b409e5b45fc4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_1.asset.meta
        // source-sha256: 9be1ce491f717653105d8e49c0fb4e13008d5d362dc43e2db7b6ab74c0ae2a99 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_2.asset
        // source-sha256: 5157702ec72de8b0d2e18e59bd2ea15835f3109b2f601a85dc9f41c06310197d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_2.asset.meta
        // source-sha256: 25d89543147c7b9da87d52c7634adf2f2878eb723e974865048a6e0d0213666c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_3.asset
        // source-sha256: 5ef515511c26b70357912966d34809c36036ee5624753e93141097dd1c76f8ff Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_3.asset.meta
        // source-sha256: 63a761c8d659d96549b8440c8759b8ea063aa13181e01f5a7497d7a701a9c91d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_4.asset
        // source-sha256: 944838969fb4b0dc81f8c17afb0b2c103d1bad60a13df8637cac6af2ba913eb6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_4.asset.meta
        // source-sha256: 80865105e7046cce1110ef6b986b85bc43ecf05c3481e410de650c468a4a9595 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_5.asset
        // source-sha256: bbcf2d3e568769bddbcef232054c0fc33454387936313ebfb251baeb9a4d038d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_5.asset.meta
        // source-sha256: 00cd169449fd38aba52e65b08bf8d1a80eb5e9d1c9d75ab8cdc7ddbfdc612676 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_6.asset
        // source-sha256: 8b5a0c72595b7cf2548d80a9120db15a7c02e525be23e371f704b8d5081f0f81 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_6.asset.meta
        // source-sha256: db1db4ee3bea13df90ee81d9f173ddc970a92b44535497f2d836758e56ca852f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_black.asset
        // source-sha256: 85817193586e84031655a54ff49477dc4903de7343b60ff08e2507025940d3b1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_black.asset.meta
        // source-sha256: c75fc1ddb6fc8ab55ed630a3a20ada9f4cc5c029893741883a9da9611330fd01 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_blue.asset
        // source-sha256: 88289fdda239429f3d53e25c2f7fdd467a092849a0a964fb55b4b8b517be2a21 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_blue.asset.meta
        // source-sha256: 458d34e51cebf30dca5d56f478c7a50ea272a80148c0b2dd4ba19a34e7df9ba7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_green.asset
        // source-sha256: 0f8f602c61976fdda45c007a3c6caedd48c07dac958b94a2d3e23f9dc34ee2d7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_green.asset.meta
        // source-sha256: 95c3e00d6c2f76d148de4663f5b70101bbce585527c80b35c1569fcac37eaafe Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_pink.asset
        // source-sha256: 353f205ecf7d70199baf6ac923325fae55e7166bbcbaafb10307d8b4d75ac4af Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_pink.asset.meta
        // source-sha256: 749bba0c455c4d311ad585f469686971aa6f60cdd76202d426265ba46831955a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_red.asset
        // source-sha256: 8b3102f1b80ba9fc5dc1a9c326a4f305b00cc240ac84103dcbcc56393938ce50 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_red.asset.meta
        // source-sha256: f3a308fcccab76109133e48a7a58ebd8e58eeca5a9122976d8c9ab4bdfad798b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_white.asset
        // source-sha256: 6445c76119c8340091126424296c5753df29e0a384cfada39ae693f0b99dd9eb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_top_white.asset.meta
        // source-sha256: d9c1638e656d125dbc0e39b7fca68cdef9b26eae7007bb18450b331bc11bc28b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_white.asset
        // source-sha256: 6e86c5d2437e8dd947a55d41f8882ba707d15086ad67daa355a3a1f59369dc54 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_swimsuit_top_white.asset.meta
        // source-sha256: ff3e89f973deb8d177f49175a7b690b9b1eeefcb7c181170bebbfaff54ca49a7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_thong_anarchy.asset
        // source-sha256: 067d5093117e6a4b423580cdc18eb57e2ec689b00b108c87e1adcd807364a53a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_thong_anarchy.asset.meta
        // source-sha256: f16e80699e454a2b5a2346ae5d2061ae1a3e0fc4bd63077b27a9ff0e137edd5f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_thong_anarchy_purple.asset
        // source-sha256: 3eb746f28dd640fa0e845b15e735794a1663129fcd747baebacf32270b6e45c1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_thong_anarchy_purple.asset.meta
        // source-sha256: efb82ef3025f7c08e9b422b2acc6be2441bb811b6417fd2fb632243ff2751b86 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_tights_deadly.asset
        // source-sha256: fcb60eb32c173a30e5b857ad0b84d7ea23592b4b953a4ebd53539bb3fd336d9b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_tights_deadly.asset.meta
        // source-sha256: c9278208a89b7e222f7bcbfa7dafe2e8b5c270ce35985a265256208a903c6cb6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_cindy.asset
        // source-sha256: eb9615413a024ad64fc434c680cd8e6dbcfc1d988a0d91c4284f167576b99219 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_cindy.asset.meta
        // source-sha256: 1cc55a39a06fee17f3bce72511f0c074e864806f8d6fc5c912fe1e985fd2f738 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury.asset
        // source-sha256: 9fa05aeb7b3c02742d91e965fb370771b78811b57b2e68bd4ddb83b9398044d0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury.asset.meta
        // source-sha256: c5c774c0bd5c574efb294e154c27fc4342999ab35e88d3796b8204411e1aa458 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury_02_cherry_bra.asset
        // source-sha256: 50bf980a6b002725fe8ac35451be8455002414f364804118e94270893be02319 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury_02_cherry_bra.asset.meta
        // source-sha256: e1ece4fc70bfdbc30254949ef544ef0875f316d82ddc98e426aaf73f42be5ffc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury_03_white_bra.asset
        // source-sha256: b7c2c9976aa79340ae884cf39f3416be27d6f2ec5943fe9ecd32ce48bf525e00 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury_03_white_bra.asset.meta
        // source-sha256: 3ac39930e24f8469cae141dc19dc832a96074295c42f493074f7eee5e414b152 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury_04_black_bra.asset
        // source-sha256: df6b7c19fb55fbdf9d6dc56d26253e5dd608cd0cb385066e23055b39304cac5d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury_04_black_bra.asset.meta
        // source-sha256: 61da6b9486d036bba9dc184363dc4d5d905957b82e2298dfbcbbddc3081badbd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury_05_golden_bra.asset
        // source-sha256: c694e87827a5baf6948e0fce54b8a2b9dc8ba1e95daac815fc57b0b94c30dce3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_luxury_05_golden_bra.asset.meta
        // source-sha256: 9eeef86e7d752be76849a6aa4054118a57f7523f523b8eed46f177755ab384da Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor.asset
        // source-sha256: 6ce35c47b7729ef3e6d28104671efa69e8617439bc9e567f815a3f3f38afc347 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor.asset.meta
        // source-sha256: 94af602bfd090ed6046f16093b307d91c245ab66f2b9c08045bf3e07c1b61404 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_01.asset
        // source-sha256: 9c2b972089bc8a4c97876ee8cd786a69185b49d91546630be8c3323c5ea7cfef Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_01.asset.meta
        // source-sha256: 59326539e762136c074a303dc986d4dd61015d986a1a9d44df47dfaf3b0c03ec Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_02.asset
        // source-sha256: 3dfc87c88105ba5ad2d7c495df123588bbe4e19347d4a29acf72e36086d99bac Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_02.asset.meta
        // source-sha256: 773a927be4f0cd5331038ff1adfeef91115947b887bc2792a0a30208318d430b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_03.asset
        // source-sha256: 7ff9dd005995eecbdced9bd1336f9ace559f89a322d674085d9c07f0e902654e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_03.asset.meta
        // source-sha256: 6063679ee660722c56d900c7d3c6f13fdbb532e3ac1df4fe1e4fcf9952fa2e83 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_04.asset
        // source-sha256: b1d21829d434181acbed4203234f525d4df6a3500ee1ea68ef90a2fd0e861651 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_04.asset.meta
        // source-sha256: 8f6ae795e79822dd2f7f7a59af7848b0f3c1757fc1acf0fffc145847098b0ebf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_05.asset
        // source-sha256: 55792d7cc3da6509de268ceddbb8f784bd154d85554d8abc27da2f0967a05fb9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_05.asset.meta
        // source-sha256: 33e6600598e7c76afd01604a00825cc387258076351022be038301b692a6a903 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_06.asset
        // source-sha256: 2edb610cbd5b007a79eb41ef65825cf0be1e5ab797f846aae37e73e6a215d117 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_06.asset.meta
        // source-sha256: f2a7e9d2fa0dd0427770a36a19976cdba0fcf897d399a45717ed1aad430e0e16 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_07.asset
        // source-sha256: c178b03721a3687fdde83e6a1b7c5778e3e251ab7e46440ae8b978271c8be5d2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_07.asset.meta
        // source-sha256: 811020fd92c831265bbf60e48006fbe54e258ee4a7cd372e5138271330385387 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_08.asset
        // source-sha256: 49481651ec0c62b1afdbfa6458d09cc381e0364ef7afaf360180e591b0462e97 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_08.asset.meta
        // source-sha256: 301464bbadd249c1665e83cf9a0dbdd04c2db9f64d2fbacaa1632b477dbe2b80 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_09.asset
        // source-sha256: f030f97fb7297ebf3f96a3845601a5f3a1bc647eef3ea5fce29641bb4c384007 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_09.asset.meta
        // source-sha256: 8565a47b7772aad0a5d33a90b3fe21400a81f37fd40ccdc6f98e8db821c49161 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_10.asset
        // source-sha256: 35459b875cdf6fecc5dd76f83df205fccf4e932e3eda7bdee9d2e36ff9a9eb4d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_10.asset.meta
        // source-sha256: 922f87b2be1db7e25b1835a910b97bea1899121ceefdcb3dbb0be45b8d3698bc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_11.asset
        // source-sha256: d44a9029e780d40a5f9c9c57fe707f045ca12aa63be9b3444eaffbc8d815b2c0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_11.asset.meta
        // source-sha256: 817963576f9afb64d4a0365c888021951e075006d070f6b6e94f8a12a4c44b1c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_12.asset
        // source-sha256: 9f97deb10be23ce4fd4dc53b32b3d664b2f14969db697223875774a3c3de4868 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Underwear/underwear_top_vapor_12.asset.meta
        // source-sha256: 334d8d4891cdfccfcc20ecb329bca3b69eb47275bccbbae14c2ed1e6228066cb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armguards_fighter.asset
        // source-sha256: e9319be5e5ca8eb7e48437b2cdee38884e30da377505fb16c47a7c7af032fdf1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armguards_fighter.asset.meta
        // source-sha256: 5a80853d841a4bac8f9f5d5e7861140d5ed599a3c352deb9e3fdda17410daed6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armwraps_primal.asset
        // source-sha256: ed79f9abe651ad26086506e942cedeb256337d7b3587db918a98c0d380adb7b5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armwraps_primal.asset.meta
        // source-sha256: 993dfa2640d56c2d96927495549a96305bd6b0fcf19eba128e36e7cc81773f17 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armwraps_primal_sleeve1.asset
        // source-sha256: 54cc184a80355c487941e89f683fe8adc8e09425ebb3e4571f733b4ec6316f82 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armwraps_primal_sleeve1.asset.meta
        // source-sha256: 27f69445adf2ff7a232017ded0a359bacd662594a57e72621861de7b9145ebb4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armwraps_primal_sleeve2.asset
        // source-sha256: f482748347df077b4cb1748669644b10c68bb01896d604f86d0e8ac5cdb206ab Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armwraps_primal_sleeve2.asset.meta
        // source-sha256: be15c068f3a8fe764fb8ea3425ae9de4b85cc532e9116f7446045d0e413a8e31 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armwraps_primal_sleeve3.asset
        // source-sha256: a043a0a2c9d3fca27bffaa7f3db23d1d83f0fd86c1bc09025783b3e49d58f718 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_armwraps_primal_sleeve3.asset.meta
        // source-sha256: e5340d3c876a33c9e5693a7ef759391f017873ef8a765e668a9f3cd80697d044 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety.asset
        // source-sha256: 52493d055dfa54f93f577662d08c3d38b218d30943856ad27fd08703b930a877 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety.asset.meta
        // source-sha256: cf2d7d9bb65920818f4ac3237421cb68f4ce67839fddf1a3a39e23c0d4f6575f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_01babydoll.asset
        // source-sha256: 9a8f11850884c61379a60c663acd8ca1c31ca2400201ebab7ba3d700d068ccfc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_01babydoll.asset.meta
        // source-sha256: f5c258c1fcd0643ec032ed6ae8179c15853ae4c0e6b6991803fdd1353ac5ee9f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_02_babydoll.asset
        // source-sha256: a0453f525e5c5727dceb06ff68bc944bb5d4da07dca03691f7b22d5e78cb14a7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_02_babydoll.asset.meta
        // source-sha256: 6d1264fc3bba5e93eaeceb02ce9c2a68ed0e13809b68cc3c0210f8de7321f4f8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_03_babydoll.asset
        // source-sha256: 2d91107e8942d48075b005e124de0a65c2e465f30932563bc3561993df138ac0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_03_babydoll.asset.meta
        // source-sha256: aba716e25b01ea4e0dba1a226c1b939f5fd39ff0d0094b9c326f88318668317b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_04babydoll.asset
        // source-sha256: b6fa48c707036cbbf64b9acf59ad4df4037b8439b6704e74672baf684051ac6d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_04babydoll.asset.meta
        // source-sha256: 294f2ad3249f8d8a9d502de881bc3f138ce4b6894f0ed8d82c1990859624e2fd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_05babydoll.asset
        // source-sha256: 7df1908600adb692c8eb7448094e7ddc6dbcdd5835ff35855f330de6fa50db52 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_05babydoll.asset.meta
        // source-sha256: 48188592d621de7761069c00edcf02c956cf1e09acf7a0c8be5251fd6f603ab6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_06_babydoll.asset
        // source-sha256: d8ffa945de182699018b988b562b9045d909abb3fe7cc06ea999ae40e9d1ed7a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_06_babydoll.asset.meta
        // source-sha256: f3bf1829a54d1f5a441845d3858657d142241907fb97fa05ddbc55a41ef5da9e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_07_babydoll.asset
        // source-sha256: db80bac0ea1ecce8f0c57efd79d8ca1b19498e1eed02cd92a47c6f5cbef3c523 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_sweety_07_babydoll.asset.meta
        // source-sha256: 426f25ca88b60a44d47c12d75e09301f4ee299aa67c6a56429aeb8fde8b3ba7f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_tod.asset
        // source-sha256: acc25d90dbd6138cceabf01722f32283e66768339043a02984c4620ac979e839 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_babydoll_tod.asset.meta
        // source-sha256: 9b00b0be9ce5e98bf4ef7a74a7794e456df93f9ca92afebbe22828588943c02e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_blouse_anarchy.asset
        // source-sha256: 1e9f7464a91f1769dddc3256755a9df5e7de60c047dfa3a28d2ff17d52c5d6ec Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_blouse_anarchy.asset.meta
        // source-sha256: 2b8ca2d8336e6501cf272a5a1c458614a243aae32d255595b488ada0643be5cc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_blouse_nerd.asset
        // source-sha256: e183ea6324fae85c8aeb2c7292c879633a998c8b20abeedb8516ff9b121c9c5f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_blouse_nerd.asset.meta
        // source-sha256: 9892d5c1eaf0c92efe0b20f432c7bc11cc94989d87a976124e93b86a56ddb14e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_bowtie_nerd.asset
        // source-sha256: 395cfbdc8231685667175701e898de4d4825036c90b50c5033e9f08a4a5ba5ff Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_bowtie_nerd.asset.meta
        // source-sha256: bddbc422f208f0be0980c3e312155209d64c9f1ed90903ee62b83809b8c62bce Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_bracelet_luxury.asset
        // source-sha256: b8e97eeb265ee8e38dd43cab4544b830bc0e93761583162cb103e6ac3e3c22ad Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_bracelet_luxury.asset.meta
        // source-sha256: 814e79f0b9ff7ea8e9f478ca0d9a51697bf5ec5376e74855db06168c2ae190da Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_anarchy.asset
        // source-sha256: 75c3dc4d0d93c534fa6a9fbf35a97404695df45cfc378bcc8432855681420766 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_anarchy.asset.meta
        // source-sha256: f1da0f6635c32ca98e87abbab0deffa3e673e34580220e5713dda4fe12bcaaab Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_anarchy_black.asset
        // source-sha256: 5a858efa388be5b7fd925d9362ca35418e3b237dbf1680e81957ed9ca234e867 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_anarchy_black.asset.meta
        // source-sha256: 27556b49ea97655131e6d1cf4c1668461f96c5ab96f89e8e384ce56721c9ef7f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_anarchy_brown.asset
        // source-sha256: 8f6b21c095131ae321373d2d78cc4b14f33fcc169636d4335bb0fdb85405fb50 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_anarchy_brown.asset.meta
        // source-sha256: 545c777c94702db42647febc78ae6887edf0c170b7fb56ba83e009ae7a1de023 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_anarchy_purple.asset
        // source-sha256: b0eaa0fec854ee07d915920a8da28058ed4b05509fcfc67b6d5ef6d1939fd49f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_anarchy_purple.asset.meta
        // source-sha256: 3efdd5fa2975dad99394a36ad55492060782ac0e2d357ffede25cca867356c92 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_riot.asset
        // source-sha256: 94e8dd7d40a8b15ef37c87da36e34bbe4f409e51cc56429efcb64ade06bae1f5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_riot.asset.meta
        // source-sha256: 8a4ffa91f3537397fc3ccceed2af45ee38b128a435e093352cbaf80136bf5aab Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_riot_camel.asset
        // source-sha256: 7e6a5a59ebed19fc77ab9d97285ad89272018641a171e2fb19543179abd703c1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_riot_camel.asset.meta
        // source-sha256: edba304aa5a7fc97ac016233442041ab81687c1d45d36430d7cf02a910365cc5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_riot_denim.asset
        // source-sha256: 2e69c4d64009ac64fc3ec1580736620c2acfadd10a439b12d9c4b289c35299e5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_riot_denim.asset.meta
        // source-sha256: 5acca8b5cebbb3f93a6083c48ec51ffff16fc213ea349954c28ebe7426d6db40 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_riot_red.asset
        // source-sha256: 64d2a0175105307fdd3f31ae27fdd6d2f74ac42fae9270c40b8ac1161109a3ed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_riot_red.asset.meta
        // source-sha256: 5ab78448428e26ac08eaf19d32246dcbf0fd4f8698cbcd2dc0d3d6ef29a1a070 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_stars.asset
        // source-sha256: acf4c2b1eec6e58db7a91f34c561da3d54c89f3d11549ad19039f7c46bc99e1c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cap_stars.asset.meta
        // source-sha256: ec5b83b98ec4997a26aaf5301b32a8176872089b1346f814c611c8e4b61279b9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_collar_anarchy.asset
        // source-sha256: acff8455bdcb80d930e8c1f0561f8d10427fd04a97c4d04eb4780b5d2b680e25 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_collar_anarchy.asset.meta
        // source-sha256: 7fcacc32980296f9941df9cd9824381637b55dd131e87f8cbd15f8f3684d6e55 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_collar_anarchy_brn.asset
        // source-sha256: 91b64bf3b258f2478f5d7dae07fa4c65ccea788d30747f91de527cf265645503 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_collar_anarchy_brn.asset.meta
        // source-sha256: 63498765bd613502ff8fa5869491cd559269be46f3716c2fff96047756c9eba4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_collar_studded_anarchy.asset
        // source-sha256: 6c3acc5da12c906f827901c05e783d34beb4e2b6350f9a557a0d6b7179572aa0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_collar_studded_anarchy.asset.meta
        // source-sha256: 788a8b8b9502cfcefbf3ec5ce722ff6e2485c14f0804b332d91ad0e98f010dc0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_collar_studded_anarchy_black.asset
        // source-sha256: 47973a5a8b2a7676a607b8a89949b5efedc28675c732c7ef6a0b6498d01a8d6f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_collar_studded_anarchy_black.asset.meta
        // source-sha256: bb8e34425f84809b6c411ac2930abe3f65cc33ed40b505f40f7c362223e4c639 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_corset_anarchy.asset
        // source-sha256: 0c04a1482b173ac9b4e6f518c38c34d31b008ebb93b3cf67e9c5302703195da5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_corset_anarchy.asset.meta
        // source-sha256: d136096a3dc605ed76b480aa8ccca80d82ee6eb838f6d63ca9b240debd1b8168 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_corset_anarchy_red.asset
        // source-sha256: 3023caea87af4b7add8fdf9aa1f92ebd4fbc0b12f089e9dafe81326127fa7fa6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_corset_anarchy_red.asset.meta
        // source-sha256: b1c643f2b1b4ea293b203b4ea9ad62cfd165b12d8ea7610430f0d0c17b0ea973 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_corset_skinny.asset
        // source-sha256: f0591eeceb4c0fbbd99bc587db0b9a48ff78905fbd0e89dd5a97cb2db843f60e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_corset_skinny.asset.meta
        // source-sha256: c34700e797e6d053bec6a4a9ef51e9c238302748176c957ffe8a9d1186d45a0f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit.asset
        // source-sha256: e0f4e3af2c57d32c55162ca10703978e6b476bd0ffa9f0b7fc71dae9140993ac Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit.asset.meta
        // source-sha256: 8bd6035bdad331526d7da81d65318379b9bfb14dfc65176b491f2f618db6a605 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blackblue.asset
        // source-sha256: c2ba28d240068bd2742f5ac0a52bd72b2b86d7f2c41cec575a2eb347d684d0b0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blackblue.asset.meta
        // source-sha256: ed896e3f18cd67897176bf55b787e48a718f355dd4a29eb9406717dcb170204f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blacklime.asset
        // source-sha256: eb249d03d69939a6ef8c8324adb89d758288fd25715982694ed3b8f94a740920 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blacklime.asset.meta
        // source-sha256: 4ff0c18755d514edfcadf26965dcbcd30bf6a4a620753bb8575c79bfbac86c24 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blackpink.asset
        // source-sha256: 99879fb8629511e10536a1a90d906c0f7c494f5f42d4af46d8cbbe43e2c85f60 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blackpink.asset.meta
        // source-sha256: ab5cbb46e4f8b1251d98ad5ce249c3a95dfc507b71b32e95b6f409f4567de806 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blackwhite.asset
        // source-sha256: 7acdbe116902e4b0840b1bc1d58697f93feae21534f4725bd3788141ce0947ec Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blackwhite.asset.meta
        // source-sha256: bef188180b106615f4e5785f396197782a76a5a930564952291f4f5f77912105 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blue.asset
        // source-sha256: 6e6508124718d829fbf8e4dcd2e0f20b308cbbea74fa1c136db27c85285150a0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_blue.asset.meta
        // source-sha256: 2291b8c31cc4f7c008b92965f9a29da34d70a9e1f58dfb889cf16185a7252bbb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_lime.asset
        // source-sha256: 659d6737d83bad63c3fa48e890817045556b5397aa8870668a79efa7c182dff6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_lime.asset.meta
        // source-sha256: a5e20896c95b3fea469bd60a626d6fdeca77105b07952005928618fa3fb49bf6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_pink.asset
        // source-sha256: a192a18be258a6efaa4166a614385e4ba507f124e0bb860b5b77929c6f4734b1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_pink.asset.meta
        // source-sha256: 5a3e1ba2c2a1270759ab9ecafbc0f27939b0904e066f6cb3b6a78ac80eccba19 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_white.asset
        // source-sha256: 4f7dfd1fb5e96ec1a4d016c2292c737afd575e3a84cc129ce08988be0b58c7e8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_1_white.asset.meta
        // source-sha256: d7b056d64a40f38850cdad99f5c5e01338f238e6f53499a0de6c3f961741648f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_2_black.asset
        // source-sha256: ebe0a395acd28e4bc32b373fb743aab9f0a6048fda4138b60e03522c496b80c7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_2_black.asset.meta
        // source-sha256: 549bac6fb50d16f22e3968d113d45c7f473b4ce77bf51d76bfd839427ed321fe Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_black.asset
        // source-sha256: 0e93d5e0dc47c9c2b7f7c85c89f97e33672fc7646e11f2a00d79b6e6b97c965a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_black.asset.meta
        // source-sha256: 036848b7798f346456b3069aafb25b464953541b9e0a13132e79c3503dac6f43 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_black2.asset
        // source-sha256: f54ac1d1368af4f9421c0032b333bcb6a96a4f90bd7b0a2a487ae53cc1a2af5a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_black2.asset.meta
        // source-sha256: 8535c0352021d3135cb5bc9598c502e37377fb0b341f9bf10e19548a27e5e927 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_blackgreen.asset
        // source-sha256: da37eaf6d2e6320fbb6f32a304d551516fac4b583b376176d7c4138395681b01 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_blackgreen.asset.meta
        // source-sha256: d12ee2dbd9c1bc5e4dbf8ee1eff8d53d025c5f9a652e94f6f19fe44108a48697 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_blackmagenta.asset
        // source-sha256: 85fa256312568d589a1be1ae3c855faf52bc0f466abcdf00efcba178c99c32e5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_blackmagenta.asset.meta
        // source-sha256: 228a0c5811fa43446cc2d4b1e4c74237d7af4ab3cdafed18634842c5d9e62af3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_blackorange.asset
        // source-sha256: f41d8cc96bb1edacf493aa8068ebd7d429678246f0c273ab977f317950e7e258 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_blackorange.asset.meta
        // source-sha256: efcbc1a60b8597bc4e92434ef990fbfc8a9ae051ab9e1922b418d5682e0670c6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_magenta.asset
        // source-sha256: bdb574abd1e1cb0eea6168c5aadf0f9a3bd4e6b6066091c8ae976fbb659e1216 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_magenta.asset.meta
        // source-sha256: 2d90facfc62fc97d0a629d9b80d5a0d3597e4e49a9ab5a6e01c0f945b1fb72f5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_orange.asset
        // source-sha256: a953308e375c46bfa3adcf19882dd185a199b17fa77df5a2501e017fe6e31bd4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_orange.asset.meta
        // source-sha256: 824c2ef564e9ff0b33357985cbeea36f23c24f60fc5048176336a1a114318e40 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_turquoise.asset
        // source-sha256: f13b8f7e60751ddae5ac4ce57d2c7dcb70f47ae6468db0322e0ae4672c0be222 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_turquoise.asset.meta
        // source-sha256: ccd2f17ca7887adef76f75662b9742eb0efee323e7a57399fff400bb52fa107e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_white.asset
        // source-sha256: 4f16c5dff4672f19416491b9b672a295e4a9d5d189b8bf2b187b97d12979e459 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_3_white.asset.meta
        // source-sha256: 2f5c9bc119f4b4d9ec12e2d739c43b2b29ee5e5834db76d6308ddd27180f8b7a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik1.asset
        // source-sha256: 0fe0f874c92e43067a513701eab107041f1620aef57e5d99038f9fcd6f32407d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik1.asset.meta
        // source-sha256: 47188bf0081dbf7a45ce1dbf7d7c8f685b8bd6f82bf6a6c17731d817ec75730b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik2.asset
        // source-sha256: 94a7f171f2250b3410711e85389b7901d9c6d4c2e2c50019c77ea92ec6b97507 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik2.asset.meta
        // source-sha256: ef8fd16fb300649037373e3e690474180eddc823db58da3d7de942b7754acc80 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik3.asset
        // source-sha256: dde475f04974f96440cd2a8cc3d49332f48640b4b6d66a155b5114973bbe7ab6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik3.asset.meta
        // source-sha256: 94cf7c2dead787fa43d78036f43fd98d995ff619f8c60bf0d44d104f2ecdf23c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik4.asset
        // source-sha256: c1daa7f41d864c1df07576e4c96bbc0295fb49602d74c4e70d0c8534656165e2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik4.asset.meta
        // source-sha256: 66ec184eb51020398e2805f67cb2dcdf640dc6fbb5789d1c986bc96399e060e4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik5.asset
        // source-sha256: a955a96e60362424ef07a7805dfbd26a54edad3286704440970aac3fbfb2f7e4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_fit_4_batik5.asset.meta
        // source-sha256: 9e1fbaeeb17983ac0a3d80e462710aef17a9a1e792272e02c3ef9468244951f1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_idol.asset
        // source-sha256: 593fb20b59c99187582b3985c28f5b9e54e2053c163935a76b7642415651d44f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_idol.asset.meta
        // source-sha256: 49902f5ffbe14ee9ebd1eca8b352150fd031f7eedfb5f95ebeaf0847247f1e72 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_idol_blue.asset
        // source-sha256: e524bd10e6287161e669be1aee2623315df2d63f608df1b58598e2cd0e4ea378 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_croptop_idol_blue.asset.meta
        // source-sha256: 369a88828a37efff7201a3c3747f6857ba28db8342c0b4b7a0c6937b70f3a5a6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cuffs_anarchy.asset
        // source-sha256: 39c3b3bf14f98b9be27aeba1fec7ca3e3ab8b2862e236b12d4a9b426bfc1fba8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_cuffs_anarchy.asset.meta
        // source-sha256: c156fe406ec0e65e124cd8869fcb28d186ad856ddaee2f39819c1b21ca5734fa Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city.asset
        // source-sha256: 344e7758eb4a355f06562ccf8829e9179aece5fe12e5eb86f6e8449de1d36db6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city.asset.meta
        // source-sha256: 8317b5ecff028ded813baa7b2a5bb52c94118535f8119e5d2f686154eba36fe4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_02_dress.asset
        // source-sha256: 8951698b2a47188695892e5b2650cc43f43f2add485d223161e2e312c3d21b52 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_02_dress.asset.meta
        // source-sha256: f9adfa84d386975b1c3cc648951c516d9864fc484a348aaf5036157364c950a0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_03_dress.asset
        // source-sha256: 0d431b3bf98690eea78e991aa12ac6d2e9bebd55d5b66933a216f645987e757e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_03_dress.asset.meta
        // source-sha256: 78f73ac7e3e0776d85e2c9dd3ac2478c4574b24faf30dca80368cc5601ac5ad3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_04_dress.asset
        // source-sha256: 42a7bb7d23cef1c5f89c8a820047f456b0c703ceb8edb5fefcd9c9c9f596d4d5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_04_dress.asset.meta
        // source-sha256: 95f430df172f2b23aca4b9625807cbe6b2a27cbc273e0fb639592058a71c7ed7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_05_dress.asset
        // source-sha256: 4578da2dc840899a88965a0174a3daee4993e64d60c2ea1d9510ff8af48ddae2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_05_dress.asset.meta
        // source-sha256: 402b5dd5d8b74a07775378cb6b4d73b40bc3965918ac9bf77c28b98765ce6552 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_06_dress.asset
        // source-sha256: 3a07671bedd5e7230a722fd7629a3bae61669badff2b5a69d70c93720ea038ae Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_06_dress.asset.meta
        // source-sha256: 6c7e27097e2093c6858b5621c1b56d47fc85bff3584fd55bc2e88d8e479e29f4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_07_dress.asset
        // source-sha256: 86980ee7fb99889dce633db744788bae3e39b043f28ead97dfca62a217b24c97 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_city_07_dress.asset.meta
        // source-sha256: 63315fc6ff63094b7df4aa1a176885913544b53fa2014fef2def1fe07dc4bcc3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_night.asset
        // source-sha256: c06cf42c7beaf59b2355c4d7436fe6d859640893b901ed5d5fc6eb068ec2c56d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_night.asset.meta
        // source-sha256: 33e29b44dfc127805509afd0b9a166b62e254a8d53293222b3909cab3d8e754b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_night_red.asset
        // source-sha256: d59a0b62190b29c4d671eb187bdde6b6ab1bb85bfd11d13f52ac46844a38262a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_night_red.asset.meta
        // source-sha256: e51f0cf3bbcce4f784d16c68a59c3860f5443b0129ef9cc2f1a492edf06182f5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_night_white.asset
        // source-sha256: 2823dd3b5ccbf8b40108f2b69342c822761707b2ac821b9dfef7cd6bf6c5da55 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_night_white.asset.meta
        // source-sha256: e11130eda88c1b2ca87b977b49443638d237357fd4a15b7c37de294dd0cd2a91 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal.asset
        // source-sha256: e44df73c95c0f812213c34b0d5cdcccbde70b68f3bce490d25481e9a41c14784 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal.asset.meta
        // source-sha256: 4593a20501da2cb4d9557bb8051925dc3016a34fe79b409769c979b6f02e3e4f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color01.asset
        // source-sha256: b6753dd59da142617425434c145e90a4212346416aedd914780d526ff670689a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color01.asset.meta
        // source-sha256: abe1e8933301e9ba7427cfb7b354b98dfc3b7f371cb5ebc3fb4bb9217965bab4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color02.asset
        // source-sha256: b967324cef1dd9ab4ac6e9b57169efde186c7f5968d8ae402abd455b825e84d7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color02.asset.meta
        // source-sha256: b652882c5d87f214c9d38770d16457712983df0182ca508c82686ceb7c78b31f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color03.asset
        // source-sha256: 348655d403f476b1c8b989ceee802d9858605997e4a00e52780b1db2ddd87366 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color03.asset.meta
        // source-sha256: 6884472c6fdec72d8e18ba0d3346d6282bfb5c29aa0fba17501bae0dba4b58da Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color04.asset
        // source-sha256: 6797f2e5a91722fde7de4833412556b65badf7a761ab777b5c60d25ff2f9ccd6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color04.asset.meta
        // source-sha256: 37cde10584429807e5f95eb9acbb264f077c6896da92ccbc4f9842e40ae17c5a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color05.asset
        // source-sha256: de5561a1a4b42f2290be6afe3e3ab08b55d885c85eb815612ef6cb65d009779b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color05.asset.meta
        // source-sha256: db774aa5a9f525c3baaf9ed4af981c2368293925515e8b8c028eaed6f4c597eb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color06.asset
        // source-sha256: c397559f1f4c9ba79f74ed84ac328a28724cffd06587d9d380a6f174c1a0e4c3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_dress_primal_color06.asset.meta
        // source-sha256: 8855f529cb2b6605d8bc7cae489b3eef70161eb572ad4994ddbe369306217439 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_biker.asset
        // source-sha256: a5059297db1b2a3c8d1762df456ac355e8eb8671fb16886a103c538e08aff517 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_biker.asset.meta
        // source-sha256: 4009c19b79941f0d4fd4c76d7b182dac6fb5fa559a17a0d314d53897c65e3999 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_cindy.asset
        // source-sha256: 64ea34ac0d4c603e10786739e2d6d6378b16b6362b434b2bff286ded602bf5d3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_cindy.asset.meta
        // source-sha256: a91cc6832cebe8621c257ee2c47fa55a20537a2b67b9d35007cb9cad6e36fb4b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_classic.asset
        // source-sha256: 5e6af2b574592f1a58205668665e63206d24adf9661e9f5c13f4afea99760499 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_classic.asset.meta
        // source-sha256: 5ab073a526413c484debb310a0f4e6cf50cb435858b72d9f077f7c9b8f13b373 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_deadly.asset
        // source-sha256: df98c5a4ce91f5785492f45cce031250ac834cc7c01535c4cee2d3f2c01947f3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_deadly.asset.meta
        // source-sha256: 7dd203e3bbbb97ae635a8fb24b00ff1723d76ca46c4ed2aa0e08717f3f857129 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_fit.asset
        // source-sha256: a34b515bb47cb57f53b73d5a2b121322a209748d90d8acfa7b0e17505fe07165 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_fit.asset.meta
        // source-sha256: c98064b75123eb575a8607d7500050e48254a776b072d772fabd74c41e6e2f8f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_fit_blue.asset
        // source-sha256: be6fc3cf6dc71f2c8eab2b675a4eb563c7b5000ad1b915f6f93a8edf2e1f2311 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_fit_blue.asset.meta
        // source-sha256: 22166e2364fc2e54fa0badafe892979252b7fb3c563cedb0b5c4403e63da8693 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_fit_blue2.asset
        // source-sha256: 25d8af8464ffff3a7af5ad942893372a22347a2224a4186bb3882250ba9f6803 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_fit_blue2.asset.meta
        // source-sha256: 7f10412de0dcacbd25d51c71d72171ee16f517268c91f3b10cd6abc22bf92bc4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_lace_tod.asset
        // source-sha256: 2e80df750f0469eb55f841ff22fed83bbd8f3a175610856f22410f863e7c47d0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_lace_tod.asset.meta
        // source-sha256: b1df103a1f2e11c636f0728937e00821205314110ccf3b7ef5137de42891bfdc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_long_anarchy.asset
        // source-sha256: 13099da4d8581fda1f35087b6511f841630e31e7f95ac26c33735bac7258eb8f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_long_anarchy.asset.meta
        // source-sha256: 2e891fcf90fb8c1bb4719fd44fa612d4502223642a353c38cbcb966a6fc63a53 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_osiris.asset
        // source-sha256: e600ff8f8e5d8b4a0bec74e79c0659ee2440f6303ec9e2c27340638ffb74e495 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_osiris.asset.meta
        // source-sha256: 553ae685edfd19ad4e50167b8b3f783c2e8bae67859abbfafb07aa3a3fee553b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_stars.asset
        // source-sha256: 7c5da9b91bfda655d27f3de3d0ea225d4facf8567c1e0438649a8a67d77327e9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_stars.asset.meta
        // source-sha256: 5606c53c590bfb8d4b26f6d7c6321254a286f1c2dfca904d62b4d7eaa411a9bf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_strap_anarchy.asset
        // source-sha256: d988839eb195b6d37716e99a5b079a1da13c17ee239c4963756e592ecae95b4c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_strap_anarchy.asset.meta
        // source-sha256: 1f7fb2a2a35b0cf06535ee1cc4f3ce5d89ebffd541b5ea87187dcde0cd84d079 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_strap_anarchy_brn.asset
        // source-sha256: 3a420171a91520de7510b1aa72fa61c87cce96f464433544678d48769b6b4ee0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_gloves_strap_anarchy_brn.asset.meta
        // source-sha256: 3c612d863d8ee5ac033e0b8dbba04d11b411efcecd99cfdde69fbd67652cd315 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_halter_vamp.asset
        // source-sha256: 575f08c8aef38b76003e043c27453cbe14102f4914540f2e7729ee8de18e9987 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_halter_vamp.asset.meta
        // source-sha256: f31f8da502dc1f6923e7bca7b8d58976cd59993cfd77e053f45f942eec1d57c6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_halter_vamp_blk_lea.asset
        // source-sha256: 0953684f0d9137bc228913db937b6fd1f4d8997f6c54febbeb22a562a19b9c31 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_halter_vamp_blk_lea.asset.meta
        // source-sha256: 4397f7238d0a97cd4e8dc036b27bf2c0d3e9753f521d8b16d2a129666cfcd5c4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_halter_vamp_burgundy.asset
        // source-sha256: 69f73f6fabec216688be33f281c945a6b25012e7b2bad82079bf0a44134b26d3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_halter_vamp_burgundy.asset.meta
        // source-sha256: b595aee5361596497cd155f02baed9a516e9efadfc1f485047effe7539196513 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_halter_vamp_purple_lace.asset
        // source-sha256: 06eb718908d526442f3e7df6f77718b47e29cb9b10d1c5dfa683747cc61671d2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_halter_vamp_purple_lace.asset.meta
        // source-sha256: b5978c7c7a63f187a260581a702b5c3b7ccbbff19286554218b5e3a49aee5f4e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_headband_primal.asset
        // source-sha256: 2c4faaef91005954d609a4f9d23e50489e7374be1a66df06edbb9a1a340b9558 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_headband_primal.asset.meta
        // source-sha256: eaa25c21d6960383bfe1e8bd2880a2ea554a615b5d285c915d394dabdfb6a73f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_headband_primal_2.asset
        // source-sha256: 4a625da64ecc28092f46fb3b957988674bfbf02a6b68693d0436d5d89fc2b5b5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_headband_primal_2.asset.meta
        // source-sha256: 390738a24143e87ab13536373ec6c618708ad50a744007d94255b644c3227dfc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_headband_primal_3.asset
        // source-sha256: fa4b14e2b7c7bdd3ff2647776f84ea86db4d256476d6c85b6b0b8a068aa10af2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_headband_primal_3.asset.meta
        // source-sha256: 831affe757d7f2e3921fd965463365b1a20868588dd8382048423a053d284dca Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_headband_primal_4.asset
        // source-sha256: c1dca5af54acf0933af23d56fb885a523a6da1cfef2fa99ad2e151dac99cfb04 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_headband_primal_4.asset.meta
        // source-sha256: bee80a3b55a4ce0237e5fcba8678fc52fd294f4a65f1a388dbf5cee8ef3a8016 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_jacket_tek.asset
        // source-sha256: 58854065ce627ffa4ca53e6259eb9deccb64338dfee34d5349fe36116c675790 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_jacket_tek.asset.meta
        // source-sha256: af98170a37bc2c3e40ee30e14e6cd2b2056de7b65efbb8ff6d4456f9cf3d5dc7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_jackettied_tek.asset
        // source-sha256: a019afb5d338b410aafc2f4f4cab136ccd54ea2b20998cecc50ce1aedf2b3b06 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_jackettied_tek.asset.meta
        // source-sha256: feba8d680aa3e0b7cfae8cd973f992dee9287b6635e33419ca823b9463ba2138 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_jeans_skinny.asset
        // source-sha256: 876aa00f5b4127d2fee19e6b337d68547556bb35e4c1cd6c12af22c3735d646b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_jeans_skinny.asset.meta
        // source-sha256: f983dcbb043557eb5cf34ad2a950f412305ccb956caec90c3c013882c93ad11e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_keikogi_fighter.asset
        // source-sha256: be7359c1b48296fc6f7b77387480809aae3132b64d461b0bcfe0273714ad0b47 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_keikogi_fighter.asset.meta
        // source-sha256: 2a387e7cb40d6f5db1aaf7372e2bd3089b93126eb5afbe8cc511c338edb7ecaf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_idol.asset
        // source-sha256: 175cfb50fde7c080094d0398823e612e5766897a40da741ceabbdbe0349ac25a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_idol.asset.meta
        // source-sha256: 826f1c120ef2134260ff7a73f8c3c08111b32391cae352a7233083daaacb441e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_idol_blue.asset
        // source-sha256: e04f1e33a8bec30ca8c4f5b6bc66c25aafe25b32f8064e2b05e52ac4caafd90d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_idol_blue.asset.meta
        // source-sha256: 35dbd23edf21aeca1f0f0b8651fb636eb51612c63580a6188ac0655514b55a3d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga.asset
        // source-sha256: 42c1391e530a0271c258534f7c7e32c594ae4d90ecbddf73ba578cf5b03deab6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga.asset.meta
        // source-sha256: 4247bf034354e731f6d64099ade4833955360cc7571447c8eb74e89f92bac872 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_01_pants.asset
        // source-sha256: 645be96e6645e6982995ca879f22dfeff065e46aac8b10d2cabcc91d873dc8f1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_01_pants.asset.meta
        // source-sha256: ce5798c4dafe24a368fb0a94bf6ceb587e9cd1f802ab721886b769a5a525b98b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_02_pants.asset
        // source-sha256: 4310b4101e61a1a35d890c5e5b99f65509ff4e8ecdca0b73963803c04adc87e3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_02_pants.asset.meta
        // source-sha256: 3f23ac7a2427e407472af6d4b54e9a224fc70878e37dc0852c3c9d711fdeb5c5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_03_pants.asset
        // source-sha256: 034c1a0d33013b12ad8b4147eba6a2c193a29b1a85229c872f70ecdf5c4a140d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_03_pants.asset.meta
        // source-sha256: 8a1617766b8932a41ac3b2ce10706a258c3b19b64486f6c67545773adccae655 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_04_pants.asset
        // source-sha256: d5d075105a30d347faa4480b696744912516a4b04690a6c7d54cf0c0ab401763 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_04_pants.asset.meta
        // source-sha256: d0fdd0ffc6de85fd820d8edd5a677af678cdfb890246fce5f356dae11aba194c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_05_pants.asset
        // source-sha256: 5171385185754fd331c5c1b10cea8f3fd32b74b529224220cc75bb4e8e104298 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_05_pants.asset.meta
        // source-sha256: 4c92cda50557fd76f76e4cc8fabdaa310d5fc79584a3d0aea1c4f119b5b5f4e6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_06_pants.asset
        // source-sha256: a97dbc6e4ebd86ca2b257ec6f7810b00c3ab2f564de59447153a63a39e2a9c74 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_06_pants.asset.meta
        // source-sha256: 17eb93df3731022cd581cbbd7cbf68b14d78be9abb60ea3ce6eac65f84e34e26 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_07_pants.asset
        // source-sha256: 5c9c659ea39d7fcd73506fcedb947fc9282e15b435d810fa263d9270ba8bd27b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_07_pants.asset.meta
        // source-sha256: 70feab8280f030bce3583a55bc9108e7ec4eddf7d45652d3c893282d22a4adee Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_08_pants.asset
        // source-sha256: fc4840c54a32950bc45c77085ad23eddd629623dbd88b4c2634dac074a3f7e8a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_08_pants.asset.meta
        // source-sha256: 9981b60cdec3395784b8b7f626c23c430f510f9b57edc8484f749fe600529c2b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_09_pants.asset
        // source-sha256: 794464ecae818f39befef53c227e95bba12236fc2289e48b75426420a01a6302 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_09_pants.asset.meta
        // source-sha256: 1e7756f644dd5881041a941db433716e64bf503cf63ec65c0ddbae2ad2a336f2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_10_pants.asset
        // source-sha256: d8a2fb04ba3af4849a51cedf8bb00a08738f999b460ce17b46a23eb97e5dd279 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_leggings_yoga_10_pants.asset.meta
        // source-sha256: dac6b8f3873f7dea8ee68c8129e08bc541a8207c95bbae955e0d9b1941cc4e38 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_necklace_beads.asset
        // source-sha256: de39c5d2317a0837dd66f77087a9f0a24f7b0e37ede310b2a05a3a26bcbaa73b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_necklace_beads.asset.meta
        // source-sha256: 2e21a001a40488a1678043c70552d71ed1b05fc33c40dfa616da26707fd9f6b0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_necklace_osiris.asset
        // source-sha256: cdb4d8afcb057b6ca29fbe51ef7f715b5624c2cb73f9895a0308c85ab353b14e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_necklace_osiris.asset.meta
        // source-sha256: e6eecfae06c07dbfeedf83415f8e2eb9d67a8f76a5f38bff1be129b9b2ebf2b8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_outfit_reiko.asset
        // source-sha256: 53f4eacefe54b73d0f72868b589ddda73f0d2f1836a3e63bf1fabecc586c21f3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_outfit_reiko.asset.meta
        // source-sha256: c034d836f3fb7e6cf36a5c6b03db72765d53b53b4555167ce6ddf7c4a54753ba Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_outfit_reiko_default.asset
        // source-sha256: 7be592cafceab18736670b81687b1d502d367346c072a518295f7bf83e562f3c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_outfit_reiko_default.asset.meta
        // source-sha256: 52d9fdbe0d2fee7dc06dd5f1bbc42c23825707dba8745037fa4ac038a419f98b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_anarchy.asset
        // source-sha256: 886fed16c983d14723262249706193c38647932e2d8cb55039b428cfbdced08b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_anarchy.asset.meta
        // source-sha256: bc10554492b17e713aa4320d24e0e0b99639e3a22c875afd664f449584ba3c83 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_anarchy_black.asset
        // source-sha256: 4857416994fe55b7b6f639f2469117391da87acdbb43060c11f7f6e72ac40fde Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_anarchy_black.asset.meta
        // source-sha256: 8f69ee362b0644ae01b159ca6c17e5e01d50b42dd4a2a144255e897b50e9d755 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_biker.asset
        // source-sha256: 932eb7f34c3d28d1806fde11ce18b4608487ba267231e22060892bcea054f6ee Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_biker.asset.meta
        // source-sha256: da50f482c57717a133d23279164d42388af098674b4e5cd4fc1afa474100f77c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_fighter.asset
        // source-sha256: 11d54dadabc53bc3299943650b45ffdbddd275b4354c1b6d9f64f83541582e8c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_fighter.asset.meta
        // source-sha256: 541d193fd73db0bab9d5e7e9aeaae1d3f597ec61ecf345c5862b66ce454d395e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_ranger.asset
        // source-sha256: 134ddf9649374fe15008b535dc1f8721efdc5a4f5ca1de257599bf14ca48391f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_ranger.asset.meta
        // source-sha256: 38990da0ff68c999532a0ecaf372886759de2bad45d7cfe588c92a79e4fde6ac Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_stars.asset
        // source-sha256: 2641910467a7d0ed58342713bd667b26ba7210865116fe43fae9c164b74e1d96 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pants_stars.asset.meta
        // source-sha256: 227320c150451b7fb84d3803a2bce84edffa229336668ef63054bc992bca579e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pendant_amy.asset
        // source-sha256: 57d85cca25742b6f1c527bf4351dea61fa71536b9b580c72b75ab7f9a5b59a61 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_pendant_amy.asset.meta
        // source-sha256: 32ae63c1469d40df87604e328c6c67c2d7058c0b4f773811ccc4d3ee5c8531fe Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_scarf_classic.asset
        // source-sha256: 6e0f44f84112f46f3ccb78582cdb2c0655387701d1f6b8e69c9e3d250c622ea8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_scarf_classic.asset.meta
        // source-sha256: 581ad415ebe95e13595c96861688f62cfb6e6e1de4a3bcdeaf1449cfef54751b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_amy.asset
        // source-sha256: 08acc57c5e3c62faa86503f4e53d55d5ab541641fb6c129d38446cceebaa9f2b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_amy.asset.meta
        // source-sha256: e83c7480b1c2c999a4333c17220954806d14f097fde8f9679b048e7c00ae6cd4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_amy_07.asset
        // source-sha256: 1ebc9f8a083214ab18a15d1ea3362b6da8438ef0228892df0b4920b9f994df70 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_amy_07.asset.meta
        // source-sha256: 6113e9e875e9157dc98a7559fdfe3a8433c5470b876a26e8d055eae6ace8ab85 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_amy_08.asset
        // source-sha256: 5ecd7502d24659cfaa0decebe483cd07395c08a1122e9b4bc5e6cc993213876d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_amy_08.asset.meta
        // source-sha256: feaa1caeb8269eb5fe900a048bd0483fd8f48d14af79c00237b4d96a2676ac26 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_riot.asset
        // source-sha256: 79f848de087cf26c067e714838ddeaf76705fbf2faa5977a6923fb6fd3d9a6a6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_riot.asset.meta
        // source-sha256: 3038352958d8fae27caa0658712f8c747aafbb1f9e213dfa97a9468ab1bb9dd4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_riot_greydark.asset
        // source-sha256: d988855a619a1cf3f283d0fb5fc6ad8920397e7970849835962015f1dc38afc4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_riot_greydark.asset.meta
        // source-sha256: f1628baf8e343003e9b7656748bdf827d7b579cb8718bb8d190811fe855dd30c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_riot_pink.asset
        // source-sha256: b195623ba190f796ce645510979b1c621ce3279c80278b4a97a32f229a34f069 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_riot_pink.asset.meta
        // source-sha256: bb7395e38b34e1b37d3dbffc887edab7d715c6dfdb7970184253f4dd6ad05d0a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_riot_wine.asset
        // source-sha256: eace6413a82a5dcbbb2cc0807ec11d28bbfd99ba514f1adc2fb0a50b9548cddf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shirt_riot_wine.asset.meta
        // source-sha256: d92d4ba4596659521427d2a75524e7109f07db9cc4aaa714835bbc00c29666a3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_cindy.asset
        // source-sha256: 22998621c95600c7ca3db60d647dddd9db4286eeae184d6dca592ee0712381ae Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_cindy.asset.meta
        // source-sha256: f30edef289824f7a1806be454e6ed23869eaad2b7f8cea7b9edbd421aa8cd49b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_classic.asset
        // source-sha256: 424f879970657cf7d4f5812a9e450c13bceae2b1755dd97e1477ef6c8aad9697 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_classic.asset.meta
        // source-sha256: 8b4ee9a03803e11044a082eb635d2bf1b6461684875eceea95daede6ae4a129f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_deadly.asset
        // source-sha256: 36ad7eab4bf2f48fd83f0ec7af26ca6517f6f4ea665179c155cdebe1839dda21 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_deadly.asset.meta
        // source-sha256: 21ef4af59324115b91be80d20886e86e76b96f256460bd7bbd4113ccc91cd055 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_osiris.asset
        // source-sha256: c63147086e396df61da6fa344ab44b88153cb7ada7c29cd7a0e6262241746ee2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_osiris.asset.meta
        // source-sha256: fe2786eca62827d4c8cd11f6fd4e31a8f88de4f294f28454c607f16f42769c3a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_riot.asset
        // source-sha256: d007627229663ba4eb5d705d1fbbddefbf487b677f932bb4280443ee0f97d9cb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_riot.asset.meta
        // source-sha256: 30f23db625f169851ea0f53dba1be0d426750ad09df70afbcb3e10b3d2f4a1da Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_riot_brown.asset
        // source-sha256: d42a42e738b34cdb075605d34093a91fedfad2e9b3bea4cf0f69aa0f79b0468e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_riot_brown.asset.meta
        // source-sha256: 5ca5d206fbc108d529d79bff97957bd7f40e84c2f5db9e6ed0aa8c453d69b570 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_riot_denimgrey.asset
        // source-sha256: 086510e7ba6d14f3d97c13faebab93e09be66f6d57d14db3368ee698f4222e5c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_riot_denimgrey.asset.meta
        // source-sha256: 6dd2d3be1084c7c150a3f1c370ff1b23c0309d9ddf005b4163074978a20d2649 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_riot_green.asset
        // source-sha256: f1ada5312e3233320bca25ab8491900b684640123f1b4b19866ba7f46b8b3d42 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_riot_green.asset.meta
        // source-sha256: 6d5b0e0d94f6cac7900c4a660c0b829f2d0c499a7a14f229e3ba50b76456d280 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_summer.asset
        // source-sha256: 6ff34d3d4a3865a1f3ae25c4fae0bfaa3dd0a0289ba9c42b5519ad63b3ad75de Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_summer.asset.meta
        // source-sha256: 3d23de4e3218deadc877512a62b7f5d0163585a36bfa23199d263dc102311bec Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_vamp.asset
        // source-sha256: 3f0fe64553f1eb8a75f445a61d71c71b8ff14cda396a90cf4145d2b95d0e8915 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_vamp.asset.meta
        // source-sha256: f5ba2d49a5b59d56bda64f17457454e631fd2efec4d1d2425f7f2498e2f9f1b2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_vamp_blk_lea.asset
        // source-sha256: 54e348e7c9736185e3f4783cadad7d437e6a93bb326138fcbdbdfb6e0dc6b82b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_vamp_blk_lea.asset.meta
        // source-sha256: 763c6d33391fbd63b84a277c9fc365fe6eb62078b575fd416ac6a4755eaef643 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_vamp_blk_lea_lace.asset
        // source-sha256: d3959768bfb6e05c4c1c36155e9f14b8f1a71dd0261703d4cb89c625ed2e3ceb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_shorts_vamp_blk_lea_lace.asset.meta
        // source-sha256: 1311137a49bd9608c23190a2aef1674f2018bebfbb385b9a900ce799148a7998 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_alloy.asset
        // source-sha256: 16f11538924839ec508580122171c53373d4baf5b301912ad2c45c5c8f124d5e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_alloy.asset.meta
        // source-sha256: ce75f467a70ee6f8167b0881a5613f6943871b7575e5c89ef0a9bb93c2adfbc8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_amy.asset
        // source-sha256: ab05b274e594d4a54f1c7ed1ff47043c52cdb1027f4347f315c9be2f0a5c338d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_amy.asset.meta
        // source-sha256: 9b2fd1f55fc9f4b8d66fb2f02e39f571fd70a7be94b1c3bc6eb04e0936dfdff8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_amy_04.asset
        // source-sha256: 7483a21bbeed0888cdd3e3a63a7ec6ed4d17ed7b48f12540188cf32bcc562017 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_amy_04.asset.meta
        // source-sha256: a687c161d2a6949bc001f1082503f1e53ca25c35ac4ecd1abaa44320412870c0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_amy_05.asset
        // source-sha256: a3d92bc7132bd351c6a6de4ac857eefb1ea00185ef80dc2ae258d488f59a2a37 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_amy_05.asset.meta
        // source-sha256: 64c1b68ae7a9053166e5d619ffb1818492c0e49d9b848cc6fbea0fef035b1f68 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_anarchy.asset
        // source-sha256: 80a8d1178037fbc1bc47b51d4a801240b4f7f0c9af9126baafc12741d8cce819 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_anarchy.asset.meta
        // source-sha256: b49ef35443a7fb64f9e0b3af441354abf05376adcb20f01b800755341b8c990f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_anarchy_red.asset
        // source-sha256: 2f7e1d0acf0bb5a0ed876cd4ee14b1893475d0354e6fbaf049efbe7a21a9c4c4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_anarchy_red.asset.meta
        // source-sha256: a89bfbbbc92480113b1f725c4da89d881099f66a0d59ba8ee1bb4e551114b079 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_flair.asset
        // source-sha256: e07f164876f7efcd337062accc2660acf10d739117e7c6c9b50945c2edf62ff3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_flair.asset.meta
        // source-sha256: 4ac2d0f76acff4ee2037aac4ed2dfed22490b74bf2f60cb88ffe605e93a8c5a4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_flair_skirt_02.asset
        // source-sha256: 402beb5dafee53d1fbc2381a1c35b03c8a2f1d5aa6d8c2295d2423f69ca68f97 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_flair_skirt_02.asset.meta
        // source-sha256: 377a13913e6bedafc7be82fd4077f3e0e59941199b87c18b22691bdcad5872d0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_flair_skirt_03.asset
        // source-sha256: 351873b5786b674b08344047c4e712bad67cd2641abf5202b6032edf582478e5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_flair_skirt_03.asset.meta
        // source-sha256: faf2ce6a65182fa2569c40124079578793a4b7f62edfc7cbd1f93651e261d825 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane.asset
        // source-sha256: 42015831d6a34b7530116c7e9991e60605c854f88831b8d63b7f90a70c280871 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane.asset.meta
        // source-sha256: 29f7802ccfd53dd3ab641aafd67386811b6415ef875caba1911cba16a792740d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_blacks.asset
        // source-sha256: 3466db488da6ff11c4a24b2d56ca9f69975e071a282ade412dfec1d07b0e3664 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_blacks.asset.meta
        // source-sha256: f5d6868a6e06ec3f2016a0cec4d5ebed1031132006fbe90b6d42d2cc79638d39 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_blues.asset
        // source-sha256: 97894d5ce43a578996e2b1124665587539390968ba13567e5d0664d458d8d67b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_blues.asset.meta
        // source-sha256: e58392f6ae255de1c6a9435c96746c695ad36a74f0593a8ae70e7fcb71014b45 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_bluest.asset
        // source-sha256: 6a82a35009619e460f9cc9d50f4a6fc2cb0a7da0d842e70e6aba9de958647724 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_bluest.asset.meta
        // source-sha256: 6b9c06863331534f6ce5a5d8ea1e22abf1945ef8cded1bec4d6960a5b02c4ed6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_lavendar.asset
        // source-sha256: 095c83c6acb2a342b2b6dbf71753fb70727b306c78e300523c87da28ca464d4c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_lavendar.asset.meta
        // source-sha256: 85a47c9310d57faa51401b5174bffc7b9ce4cb1e480483ae67bb803f079ab7d5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_reds.asset
        // source-sha256: a397818e976a0b6edd22eb53428b27e3e9ce83dd6797e15b91da7b90fb6f3724 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_reds.asset.meta
        // source-sha256: 38d4dd460c26531f14bc82746bb09cba2f4ef29d6aec60a58d5c1b6c61419d47 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_redst.asset
        // source-sha256: e12f442fdad302c7db37968c6911f28380f38cf5c5903515b702c8dc1925c2da Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_redst.asset.meta
        // source-sha256: 07ec355c3b86bad7f57a5289f805e61b0a554b68544dd3fdda86d3fcab96f5d2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_rosey.asset
        // source-sha256: 237a307e9bca525d3a210f43248d027b6be28a3e7b6d4ed1fcd6b9b4a214c4b7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_rosey.asset.meta
        // source-sha256: f99e19c3f289c34e109ab9f091bb5f8615cc4fc956031ff9d40271ef8d5463bd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_roseyt.asset
        // source-sha256: 73b938c693ac5fc6f2af7bdaec3c3cc8254e8c3eabf14b758f35bf9198f753f4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_jane_roseyt.asset.meta
        // source-sha256: d570fac77e8df342672689838df7092c5c142750ed35b4acdf057fd7ceac963a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty.asset
        // source-sha256: 62d251e83e3e86f825ef4bc326627e75daacfcf6ccbae99ce5922ca8d55fb86f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty.asset.meta
        // source-sha256: 192f7e924e6a7c8fcc7741cb98826c612a344b12a3f4ceb82796ee3b89c4a14d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_2.asset
        // source-sha256: 786d8f195ed21c830f8c351dac5aefefc85c082aea8799fa73da9cc0aa4805f5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_2.asset.meta
        // source-sha256: 32d8f20f5d581604c31ea89a96ef4985b94eb9b34d4a93735055aa3a2248385a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_3.asset
        // source-sha256: 781d3aea73d2f168754fbdf77141eadbc65e8a651b7694ec9eed216c67a90487 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_3.asset.meta
        // source-sha256: 625e4db3c601c0d05164aa87a61df3364e11e5fc72805d436efadd31be5ed960 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_4.asset
        // source-sha256: 9871d40ad4798080ae5a35ccc731f197f610eb8d83e0aa19746f495b6e5af6a4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_4.asset.meta
        // source-sha256: 5f1910eecf4de50f8ab5398cc84a940f50e8e144244c530761fa19323ddf0ca4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_5.asset
        // source-sha256: 54a4305ba6e7b207dd960ccf3ac6445fad5141083b5d9486c6942d5db9b66ac9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_5.asset.meta
        // source-sha256: 3494fb82f694ab68aea26c00213972a99542ed60ad64a7dbf2d08868d4f4833b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_6.asset
        // source-sha256: 81d8ef3097e277a85b853c8e66a1fe5e95655d8aa8154ecee0ada8d53d9f3c41 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_naughty_skirt_6.asset.meta
        // source-sha256: 5a2991af472564aa32db909cb6c7827bdf36f0f29842f738f385b9e93abf1395 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_primal.asset
        // source-sha256: 2373b25df56fe1e5b98f6347dcb42b5eff5653a14e2a0cb49e16c18b81e167f1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_primal.asset.meta
        // source-sha256: fc672b5043df92ac26118b03b9567e62ef891b388243cf7fb5122130ff3cf14b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_primal_1.asset
        // source-sha256: 1d8037df10cedb1b271e34bacb39373cfe59b84c299803e53b7b26e346371ae8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_primal_1.asset.meta
        // source-sha256: 15addca0ca2dc1732f8542eb4eb3c7d41710fbaeb49d888d55323b496de9cabd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_primal_2.asset
        // source-sha256: 62463051e57713ceff2c8a7328df21fb35e10bf3914e93eea97f04c85b07f9fd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_primal_2.asset.meta
        // source-sha256: 3879a0fc2ea7967db143a3bd38f815c8eca5ba7820358c544f5ac430535c8a9e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_primal_3.asset
        // source-sha256: f78fef41c3ec652419328e382b4dc5c86841411381dfed339715370e526da2db Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_primal_3.asset.meta
        // source-sha256: 4575a8a7dbb17c838e259f101a0ee6c47a022ddc4ae17c0b26f18a070b615cc3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_wild.asset
        // source-sha256: 37ce09ef40cc71778d2abfb4cc9a3aa51751e320dbaec509bf03b617f231afa1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_skirt_wild.asset.meta
        // source-sha256: 521333675709840814a6137b503f44071fcb5bdd1b249dc28bddfdbe04bd4408 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sleeves_idol.asset
        // source-sha256: 11ffbc75a2fab8e50dd2cdb682c20fc4026fb2b9d580caf25c8883667adac7e5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sleeves_idol.asset.meta
        // source-sha256: 1cf407c2dc48810ce0c02fcdd6fa57f051a05c51c70652ad226e10227fc77f99 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid.asset
        // source-sha256: a709dbb15f070eef48d924e049f2cfb3381d761d254286020f53253b71c1320e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid.asset.meta
        // source-sha256: 9d6005b9c967059510ad1af78a64e1096393445e5e2bb81aa21001e3ff913028 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_bluemesh.asset
        // source-sha256: 496af219e37ad287518077388cba2241ddb455c7abceadf854a28417a14d7fdd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_bluemesh.asset.meta
        // source-sha256: 1fce3274e9320956862b622e5b49f4ad3604809ab5c250379a4191536d223387 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_blueshine.asset
        // source-sha256: 3cd64d11141a63b71ac6042d80675cfbe7a8657043627461d22375a76a5df085 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_blueshine.asset.meta
        // source-sha256: ec85c5641f260615431c6e9411b4af6fc7c270376b9d39599c344755e37b22a8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_pinkmesh.asset
        // source-sha256: 02c02031add814e5ad7c1aed1daec5802a522ebfcdfa2e21a26588b67c1d0bbe Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_pinkmesh.asset.meta
        // source-sha256: 6a55259ab21075b3d9762c3872893c1299527c3bb81310c3f3644b9ffbc875bd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_pinkshine.asset
        // source-sha256: 4ef9cc0181be175b45deb9d0dc3ae67c9c72b96137947782d0317eaa446d6d96 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_pinkshine.asset.meta
        // source-sha256: fcff3395fcc3e6453d16c069fc2c2df8c0795263aeed3c11cdeaeed388e96f49 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_purplemesh.asset
        // source-sha256: 98ecf24b00acc62a258710105baaac3cc31227f767e92c94f5f058351ea05cbc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_purplemesh.asset.meta
        // source-sha256: 3f349fa5a1f32db5879c6bf43ba8888e34c7916b4e2d308c50a2eb75f5a5fed9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_purpleshine.asset
        // source-sha256: 1db69f724c336e3907e5544439b5f01a6571ce1e3cb95bc568dca538ac16c429 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_suit_bandaid_bs_purpleshine.asset.meta
        // source-sha256: 195ec85e8bbf7bade3332d840f2220adc7bb1ab458f9a8353bfe97d34db8e48c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury.asset
        // source-sha256: d1bc33f6bf85c955892d7066c06b9a8af1b716a9fb2c566518057f5665065366 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury.asset.meta
        // source-sha256: e869b89e034a14b4490745616aaa5e1108f1113a8332c96c483ffb5bce119a13 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_animal_print_color.asset
        // source-sha256: b2a66fe59cd5c5a8f453cc00aac0e5ae3723f18b25f4ff1807639b0a2203def8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_animal_print_color.asset.meta
        // source-sha256: b55d40a72b60293cb09fa6986a4bd9f05628d548822278586342bf74cb06393f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_burgundy_color.asset
        // source-sha256: 212b875028de22e8e3664a4a4013030c5b2d7f3d209922f8cfc1924b1ccd7280 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_burgundy_color.asset.meta
        // source-sha256: d8f0f68c8a45cb6a246c29f9399bc917d86731d77230cf7c334850a360a568c4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_light_peach_color.asset
        // source-sha256: 11e9b172964f6da0a49f9f816348009608dad10a128888b9c8e5a08f683ac15b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_light_peach_color.asset.meta
        // source-sha256: aa8534b4a6e8ea0f6b4dfdc90158bda42accdfd0461db8bd93ab0788b281af37 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_rainbow_color.asset
        // source-sha256: b53b3313eb033474b9ce20dd0aab647dde2e55e6ee982c50473b74743ca5cb28 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_rainbow_color.asset.meta
        // source-sha256: f8047018c2a198dfc6b87ee080e320fa42fb814c967f44b1c2bc333ac65efdf0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_sunset_color.asset
        // source-sha256: c8d9f1e7cd26e67ee09971f52808bde3651845c3c6f74dbe7c425447284e175a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_sunset_color.asset.meta
        // source-sha256: fa543a140ea6edef3d3a872d967d4bba5910e57e66d48b93f34e620161d32cde Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_with_color.asset
        // source-sha256: 5baecc515c021fae51a5d21c4dcbb74b2e8dae63bc8aabec51948cae60b85f97 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sunglasses_luxury_with_color.asset.meta
        // source-sha256: eb85af9916d4991105afb9875164f34fc58a9b3777dc2eaab2d198bcb13c4ffe Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_flair.asset
        // source-sha256: 50cb6ca10d867ec1a0d416eb20c12f8cac6523d9fed44631b44f4e89f33f0b27 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_flair.asset.meta
        // source-sha256: df08c79112d1fa5d504548c8a311bf2cf5da94cc9e842eb19f5837f31d54456f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_flair_sweater_02.asset
        // source-sha256: 702ffcf94f5a196aee8d30519953facc0b1b5aa2363ae8ba5042412974b8ecdf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_flair_sweater_02.asset.meta
        // source-sha256: 4be8bcc81dcbda354b1946df3b9fa3f9daf8b5d2fb78c8df36aabeff7fa9ef44 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_flair_sweater_03.asset
        // source-sha256: 7819ac7d4d0fe58c21840a956555e509ff9ca0a9d2dfe042a83c4d6108a00c13 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_flair_sweater_03.asset.meta
        // source-sha256: 45a0d337c610044fcb74c9c68e00fe904443846494fd71574020c3c0696e59c8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty.asset
        // source-sha256: 2928e97a76ec5b167241a2144726a14f361ac89b98f917ec5273600f1b3dc75a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty.asset.meta
        // source-sha256: bdfd52db072459322976079816b16e11527f14ebdaf48c4918ace79baa14c6a1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_2.asset
        // source-sha256: 88df5c0e4f8e3a949374c6b3a55fe8e622e1e8c95bdb31b465364e668c8b5f82 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_2.asset.meta
        // source-sha256: 656be0147e426e43332c6f9c95c6dad6b7af596bdb2f6109c4bd14774bafa681 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_3.asset
        // source-sha256: 8652c550f38de2b6242788276b5ba0b5449737da1b0676dca5c54e7236dd6187 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_3.asset.meta
        // source-sha256: 9494595b99fef1412760eed72453fc853bbb12478fa329ea640d1a35681aa5f2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_4.asset
        // source-sha256: 358e6f2387cd83c04fa3b8903f14c9ee5a11eaeeb27f3b17ec9a4d656d519671 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_4.asset.meta
        // source-sha256: a54f6b3579330c61797257ba023857f73a7023eef89e5ffdd318b5ca0d909153 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_5.asset
        // source-sha256: cd60069968a8f605c60e8eef7689a355f6cba9b10b2600e9145e398d7834bb5f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_5.asset.meta
        // source-sha256: 5de779c977bba124795c15ae515a91f064743a304f4bf1be5301bf0fde088e95 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_6.asset
        // source-sha256: e5f1dd1c26afb4a128de42e211c9af7c6d498e36788c6d7b35ee4839168b65d5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_sweater_naughty_sweater_6.asset.meta
        // source-sha256: 337f67793f2089359e839e2343bf5a6681084ca3c59bc3c254fef4cd52249ed9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane.asset
        // source-sha256: 01307e902ee5ef9530522b0b549bfd0013798d15dcc8b21c207c627cfce4c492 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane.asset.meta
        // source-sha256: 55ef3577c247a9039fb539fa6dac49bf6cf39a7942897debd05a9275ae7e37d3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_black.asset
        // source-sha256: 27d84c1c6ea848fed8f519064016838dea16585664d7673f80b6f5bf513ce456 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_black.asset.meta
        // source-sha256: 25c7f7a532cc0f759ffe0bff7019823f1d8412a34fb02c264181e068a4919b91 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_blues.asset
        // source-sha256: dd28a84754579d918425b638b0ad7ac37ba79b12cfce6376ff4d3aa87c7c923c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_blues.asset.meta
        // source-sha256: 096f80161d9ef3c43eb5fa0bba5ab757f8bcd3bb08832e18b76d1fdfabae4b02 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_lavendar.asset
        // source-sha256: 85aab67547efd02c762b9d973a9e67cd3e2238d38c015f198d01e0ccd80942fd Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_lavendar.asset.meta
        // source-sha256: df94c8b765b722ad2aab52e8512b12f283caaeaf7a198053f1525eb58ebfb1f4 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_reds.asset
        // source-sha256: 2c941f747e162974704ff17f3defa0259ea12f7801a1072afbef0c0324428c1e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_reds.asset.meta
        // source-sha256: 2939fd47b304afbf582f48454f4bc279e454b640dbc7740beb6708dbdc12447c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_rosey.asset
        // source-sha256: 67f2adc9858d9d26429a78a24af9e09c340bfebe0dcabda4cfb952aa3e76b7de Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tank_jane_rosey.asset.meta
        // source-sha256: 463dc752efb6ae627cdad1e7d71f65a028c031d5b3091160fafc4e6440a13a70 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer.asset
        // source-sha256: a42e5b1ee1174436a34df3ce2b1f910dffbf46ff139b0604898a09a23f2c2b5d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer.asset.meta
        // source-sha256: 41308706bfeb582af84b8c19fd21469edebf49f1ddc4cddc2ba723715c2c9243 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer_i13sw_tank_02.asset
        // source-sha256: 76df1d56178e2ef00a10508a39e1999381b9fcca01f4d5f8e2029338bb27deb8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer_i13sw_tank_02.asset.meta
        // source-sha256: f9166e9d6e2db911d52684157951ab4978b3296c06084aa41871532c08a97b17 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer_i13sw_tank_03.asset
        // source-sha256: cb4d6b00224c8f762de3e5718189fcaef024dd36657694352d994c69417a0f49 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer_i13sw_tank_03.asset.meta
        // source-sha256: b3f079ab0dc73686c6f8f8bf0df0fc77a35f18ce749fcb45f91c9f11b7e4e223 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer_i13sw_tank_04.asset
        // source-sha256: d4aaa922db45fb9a5be1c140a5dd253d2078d205f5e82c8c912abdc45b8ab3e1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer_i13sw_tank_04.asset.meta
        // source-sha256: 4312b44d8c6dac257f6c22fe03282af81f3dd23ffb4ccc93d3ba05f42eeeb95a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer_i13sw_tank_05.asset
        // source-sha256: 4252c3ce36dcdb52ca6e882ee39e7f8b136160d02cd650f66b97763a1d76f187 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tanktop_summer_i13sw_tank_05.asset.meta
        // source-sha256: c1aada4b237f218b3f97b6bfb80e2b391d37e434bece79266f15755465cf27a2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_alloy.asset
        // source-sha256: 7da0006726e35195766d1fa0d0d7db459d51ebc30897dfc26af90ada20d9f069 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_alloy.asset.meta
        // source-sha256: b1cede06f4c77f394011f009ac2380bd60d30373ffb128af8d5e5b8e62c5ba96 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_anarchy.asset
        // source-sha256: f99c19e989f8a513fbc4515741fd95642f3835fc92036a3c673b166ea28f2419 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_anarchy.asset.meta
        // source-sha256: ed0e09ff3bca97f3dd7b9842b5cd3c276daba9cc0385f87389a9ada2fbbe64ce Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_anarchy_bra_purple.asset
        // source-sha256: 5e2058c94c55cfbe0d49ebfd07a130c7488c0e48ba725ae9b89fe379cd8e2641 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_anarchy_bra_purple.asset.meta
        // source-sha256: fe21bd104cb76b5d8797e4d69feb23c460816fd024212c0d7a076c4b8a6bd90c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_anarchy_bra_red.asset
        // source-sha256: 161417667555c7e6299013e6db585f3473e7af239ab45e35a86c9a90a872749b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_anarchy_bra_red.asset.meta
        // source-sha256: f20ce3610a9718374398f7b4a9070f4d502e0147a3f62f23998f6556f5e8b6d8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_anarchy_red.asset
        // source-sha256: 5103d30993ab991a5a00fde9fb037e55c1643f80a5e655812d7e949c44bcaef7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_anarchy_red.asset.meta
        // source-sha256: 0ce628fc6663b3408acd6216ab4cbe3da32830e8c046b0e6deff5a734f44b356 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_classic.asset
        // source-sha256: ac0fccbc0e121d51734f8b216e695ef5057e8998a560431b0b3cf0f7a19fd8a0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_classic.asset.meta
        // source-sha256: 28b88836c873f0dc40c5d5ecf837c238efcdde748680d26615938fbb57a0069b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_deadly.asset
        // source-sha256: 77e9478e36711903ae9bbb9228cb6b008873600337e0cfafecd29fe6bb46d566 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_deadly.asset.meta
        // source-sha256: f9c640c2ae07264ae5a98c54bfb5ae2b83cb8ebf8d1f7ed955d5cb9e62ba999b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_fighter.asset
        // source-sha256: ae66831af16bbf519c0f2419a581a5cf03dad5dc2b6912d803a7aa8939c17bca Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_fighter.asset.meta
        // source-sha256: dff6b723772fa050b7816b1bd28cff478dc0ec5a119aa63f8a35bc53142b8229 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_folk.asset
        // source-sha256: ebd58fa57d4c4fd33099c1871cb2cc7317987878575bfe749afeec76b878e2dc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_folk.asset.meta
        // source-sha256: 630183b4ddb56822fbbfe050a37502734388bcf9adac0232bf1703612b57b251 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_osiris.asset
        // source-sha256: 51bd46f1745fa627c5ad8d4f452299e1da2a0d90eaeaff349e9035d322eaa6e7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_osiris.asset.meta
        // source-sha256: 7263ae3a16a9062fa2095e79565c723cf60c79aa61bdbae90fe575ae27337ee0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_primal.asset
        // source-sha256: 89d10933421882d3f222154cfb0fc8e2426bb482ca547c3fbe31a98766604467 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_primal.asset.meta
        // source-sha256: 2ea96960691582ff03052ba518ed09259c366aba302a918a07d92dd2d1687f08 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_primal_1.asset
        // source-sha256: bcebfc7afd8bb8bbb89d40219d19abf65a865e19a21519d075a3703975642ffc Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_primal_1.asset.meta
        // source-sha256: 632a6c324dcd3406176f3b6995f5ccd0bac8eb0baa778383e62abe64d31cffc9 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_primal_2.asset
        // source-sha256: 356d63106b69b946d39d611d67487410212c11c7e7fc1195a7e5bec08e85056e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_primal_2.asset.meta
        // source-sha256: d4fb572566147e18c7b34cc4504ff229b9431f788411a1377a7a0ea7d9d3a1da Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_primal_3.asset
        // source-sha256: 6290798facf3e2a9a26679c7d5c967c080a3959fa366b04576dffcdcc40e3d50 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_primal_3.asset.meta
        // source-sha256: 6719cca3a73033cd09324ce305fb664ec77742b9092057a2fb538b58d1fc89e6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_stars.asset
        // source-sha256: 4d2440509f34ac4799b00a666877de1b0807eb319629b416af5f4c528532af58 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_stars.asset.meta
        // source-sha256: 5b83feafedf28563b1722f08da15534ce054295d71a3c1c21b93a55594e3ee39 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_tod.asset
        // source-sha256: 10c2b3add4686a8340a84a0f80ef280d711a38c926f3318504c372157a3d8c50 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_tod.asset.meta
        // source-sha256: 55c363a5ea24777d05061d869601ceda00a2ac791dad26698d80e59881f74e6b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga.asset
        // source-sha256: 2b09d33da11e9dbcfae0fb7b5ca70946894381bd392053ccb08b837b66e87c92 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga.asset.meta
        // source-sha256: 8df019b731f09d962137ca3678ccb021e24cc0c8a8b836f7e17e17a969d88cd0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_01_top.asset
        // source-sha256: 3b8754567282c72b4dfbb5192d0ad7a6997e232b42b1cedfdffccce6fca17ea6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_01_top.asset.meta
        // source-sha256: 6276d19c95a3cc8f367964c8a4f37da3137927be7296422af1ab7ec38049f18b Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_02_top.asset
        // source-sha256: 720ce2542d126a739a15a29f1e40896a95d35bfa66dfd459aa8baac28f5d741a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_02_top.asset.meta
        // source-sha256: 9e3beef44be33754dd999eee436af1c1a292d3ae6137bf612c25b151a5aac617 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_03_top.asset
        // source-sha256: 70f5a24dafc20bee8cac8acbc2d369e01ba5f997819742339da949362476f321 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_03_top.asset.meta
        // source-sha256: 83119e096aa6eba0420bc3450986edc45d409d21b224b5c274c6b0a99c57c3b7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_04_top.asset
        // source-sha256: 4cd4bf63de47175f63893b21074277bfc646f43466c38a3de0dde6bd465673bb Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_04_top.asset.meta
        // source-sha256: d6ac40047bbdfadd6bf137a16ffa21b19ea6155de9beae46fae4a62b2570798d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_05_top.asset
        // source-sha256: f02a22bd9da96bc7bb825b78878f78d5aa6168f38ad6647b10594cfa96222d1f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_05_top.asset.meta
        // source-sha256: 49ee685f116573d06ea4ce290db271518515c5035727169d2443f9fa1a21c111 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_06_top.asset
        // source-sha256: fd171428ce39803e182c289968b3d043e85e570aacfce4149c77db0d6e40c97e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_06_top.asset.meta
        // source-sha256: 358834db7109fc267e39e93f56f91d46f0ba513e94382a86e95769fdeab9ffdf Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_07_top.asset
        // source-sha256: ce122e6d453275ca207066b3aa845db066e89d9b11303b282748f1e184792793 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_07_top.asset.meta
        // source-sha256: f54b9391c7bb6aea49c80f1de2f060630d9cb55f9c0f94324c1564e8c9e74d39 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_08_top.asset
        // source-sha256: 0c3fc70d80f315b0f9ac0750d1d660d51b4a642ae2bca5b21096940fdaff6b42 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_08_top.asset.meta
        // source-sha256: af702a2b9244221743c02f900cdca5e09d3f7680e180e90661759d9dc9da48af Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_09_top.asset
        // source-sha256: d8bb7a9680b1ce001d7cc26aec50e2c68cdba0b1448117e3dc6b3ff05af9d59d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_09_top.asset.meta
        // source-sha256: 7e822d002569285ce0b3f83c8d0a6ea450964f77f70e70e827b4f6b61eda1ca0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_10_top.asset
        // source-sha256: 79c058dfbbecc27843ca0ec9bbafb8b9078c08b56a7bbe510ccf625fd672a530 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_top_yoga_10_top.asset.meta
        // source-sha256: cf17eddc4dc6968dbb281f312c0735c67795ea7865bec950548e5532a640a6b0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big.asset
        // source-sha256: 8519c78614dcc2416a43b4e8aa7a87a5289504ec6480be73d4048bad4e8c6065 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big.asset.meta
        // source-sha256: f5ed82384c188d806eaf677a616d7222bfccebb95cfa00a509b5a93970c782a5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_charcoal.asset
        // source-sha256: 7d93e1314607b70ad4414df3e38d7dac02e9dbec72fc36af12bc4595f7dc6697 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_charcoal.asset.meta
        // source-sha256: 09a9a0cd3a4f07e7ceb1c22e2369fd9bcaca9c98e8e219a974e82aac9bd24177 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_dusty_blue.asset
        // source-sha256: c1a95481795029bfdc924055af103cb903bcd2083f63ee99cfadbfbff3ab1b07 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_dusty_blue.asset.meta
        // source-sha256: 006e3ea25ed6562921a190c575296cf5f524743592015abd7de743e5ad197559 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_dusty_pink.asset
        // source-sha256: 2ccb72bc36d084f6f4ee994c295b064d98b550170f25f1583d3031dd5b4d87b0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_dusty_pink.asset.meta
        // source-sha256: 163368743047c295a8fc58eda5cf1181f00b531cd5738fae9777d9f46f06a889 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_dusty_purple.asset
        // source-sha256: adc42d279f2a70c6ceee0ab64e3dcc7b420ba19c329db1050859b3f8cc007d0d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_dusty_purple.asset.meta
        // source-sha256: eb943cd60b94eb96c3e9baae19f05cbbbbf6ff205f9c0c41a90c7f00ca26c43a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_grey.asset
        // source-sha256: 000f9f796a98005087513fda6c7975d73bdca596bea6cbb51ec6259ae1c6fdf0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_grey.asset.meta
        // source-sha256: d8a56a77273ef0ba977cca4058cb923014fa20249de617e05f0691eff7b3932c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_red.asset
        // source-sha256: da17a908d8ef2e96c91097c2c43de22bdb8e2debd7e395fb5ca8f36dddef78e0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_red.asset.meta
        // source-sha256: b0fa13b4a432654551c9d436dfdd2f93dc0ce2b97f116a453bd9ef3a86834411 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_sky_blue.asset
        // source-sha256: 79bd02dfd27c3e9a8970df099a565b9d5d603a0ee4d2a30f45f4302208cbb8a1 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_sky_blue.asset.meta
        // source-sha256: 8fa35e84acaf1633529f7bc8222958d85ad6b2494c8f2b640a446535beb88db0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_white.asset
        // source-sha256: cde54a27c9feece67013e898a8a2d1c241d84355c827536e25c8f7e31aff9451 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_bt_white.asset.meta
        // source-sha256: 3ad5a9b00e6001ea5f2afcee1451de3edbe9d5b1516256c1a8bbb49f98d9a20d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_american.asset
        // source-sha256: 305ec278b372f150bf424447e0fa5a92b3ee40a76d07c6d3f29a86e983855ade Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_american.asset.meta
        // source-sha256: 1630326dd69387f48493d6c277abcb867160be1bfa527a4615e296e4c697ed9a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_born_to_ride.asset
        // source-sha256: 227c0cbea7cc18734d850b8188b80a50f761d425cdde46f950c7befa8cd11a24 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_born_to_ride.asset.meta
        // source-sha256: d8a0ab93497b4abbe819be067df95536519797799d584a5e65d65e34cd1c0b7e Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_california.asset
        // source-sha256: 5b134fd92f54f43f0ed18a1c6a7bf71174697060ab675099cd35116a0ddfc260 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_california.asset.meta
        // source-sha256: cba8649a88b0c944fa947a07b71ec81aea677603d514ab73548cc44faf3fd3c0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_denim.asset
        // source-sha256: d9fc28472477aa2c370d4db37b05e7d41aa42c68e302c56b8958427a83f95cb2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_denim.asset.meta
        // source-sha256: 26872bafbc7e70503c285ffb2a7806ae5925c0af3d9b63c0e764411b01b73708 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_eagles.asset
        // source-sha256: dcc836f849cbd5207392be2c01f3450f9d930c6fd77b957f72e897ce8fdfbe87 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_eagles.asset.meta
        // source-sha256: 8d021e78c7e9cefa761a48f9b98ce79c3b17deb48d6e5ef15e703753b978782f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_heartbreaker.asset
        // source-sha256: 9ab26a35e7cee8b58bb7124eb3db3f0e9be64d5d07341cc9449733f6acd9d3b5 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_heartbreaker.asset.meta
        // source-sha256: 081a25c9f1c0e7703bdc3b66918330b8a9658d1fb64014557fae28569240a67d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_lost_angels.asset
        // source-sha256: aadb7f880e4afd1ca53ba475e0396c8bc4fc9120730922cdfa30fb39174787de Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_lost_angels.asset.meta
        // source-sha256: 2b5bd91274e92cae763fa9c400d71f944914a0d480cd060a88425fc570836448 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_queen.asset
        // source-sha256: dab8d22550c0164d1f106677cc6d952dfd5dcf3d3f7d467da82f9247aa990903 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_queen.asset.meta
        // source-sha256: cdad6d75264f39037ccc8f2988c9a7b45d2259bcadcfa9773dab73f6da422f65 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_racing.asset
        // source-sha256: 5923f20c82078eb0b072040eddcca47f755fbd947a4a7894f52d23b0bad9fc06 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_racing.asset.meta
        // source-sha256: 70168138d8517cb729ee7a69d02610bf1ca35572474553f7427c997535850f46 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_saints.asset
        // source-sha256: 2ca8e0b62738ce5e743dc48cc1a5e5af6decd22a73a506b2ca764de214a23a08 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_big_tshirt_saints.asset.meta
        // source-sha256: a58456ba9a1c9e31259817857bc36884dd61922fb9e8cf572908c92950cc2913 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer.asset
        // source-sha256: 26ed951c104400ef1fe73599450ec27b7b137567b99c1cc1c7faa988cedd6680 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer.asset.meta
        // source-sha256: 1fe4284edfe09f717bd2a261e61dc73a26fbc5e5b22e3821a851f8e94bb68681 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer_i13sw_tshirt_02.asset
        // source-sha256: 39a7cbe68e0d5661bae2ea2e83a4a3637fc64eae400e23cf1df12101f7ddedde Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer_i13sw_tshirt_02.asset.meta
        // source-sha256: b3097866f97fdb4f7e287dfdddf0cd2c5a9a337e67243533b5ef66cb9b219eb6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer_i13sw_tshirt_03.asset
        // source-sha256: 07bad606ac957ad9863fa1060eb40c694b654f359499383f9ca29ac90caa0f01 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer_i13sw_tshirt_03.asset.meta
        // source-sha256: eb2ebe596e80e4ca743955cdf8f19e5136690c06e965f5924a1397cd5e660da7 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer_i13sw_tshirt_04.asset
        // source-sha256: 386f914f0526ddf8b497a446cc1e223007a94ea172727b0ff2c35f50cef771a3 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer_i13sw_tshirt_04.asset.meta
        // source-sha256: 55a2dbbb7e0a629501839e6b3c922efb63b8a2d7b3877fc4e2a4a922095c909f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer_i13sw_tshirt_05.asset
        // source-sha256: 70a92ef5b335a60070a01bc7e44b8d0ba9ba1592f3b781a8e93f802cd6e7f51d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tshirt_summer_i13sw_tshirt_05.asset.meta
        // source-sha256: 4038c8b1da9e9bb4426f9c46f75c2bf49ea182cdd6c35959f00eb3c32f9bfb3c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tutu_nerd.asset
        // source-sha256: 178f2cd7dce96ff3d27ea09f6e91d6c7d06f0f5bd906949bf65a81315258c74f Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_tutu_nerd.asset.meta
        // source-sha256: 4d121c676942926729fe2342efc7facb682b21e427a855801f34d047b7b7450a Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek.asset
        // source-sha256: 35eea17e2aefddbce20c20df8ae1aa40992dfe0c1f22e6a434632b2fc0ea1fd2 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek.asset.meta
        // source-sha256: 3021c3f7ef4261d364443acd6dc4e8b79b62957db61a50b6176c9de5a949b246 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_01_black_mesh.asset
        // source-sha256: e34feb1c44de0cc6b079b8aa0866f4a26dc94700fcc0504c1993cb192ca32f3d Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_01_black_mesh.asset.meta
        // source-sha256: 5fdc3c91cfc2309bd3a28bf94d0f875c305af8776d3de1a1ccf6163cf4a8519c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_02_black.asset
        // source-sha256: e19532feb00a25a405b05019076c5cde64f725485448b2600217d39c4d24a5a8 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_02_black.asset.meta
        // source-sha256: 83f99234dee22f84e8e7fafa0c0255b4e5392e16a7d13f783dcf15df79932a70 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_03_black_red.asset
        // source-sha256: ccd24633c0f6d5cdf440e68365943d1f29f2d93b859ab1dd37d50de2de2e2071 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_03_black_red.asset.meta
        // source-sha256: 429d05c1708541ee017f079ef317e8c4e0cb8c30eb738028e2e73f7656a907ed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_04_black_orange.asset
        // source-sha256: ca4934b27107215325cd04c99881809cf90a106f5f303030d8d5883d1f5ff026 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_04_black_orange.asset.meta
        // source-sha256: f18ce1058e4b03186d2ce77c0aef6a88ab471e504bdf773dffce1f9e5f543e25 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_05_black_pink.asset
        // source-sha256: 56d14a53a208fefbb3159b664985141d1abde12251c8fadf541c76c1d3388ba6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_05_black_pink.asset.meta
        // source-sha256: 8eeb9d4359237c5659df74a29e3afb0d6135c082128d4fe1948a4a544513a4f0 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_06_black_blue.asset
        // source-sha256: ce82065da6094494b727a0f2df0f70e140a8ed6b4032ee607f955bc5a25a6a33 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_06_black_blue.asset.meta
        // source-sha256: 148a9d64d2c42018650431b5776bb6894a57af0c5c32b8aa4b10813e1aa6dbed Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_07_black_plain.asset
        // source-sha256: 981af7d51e9523a7d489a15f30ea92a77e375e157a1cb76c88072e2e3f79fc47 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_07_black_plain.asset.meta
        // source-sha256: 835a798f5f85d79fdb1a72e9fdd9342a5a72a8b86cf95126c76b2b6244ede684 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_08_white_plain.asset
        // source-sha256: 38661e3dd8ebd763511bd1fce1669def250ea2970158ecbc0016e8e647d02519 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/clothing_yogapants_tek_yoga_08_white_plain.asset.meta
        // source-sha256: 55c434459631409de40a8ba144c55d095dfc387762c0ca705ff6151a4270c345 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/fco_belt_male.asset
        // source-sha256: ca46f72f199e9760fad99da172c3935739ff5a21b23079f4a614f259ebcfa254 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/fco_belt_male.asset.meta
        // source-sha256: 387deba6d92b737ce387080b0533d9f05e840c53c9424b15de28fd32e090dea6 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/fco_gloves_male.asset
        // source-sha256: 927083e64d77463abe613f5ca771e6ec733cd77d87475b43fb8c9c3e52e46220 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/fco_gloves_male.asset.meta
        // source-sha256: 7bae798ecf482b6ef684f5359f3d295496c57661afe65da990749c06e944939c Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/fco_pants_male.asset
        // source-sha256: 2c32314b6ba5687163bc64e890fafb8d0fcd563be2ed15296f25d2dc6699de26 Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear/fco_pants_male.asset.meta
        // source-sha256: e8ad9b20fb5d641798e81d62784e568db8a235a28fda2f39251964788b325b72 Assets/HexLive/UnityPresentation/Wearing/WearArtAliases.cs
        // source-sha256: 2e6b36a4b961d80d14dea6a8ba223d9fd04e2307d9ead1cf221eacd72730ddf8 Assets/HexLive/UnityPresentation/Wearing/WearArtAliases.cs.meta
        // source-sha256: 620736b894507975417937b154f4fc51092956215beedf492be35e1465351ca9 Assets/HexLiveContent/RuntimeSource/GarmentCatalog.asset
        // source-sha256: 4f73cee19b153c2ae67b7e3813283ef9e0d1a62a350a9c56c6176808e5c9bc0c Assets/HexLiveContent/RuntimeSource/GarmentCatalog.asset.meta
        // source-sha256: 13c6b4b321fb7f3e9748dd64981fdcc49f71878bbe8081fd55350d1da877d5d9 Assets/HexLiveContent/Wear/FAO Harness Male/FAO Harness Male.prefab
        // source-sha256: 044777503a735d7d423e40d4fde34bfcdf7401e4f6faa735dbf294fd6b89f5bb Assets/HexLiveContent/Wear/FAO Harness Male/FAO Harness Male.prefab.meta
        // source-sha256: ff08cdc36bda3070894695d7914dcf24162de5e43eb5ce74267dda0b32f906f7 Assets/HexLiveContent/Wear/FCO Belt Male/FCO Belt Male.prefab
        // source-sha256: 74f7425303176ec7c1186756a855d4d616646ab126c2811589dbdcdadab53e19 Assets/HexLiveContent/Wear/FCO Belt Male/FCO Belt Male.prefab.meta
        // source-sha256: 6458dc5e9d6588bc1e9665c248d9886e71298ea89c23d809fcbf4ca415417793 Assets/HexLiveContent/Wear/FCO Boots Male/FCO Boots Male.prefab
        // source-sha256: e925e8ccc2614b5da91106885d4b461b33d60881b47c9b6608ecef26921631fe Assets/HexLiveContent/Wear/FCO Boots Male/FCO Boots Male.prefab.meta
        // source-sha256: 76d2bf74f3197314b78823de120a3f6c5669026eff2b24b23f40a29026974648 Assets/HexLiveContent/Wear/FCO Gloves Male/FCO Gloves Male.prefab
        // source-sha256: 9d43d4444ead4c65451cd038063229cd08ee905e49d2f006df8a29af4cf2fe0f Assets/HexLiveContent/Wear/FCO Gloves Male/FCO Gloves Male.prefab.meta
        // source-sha256: c50a33f58d17765056787d6b6291a0e68fe1742563ef662b52bf11eef4cd8e3c Assets/HexLiveContent/Wear/FCO Knee Straps Male/FCO Knee Straps Male.prefab
        // source-sha256: 5bfd9ab380e15170c776d48d82a997f8c61c4be987d1d424326cad24f59d658f Assets/HexLiveContent/Wear/FCO Knee Straps Male/FCO Knee Straps Male.prefab.meta
        // source-sha256: 4a6c0a475014b76b184a986c21818e1a02a493cb1a216be5d5384df1fba77514 Assets/HexLiveContent/Wear/FCO Legs Straps Male/FCO Legs Straps Male.prefab
        // source-sha256: 68e07cb0a9a6bd323779b702eef640a5281b095d60b949b025b0a75614e1f09a Assets/HexLiveContent/Wear/FCO Legs Straps Male/FCO Legs Straps Male.prefab.meta
        // source-sha256: fd6da0641e044add54f0384da0186003ad6fa890647fa2c93c474b23f237c7a5 Assets/HexLiveContent/Wear/FCO Pants Male/FCO Pants Male.prefab
        // source-sha256: 34caf46c7dad193fd2c76422da7ee7798a09dc5205906fdce0014671cf2906ff Assets/HexLiveContent/Wear/FCO Pants Male/FCO Pants Male.prefab.meta
        // source-sha256: 9db5b855b9585d928c6510f24e90460a2d8a69e353a1a9c038017c98d9bc1a17 Assets/HexLiveContent/Wear/FCO Waist Strappy Male/FCO Waist Strappy Male.prefab
        // source-sha256: 916da06085322b1bf5f4cf0f510dd3aeeb694e60bb1243999c246af52fe9b7ea Assets/HexLiveContent/Wear/FCO Waist Strappy Male/FCO Waist Strappy Male.prefab.meta
        // source-sha256: 6fdc4bb2498e4b906b2326a90f9c8f6b0570b1ecc6182bb9027d6c51d87c4535 Assets/HexLiveContent/Wear/TonnyFlash/TonnyFlash.prefab
        // source-sha256: cd1a1b1c711d75fe76f4e73b900f18ae726929d90ad473adf62db639f383757c Assets/HexLiveContent/Wear/TonnyFlash/TonnyFlash.prefab.meta
        // source-sha256: 3b06d134c32500404f9ecc12b169d219c287c996f285aded70a7dea7bc6d20c2 Assets/HexLiveContent/Wear/clothing.ankleboots_amy/AmyAnkleBoots.prefab
        // source-sha256: 6195159ed981d7460f3cda6a47e3efde1bb0fa362ffe27cb19ede1761245364c Assets/HexLiveContent/Wear/clothing.ankleboots_amy/AmyAnkleBoots.prefab.meta
        // source-sha256: c6c5f13f8e056769f5c6ad03f47e0ced23574b17f465d92151d891c2777172b8 Assets/HexLiveContent/Wear/clothing.armguards_fighter/FighterArmGuards.prefab
        // source-sha256: 5819748f6c6d76121090711a581f05e98f8c2b15db80af867902ab0365104cc3 Assets/HexLiveContent/Wear/clothing.armguards_fighter/FighterArmGuards.prefab.meta
        // source-sha256: 3181dbabe2bee84d7f6207b4574da672117549b97dcf7159e9d4bab95d438187 Assets/HexLiveContent/Wear/clothing.armwraps_primal/PrimalArmWraps.prefab
        // source-sha256: 41562584662b4cb9590692b5a47c6ca15cc5c75ec2989aa2c13b46fb29129872 Assets/HexLiveContent/Wear/clothing.armwraps_primal/PrimalArmWraps.prefab.meta
        // source-sha256: 27fd818aa862e9b5ee524de2faade7c25539681ac8a0846f94790b02e1432aef Assets/HexLiveContent/Wear/clothing.babydoll_sweety/SweetyBabydoll.prefab
        // source-sha256: 8967a4e876db197f6ac7e36fc9409a1286285f5e51144eaf5b6026b4c42bd63b Assets/HexLiveContent/Wear/clothing.babydoll_sweety/SweetyBabydoll.prefab.meta
        // source-sha256: afdfa975811b4982275c2f6c74ba9f410e6b25147f6956d964f87fac45a9fe01 Assets/HexLiveContent/Wear/clothing.babydoll_tod/TodBabydoll.prefab
        // source-sha256: 1a21380d59fc9f8a46c43368ee1be9f2ccd695896afbefe25fc85481d68db89b Assets/HexLiveContent/Wear/clothing.babydoll_tod/TodBabydoll.prefab.meta
        // source-sha256: b0893c7c56a1db6a2b0d62397528206a385ace65ad40a897cff117616605e29d Assets/HexLiveContent/Wear/clothing.belt_anarchy/AnarchyBelt.prefab
        // source-sha256: 2ad6a083c4e977e8085f3bac6703c365e6c4c2101dc4564afbf706a2349c4a07 Assets/HexLiveContent/Wear/clothing.belt_anarchy/AnarchyBelt.prefab.meta
        // source-sha256: db87f339321cf285f8db298fab73422bfabd5dd7cd82251d917865c451587b27 Assets/HexLiveContent/Wear/clothing.belt_cindy/CindyBelt.prefab
        // source-sha256: fe9598ce063106aeb1bfa41db92a0662804045045db7c47784241dae27c66a67 Assets/HexLiveContent/Wear/clothing.belt_cindy/CindyBelt.prefab.meta
        // source-sha256: 030c7ba2a820a9fdcdbdccd272a434e76f5d8e214e07197f0547b6f87aa48520 Assets/HexLiveContent/Wear/clothing.belt_fighter/FighterBelt.prefab
        // source-sha256: 44d353973beadc7ea7676163b90d05bd5bf39a2cbcaa916979a08ab317b25971 Assets/HexLiveContent/Wear/clothing.belt_fighter/FighterBelt.prefab.meta
        // source-sha256: 2d590f7db972fed532340a4df7562c5bc0136362cfa8410b94b7aea9b0585388 Assets/HexLiveContent/Wear/clothing.belt_holster/OsirisHolsterBelt.prefab
        // source-sha256: a066bc63fdf84b008601645ed2df898492ed9ccebb74339e7e79de08c4a81ed9 Assets/HexLiveContent/Wear/clothing.belt_holster/OsirisHolsterBelt.prefab.meta
        // source-sha256: aff18aeacc349322b0466ecc25108562b144b9fb33e4a8cc3fa6100e8c4b7a0c Assets/HexLiveContent/Wear/clothing.belt_stars/StarsBelt.prefab
        // source-sha256: efe40772faedd4fe9b7618b9eeb879493c37323ae3f375d580507727d228fa94 Assets/HexLiveContent/Wear/clothing.belt_stars/StarsBelt.prefab.meta
        // source-sha256: 62d7ea8b5235a8434af6970fa36b9a896a4d41331a9d888a915ee4273a145621 Assets/HexLiveContent/Wear/clothing.blouse_anarchy/AnarchyBlouse.prefab
        // source-sha256: f85ab9cbc2202e10465c9c4823c0c99d9a2d79c5c3f5a0a1ec68fdd6d8a2d928 Assets/HexLiveContent/Wear/clothing.blouse_anarchy/AnarchyBlouse.prefab.meta
        // source-sha256: 21b12a53dcbab5590a33f89b1acdd06b9becffb00402a41b34f86033c6781425 Assets/HexLiveContent/Wear/clothing.blouse_nerd/NerdBlouse.prefab
        // source-sha256: 215e2da2aeef2651c5180b6b6793ec0b8866abc4bdb3382dced1232e68364ca4 Assets/HexLiveContent/Wear/clothing.blouse_nerd/NerdBlouse.prefab.meta
        // source-sha256: 7ab696f5a8d542633b47b3368a6a36a089225316af6c7da5b377405c737570f4 Assets/HexLiveContent/Wear/clothing.blouse_riot/RiotBlouse.prefab
        // source-sha256: fc52d86991fb16fdca6d2e3a074bbbd196992a844b64166efef55c711f491e2d Assets/HexLiveContent/Wear/clothing.blouse_riot/RiotBlouse.prefab.meta
        // source-sha256: cefbf43505b8480f2e6787d7dc45498db10465ae76ff806273551466e6c1a430 Assets/HexLiveContent/Wear/clothing.blouse_waist_riot/RiotBlouseWaist.prefab
        // source-sha256: 29c65e49eaaced54839913e938f64f0914d90e4faf5f09570be09bdc53fb48f2 Assets/HexLiveContent/Wear/clothing.blouse_waist_riot/RiotBlouseWaist.prefab.meta
        // source-sha256: cefa058f7cd7d2a4a61564d3109afd9bad5e810289e91d479d9fcaba9a67f608 Assets/HexLiveContent/Wear/clothing.boots_alloy/AlloyBoots.prefab
        // source-sha256: 539b6e8baa63457c069d582420b92a19aa445900986d3c18875848937f53191b Assets/HexLiveContent/Wear/clothing.boots_alloy/AlloyBoots.prefab.meta
        // source-sha256: f084c80842ad1baca5fa36f008ae969b0ea7be1240dfb7d8fd8471c2c95963ed Assets/HexLiveContent/Wear/clothing.boots_anarchy/AnarchyBoots.prefab
        // source-sha256: 885b253dc3164e2c2a613f086ab15ced37b377d0a967e7d8d4328a2050b96025 Assets/HexLiveContent/Wear/clothing.boots_anarchy/AnarchyBoots.prefab.meta
        // source-sha256: ddafcc2a878eb9d793959c71deaf7a28b23d418ae7c360012b2da839e55ba0c7 Assets/HexLiveContent/Wear/clothing.boots_charm/CharmBoots.prefab
        // source-sha256: 945fbe905e82682da1c0fbd10f629e1b903cbf36a21dc8008a3577f0e7f99994 Assets/HexLiveContent/Wear/clothing.boots_charm/CharmBoots.prefab.meta
        // source-sha256: 306d75dcea4c6e4e9923445297db2349de5a3c8c9b92ad81a23dbf3ec9424f3b Assets/HexLiveContent/Wear/clothing.boots_cindy/CindyBoots.prefab
        // source-sha256: cb13c7c7e5ec191b7308bcf077053e74ee4c21b427201a6bde3252763ee03630 Assets/HexLiveContent/Wear/clothing.boots_cindy/CindyBoots.prefab.meta
        // source-sha256: f570a97cef0ca475d4873b2fc8c3c3d11f7d588dd42e2cda86cda3c97d5f782a Assets/HexLiveContent/Wear/clothing.boots_classic/ClassicBoots.prefab
        // source-sha256: 8c0bb64a6f7a76fa2b023c3f0d6644238a038e76bb5122df1363b3cdec87ad83 Assets/HexLiveContent/Wear/clothing.boots_classic/ClassicBoots.prefab.meta
        // source-sha256: 70428013d4924a3dcfe06eaa59e99d5567f44450bc8842a2c2c0c039c1e5fc1d Assets/HexLiveContent/Wear/clothing.boots_leather/LeatherBoots.prefab
        // source-sha256: fcf14f76dc28a1a999d89a399db207efb8cc77b842f8c3ab1c69bbc2ca098fc7 Assets/HexLiveContent/Wear/clothing.boots_leather/LeatherBoots.prefab.meta
        // source-sha256: 20ee1ff4ad9d7c6130726cf989e9ac52d4ac0b40204792e4c31c7a73c6498d05 Assets/HexLiveContent/Wear/clothing.boots_osiris/OsirisBoots.prefab
        // source-sha256: e396ab236e9857118cce5bc43e6b93ea646cd40412d730701c43f796dc18f4b8 Assets/HexLiveContent/Wear/clothing.boots_osiris/OsirisBoots.prefab.meta
        // source-sha256: 40b6b90dae1f8331d8cb48a5356b77c5441dcfbc4c4ad285be232251846542c0 Assets/HexLiveContent/Wear/clothing.boots_ranger/RangerBoots.prefab
        // source-sha256: 78e03830c12dd403b131d33d0525b80e62bd0633e19c38600a772a8ba2287446 Assets/HexLiveContent/Wear/clothing.boots_ranger/RangerBoots.prefab.meta
        // source-sha256: ea60208dae5c475f8a544998cc1107e30a274cf91258a313a1b6d6399a0dfbc1 Assets/HexLiveContent/Wear/clothing.boots_riot/RiotBoots.prefab
        // source-sha256: 9c889d073e938b7b47758bb2088c18215af04e5c362c5908e8a1bcd87fa97008 Assets/HexLiveContent/Wear/clothing.boots_riot/RiotBoots.prefab.meta
        // source-sha256: 863c18129150bbf8e2091dc64c3bf9ef95eea727329c3d4a157538e6e3fea5f2 Assets/HexLiveContent/Wear/clothing.bowtie_nerd/NerdBowtie.prefab
        // source-sha256: 1fc37124344be6de4c9de3885198d07aee4c23c137d43465964ab90434cdaf85 Assets/HexLiveContent/Wear/clothing.bowtie_nerd/NerdBowtie.prefab.meta
        // source-sha256: 0c6da7b871a993f54e22ce28da0c4d98649314167f84d9b20ef565f74fb4937c Assets/HexLiveContent/Wear/clothing.bracelet_luxury/LuxuryBracelet.prefab
        // source-sha256: 7d300d57a8d1a99322bbeecb99827a67fcde48246a66d56bcc37d5dc730a9f20 Assets/HexLiveContent/Wear/clothing.bracelet_luxury/LuxuryBracelet.prefab.meta
        // source-sha256: a96f297caf164386fd734eb4a2fcbd11e3df1705e54228017a6377917cfbba5e Assets/HexLiveContent/Wear/clothing.cap_anarchy/AnarchyCap.prefab
        // source-sha256: 6bce9b83dbfa72543cc7a8a6c8479cbb1749a47ef55ac7298f43c96449638571 Assets/HexLiveContent/Wear/clothing.cap_anarchy/AnarchyCap.prefab.meta
        // source-sha256: 3a4b61b96cbae06d48c04bd8bcf38dd13bf4f81b50268d5526e6e33991237b4f Assets/HexLiveContent/Wear/clothing.cap_riot/RiotCap.prefab
        // source-sha256: bd90001e050171edb47d2e9b13da048cfe0dc99be27fb389f89bd2247082ea56 Assets/HexLiveContent/Wear/clothing.cap_riot/RiotCap.prefab.meta
        // source-sha256: 669e6d6731fe86d288bba2083cc60d5bdeb8258889d0193c08fe115db7912be0 Assets/HexLiveContent/Wear/clothing.cap_stars/StarsCap.prefab
        // source-sha256: ffdd31ea52073bfeb613ad95f3da5edfd38dc92bfd847fe12bc6a4d4ce9141b2 Assets/HexLiveContent/Wear/clothing.cap_stars/StarsCap.prefab.meta
        // source-sha256: b33ac12ad1a92c44de687bba21d68429de23a496877486a17d4fdf7eb9d56607 Assets/HexLiveContent/Wear/clothing.collar_anarchy/AnarchyCollar.prefab
        // source-sha256: 2940a6447e54705a552839c6a3b5ac186ed57d1487eabdce2dabc0f38e598430 Assets/HexLiveContent/Wear/clothing.collar_anarchy/AnarchyCollar.prefab.meta
        // source-sha256: 3ab984e8ed1b6fb56488f7938df60e1faf181eec7037f3954d66550d02534c19 Assets/HexLiveContent/Wear/clothing.collar_fur/PrimalCollar.prefab
        // source-sha256: 0cf56a83be59c152c02452bc4caaeacf4f6eb8b433dc2ef178159bbea054f1be Assets/HexLiveContent/Wear/clothing.collar_fur/PrimalCollar.prefab.meta
        // source-sha256: 7cb8274585a6e251c5572cbbc3465293487a014ff44aa1c40ffe33457d3ff6c4 Assets/HexLiveContent/Wear/clothing.collar_studded_anarchy/AnarchyStuddedCollar.prefab
        // source-sha256: f9464fef0855d1d58c31869a3b4d5930193e61f4b605bdbefa0ada0eeb304322 Assets/HexLiveContent/Wear/clothing.collar_studded_anarchy/AnarchyStuddedCollar.prefab.meta
        // source-sha256: ce224e1e27264a0e659f70988f0f45e7938d192c269af5cda2937c788965ff3d Assets/HexLiveContent/Wear/clothing.corset_anarchy/AnarchyCorset.prefab
        // source-sha256: f00449a17d0feb9b696817d1a1b8dd2a71942ef5695ce875048608bacfcfc342 Assets/HexLiveContent/Wear/clothing.corset_anarchy/AnarchyCorset.prefab.meta
        // source-sha256: 62658e64a5ffbc297026f8f48ede79a92f913aa6dd5485289f267a38d0bb41ba Assets/HexLiveContent/Wear/clothing.corset_skinny/SkinnyCorset.prefab
        // source-sha256: c4ddc54b0bfeec4317467b39f1af75700c8476dffc63896e179f176973ee3f5b Assets/HexLiveContent/Wear/clothing.corset_skinny/SkinnyCorset.prefab.meta
        // source-sha256: 523bd0c17f29718c4b76697bec200122d98cfda9c307d77c3afe2e2aeafd951c Assets/HexLiveContent/Wear/clothing.croptop_fit/FitCropTop.prefab
        // source-sha256: 99a7654f724da254da18bc55e44fb72761a3968a0169c430de5b7971e4f42612 Assets/HexLiveContent/Wear/clothing.croptop_fit/FitCropTop.prefab.meta
        // source-sha256: 7be43b17b5a1ed7091ba8a0f771f9d1fb576195c1dbbeb2362c7499baf26757b Assets/HexLiveContent/Wear/clothing.croptop_idol/IdolCropTop.prefab
        // source-sha256: 9c29b7eef226571fedb84ba9a14fec2733b4924cca6855fe31ad93bcdf9dbf01 Assets/HexLiveContent/Wear/clothing.croptop_idol/IdolCropTop.prefab.meta
        // source-sha256: c4d2be6610e7a6a2e2d314ba54afb7d75f9e9c630fefbf5691af6300763c602f Assets/HexLiveContent/Wear/clothing.cuffs_anarchy/AnarchyCuffs.prefab
        // source-sha256: b63c7bb0057aec7f6242e657bd05b4e52917e00378ce2883908256ee37196881 Assets/HexLiveContent/Wear/clothing.cuffs_anarchy/AnarchyCuffs.prefab.meta
        // source-sha256: 103a415530ad446772db3757d4e8c7b8167a449c2ce1f4cb9eaeaaa6e83686db Assets/HexLiveContent/Wear/clothing.dress_city/CityDress.prefab
        // source-sha256: f641be9681d4dc7da1684248288f2fffcf14770cb61878524ee9ea6e37e23cf9 Assets/HexLiveContent/Wear/clothing.dress_city/CityDress.prefab.meta
        // source-sha256: 3c94cbe404d71735e7028cb9bf8ab06420c08daa8f2b8067251f10caaa132307 Assets/HexLiveContent/Wear/clothing.dress_night/NightDress.prefab
        // source-sha256: 6582262ca67e037a86499e358f10a0777beba8ba77d2f422bbbf5c3eda3b366e Assets/HexLiveContent/Wear/clothing.dress_night/NightDress.prefab.meta
        // source-sha256: 1e61f9033bb8198daae339a0593c06d0d174651767dd2a58fea5aa9b84076ced Assets/HexLiveContent/Wear/clothing.dress_primal/PrimalDress.prefab
        // source-sha256: c1031a5a307bcdfb6b317c1b13fa8b84e5e0e679b18f7f8c2c82524d71ee952f Assets/HexLiveContent/Wear/clothing.dress_primal/PrimalDress.prefab.meta
        // source-sha256: d03842d7d8a4c990b4bac36a6443800f176a0476a81c102347f8c3e3cb8a236f Assets/HexLiveContent/Wear/clothing.footwear_fighter/FighterFootwear.prefab
        // source-sha256: e93137d2026e4e23e07eaff656f459356ab9397dabd82e972322d39ec4cc6285 Assets/HexLiveContent/Wear/clothing.footwear_fighter/FighterFootwear.prefab.meta
        // source-sha256: a29ba3db455fe8b352a932a105ced7398f7b63b7d596e571419c50e632481186 Assets/HexLiveContent/Wear/clothing.glasses_nerd/NerdGlasses.prefab
        // source-sha256: 2c61bda41cc6577ce8bc0083b0c6756bd8608796d99fc3ef3d5c21315f506425 Assets/HexLiveContent/Wear/clothing.glasses_nerd/NerdGlasses.prefab.meta
        // source-sha256: 067e0d15380389a37ec0f1914b60058678df0bd6622db7d8c62463595694217e Assets/HexLiveContent/Wear/clothing.gloves_biker/BikerGloves.prefab
        // source-sha256: 4b24d0e22e3baa28432db0d268f5e46dfdc5a31d141aa52ba51aef9373327e8f Assets/HexLiveContent/Wear/clothing.gloves_biker/BikerGloves.prefab.meta
        // source-sha256: c2408927b3fe098fbb7883914a2d453b231dde8f8c90962d7da74bc8ef445897 Assets/HexLiveContent/Wear/clothing.gloves_cindy/CindyGloves.prefab
        // source-sha256: 2b57a064408caec70b964e900080df116d0e3fe40b41a6df859b50ca4cfa938a Assets/HexLiveContent/Wear/clothing.gloves_cindy/CindyGloves.prefab.meta
        // source-sha256: 890b71d316ce79647de8f088552952c19dd6ae3277826898fa7608f4d5d1e121 Assets/HexLiveContent/Wear/clothing.gloves_classic/ClassicGloves.prefab
        // source-sha256: 7ca4a6672b0686b51fa63af87d677214bc741e7ff2a4369961c6564e0e5f2f8d Assets/HexLiveContent/Wear/clothing.gloves_classic/ClassicGloves.prefab.meta
        // source-sha256: 900b1e4df0870c0e00e8893c9cfceb6a2a3a47340d2aadac8c73eb8a145e1091 Assets/HexLiveContent/Wear/clothing.gloves_deadly/DeadlyGloves.prefab
        // source-sha256: 278b3137614998eaa29fab74016cada744afa370d1522ca6a4119aedbfa68ab6 Assets/HexLiveContent/Wear/clothing.gloves_deadly/DeadlyGloves.prefab.meta
        // source-sha256: ca93d2efae63dc4750f8a24d19a07118fb74b4bef15aee17af22dad0ef5f8b94 Assets/HexLiveContent/Wear/clothing.gloves_fit/FitGloves.prefab
        // source-sha256: c6a62eec451d4717ba59e00af6ab618e60676d8b19c7dd72ff2017750587b117 Assets/HexLiveContent/Wear/clothing.gloves_fit/FitGloves.prefab.meta
        // source-sha256: f3fd4f76311d296ec114f53e4f297184f55c892e23988b94b30721ca2d136017 Assets/HexLiveContent/Wear/clothing.gloves_lace_tod/TodLaceGloves.prefab
        // source-sha256: 2762b1b7881620815cd54881f4736a57870031539de78e5f506a6a9aea990f49 Assets/HexLiveContent/Wear/clothing.gloves_lace_tod/TodLaceGloves.prefab.meta
        // source-sha256: 2499ee7839a5cc8a4cdab0672e9130bff614c60b4a10362ce7f7ca735b9c86b1 Assets/HexLiveContent/Wear/clothing.gloves_long_anarchy/AnarchyLongGloves.prefab
        // source-sha256: b3af12b4263745021bc26bb9716090ac3dbdfef4d3b878d96df0c25977b00dee Assets/HexLiveContent/Wear/clothing.gloves_long_anarchy/AnarchyLongGloves.prefab.meta
        // source-sha256: edb9a0f5c57d5d8a5629fef36ac69514a4ee1c050575fd852ff6315d7319527c Assets/HexLiveContent/Wear/clothing.gloves_osiris/OsirisGloves.prefab
        // source-sha256: 35092f2d8796415637ee2a8109f3489db607f3e0bd3f0d23cbee376d093cce6d Assets/HexLiveContent/Wear/clothing.gloves_osiris/OsirisGloves.prefab.meta
        // source-sha256: 35abd6e9c8961b3b19c7567df0f2f17bba4ee581fe9d0e6244d764339bee4ef5 Assets/HexLiveContent/Wear/clothing.gloves_stars/StarsGloves.prefab
        // source-sha256: b814762cd2f1c64ca0ac30d9295c39904c0e560a5c8d18e61e3cf91f6262abbb Assets/HexLiveContent/Wear/clothing.gloves_stars/StarsGloves.prefab.meta
        // source-sha256: db7c505f4cceedac099619108d1abc02118558ed05a55c46754540e5fdf11f02 Assets/HexLiveContent/Wear/clothing.gloves_strap_anarchy/AnarchyStrapGloves.prefab
        // source-sha256: 85156d87642d2d3afc9f64db8b98f9ea026d012344393bb9b06ace9f75287042 Assets/HexLiveContent/Wear/clothing.gloves_strap_anarchy/AnarchyStrapGloves.prefab.meta
        // source-sha256: ce4b447b4a19ebdd2d71ed08298e338af1c580ce5c978f01d3d97177ca937900 Assets/HexLiveContent/Wear/clothing.greaves_tod/TodGreaves.prefab
        // source-sha256: 50af7edbe09326320587bf8888b2ed5a1ac84d8d32d859a136b6717a8186fcea Assets/HexLiveContent/Wear/clothing.greaves_tod/TodGreaves.prefab.meta
        // source-sha256: 4cc160eba5819471cccc2abaa5ef5eb65e27fdc7640092b8c1513c5677cde0fc Assets/HexLiveContent/Wear/clothing.halter_vamp/VampHalter.prefab
        // source-sha256: dd146937c42b8dcd03c11f640955e3172a9bba3e9302e92c7fcd85dd6cfc6324 Assets/HexLiveContent/Wear/clothing.halter_vamp/VampHalter.prefab.meta
        // source-sha256: 21bf361f6bd9fec8af5c3f9648aad36566125e00a68dda6329da6342082355f8 Assets/HexLiveContent/Wear/clothing.headband_primal/PrimalHeadband.prefab
        // source-sha256: 0190f3b2589b9e7f1e4ffa3bae8dbf247c5db3a07f8f9f1f75b44575a10d95d8 Assets/HexLiveContent/Wear/clothing.headband_primal/PrimalHeadband.prefab.meta
        // source-sha256: ad2891d2b889694b0d2a062c3390df2c314021d546ff726a052941a818a4493c Assets/HexLiveContent/Wear/clothing.heels_luxury/LuxuryHeels.prefab
        // source-sha256: c87fa9d903a8968833d194ca26779154eb4946d0f346db6d61fa96aca3e76699 Assets/HexLiveContent/Wear/clothing.heels_luxury/LuxuryHeels.prefab.meta
        // source-sha256: 494667104b0e0e93626c719b60e4f59f01e77543ad3b464beb9df36044153851 Assets/HexLiveContent/Wear/clothing.helmet_bull/helmet_bull.mesh.asset
        // source-sha256: b8657e5346de83b60ace4089bfd077951ad2bd46099f5664c628365b94f68033 Assets/HexLiveContent/Wear/clothing.helmet_bull/helmet_bull.mesh.asset.meta
        // source-sha256: dda64c63312997d1949a5d378c3cd610ae998d56e67a218f18c9cdc743ffc96c Assets/HexLiveContent/Wear/clothing.helmet_bull/helmet_bull.prefab
        // source-sha256: eb240a6c948f1d15bdbd278bb37825249c64e36b07c5d2fa6f43067c2e6c7688 Assets/HexLiveContent/Wear/clothing.helmet_bull/helmet_bull.prefab.meta
        // source-sha256: 024c4af0ee824ce798ae43ecba7778714dd36c40aee325980042402cd4fe122f Assets/HexLiveContent/Wear/clothing.helmet_carbon/helmet_carbon.mesh.asset
        // source-sha256: 2c4a9ec733dd54132e7889ab971eb0fd4d38704255a1a181baf8eab49df3c0ed Assets/HexLiveContent/Wear/clothing.helmet_carbon/helmet_carbon.mesh.asset.meta
        // source-sha256: 0ab0d73cec7c557cf00927fa6314e63ba6c614155927480fa73ca9b009491942 Assets/HexLiveContent/Wear/clothing.helmet_carbon/helmet_carbon.prefab
        // source-sha256: eb6c5b92c2a1a93d3b852cdd5eb39657f6e80b7f87dfae66ef1c89fdb3d07273 Assets/HexLiveContent/Wear/clothing.helmet_carbon/helmet_carbon.prefab.meta
        // source-sha256: 99e5bb998fb3c97bbb578bb93549e14419e8ddbb95cf8aa098c8cfcddcce371e Assets/HexLiveContent/Wear/clothing.helmet_knight/helmet_knight.mesh.asset
        // source-sha256: 3c0b43399822ac49eadc183fd62af364db42199fc1fac4e9e4443a77a476a60e Assets/HexLiveContent/Wear/clothing.helmet_knight/helmet_knight.mesh.asset.meta
        // source-sha256: 3e8d4ebca20e92db39a14a27dd72cbcf7b1cad831e26b931593db8e782bef0c1 Assets/HexLiveContent/Wear/clothing.helmet_knight/helmet_knight.prefab
        // source-sha256: 426ebf679472fa3d429d1ce3f807498618ed41832e6d1b0c6b13252b4966c833 Assets/HexLiveContent/Wear/clothing.helmet_knight/helmet_knight.prefab.meta
        // source-sha256: 82d10f0a01dfe1ae755c320919035ff3d92c60d401dd7d57e6bbdcb9ba10a732 Assets/HexLiveContent/Wear/clothing.helmet_m1/helmet_m1.mesh.asset
        // source-sha256: 64ba6de2996966af60f84f5891f7f5a94a11b778d3f0fbc79776e232769b9bc2 Assets/HexLiveContent/Wear/clothing.helmet_m1/helmet_m1.mesh.asset.meta
        // source-sha256: 79935a25cdfa44d8deb0adc27647c88641cd5365034765054645f63b0e3effb8 Assets/HexLiveContent/Wear/clothing.helmet_m1/helmet_m1.prefab
        // source-sha256: 7d7c3e5c990716a8674a95dedbe8b2b01e0cafcdcae0547a327b0531352e7ec5 Assets/HexLiveContent/Wear/clothing.helmet_m1/helmet_m1.prefab.meta
        // source-sha256: d349e091c98a4be6957cc7b79397bd1bf131d6378baac5df3c3eca3785fa499e Assets/HexLiveContent/Wear/clothing.helmet_moto/helmet_moto.mesh.asset
        // source-sha256: 2a3d7c1fb4977da762ec071cbf85d5a22028104e576506886a2f64814ef73d9c Assets/HexLiveContent/Wear/clothing.helmet_moto/helmet_moto.mesh.asset.meta
        // source-sha256: cb5350e5cba24d7945e3b73691cecd034ef50e4cd1b64a814ad3ca7ffcaff323 Assets/HexLiveContent/Wear/clothing.helmet_moto/helmet_moto.prefab
        // source-sha256: e0de4a64cb11ec023d4dbb729abbd08d4d39d75a6392119fad72bc79c8e00b1d Assets/HexLiveContent/Wear/clothing.helmet_moto/helmet_moto.prefab.meta
        // source-sha256: c54d61a8b057b3a20a1c3e0c86c814928092074ae666c7c9e0e169f26cca3eae Assets/HexLiveContent/Wear/clothing.helmet_racing/helmet_racing.mesh.asset
        // source-sha256: b5fa3937916beb8555f6ca458f540d83be7f42febc35a1da9099ab3547b50580 Assets/HexLiveContent/Wear/clothing.helmet_racing/helmet_racing.mesh.asset.meta
        // source-sha256: 46518861561124015962d62dcba7598e3f8186fc60fe742e69b9aab70de224f2 Assets/HexLiveContent/Wear/clothing.helmet_racing/helmet_racing.prefab
        // source-sha256: 928f4c57c7d1c5a1bf9fde50a9800237497da4ea477d8571aa5a7c53ca4d9d60 Assets/HexLiveContent/Wear/clothing.helmet_racing/helmet_racing.prefab.meta
        // source-sha256: e9798210d358b50ae873296eacbb92b5ab921882735689ad28b4980ba6bae4c1 Assets/HexLiveContent/Wear/clothing.helmet_retro/helmet_retro.mesh.asset
        // source-sha256: 1b45ed835b609d90070b8bfadb778a06ded3403200a6f696239aac936ec0df38 Assets/HexLiveContent/Wear/clothing.helmet_retro/helmet_retro.mesh.asset.meta
        // source-sha256: e127820de431e43a060087a62eb84a7f893ca17f33f516940402753bfcc3c3c4 Assets/HexLiveContent/Wear/clothing.helmet_retro/helmet_retro.prefab
        // source-sha256: f28c560b979c687add55133518d884a7c7d2b7b716b16e21dbac1f086d4d147d Assets/HexLiveContent/Wear/clothing.helmet_retro/helmet_retro.prefab.meta
        // source-sha256: 970dfb182f58f9da88b9b51cd53b2e6fa4bc951e8e9e2b6823aee24886db205a Assets/HexLiveContent/Wear/clothing.helmet_space/helmet_space.mesh.asset
        // source-sha256: 9e6ed3e878824da0fb69efdf6ab40e8dd36f4860f100726b1ff4baf35279906f Assets/HexLiveContent/Wear/clothing.helmet_space/helmet_space.mesh.asset.meta
        // source-sha256: ae4a1f1eeaf7817d785b1aa5462dc0022b05022f6ffb3e03828f47019fcf32c4 Assets/HexLiveContent/Wear/clothing.helmet_space/helmet_space.prefab
        // source-sha256: 4d165d0deeecfb2b376c98c7cc477fa603fd60534907b677ab38334bed505dd6 Assets/HexLiveContent/Wear/clothing.helmet_space/helmet_space.prefab.meta
        // source-sha256: f31743d6801c3ebef3fe031a80de17e538352d96c800ca456b3fb2a798ab5e2f Assets/HexLiveContent/Wear/clothing.helmet_t1/helmet_t1.mesh.asset
        // source-sha256: 350385737e5a672f3434adf1f6086d73fba4a428f3140a3ae7d72d11b494d673 Assets/HexLiveContent/Wear/clothing.helmet_t1/helmet_t1.mesh.asset.meta
        // source-sha256: 49add4eb8437fea8b1d24965a8223cedd22ab134b8fc6284f3fdca1a0d97f5b1 Assets/HexLiveContent/Wear/clothing.helmet_t1/helmet_t1.prefab
        // source-sha256: 6e8050b507c446035c894582ee2e580b1d75d5debc7889a9b29f9d37aa471176 Assets/HexLiveContent/Wear/clothing.helmet_t1/helmet_t1.prefab.meta
        // source-sha256: 5fd4f47ce83812fd6eb9a255651c218d482b7916e40755f57158f025a367242e Assets/HexLiveContent/Wear/clothing.helmet_tactical/helmet_tactical.mesh.asset
        // source-sha256: d9fd306c5b34ab83c3929fc23d314c881f4126ab1a738b3f722848680da8517d Assets/HexLiveContent/Wear/clothing.helmet_tactical/helmet_tactical.mesh.asset.meta
        // source-sha256: 894f585d2aa050e2ef7445eea7d0d5f88a2814c7af4a14bd4eb6e6a245e0a677 Assets/HexLiveContent/Wear/clothing.helmet_tactical/helmet_tactical.prefab
        // source-sha256: 755cdf51352b6d005fdd6bbb4d861579d7d09be1476c5ec7d6bae36610e5c9e0 Assets/HexLiveContent/Wear/clothing.helmet_tactical/helmet_tactical.prefab.meta
        // source-sha256: 9a232007337496f2238bd56be7a45a41f1739e39870c3e326975996175f719ae Assets/HexLiveContent/Wear/clothing.helmet_tactical_headset/helmet_tactical_headset.mesh.asset
        // source-sha256: 3d045b8fbd357e80272634cf9fce1ea880429a0f6e9c9d1bc71ca9bf88fa3f4e Assets/HexLiveContent/Wear/clothing.helmet_tactical_headset/helmet_tactical_headset.mesh.asset.meta
        // source-sha256: 8cbf45156b8a7ba29bfe41de2f0e2180fed545ae34616d618afab67cc8475a4a Assets/HexLiveContent/Wear/clothing.helmet_tactical_headset/helmet_tactical_headset.prefab
        // source-sha256: 0423d0ccfd49c68e5c20f45e6b7106882149f6ecf282f2b929ce285ca4e2ce47 Assets/HexLiveContent/Wear/clothing.helmet_tactical_headset/helmet_tactical_headset.prefab.meta
        // source-sha256: d18735ed499329ac87fd82717317640715d40b83ec3ab0b912b528b260028788 Assets/HexLiveContent/Wear/clothing.helmet_vietnam/helmet_vietnam.mesh.asset
        // source-sha256: 8086daf3f090aff1beef5e93f48df6fa8fcee84b17766868bb33ca7abe3d22ec Assets/HexLiveContent/Wear/clothing.helmet_vietnam/helmet_vietnam.mesh.asset.meta
        // source-sha256: de080607c8ae2598a44c77ce8ef9685ae2f023c646dc73b8b09bd81e35c54067 Assets/HexLiveContent/Wear/clothing.helmet_vietnam/helmet_vietnam.prefab
        // source-sha256: 67115dfbc51d863556954e572c7bdae9784a657592f57f7c480a463df69978c0 Assets/HexLiveContent/Wear/clothing.helmet_vietnam/helmet_vietnam.prefab.meta
        // source-sha256: 08ed2e04df929e8f762ff89a737b3f5dbcfbd7e0af49c4e5470b16c7734a3427 Assets/HexLiveContent/Wear/clothing.helmet_vintage/helmet_vintage.mesh.asset
        // source-sha256: 648d713bc585632bb92ea84b6222b1dabcd320769a5e416fd337684dfe65c991 Assets/HexLiveContent/Wear/clothing.helmet_vintage/helmet_vintage.mesh.asset.meta
        // source-sha256: 0080d82ce30999c9f87e5cdb24f799c2a2c3bdec98d062e80e9182c1fe1da497 Assets/HexLiveContent/Wear/clothing.helmet_vintage/helmet_vintage.prefab
        // source-sha256: 5f24419572a393602ca43575fddc7e4f62b6bab94ef98d6ad28ca2ecba062826 Assets/HexLiveContent/Wear/clothing.helmet_vintage/helmet_vintage.prefab.meta
        // source-sha256: 30c1a15f40d9080fd360193ec9a9838bb9e71a07b132a5d3701b7da125a7e644 Assets/HexLiveContent/Wear/clothing.hipbelt_anarchy/AnarchyHipBelt.prefab
        // source-sha256: 4cb9397ca1bd8782533e6125026a24af65f0c316ef98b49ba39aec5626d694b4 Assets/HexLiveContent/Wear/clothing.hipbelt_anarchy/AnarchyHipBelt.prefab.meta
        // source-sha256: 778a373f155595f1e13f49689ae9e8745f1276de3254e735a47a899ca92d850d Assets/HexLiveContent/Wear/clothing.jacket_autumn/AutumnJacket.prefab
        // source-sha256: ba46ecee946c72fa5e1821f7e4a0dbe66e54877e5507f5bbc9f7654abd451d03 Assets/HexLiveContent/Wear/clothing.jacket_autumn/AutumnJacket.prefab.meta
        // source-sha256: 9c60f8f30cb95d3d1fd02d63b7b56eda183ae00196076ba66278ee43e1c1e136 Assets/HexLiveContent/Wear/clothing.jacket_biker/BikerJacket.prefab
        // source-sha256: 8af004f3c46cc09943b22965058dffc610f6735f1876f800bc94863a20bcc8c5 Assets/HexLiveContent/Wear/clothing.jacket_biker/BikerJacket.prefab.meta
        // source-sha256: 3c9370a2af8b4019fb67dcbba061a7b243cf6df2c8a0de56334d0fc1355e5a53 Assets/HexLiveContent/Wear/clothing.jacket_cindy/CindyJacket.prefab
        // source-sha256: 7adefa039f3f9233c0b25d8ff86ef3ce70076c35b8378b5ffa63a6550f8a54d6 Assets/HexLiveContent/Wear/clothing.jacket_cindy/CindyJacket.prefab.meta
        // source-sha256: 8219eb0779be2cc7019dc4bdaada4bfbcb6309779e87a49a7549c392a8853310 Assets/HexLiveContent/Wear/clothing.jacket_ranger/RangerJacket.prefab
        // source-sha256: 8791bde3fc45fd44fa1f2136cbb92decc5dc905137785b13b04402a1a540490c Assets/HexLiveContent/Wear/clothing.jacket_ranger/RangerJacket.prefab.meta
        // source-sha256: 7ade4932a1ec1f78f9b2413f3d55108aea450cf85ac2a822c04c53068ef67edd Assets/HexLiveContent/Wear/clothing.jacket_tek/TekJacket.prefab
        // source-sha256: 8b74bcd6e478477070abf88d0ebaf310c133a49433403494f4472aef467b5f07 Assets/HexLiveContent/Wear/clothing.jacket_tek/TekJacket.prefab.meta
        // source-sha256: 69396c5329997bfb6f33c5db706ccf4a8e50b50ced37951b8bd82df36441c096 Assets/HexLiveContent/Wear/clothing.jackettied_tek/TekJacketTied.prefab
        // source-sha256: 0e9f69cf0b7cfd2ce6eff05ba9ad9c4d1945c8ae50b0872c361387b053a3fdf8 Assets/HexLiveContent/Wear/clothing.jackettied_tek/TekJacketTied.prefab.meta
        // source-sha256: 8bccc77ad6dd9b359bb3321c270d2762a84e8c6cf11527ccdcdb71d07f4e0796 Assets/HexLiveContent/Wear/clothing.jeans_skinny/SkinnyJeans.prefab
        // source-sha256: 9b348e2d7014aa44cc90b503abec80f5c8502c63e460c0440ab6383cee0458db Assets/HexLiveContent/Wear/clothing.jeans_skinny/SkinnyJeans.prefab.meta
        // source-sha256: 56c4b489fc510819eedbf8180a21cd8abbf484105f12f1c9127dd5129a11708c Assets/HexLiveContent/Wear/clothing.keikogi_fighter/FighterKeikogi.prefab
        // source-sha256: 6b9f96d900b28cf48e3a1736bc9789e67f29eb2f4a412b5280b1ed8680209565 Assets/HexLiveContent/Wear/clothing.keikogi_fighter/FighterKeikogi.prefab.meta
        // source-sha256: 4b9b53685956e7239931f9e45486c53b9438be38e20a2ad40ad0b9c0092e70f6 Assets/HexLiveContent/Wear/clothing.leggings_idol/IdolLeggings.prefab
        // source-sha256: 88b82f9b146d604f61e79737f2c115a0dd006b6ce18e72b4160c01ff1797c3cb Assets/HexLiveContent/Wear/clothing.leggings_idol/IdolLeggings.prefab.meta
        // source-sha256: fea5d0e6ddeae80d899a6831a9d08c01ccd84103d54e32da14bd33bd0d9feab1 Assets/HexLiveContent/Wear/clothing.leggings_yoga/YogaPants.prefab
        // source-sha256: 6eb52b512dff90e71ab0405c3bd3272318a51e400266bb11bb25244ec505c178 Assets/HexLiveContent/Wear/clothing.leggings_yoga/YogaPants.prefab.meta
        // source-sha256: 11319da0055874737120bd22164a2cb693079315a92234193be3405574ddc8af Assets/HexLiveContent/Wear/clothing.necklace_beads/NightNecklace.prefab
        // source-sha256: d89f3663dd043f4b15fd27fcbd77e3a65d2964001120cb78688c1dced3f4b7f4 Assets/HexLiveContent/Wear/clothing.necklace_beads/NightNecklace.prefab.meta
        // source-sha256: 3a0b15cdb01b2df52f863b8adc84f1795aeb4af17c3d5d18426d693d206ba581 Assets/HexLiveContent/Wear/clothing.necklace_osiris/OsirisNecklace.prefab
        // source-sha256: 6a0ccc20102b7de82f543c0dc0c6162f7079e352d1e9676725a10048a844fc12 Assets/HexLiveContent/Wear/clothing.necklace_osiris/OsirisNecklace.prefab.meta
        // source-sha256: 014863885d40cc0cfa53b2449ab037032a7adb5d2c9c87ead1ff725429b1fb90 Assets/HexLiveContent/Wear/clothing.outfit_reiko/ReikoOutfit.prefab
        // source-sha256: 484401310fc7bd235b1cb24fdc94f2223d74376f81a13d6f1d0852db820b6086 Assets/HexLiveContent/Wear/clothing.outfit_reiko/ReikoOutfit.prefab.meta
        // source-sha256: 9975187875615fdb527fbfb2422d73a3474ddde8b6fc1d3ae38d86bfa3245d4e Assets/HexLiveContent/Wear/clothing.pants_anarchy/AnarchyPants.prefab
        // source-sha256: b82d95985e9e092b72f94e3f63581b97638f1fee430a490bdf84bbe14dd469de Assets/HexLiveContent/Wear/clothing.pants_anarchy/AnarchyPants.prefab.meta
        // source-sha256: 0b657d56519aeeb633431139dff4d354994562854e93fa54b193fa73756ab59b Assets/HexLiveContent/Wear/clothing.pants_biker/BikerPants.prefab
        // source-sha256: d3535ccd88596996890962dbc995249e1cefff26640e7f6f7bfe69ef86d0b93f Assets/HexLiveContent/Wear/clothing.pants_biker/BikerPants.prefab.meta
        // source-sha256: 817c153103be18752dd029f7ca715f4c1d58afebff9c88fa5892b1e3421fabd1 Assets/HexLiveContent/Wear/clothing.pants_fighter/FighterPants.prefab
        // source-sha256: b62eabb9b17f281fb2011e4fd87db029cd0f79cc32ac397c0413fb6c9fa2ac62 Assets/HexLiveContent/Wear/clothing.pants_fighter/FighterPants.prefab.meta
        // source-sha256: a272f8cb586c8d5a3f972e1970bb90dee24f784d2183446fcfb4507c42a01985 Assets/HexLiveContent/Wear/clothing.pants_ranger/RangerPants.prefab
        // source-sha256: 04ea8412a4a3a71355b6e71ea522c4462a8a67467860d00f8c0153af805389ce Assets/HexLiveContent/Wear/clothing.pants_ranger/RangerPants.prefab.meta
        // source-sha256: c77429d4aa0dc52e25a1c76055f768c289e6f65a4ab643bbde50ab0b2e40e04d Assets/HexLiveContent/Wear/clothing.pants_stars/StarsPants.prefab
        // source-sha256: 608898ebde35a13948d97a95acbc9e5586b091b8661d2bd8f703ed20b7d6da4b Assets/HexLiveContent/Wear/clothing.pants_stars/StarsPants.prefab.meta
        // source-sha256: b635c4b57aa2912d70f22c0f5a42042d5e76642b820d27d51798513d95a09c52 Assets/HexLiveContent/Wear/clothing.pendant_amy/AmyPendant.prefab
        // source-sha256: 7110fcc5901b90661088529d15ac916ab9319df39b4dfcd121bb7051630ac1b2 Assets/HexLiveContent/Wear/clothing.pendant_amy/AmyPendant.prefab.meta
        // source-sha256: d38bb4a9e53a6c667e0a2dc5d1cd73d37dd030082aabce1e68d9787b4a5a0a92 Assets/HexLiveContent/Wear/clothing.pumps_flair/FlairPumps.prefab
        // source-sha256: 1b216ae0ef5c95628254931ef9953e848c32432de011107c3a07ce26f74c6b3b Assets/HexLiveContent/Wear/clothing.pumps_flair/FlairPumps.prefab.meta
        // source-sha256: 30f32a7f4c6cafb8ccbb8bc558ea409b7fb3d1a43f30a68521ddfd23ce29c95f Assets/HexLiveContent/Wear/clothing.sandals_summer1/Sandals1.prefab
        // source-sha256: 422db1ae81433c8d174bc0e9cfa8be1954765bc1080d8bc657179a68ade2fca2 Assets/HexLiveContent/Wear/clothing.sandals_summer1/Sandals1.prefab.meta
        // source-sha256: 2de2f27a420c0800cec690fe092488d58e6ff6149c2849b9eb24140057c1ff26 Assets/HexLiveContent/Wear/clothing.sandals_summer2/Sandals2.prefab
        // source-sha256: 8d9509b6a9f7bdc18aaa32cf14efa3aefeeb6c5d4b50a845e7089c12b4be4b4c Assets/HexLiveContent/Wear/clothing.sandals_summer2/Sandals2.prefab.meta
        // source-sha256: de21f8a5e2e13006beb08f9c7116aeaea41ad406546bafd93a1407f3b8c8ddf0 Assets/HexLiveContent/Wear/clothing.sandals_summer3/Sandals3.prefab
        // source-sha256: 9a52644074ce4df8a644fb61e0c655033cf2e4a1191deef465aa61915bcf7737 Assets/HexLiveContent/Wear/clothing.sandals_summer3/Sandals3.prefab.meta
        // source-sha256: c88c5513174db5d38a7dc81cd2047ba9b8a8c46d1cd9ec7e7801237602e43f34 Assets/HexLiveContent/Wear/clothing.sandals_summer4/Sandals4.prefab
        // source-sha256: 5a0c15d408b3bee73fe72ef1d92e4a648a53095a31f02fc62049271959a50ea6 Assets/HexLiveContent/Wear/clothing.sandals_summer4/Sandals4.prefab.meta
        // source-sha256: 34d42975574767f49e0df85287d4f2b7ebb845b381c0610558f9c5da8be0f7c4 Assets/HexLiveContent/Wear/clothing.scarf_classic/ClassicScarf.prefab
        // source-sha256: 965fb8fae0a95d9efd48550aa2f471c1d158ecc7344d9bbf1abb258e9382af45 Assets/HexLiveContent/Wear/clothing.scarf_classic/ClassicScarf.prefab.meta
        // source-sha256: 0b2b221def140bb2211e29cb1b45fe582018e33aa28183b5874ef86f3fe0c865 Assets/HexLiveContent/Wear/clothing.shirt_amy/AmyShirt.prefab
        // source-sha256: c0d1852df529ef75123c107bb7f75b581039e974a7bcaa5a75e8394fb3bb5343 Assets/HexLiveContent/Wear/clothing.shirt_amy/AmyShirt.prefab.meta
        // source-sha256: 9c166ad8afa12077594853c2184e93780c972e19b9d2718a81ec8c5252eaf50d Assets/HexLiveContent/Wear/clothing.shirt_riot/RiotShirt.prefab
        // source-sha256: 39cad975f4e6662db7d3d67e2d9993ba966ba519ca8ce438988453119e506c1d Assets/HexLiveContent/Wear/clothing.shirt_riot/RiotShirt.prefab.meta
        // source-sha256: 7d5e1d864e6859086e64640dae00f6308068cfef654efa22d95f44a2a93a0d1b Assets/HexLiveContent/Wear/clothing.shoes_tod/TodShoes.prefab
        // source-sha256: 03802c07c33bec19b33eb3e60ce306d432d7464787bb92e40e3c75223e85e450 Assets/HexLiveContent/Wear/clothing.shoes_tod/TodShoes.prefab.meta
        // source-sha256: bea1e66383ae5f75e30f08c8f8bcccf813998818e5e24140751efc119e5793b6 Assets/HexLiveContent/Wear/clothing.shorts_cindy/CindyShorts.prefab
        // source-sha256: 9f1eda3a41f1e8b4f897fab9ee7b77d76875d8ba48adfcf3ccdd9f2a33f35e04 Assets/HexLiveContent/Wear/clothing.shorts_cindy/CindyShorts.prefab.meta
        // source-sha256: 3c8d882471f815ddbab4aad1a11e6b61f6655db39297d281d32c5d815d4045c9 Assets/HexLiveContent/Wear/clothing.shorts_classic/ClassicShorts.prefab
        // source-sha256: fe250a941c44c9d6514ee1232a951074b9ff87c09f8f6b38b684178c5f616595 Assets/HexLiveContent/Wear/clothing.shorts_classic/ClassicShorts.prefab.meta
        // source-sha256: 8f327341685ecfe6a0f68ff2c7df494dc1c630142a70139ddcce216b66b192c9 Assets/HexLiveContent/Wear/clothing.shorts_deadly/DeadlyShorts.prefab
        // source-sha256: 55cf19933d46b2e47e54863f691dfde7427d64f092f2a9654e9b5753dbee0f86 Assets/HexLiveContent/Wear/clothing.shorts_deadly/DeadlyShorts.prefab.meta
        // source-sha256: a59a7054f879a04cd84cdbdf4844b699187704b3e5fe3631e493fefe58cbb752 Assets/HexLiveContent/Wear/clothing.shorts_fit/FitShorts.prefab
        // source-sha256: 2a77976ae9c34714438d15bd6df9b15e11f6f5e7cc539de0698445cd04a630b3 Assets/HexLiveContent/Wear/clothing.shorts_fit/FitShorts.prefab.meta
        // source-sha256: ad052ea1d622e5e644861376292f4fc420fa30b8a8a815c3ec662b4adccfbdd4 Assets/HexLiveContent/Wear/clothing.shorts_osiris/OsirisShorts.prefab
        // source-sha256: ac74cd0b143e34600955c7d227bbd15e898edead2ed0fbc572ca74837b516788 Assets/HexLiveContent/Wear/clothing.shorts_osiris/OsirisShorts.prefab.meta
        // source-sha256: 35e78785b703439c2119a9a13056aa98c18c85396e902151f036d0c4c1e29432 Assets/HexLiveContent/Wear/clothing.shorts_riot/RiotShorts.prefab
        // source-sha256: 1bda51a27624b2e63d7d9b2d56f0161dc28f740f42781030a63f3bc83f287dd2 Assets/HexLiveContent/Wear/clothing.shorts_riot/RiotShorts.prefab.meta
        // source-sha256: a601ebb4f8f20c1945aa5177fb3b0e549670cb8330b05d146d7e406977b7b5cf Assets/HexLiveContent/Wear/clothing.shorts_summer/SummerShorts.prefab
        // source-sha256: 3f00bd228115bdd5af578329f5ab417759af8fb3c774c0154bad818d638a00fe Assets/HexLiveContent/Wear/clothing.shorts_summer/SummerShorts.prefab.meta
        // source-sha256: 8d9f7ca814b53cf91f528c7599adc49cba8e23f2b3ef359a8ddef5788818fb46 Assets/HexLiveContent/Wear/clothing.shorts_vamp/VampShorts.prefab
        // source-sha256: 5c3d341f3728573c4bc5950865ae9fbdca71cd411f152b39d2798d2bfe023564 Assets/HexLiveContent/Wear/clothing.shorts_vamp/VampShorts.prefab.meta
        // source-sha256: 94fdefe648ffa9ab64f66ca64e7190535a3014a771c7242bc5322ee03118152b Assets/HexLiveContent/Wear/clothing.skirt_alloy/AlloySkirt.prefab
        // source-sha256: aae2d0ee5164b0035277730cb25f213ac588aa1f9988f2679066e431c6c5f3d1 Assets/HexLiveContent/Wear/clothing.skirt_alloy/AlloySkirt.prefab.meta
        // source-sha256: 964383752ad44dfdaa8f8ab7abfabdab628fe5acd1c41a299dc99a8aac192f55 Assets/HexLiveContent/Wear/clothing.skirt_amy/AmySkirt.prefab
        // source-sha256: 4e7d52450d5b85e6820093a2386b9a858d5b8f87020d086d2df96577192e6d9a Assets/HexLiveContent/Wear/clothing.skirt_amy/AmySkirt.prefab.meta
        // source-sha256: e4c500b9974ebe3378f0e7102eaeaee707689e23ad10beab6deb26adf4b9c58d Assets/HexLiveContent/Wear/clothing.skirt_anarchy/AnarchySkirt.prefab
        // source-sha256: 740cc3a2bf1d92f52c2ee56790274093ad7cac73760ff18bdc5d3f9ef99db426 Assets/HexLiveContent/Wear/clothing.skirt_anarchy/AnarchySkirt.prefab.meta
        // source-sha256: 950d2d3b0a469c37a428ba6134fe91e423a3c1299ce5a9ee9b2fbe7cf294ff75 Assets/HexLiveContent/Wear/clothing.skirt_flair/FlairSkirt.prefab
        // source-sha256: afc578ce5115416f750fca251292cd2bc55e9b864745916d10603349a64ff179 Assets/HexLiveContent/Wear/clothing.skirt_flair/FlairSkirt.prefab.meta
        // source-sha256: 3e40f2a7199f32bbd0548a7d50ebc38a2504f9577f313d701f11d2ee1d669a32 Assets/HexLiveContent/Wear/clothing.skirt_jane/JaneSkirt.prefab
        // source-sha256: e9277d8a490b6efed48d596490ee39abf374fb13bea759f14bc13d56a3a5fa67 Assets/HexLiveContent/Wear/clothing.skirt_jane/JaneSkirt.prefab.meta
        // source-sha256: da1b02a9a0d5de9ad15178c1c63a15b27a9448188eda18e78ab0ebe95868d9c5 Assets/HexLiveContent/Wear/clothing.skirt_naughty/NaughtySkirt.prefab
        // source-sha256: d3de365795c08781eceec794e334c42713e41050d17d38057b9a5f1a62a06fe3 Assets/HexLiveContent/Wear/clothing.skirt_naughty/NaughtySkirt.prefab.meta
        // source-sha256: f19be5079ebee6973e720043d120ffe3b1ced5fa38f3fb4a6684c9e1b79997e4 Assets/HexLiveContent/Wear/clothing.skirt_primal/PrimalSkirt.prefab
        // source-sha256: 294cdb5898e95b08f16afe203c4d86727afda9e3899e661e28a897b01686e58b Assets/HexLiveContent/Wear/clothing.skirt_primal/PrimalSkirt.prefab.meta
        // source-sha256: 3fd874f575753aa16810ff8cd2aa6d341dd80293e93fca1af97768ce2eb0c053 Assets/HexLiveContent/Wear/clothing.skirt_wild/WildSkirt.prefab
        // source-sha256: b3899c670ff65d14656fd860d0d36d5c63b3e9d7e908b950cc03cd2f4ad8fbda Assets/HexLiveContent/Wear/clothing.skirt_wild/WildSkirt.prefab.meta
        // source-sha256: 02d92e52f72456b79c4686f390cd430773e1ae91ef4272a31af8e4086c7c26d2 Assets/HexLiveContent/Wear/clothing.sleeves_idol/IdolSleeves.prefab
        // source-sha256: 4639dcfaf70c48d91a06361726074ed4349e5f3c3d671b7deb9ec2429db87469 Assets/HexLiveContent/Wear/clothing.sleeves_idol/IdolSleeves.prefab.meta
        // source-sha256: bcc3d325751fa8bc7c5f01f023fd20af7c126773ae0e77e022e2d59ea065ecb7 Assets/HexLiveContent/Wear/clothing.slipons_fads/SlipOns.prefab
        // source-sha256: d1fb204edbc0499ac8eac0ca46e40b0fdb903a6f13792170505efddf38b7238a Assets/HexLiveContent/Wear/clothing.slipons_fads/SlipOns.prefab.meta
        // source-sha256: 6b867ad9231cd8d9baa8d8cb1414b63601cddecae690f64e21d566e593bf5379 Assets/HexLiveContent/Wear/clothing.sneakers_canvas/RealSneakers.prefab
        // source-sha256: 3b37cdbd3e20e308fdf80cf436071ed317c34e6460dd9b7351aa991fa8665adf Assets/HexLiveContent/Wear/clothing.sneakers_canvas/RealSneakers.prefab.meta
        // source-sha256: a2c72cf60a0e7eed30e59f8a84f0e92e2df5c320ec9a8ad8509de2662aa83509 Assets/HexLiveContent/Wear/clothing.sneakers_nerd/NerdSneakers.prefab
        // source-sha256: 1ad99848258029cd269e756728ed99e63c235c414f77ecc31425e39858387ae7 Assets/HexLiveContent/Wear/clothing.sneakers_nerd/NerdSneakers.prefab.meta
        // source-sha256: 4601615335bdb2a250c9eb851013ba88182d3fc10ba119e02434539687171287 Assets/HexLiveContent/Wear/clothing.sneakers_sport/SportSneakers.prefab
        // source-sha256: 5a7974591d1148735ff0968ecc72b49b8cca8a0f9bd3017da847ee9bc7a9e2a0 Assets/HexLiveContent/Wear/clothing.sneakers_sport/SportSneakers.prefab.meta
        // source-sha256: 0470f4a473ed83a44f5f6094fb28db018b9dd6701a33ce913b0ab84854b03380 Assets/HexLiveContent/Wear/clothing.suit_bandaid/BandaidSuit.prefab
        // source-sha256: efb16295989c8af8f32c736ae66c8205eef890f7bab87aad7d717a6c7c14df30 Assets/HexLiveContent/Wear/clothing.suit_bandaid/BandaidSuit.prefab.meta
        // source-sha256: 41c09ff867067ed8fb6e3d1b8fda903edd8aacf701cc8f2905482f1dc408eb79 Assets/HexLiveContent/Wear/clothing.sunglasses_luxury/LuxurySunglasses.prefab
        // source-sha256: 3ef842ea3861e79076d8fe7cdd3d5ae86a99662ef6666b3cc50d528dfe91016f Assets/HexLiveContent/Wear/clothing.sunglasses_luxury/LuxurySunglasses.prefab.meta
        // source-sha256: 0f5842e24f264ba0a49a6e62bfbc7c77051d4753b4c26c451a3329eb9da41911 Assets/HexLiveContent/Wear/clothing.sweater_flair/FlairSweater.prefab
        // source-sha256: e88131b908b021808dd31c4a5c3d1c626114b417283dcb09fd82e40c19015657 Assets/HexLiveContent/Wear/clothing.sweater_flair/FlairSweater.prefab.meta
        // source-sha256: e0ad4bf647d265818f7c524df183b98cfbca795058b44c848a0e96527818320e Assets/HexLiveContent/Wear/clothing.sweater_naughty/NaughtySweater.prefab
        // source-sha256: 4eb630babd793c7de259eccd89ad53bfbd64d1c880f4e683f05d7a2a5e187a20 Assets/HexLiveContent/Wear/clothing.sweater_naughty/NaughtySweater.prefab.meta
        // source-sha256: 45b496f0ca92fb81ae0df096a9fe10130ddd41c0a524a4d45a39f512fb37a173 Assets/HexLiveContent/Wear/clothing.tank_jane/JaneTank.prefab
        // source-sha256: c6c327df3e12c46d0ac7e7c5f65ffedc645eb2be8bf075a791f5b65c0f974ce5 Assets/HexLiveContent/Wear/clothing.tank_jane/JaneTank.prefab.meta
        // source-sha256: 95e5444fdb7ba94cc474c9b813611d0f330d086b5eb4e7f6f6158a32d38da0cf Assets/HexLiveContent/Wear/clothing.tanktop_summer/SummerTankTop.prefab
        // source-sha256: 6f16c02cc38424a055d47f800f011184e475e86ecea74f3f985aa48e0e2d1123 Assets/HexLiveContent/Wear/clothing.tanktop_summer/SummerTankTop.prefab.meta
        // source-sha256: 310422eaad53f439e71a7b7c18424407a35dace340ccf755d442490a8a58fff3 Assets/HexLiveContent/Wear/clothing.thighboots_amy/AmyThighBoots.prefab
        // source-sha256: 4b00c9b276315e4fc6ac896da90483f5b72dbbe992bc8f6791f2929b6d8db6ee Assets/HexLiveContent/Wear/clothing.thighboots_amy/AmyThighBoots.prefab.meta
        // source-sha256: 3d660f4f3ef799a93a069ad8fb99747cd87b4b3d549d7546d582312c19b4799a Assets/HexLiveContent/Wear/clothing.top_alloy/AlloyTop.prefab
        // source-sha256: 402f08a7b78a18d2cac8bf377676664812d269a77e98bb18d263e03c15f424f7 Assets/HexLiveContent/Wear/clothing.top_alloy/AlloyTop.prefab.meta
        // source-sha256: 34a3f41a7d8f08c6e104d8b4a9fcf4e977de8a9cf094af1b939bb71505f585ba Assets/HexLiveContent/Wear/clothing.top_anarchy/AnarchyTop.prefab
        // source-sha256: eafbad554632fd320aa172ab6caa7bb2fd355763b48b6b999770c61886dcda99 Assets/HexLiveContent/Wear/clothing.top_anarchy/AnarchyTop.prefab.meta
        // source-sha256: a8310699f99924408d161bd57ac97e58c9ae5850e8ac15f5726fb893395200e5 Assets/HexLiveContent/Wear/clothing.top_classic/ClassicTop.prefab
        // source-sha256: deff797d5f8b1cdefbc25d5ffe392b0a84f294aa1313369efd86027f1bce724f Assets/HexLiveContent/Wear/clothing.top_classic/ClassicTop.prefab.meta
        // source-sha256: 8af2d090598b45c4183beff0b245a5bf9759f01f2379e3b06c888e6b91e4a7c4 Assets/HexLiveContent/Wear/clothing.top_deadly/DeadlyTop.prefab
        // source-sha256: fffad1fb0a24c702ea5615469d74ad6293c02c5f4c43b5d9a32ada31cdf8c72f Assets/HexLiveContent/Wear/clothing.top_deadly/DeadlyTop.prefab.meta
        // source-sha256: 72c94d032511ca0b6702b299c9061dfcba4d834804e712f3f88b77b696eb2046 Assets/HexLiveContent/Wear/clothing.top_fighter/FighterTop.prefab
        // source-sha256: aaae9f8924f9c40bb533ceb77db39228c3817c647a4aaba62e382e4a0a3ea2d2 Assets/HexLiveContent/Wear/clothing.top_fighter/FighterTop.prefab.meta
        // source-sha256: d219d2745b7b2b1b8722faa440b330643d408859e26cc8e00f20b491212b24b7 Assets/HexLiveContent/Wear/clothing.top_folk/FolkTop.prefab
        // source-sha256: e9fb10b2c0000e0ff012908d24f07c926ce0e3ee8a612d741d28fe90ad95a5dd Assets/HexLiveContent/Wear/clothing.top_folk/FolkTop.prefab.meta
        // source-sha256: 92a6aab073f13d09c8577e1ab06f8c46d31cf42f07c6352c4623f20b83af5e26 Assets/HexLiveContent/Wear/clothing.top_osiris/OsirisTop.prefab
        // source-sha256: e80aebefac116965eb2fe0eed3c6ee8d7804b23dc7daa6ea4fc9f2fcb429820d Assets/HexLiveContent/Wear/clothing.top_osiris/OsirisTop.prefab.meta
        // source-sha256: dc4225325f38071d1be7b197d62d63e0fe940ab9d975659aacda2977746c0ff1 Assets/HexLiveContent/Wear/clothing.top_primal/PrimalTop.prefab
        // source-sha256: 8405bef2db255d5af053ef95e392864bd87ba8e9bedc074bc40efb5a3d72e7f8 Assets/HexLiveContent/Wear/clothing.top_primal/PrimalTop.prefab.meta
        // source-sha256: 84578420e04a2f1089387c05df4d824326cbe00b0ebac86ea93bfb87cd6705cc Assets/HexLiveContent/Wear/clothing.top_stars/StarsTop.prefab
        // source-sha256: c0732cacaf381e3da6cceb75aee9e9743b14d4a256b8e0ff91dd4fb2ad384a59 Assets/HexLiveContent/Wear/clothing.top_stars/StarsTop.prefab.meta
        // source-sha256: 88f72bf0bba6fce8a7e5ad64e3f0824e1767085c71ca09a7563936fa4e9f768a Assets/HexLiveContent/Wear/clothing.top_tod/TodTop.prefab
        // source-sha256: 3e6bff170c24658314891c4b8fd215dc167cc285cd833a5668f3b1c201165f86 Assets/HexLiveContent/Wear/clothing.top_tod/TodTop.prefab.meta
        // source-sha256: 90ab9405413a76600e38c37435dc7ccc16c8127e8388604e12c8b512b0a84402 Assets/HexLiveContent/Wear/clothing.top_yoga/YogaTop.prefab
        // source-sha256: 2dca13d7b4ad24864be9fc0611862172c24278ef10db1613035db06da469b42b Assets/HexLiveContent/Wear/clothing.top_yoga/YogaTop.prefab.meta
        // source-sha256: fd9132a053f2becaf21aa53aea82bef4c0b7a6219039b137f891a5038609a8bf Assets/HexLiveContent/Wear/clothing.tshirt_big/BigTee.prefab
        // source-sha256: c57e53ed89d396dab4d998fcf62e51b45d684c36b91d9b05ebacb96d0453c03c Assets/HexLiveContent/Wear/clothing.tshirt_big/BigTee.prefab.meta
        // source-sha256: 48ab3b79561845bb4006f4d6048117bbb2fa427b098c5e2514468737e35e29ea Assets/HexLiveContent/Wear/clothing.tshirt_summer/SummerTShirt.prefab
        // source-sha256: 5b89a5f6c10440942216d0e1e4cfd582fc5fb387fa9e19a39d7c3b1c5200797b Assets/HexLiveContent/Wear/clothing.tshirt_summer/SummerTShirt.prefab.meta
        // source-sha256: b0bb0c5bb92b7b74620eec8666090b67482fc59071f64e939b17936fffd3ce8e Assets/HexLiveContent/Wear/clothing.tutu_nerd/NerdTutu.prefab
        // source-sha256: e0f7a57d363802c3c48bb43a41e6fbbf6da7f82f6f169fe06cf7113b09b965b5 Assets/HexLiveContent/Wear/clothing.tutu_nerd/NerdTutu.prefab.meta
        // source-sha256: 8a166a8e2eb0cde9e02f34461d2b32f3b9966bc8fc7ac5478daa50145d1de296 Assets/HexLiveContent/Wear/clothing.vest_ranger/RangerVest.prefab
        // source-sha256: b61bfb84bd723631d577f4107fb49549d002eeb4d357f05af25a18147c8ba62d Assets/HexLiveContent/Wear/clothing.vest_ranger/RangerVest.prefab.meta
        // source-sha256: df8bb60eff07e9ff613d1e99897e46527e3b38ecf0a125cb4e90003871f8d826 Assets/HexLiveContent/Wear/clothing.vest_stars/StarsVest.prefab
        // source-sha256: 75d38c9f5a75de79036b1d6fed53940307cc3c670a675d344c0d5f908624d52a Assets/HexLiveContent/Wear/clothing.vest_stars/StarsVest.prefab.meta
        // source-sha256: 7e2581f01c3b064c0acb70bb4ab9a3deddcc38ec564db967a5575bd3ff393a4c Assets/HexLiveContent/Wear/clothing.wrapboots_primal/PrimalWrapBoots.prefab
        // source-sha256: 1fbf7446c7a6719ecd7d0cf2161d1bc7ed73baefa42fc609c522e46bb948bfb8 Assets/HexLiveContent/Wear/clothing.wrapboots_primal/PrimalWrapBoots.prefab.meta
        // source-sha256: 157cf9f266c7f6e1c06a9a7feae312a84d01f55f5bc788934c376bf79a1e019b Assets/HexLiveContent/Wear/clothing.yogapants_tek/TekYogaPants.prefab
        // source-sha256: b4837ec63fd535c53974d08e76f8d1d5c4c0288879bade7ec4205323193d2dd2 Assets/HexLiveContent/Wear/clothing.yogapants_tek/TekYogaPants.prefab.meta
        // source-sha256: c9fe9075bf927c5c747e37282587e6ceb66afab5d49cf6cecbc126612a3586e2 Assets/HexLiveContent/Wear/gear.backpack_osiris/OsirisBackpack.prefab
        // source-sha256: fae352ccc012ece7a5e98f8b3a8f0203a28f65b0da3ecdedfb7518a672e48759 Assets/HexLiveContent/Wear/gear.backpack_osiris/OsirisBackpack.prefab.meta
        // source-sha256: c7dd31b24135bfb97ae715bf8516e82341dc8b99938abdbde686b8864b3bcae6 Assets/HexLiveContent/Wear/gear.backpack_riot/RiotBackpack.prefab
        // source-sha256: 7f75a96b632b92d69961ff3589b31109a794ceddd5d35d56045d19df3ff38ab0 Assets/HexLiveContent/Wear/gear.backpack_riot/RiotBackpack.prefab.meta
        // source-sha256: 2b889cf067c06a7b5ae0bbd29a966d2e16f4b33fbaf0ed1d716241ee3fb8cdce Assets/HexLiveContent/Wear/gear.beltpouch_ranger/RangerBeltPouch.prefab
        // source-sha256: f8ba9ff05b890337cb31844d3b0ccc90e6a15750014cf0b98ce7260a3273e6dd Assets/HexLiveContent/Wear/gear.beltpouch_ranger/RangerBeltPouch.prefab.meta
        // source-sha256: 7fa308ba7fe739b9f70ab6dc2912c33cfd7eb75f72e6ef4bb97c742d093cc8cc Assets/HexLiveContent/Wear/underwear.bikini_briefs/BikiniBriefs.prefab
        // source-sha256: f1309a48a64d2cfb081d7120aa44c4d1b8d6d096d29342348e224e6f6b6a9d8d Assets/HexLiveContent/Wear/underwear.bikini_briefs/BikiniBriefs.prefab.meta
        // source-sha256: 3c21abf2dcf90fd0e4ab043481b9f70ef6b851e58c6fe8405fe5d0c5ef39a58f Assets/HexLiveContent/Wear/underwear.bikini_top/BikiniTop.prefab
        // source-sha256: 598186ccd0bcb261cc64d246a141de938dcda251503c90430eb68433066b0f26 Assets/HexLiveContent/Wear/underwear.bikini_top/BikiniTop.prefab.meta
        // source-sha256: 42a7be53666f2f431fd7ea3544861e66620940aa738f60987c1b240e5900fc76 Assets/HexLiveContent/Wear/underwear.bra_lace/LaceBra.prefab
        // source-sha256: d747159ce3bebc0cf37898a44e4e7ba68e58ccfc37a149c619b80eb3420d9701 Assets/HexLiveContent/Wear/underwear.bra_lace/LaceBra.prefab.meta
        // source-sha256: 60c2b9b40aa9f54b81fb999f0635b4152b6f13eb932120991148eef37b1b1f63 Assets/HexLiveContent/Wear/underwear.bra_openback/OpenBackBra.prefab
        // source-sha256: adcd8195229e66c3ff9fdcbbe25a29f78f0bf00a4ea4fe1d0ebef485340244d1 Assets/HexLiveContent/Wear/underwear.bra_openback/OpenBackBra.prefab.meta
        // source-sha256: 9bc2ae08b56f518f27f59b1367cd2adc0a3c6f721919b34a51fb94d71dbcc993 Assets/HexLiveContent/Wear/underwear.bra_riot/RiotBra.prefab
        // source-sha256: 4dacc8ae81b19d6352c6454c3ac3c7d214f9449a04edbcc69f749673a37f44bc Assets/HexLiveContent/Wear/underwear.bra_riot/RiotBra.prefab.meta
        // source-sha256: 718341042875261b804db78b3a14a08799c482d47c9eb91e834795a954b86397 Assets/HexLiveContent/Wear/underwear.bra_strappy/StrappyBra.prefab
        // source-sha256: f976921135140dac7d7cba873166c24bf0191179caf75bf7bf1879e1a22e1e28 Assets/HexLiveContent/Wear/underwear.bra_strappy/StrappyBra.prefab.meta
        // source-sha256: ba1e46f55b56aa39e81c4beebd7ecd49cc9587512911df95abfbff63a5b9cae8 Assets/HexLiveContent/Wear/underwear.bra_wild/WildBra.prefab
        // source-sha256: 794b3b9f0e3f34846d7da87ecec2efeba37d9a906da7d5bd7e0ed57a7ce71798 Assets/HexLiveContent/Wear/underwear.bra_wild/WildBra.prefab.meta
        // source-sha256: 7d136cd46e9418895b1c7193043abd4012c6f57af536da14a94af62dd2ac6776 Assets/HexLiveContent/Wear/underwear.briefs_cindy/CindyBikiniBriefs.prefab
        // source-sha256: 2c41c48314b1021e577df882bac45385c521b4bbd4d79bc53d66d9ad749593d6 Assets/HexLiveContent/Wear/underwear.briefs_cindy/CindyBikiniBriefs.prefab.meta
        // source-sha256: 1596910c14db2c401d4f83d7b66d13d448a75fb96186d2eb1d447ded266dc6af Assets/HexLiveContent/Wear/underwear.briefs_flair/FlairBriefs.prefab
        // source-sha256: 134d16a06269978ef19abf055daa64255b3e21445811df97db7d760a6bbed7f5 Assets/HexLiveContent/Wear/underwear.briefs_flair/FlairBriefs.prefab.meta
        // source-sha256: 20b49b9d9c3b518ecfe885c4b5dbc77312d29e103b8657702867aaa1241b37bb Assets/HexLiveContent/Wear/underwear.briefs_lace/LaceBriefs.prefab
        // source-sha256: 506c9069b9d23acda73201ccca0390a869593d61d85115091d071e5e565de714 Assets/HexLiveContent/Wear/underwear.briefs_lace/LaceBriefs.prefab.meta
        // source-sha256: 5badc6fa1241127240e48cafb88033daf1784ca2a8873a091ee86b84e15d9454 Assets/HexLiveContent/Wear/underwear.briefs_luxury/LuxuryBikiniBriefs.prefab
        // source-sha256: 25c565766793d265a4339bc06fcc7185125d8f58405bd51ce51508bf540e3011 Assets/HexLiveContent/Wear/underwear.briefs_luxury/LuxuryBikiniBriefs.prefab.meta
        // source-sha256: 2276306b9b211158e73d90a48b6ea6ed5574624265d5a3b7020f78e397be065b Assets/HexLiveContent/Wear/underwear.briefs_openback/OpenBackBriefs.prefab
        // source-sha256: 9489f93ae96b3bfc55377ae202efd1c2a1cb9747c3504e041a646c80395e5eb4 Assets/HexLiveContent/Wear/underwear.briefs_openback/OpenBackBriefs.prefab.meta
        // source-sha256: 2cf334a6093e3f9bf937c2153a8047db15b14d24ba2967837b508261f848cbc9 Assets/HexLiveContent/Wear/underwear.briefs_plain/PlainPanties.prefab
        // source-sha256: 491facad3d9ecff8e221c5e6ba9525d0fa8b0157271e836d62450c8c7ccd2cfa Assets/HexLiveContent/Wear/underwear.briefs_plain/PlainPanties.prefab.meta
        // source-sha256: a5d0924034a22b50b6138e6de30653472945ba4a56b8eb584e9256513220ea80 Assets/HexLiveContent/Wear/underwear.briefs_primal/PrimalBriefs.prefab
        // source-sha256: 476442a1ec88b9b0d0a06d5abc08de185a32b654782165c4d368eaa2d3a7ccc1 Assets/HexLiveContent/Wear/underwear.briefs_primal/PrimalBriefs.prefab.meta
        // source-sha256: acd4742cba0d3e8e18e171bf440aaf6ae0b2ccc07d7e5aea07ba56ccae8f2f21 Assets/HexLiveContent/Wear/underwear.briefs_sport/SportsBriefsJmr.prefab
        // source-sha256: 303801d7e32d50bf4453ce741bffbfa3f2baae04b65a35d9096fe0d06beb4308 Assets/HexLiveContent/Wear/underwear.briefs_sport/SportsBriefsJmr.prefab.meta
        // source-sha256: 5aa70de48fbd18fb65621cff57fb1fbd5f68f06de8e3f5ab8f25a169340dd37e Assets/HexLiveContent/Wear/underwear.briefs_strappy/StrappyBriefs.prefab
        // source-sha256: defeeca4b0005b9440476fc921299a0b7e940997ccbbcb78a6278eb956877e34 Assets/HexLiveContent/Wear/underwear.briefs_strappy/StrappyBriefs.prefab.meta
        // source-sha256: e31f3ba5ae2689638b06d5d38124e9509eadc447246e20d5439c0668762b3205 Assets/HexLiveContent/Wear/underwear.briefs_sweety/SweetyPanty.prefab
        // source-sha256: 92be117a1aa8552ff4035e599b02e5fea29d5d2be710551b01a56b357f68b3c0 Assets/HexLiveContent/Wear/underwear.briefs_sweety/SweetyPanty.prefab.meta
        // source-sha256: b77fb12985c78003fdf9f4207f7d9b7e938d3db88bbc657caa2dd8d8bcc66efc Assets/HexLiveContent/Wear/underwear.briefs_teez/BigTeePanty.prefab
        // source-sha256: 9826584a99884ca3b4574bbf8f7a349c824aeb4ec19079f41f7bb7b52cf450c1 Assets/HexLiveContent/Wear/underwear.briefs_teez/BigTeePanty.prefab.meta
        // source-sha256: 070c3e2c92b0923484902240423e69a0f3d6609fb5b8da786e3b660b58c621ff Assets/HexLiveContent/Wear/underwear.briefs_vapor/VaporBriefs.prefab
        // source-sha256: 1671d73b9cd48b017427f716af44cc7967e7b2dc8b5d75148fa932c29da3ed97 Assets/HexLiveContent/Wear/underwear.briefs_vapor/VaporBriefs.prefab.meta
        // source-sha256: 840016b2892c17e2fabb013079bd46efeae331136356764f7f791be4c9faddd8 Assets/HexLiveContent/Wear/underwear.briefs_wild/WildPanty.prefab
        // source-sha256: a2a7fa6e3ca73cc354e95d44b6bae1b292cf81b1f14592e59c8a77afb148f104 Assets/HexLiveContent/Wear/underwear.briefs_wild/WildPanty.prefab.meta
        // source-sha256: e77384f89e4ac604095264c02b7bcc146d97943d8bb1aebf9421f35fc9985147 Assets/HexLiveContent/Wear/underwear.kneesocks_nerd/NerdKneeSocks.prefab
        // source-sha256: 59c54e7a9f12cfb266a9c302cf20916ad9a7833dd8ae8839b6327e08c42332a0 Assets/HexLiveContent/Wear/underwear.kneesocks_nerd/NerdKneeSocks.prefab.meta
        // source-sha256: c4358f548a4a3bfe0ca2849fb596620bde4086c375a999297fa5d6bdf0600ec0 Assets/HexLiveContent/Wear/underwear.panty_tod/TodPanty.prefab
        // source-sha256: 358cf6bfd67c3f3ddcab9e11d80faa21c38ef2d7bf0cf5444e67a1bd3e6842db Assets/HexLiveContent/Wear/underwear.panty_tod/TodPanty.prefab.meta
        // source-sha256: 7eb211cb2d23e33dd5993442520d2ecc127b73c3137acf7f537d0bb91ca493f3 Assets/HexLiveContent/Wear/underwear.socks_fit/FitSocks.prefab
        // source-sha256: ab009bc5c7a995746335e4b4b41de155670e858f1e807e4440fa8acf5f37b6eb Assets/HexLiveContent/Wear/underwear.socks_fit/FitSocks.prefab.meta
        // source-sha256: 4a64792287c36e8a0febab07eb446f34556b426bd7ee068beffe1c4353483261 Assets/HexLiveContent/Wear/underwear.sportsbra_jmr/SportsBraJmr.prefab
        // source-sha256: 17a8a70dd4d641b69cef495168bf031b0c4e36d38a5be1e7deae0148268b0d44 Assets/HexLiveContent/Wear/underwear.sportsbra_jmr/SportsBraJmr.prefab.meta
        // source-sha256: 08996cda7aa219350c5aa47a4fea4b062aac219e2991613ac8de76b7af3b65ac Assets/HexLiveContent/Wear/underwear.sportsbra_tek/TekSportsBra.prefab
        // source-sha256: 6791600b70165a97268289443757d1886a18526a3175c565ac75e4e38c306980 Assets/HexLiveContent/Wear/underwear.sportsbra_tek/TekSportsBra.prefab.meta
        // source-sha256: c276ddd6d5c46fecf17295465cc7df8a3b3940c792564b6e465caaf4baa59277 Assets/HexLiveContent/Wear/underwear.stockings_overknee/OverKneeSocks.prefab
        // source-sha256: 8ac196b40409b03ae295668a044ec289d833bfc4286892a51fd28458389c1ce0 Assets/HexLiveContent/Wear/underwear.stockings_overknee/OverKneeSocks.prefab.meta
        // source-sha256: 49e3d7dd0b42496460fb40003ac1f3de42bcc597b0d7336a000151934a73a3a4 Assets/HexLiveContent/Wear/underwear.stockings_riot/RiotStockings.prefab
        // source-sha256: b8894650b23dff3ce6636cf79703b742a74d7526fa2a451bde3b84ab28781a7a Assets/HexLiveContent/Wear/underwear.stockings_riot/RiotStockings.prefab.meta
        // source-sha256: ca8833b24d5cf8d6e80ece215edc1af693d53449e5db19bea274e83378dce6f6 Assets/HexLiveContent/Wear/underwear.stockings_spooky/SpookyStockings.prefab
        // source-sha256: 1c7375aefa3be1b9b5593d615e6b6a059228ba54914934f977e0c1fbe84a2797 Assets/HexLiveContent/Wear/underwear.stockings_spooky/SpookyStockings.prefab.meta
        // source-sha256: b4ba46c74a3301295ebc0c9ff8f8313b31e24ddee5582905fca028d72826635f Assets/HexLiveContent/Wear/underwear.stockings_tod/TodStockings.prefab
        // source-sha256: 8fd4eed3189769e9507cb177f9ddd3017b5aeb89ebfef783d6cf662d0773261e Assets/HexLiveContent/Wear/underwear.stockings_tod/TodStockings.prefab.meta
        // source-sha256: 5f518f473d9eacaae4a6ff1be53d515678093e9d764de6c52ea43ef0039968c3 Assets/HexLiveContent/Wear/underwear.swimsuit_briefs/SwimsuitBriefs.prefab
        // source-sha256: a02c3c62dbd236f1c93b5f17b6f3b084271120ff30f2ec2d284b5217d83781a4 Assets/HexLiveContent/Wear/underwear.swimsuit_briefs/SwimsuitBriefs.prefab.meta
        // source-sha256: 27f57bdb973c77b9778a4fa8e360f96567bcb2eb41ed2bf36a7096393bf2b09f Assets/HexLiveContent/Wear/underwear.swimsuit_top/SwimsuitTop.prefab
        // source-sha256: 7bc3a54440ef053371063ccb251b85aeba8b90b9a5dc67ba8d2c61da0ddf366d Assets/HexLiveContent/Wear/underwear.swimsuit_top/SwimsuitTop.prefab.meta
        // source-sha256: df18687ff2a91d35d4fa2afd3e368d3ec78c1c90269952453bef1d0943f84a72 Assets/HexLiveContent/Wear/underwear.thong_anarchy/AnarchyThong.prefab
        // source-sha256: 3412882d30f7ed4cdc71ab3ef621318138891a7aba7dae3bf9334f489d16c45f Assets/HexLiveContent/Wear/underwear.thong_anarchy/AnarchyThong.prefab.meta
        // source-sha256: 117dd576397deee1ee5869a76516dafa839111ee29ebe580080bc1aa3dd78f84 Assets/HexLiveContent/Wear/underwear.tights_deadly/DeadlyTights.prefab
        // source-sha256: d3d10108d545d3a9242deeee42b06be0859a52e70f27bc25e87771aeb84251ba Assets/HexLiveContent/Wear/underwear.tights_deadly/DeadlyTights.prefab.meta
        // source-sha256: d2f109dd2fd8ad8a8b1792a42ff7e05d093887a143427dcc7488c106ba0dade5 Assets/HexLiveContent/Wear/underwear.top_cindy/CindyBikiniTop.prefab
        // source-sha256: f5b37ee27d4266bedd9a66a8b8226f2b87a2a89cb33ea4ca94a02be6daa16b75 Assets/HexLiveContent/Wear/underwear.top_cindy/CindyBikiniTop.prefab.meta
        // source-sha256: 7f65f4f03a9688fdffd28f6488321362fefa0b7c3c9b6d064e3d5a2133ef9c4e Assets/HexLiveContent/Wear/underwear.top_luxury/LuxuryBikiniTop.prefab
        // source-sha256: 3e6cd84da1541d78fd12e7a7d0cb44007c8a666bfbba434a87da3c688cbba039 Assets/HexLiveContent/Wear/underwear.top_luxury/LuxuryBikiniTop.prefab.meta
        // source-sha256: 17842b941367cfd893514c22d9838f70db270d76d7d37cfe06cea24fe8fa35c0 Assets/HexLiveContent/Wear/underwear.top_vapor/VaporTop.prefab
        // source-sha256: 89e66b8d9b3efe9cbe6b639c9302da9c9d18a333055c531e6648eb7ce5385438 Assets/HexLiveContent/Wear/underwear.top_vapor/VaporTop.prefab.meta
        // source-sha256: 5cac8d6b61bb13eb58444b826acf6d0a5144668d233835d68ceea9cc5f3817fb Assets/ImportedActors/Wear/AlloyBoots/Meshes/Jana.mesh
        // source-sha256: f17189139b5613a2ca9a2a6f439a229c13bd247957b7eb34f9e7d96fb9894b51 Assets/ImportedActors/Wear/AlloyBoots/Meshes/Jana.mesh.meta
        // source-sha256: 6937520c2792874e93e8820d6d497f11b1ac6ac27ecb6fa9d5187a7c525cbbd1 Assets/ImportedActors/Wear/AlloySkirt/Meshes/Jana.mesh
        // source-sha256: 150aafa24702b361ece610f4c2d433e7c89ff8acb6a11ac0a1c697bc86f5a4e7 Assets/ImportedActors/Wear/AlloySkirt/Meshes/Jana.mesh.meta
        // source-sha256: 1d1dfc2c5fc810fc2652367ef5a81c30096d377a924c232e2d06b4dd262a0271 Assets/ImportedActors/Wear/AlloyTop/Meshes/Jana.mesh
        // source-sha256: 3cf37a8d4f01ed26ce7c72dee1dcbe38f236f96ffd8d73b417cab42688b276a8 Assets/ImportedActors/Wear/AlloyTop/Meshes/Jana.mesh.meta
        // source-sha256: 32da6024e4a1a960acbceb682db9088e001158fcce29171ad92559046db3bb54 Assets/ImportedActors/Wear/AmyAnkleBoots/Meshes/Jana.mesh
        // source-sha256: b70898d284bb044fb0b238d72d32a751d63f00bc7ab94ca2eed38e84ab76e357 Assets/ImportedActors/Wear/AmyAnkleBoots/Meshes/Jana.mesh.meta
        // source-sha256: 2647b665bae4ae1e8522a805bc0e1966c6c99e3b0450b418a41475f1c5acb193 Assets/ImportedActors/Wear/AmyPendant/Meshes/Jana.mesh
        // source-sha256: 859f313fa4d235b43866ba04040504d879e2dedf7218890a3f77b331a889dd2f Assets/ImportedActors/Wear/AmyPendant/Meshes/Jana.mesh.meta
        // source-sha256: 5ec72c17a9175e398304736cdd80f5eaebb5deba49ac441f455733a541b20f39 Assets/ImportedActors/Wear/AmyShirt/Meshes/Jana.mesh
        // source-sha256: d6829ef4b7766273a31242fee8aaec4e06c0821875bc7052059bae7a5f37ef2a Assets/ImportedActors/Wear/AmyShirt/Meshes/Jana.mesh.meta
        // source-sha256: 514cbb4cea7cc1251b1235653059ec86b6f974eed3a3c2026722f783ece2c99c Assets/ImportedActors/Wear/AmySkirt/Meshes/Jana.mesh
        // source-sha256: b976f23ce99239787913d25791c8f285145227763bd7388db978c15caf8ab76e Assets/ImportedActors/Wear/AmySkirt/Meshes/Jana.mesh.meta
        // source-sha256: 14c9f3e468fee14a66b0fcd2580aa8015dc71aa9c9550a13096d744af71cd9f3 Assets/ImportedActors/Wear/AmyThighBoots/Meshes/Jana.mesh
        // source-sha256: 74a38f5a27e3e9f5577910f42d40cbedea24aea34c15a9c34cf9326f00517b40 Assets/ImportedActors/Wear/AmyThighBoots/Meshes/Jana.mesh.meta
        // source-sha256: 94c1e02bdeab3aa34ca91f6d611a6c438da56b419b6e78872bcbfc76d5a5aa1c Assets/ImportedActors/Wear/AnarchyBelt/Meshes/Jana.mesh
        // source-sha256: 592d4701f38fa9d7e1cb3e9ac65e3a4dd70153945973ded9b787ba7b79501f3d Assets/ImportedActors/Wear/AnarchyBelt/Meshes/Jana.mesh.meta
        // source-sha256: d9582870bc2b527dc4fd5816bf8369fa1807a89734079b747ce80d246d7e7625 Assets/ImportedActors/Wear/AnarchyBlouse/Meshes/Jana.mesh
        // source-sha256: ff1662a9bc2d71f9b58c9a38cb1088a6f163e22b8dc2527fec4b613561cdc47e Assets/ImportedActors/Wear/AnarchyBlouse/Meshes/Jana.mesh.meta
        // source-sha256: f7f309f170f9f8863382f0d039b2238242f5103e7c0dc3d955587f1bd736c705 Assets/ImportedActors/Wear/AnarchyBoots/Meshes/Jana.mesh
        // source-sha256: 92cc6def3f8378f0109a211d492bc2b73ad2428d74a46ffe4540bd454d8abd21 Assets/ImportedActors/Wear/AnarchyBoots/Meshes/Jana.mesh.meta
        // source-sha256: 1735d5123496f6951810c6ec5104fa614140385297c5100e71d30e03c48ad461 Assets/ImportedActors/Wear/AnarchyCap/Meshes/Jana.mesh
        // source-sha256: 2eec331eda5f39565f689c073cbce9bc53a7c6c874be24ed501e5138448fbfa2 Assets/ImportedActors/Wear/AnarchyCap/Meshes/Jana.mesh.meta
        // source-sha256: 27f78018c96b0bfc518d0b71f71fe459e22cdf88ecf2b3b8bb998b873c827cb6 Assets/ImportedActors/Wear/AnarchyCollar/Meshes/Jana.mesh
        // source-sha256: 4b1308a918a46d251f0df5065004fdcf02cc26b4666809784deac6812e383111 Assets/ImportedActors/Wear/AnarchyCollar/Meshes/Jana.mesh.meta
        // source-sha256: 14c14cab3a91e8c4ab593ad6a74176f4022548d00b2714d69b6c6c7d6f747077 Assets/ImportedActors/Wear/AnarchyCorset/Meshes/Jana.mesh
        // source-sha256: 2ffcbc62d1322ddf55c8b802ca34005386b17051f57aac29eef68703dcf32a4c Assets/ImportedActors/Wear/AnarchyCorset/Meshes/Jana.mesh.meta
        // source-sha256: a3de8c0041e189346aa7be0b859a5b8632a2e26b2c7f40a6afb223ee3a5de432 Assets/ImportedActors/Wear/AnarchyCuffs/Meshes/Jana.mesh
        // source-sha256: 9a063fd5b13c4dfd47d2b8e92813ffaac95c191b43d346738b164475030d8692 Assets/ImportedActors/Wear/AnarchyCuffs/Meshes/Jana.mesh.meta
        // source-sha256: 7e0f651d18d51fbb4189693aaf2fb0ab2c8f6ac4305cbc62e5eef68344d31005 Assets/ImportedActors/Wear/AnarchyHipBelt/Meshes/Jana.mesh
        // source-sha256: 8ed9b7c70c1a88fd942352f6c22267fadfd4059516a7f7e210bb5b4ccd4dbd36 Assets/ImportedActors/Wear/AnarchyHipBelt/Meshes/Jana.mesh.meta
        // source-sha256: b8eac2cfa2c95376555a01e4886cf58183bf764604ba38da99f6466d00de4c77 Assets/ImportedActors/Wear/AnarchyLongGloves/Meshes/Jana.mesh
        // source-sha256: 94bddd0e0bb3cadd3d21f0638a430fbfeabf70f5be3c449b0e8ebb8850c5c5ac Assets/ImportedActors/Wear/AnarchyLongGloves/Meshes/Jana.mesh.meta
        // source-sha256: d69c4e5b1eee372c126491f517a44f6cb3929a58f4c54115f1b8d6526ac096ef Assets/ImportedActors/Wear/AnarchyPants/Meshes/Jana.mesh
        // source-sha256: 58c87012cad99c8cc8b32d0e314c5c0f385abef64065a4fde430e834fb0a9348 Assets/ImportedActors/Wear/AnarchyPants/Meshes/Jana.mesh.meta
        // source-sha256: f5e45e716a413ea0d65d946cca46d32aaafc4b90f88930a442fc5362cae0c918 Assets/ImportedActors/Wear/AnarchySkirt/Meshes/Jana.mesh
        // source-sha256: 2808ad3e0a3b01de15bae615bc857c2bd28846cae5e9997647a0728ae90967e3 Assets/ImportedActors/Wear/AnarchySkirt/Meshes/Jana.mesh.meta
        // source-sha256: 4da571690dbe740df51915beb13a147fd8acb79a5bc8dc9524d732468aff44f6 Assets/ImportedActors/Wear/AnarchyStrapGloves/Meshes/Jana.mesh
        // source-sha256: 40d58fa70d1cadfc8d82e3d5371b6b0c28c6d8575286a8738a5b51d922cf28ea Assets/ImportedActors/Wear/AnarchyStrapGloves/Meshes/Jana.mesh.meta
        // source-sha256: f1ab07e545bebaca46a4927640a14f7252a16d16c3250cf80d5cbcfea1553f8e Assets/ImportedActors/Wear/AnarchyStuddedCollar/Meshes/Jana.mesh
        // source-sha256: 6636563a787f854ee604114cc6757bce0c047131fd07c34327e07ab5c0757904 Assets/ImportedActors/Wear/AnarchyStuddedCollar/Meshes/Jana.mesh.meta
        // source-sha256: 33bbd27900f4e2a15ec23ef86d389056d141d2f04cfd236f4f050e8a2eec2274 Assets/ImportedActors/Wear/AnarchyThong/Meshes/Jana.mesh
        // source-sha256: 46c4d7fc62f6faa59ada7e092580bf874973812b11e9878c5e931c40f86db0f7 Assets/ImportedActors/Wear/AnarchyThong/Meshes/Jana.mesh.meta
        // source-sha256: 04fccdb190754e5015d2496341362ae12432c202373ec0f0d24039c03d7c0a73 Assets/ImportedActors/Wear/AnarchyTop/Meshes/Jana.mesh
        // source-sha256: dfa7e646832ed63a36b252f2048320f1af4d96fba1f93e9f2c7d05f2a7a91e46 Assets/ImportedActors/Wear/AnarchyTop/Meshes/Jana.mesh.meta
        // source-sha256: 765ad5a00ca1b1d30422db9fb5804c243c8fd92c8db1e5e4d1f57696cc37f2fb Assets/ImportedActors/Wear/AutumnJacket/Meshes/Jana.mesh
        // source-sha256: c13bb9bdac1a5cf74dc06d16221c581b6a7e002e4d136af0c0744a0e4b79ea01 Assets/ImportedActors/Wear/AutumnJacket/Meshes/Jana.mesh.meta
        // source-sha256: 85bdd0959db9aa9f0d981852602d31987c2caf71667fc4b6e466bbcbcf8a8172 Assets/ImportedActors/Wear/BandaidSuit/Meshes/Jana.mesh
        // source-sha256: 2c964434ad49a44d61976f5ffc2eb14faa11e4981da42bd19d32ae8382086266 Assets/ImportedActors/Wear/BandaidSuit/Meshes/Jana.mesh.meta
        // source-sha256: 1b7badf0f175e7c96fd7c9ab6a2ce3d245e60d2eaa6f33bafdbca6e0435a8f53 Assets/ImportedActors/Wear/BigTee/Meshes/Jana.mesh
        // source-sha256: 5d11b63fad913512e13a54a0754730e7d4f103b9266f83a5a5902356ab3966ab Assets/ImportedActors/Wear/BigTee/Meshes/Jana.mesh.meta
        // source-sha256: c33424e7a45ccd1a57944f9ed460f1550ad8711392c097bdecf5f79cac5c5127 Assets/ImportedActors/Wear/BigTeePanty/Meshes/Jana.mesh
        // source-sha256: b32a0a558a595bc39dfdddd4c3951c4c044dd198af95fc0937d0eddd9e95ee59 Assets/ImportedActors/Wear/BigTeePanty/Meshes/Jana.mesh.meta
        // source-sha256: a976cc371a5a5cf6a46d9354274c7facc350adf45c9fd36d328682b1f46a35aa Assets/ImportedActors/Wear/BikerGloves/Meshes/Jana.mesh
        // source-sha256: 2501d97c9a93d96f05c6159127c55ca6f906bbd18876f36c91233b0be42d285f Assets/ImportedActors/Wear/BikerGloves/Meshes/Jana.mesh.meta
        // source-sha256: 38d008caba80a57a18131b35a470fc240c97a91579cfe4b0b780f1b669c9f4d3 Assets/ImportedActors/Wear/BikerJacket/Meshes/Jana.mesh
        // source-sha256: c2b807c98416ddef878734d0bf03c82bf8d88c6b426d9ea7317a62b33e751d96 Assets/ImportedActors/Wear/BikerJacket/Meshes/Jana.mesh.meta
        // source-sha256: 0ce119bcc22199c76f861238019c7279fe13ce38499166c86c1c4ed793c0f3a3 Assets/ImportedActors/Wear/BikerPants/Meshes/Jana.mesh
        // source-sha256: 9634e2d90a8145cd6cf6d0c041a94446278db0924bfb082924258e786d280a54 Assets/ImportedActors/Wear/BikerPants/Meshes/Jana.mesh.meta
        // source-sha256: 78e5f4676e0482f9a4033e894693a16351d625c8f258b571869518902521322c Assets/ImportedActors/Wear/BikiniBriefs/Meshes/Jana.mesh
        // source-sha256: a352aa12c731f0388f3af893a7b0daaccc7f17bba2fa470459a317cd57556010 Assets/ImportedActors/Wear/BikiniBriefs/Meshes/Jana.mesh.meta
        // source-sha256: 5effa271a1dcb834151d3966493376ce7ea5bc5a21d0e7d14b442ea120472ef3 Assets/ImportedActors/Wear/BikiniTop/Meshes/Jana.mesh
        // source-sha256: c651aaa08d54263760cb3e3c486215a4110597d1925af520ee5236968629e756 Assets/ImportedActors/Wear/BikiniTop/Meshes/Jana.mesh.meta
        // source-sha256: 898c25116bc5f68311cccc0145450bd5ecda7374de7d31a7776a4b39141dd16f Assets/ImportedActors/Wear/CharmBoots/Meshes/Jana.mesh
        // source-sha256: b97812a28475f5ee6553814bd0d192462ef688eb51fe916fc2a15ed641729861 Assets/ImportedActors/Wear/CharmBoots/Meshes/Jana.mesh.meta
        // source-sha256: 8ebd4039d33570e4c9a2d5e5059dc958f88cd6b5ad970227cefd6dd3ccbd60d7 Assets/ImportedActors/Wear/CindyBelt/Meshes/Jana.mesh
        // source-sha256: 47b085b02298e5a3d660fc94724e48d936b63be0b289b055199e1041ff54898a Assets/ImportedActors/Wear/CindyBelt/Meshes/Jana.mesh.meta
        // source-sha256: 02f43f494fb8fe08c4d17299d9007f37ca22b9016f4e7fb27c03d2d38cad96c0 Assets/ImportedActors/Wear/CindyBikiniBriefs/Meshes/Jana.mesh
        // source-sha256: cdfad850cf3b223d1f26436fbf71e3e90adbb85ad320d9c875e093354fa54db5 Assets/ImportedActors/Wear/CindyBikiniBriefs/Meshes/Jana.mesh.meta
        // source-sha256: fc018b67b1ce4d06ebebcf9b5f450c9c791c246958c8cf0df340c1c685d2dae5 Assets/ImportedActors/Wear/CindyBikiniTop/Meshes/Jana.mesh
        // source-sha256: e3a75720ef37d413f07825bd424a5d64fc2243774b97adc61904fcdb9d9b1cc6 Assets/ImportedActors/Wear/CindyBikiniTop/Meshes/Jana.mesh.meta
        // source-sha256: b0cf9b0d72fbce6775e3955827fc80aec507cbee4f5ee529f2cf392dbb8e99e6 Assets/ImportedActors/Wear/CindyBoots/Meshes/Jana.mesh
        // source-sha256: 1e775404b09edbf928eb3817cdd63857542b66f5009392f4554c331c9d81e200 Assets/ImportedActors/Wear/CindyBoots/Meshes/Jana.mesh.meta
        // source-sha256: 67297158bdc9424842dffce94b45de116d3a45651de0af17a95b7c9e91e2cd1c Assets/ImportedActors/Wear/CindyGloves/Meshes/Jana.mesh
        // source-sha256: 08ce69dd800f8975ebf24aba84a552ec68974004915e1c906f26088b282c8e04 Assets/ImportedActors/Wear/CindyGloves/Meshes/Jana.mesh.meta
        // source-sha256: e3120ed06e317d23a7169ed4e4e348e1cb16d8d9efc597e933fc54b15144ed29 Assets/ImportedActors/Wear/CindyJacket/Meshes/Jana.mesh
        // source-sha256: 0a88107c8517f59f5561d549dfa8977dcbe618ba2d04d3f1f04200854f9231e0 Assets/ImportedActors/Wear/CindyJacket/Meshes/Jana.mesh.meta
        // source-sha256: e28b059d3cc5b93f1493330917d97c845ee2f9e31703b784d36bb6011d7444c6 Assets/ImportedActors/Wear/CindyShorts/Meshes/Jana.mesh
        // source-sha256: d25ba34b40c7e007f0b88fe540067896ed22203d3002abc0aa1b97407c30468f Assets/ImportedActors/Wear/CindyShorts/Meshes/Jana.mesh.meta
        // source-sha256: 91b4d32effd12935d7dd2495bbf2e26ab0eb6e22b1e1c4b5e85017675ded2c09 Assets/ImportedActors/Wear/CityDress/Meshes/Jana.mesh
        // source-sha256: 9e1dc731c5d02521173b8e527233a1c6a759a8dcdc71605944aff6180b14a795 Assets/ImportedActors/Wear/CityDress/Meshes/Jana.mesh.meta
        // source-sha256: 815e3a720bb436b53134cb42d244c7f93b8104b67845b4487bbfc58ca5d032f2 Assets/ImportedActors/Wear/ClassicBoots/Meshes/Jana.mesh
        // source-sha256: bc9b41a6bb587b9af97402b010e43e5b24c89ecf6ddffa9c57606b5aeb24c1ba Assets/ImportedActors/Wear/ClassicBoots/Meshes/Jana.mesh.meta
        // source-sha256: a5ed215f824ab8d9e4fbdde97b5051d3ee75035dca718251c7a953a8a4336f8d Assets/ImportedActors/Wear/ClassicGloves/Meshes/Jana.mesh
        // source-sha256: 7a090e3805e7768e07e28c90d64be222509cbaad59f08775cebf313b2409a2de Assets/ImportedActors/Wear/ClassicGloves/Meshes/Jana.mesh.meta
        // source-sha256: a2067e605c70aa9f1714cad1e64498bc99d77018ea59af1a00952d1ba9656fd2 Assets/ImportedActors/Wear/ClassicScarf/Meshes/Jana.mesh
        // source-sha256: d4f022da4ecda45c439c8071ea57eb164841abcb9fc041214cfd38ca671dc9d4 Assets/ImportedActors/Wear/ClassicScarf/Meshes/Jana.mesh.meta
        // source-sha256: 9619e2ecafba64665451896ad529f0889c81c878bb518fc190f19b3d26e3cae7 Assets/ImportedActors/Wear/ClassicShorts/Meshes/Jana.mesh
        // source-sha256: 661f8fb9e5650d7e297e0c38f92550ca05ce75c71f92c7268b24f24305cfa732 Assets/ImportedActors/Wear/ClassicShorts/Meshes/Jana.mesh.meta
        // source-sha256: 34bfc9848c8b7688a62c343b2b837f563c44563971d8bed33619d251594fdd0a Assets/ImportedActors/Wear/ClassicTop/Meshes/Jana.mesh
        // source-sha256: 9faecefca7661ce9c9b9825df9fe227eb8413e773ca56c6ae5acb496371dedbb Assets/ImportedActors/Wear/ClassicTop/Meshes/Jana.mesh.meta
        // source-sha256: 12987dde098d03e9386cf4da3b7e47b58ddbe2f79a3188b95f4f23450611e7f3 Assets/ImportedActors/Wear/DeadlyGloves/Meshes/Jana.mesh
        // source-sha256: e2f88960fcee83be00e0c45db9a36c828dce14737a25bdc70fd41a7185eaa268 Assets/ImportedActors/Wear/DeadlyGloves/Meshes/Jana.mesh.meta
        // source-sha256: ed720b44fab9b881d524580cdf76a63113b72112a5f21e666c4ce9d53c510458 Assets/ImportedActors/Wear/DeadlyShorts/Meshes/Jana.mesh
        // source-sha256: c2b6522c09b61ec3a82e47e46143032afa16408e24f0e1efc7948643892227a2 Assets/ImportedActors/Wear/DeadlyShorts/Meshes/Jana.mesh.meta
        // source-sha256: f70d15d818600e577b99eb5562e21884faf044b59bd59268b6ff6e671d831763 Assets/ImportedActors/Wear/DeadlyTights/Meshes/Jana.mesh
        // source-sha256: c9acd0c14e0794e790fb94ac53f52467a78db7e996352d3932aaf55a53036483 Assets/ImportedActors/Wear/DeadlyTights/Meshes/Jana.mesh.meta
        // source-sha256: 665b55910f46919c0d5af792ba8bd19a09cac3c41f788a5d88ce5aa73689e38a Assets/ImportedActors/Wear/DeadlyTop/Meshes/Jana.mesh
        // source-sha256: 4c04e4acb0705639958ae0912d1fba5f60613bfea1d688d188e5b4314b3eec3c Assets/ImportedActors/Wear/DeadlyTop/Meshes/Jana.mesh.meta
        // source-sha256: acfe056300478bfc3b59572e52a2245c03f479cf45780a6563a389501c00a553 Assets/ImportedActors/Wear/FAO_Harness_Male/Meshes/Kshishtof.mesh
        // source-sha256: 7fe1d02379d4157e4bd70c2f734f52e3a2ca1aff55729d57db16439145b7ffa2 Assets/ImportedActors/Wear/FAO_Harness_Male/Meshes/Kshishtof.mesh.meta
        // source-sha256: a6bb84699921d24d5cf1cf3503f3012354a07301e741570acf926de051facc00 Assets/ImportedActors/Wear/FCO_Belt_Male/Meshes/Kshishtof.mesh
        // source-sha256: a4772b590e417c5c54db7367999221641973376a1fe62c4705cf8c506ee90504 Assets/ImportedActors/Wear/FCO_Belt_Male/Meshes/Kshishtof.mesh.meta
        // source-sha256: c1ad1d53889ce2ab5e9399141ad686acd060473b3dd2a6128bfaac4c4547122d Assets/ImportedActors/Wear/FCO_Boots_Male/Meshes/Kshishtof.mesh
        // source-sha256: e761a19ab82b5209bbd54db02d35b1eafb5d6897cec48c848aa6ca01ac4e0601 Assets/ImportedActors/Wear/FCO_Boots_Male/Meshes/Kshishtof.mesh.meta
        // source-sha256: 7f91da2488015a47f08eeab58e0fd842ec7d1001a310ff36cfcfea78d0ea1011 Assets/ImportedActors/Wear/FCO_Gloves_Male/Meshes/Kshishtof.mesh
        // source-sha256: 9fa135eadca70a33680f70c0661cbebe4c23eba08fae66b4d232e2c96ed3101f Assets/ImportedActors/Wear/FCO_Gloves_Male/Meshes/Kshishtof.mesh.meta
        // source-sha256: 830555c332d7aadfcd0472ff575220ebaaa96daa78020aa024b79f2ca8d16567 Assets/ImportedActors/Wear/FCO_Knee_Straps_Male/Meshes/Kshishtof.mesh
        // source-sha256: 1de81047b68866a58678acb3d9929f22151e33688ca9805ba9ea0e514f219257 Assets/ImportedActors/Wear/FCO_Knee_Straps_Male/Meshes/Kshishtof.mesh.meta
        // source-sha256: 7efba83a6808fd61e1f410439c34413a204f2ec64e001779d66afe56f20a6f35 Assets/ImportedActors/Wear/FCO_Legs_Straps_Male/Meshes/Kshishtof.mesh
        // source-sha256: 283125057cf3774184c1d3e99e6502b2a4b714506f6e64b3c98f8f82c042d2f8 Assets/ImportedActors/Wear/FCO_Legs_Straps_Male/Meshes/Kshishtof.mesh.meta
        // source-sha256: 3635ecc0c9370ef3d53ef520f83c178f6e86370c5681ac34bad6d37067da97d5 Assets/ImportedActors/Wear/FCO_Pants_Male/Meshes/Kshishtof.mesh
        // source-sha256: 7459886c5198e667c55bfe19e0c9570e3d0ee9ce48146dfc5186045e6480270b Assets/ImportedActors/Wear/FCO_Pants_Male/Meshes/Kshishtof.mesh.meta
        // source-sha256: 48f932756c72b83a86e55034e91c40142a39a583dfa74794c08318ebcd5c3d33 Assets/ImportedActors/Wear/FCO_Waist_Strappy_Male/Meshes/Kshishtof.mesh
        // source-sha256: cad5b58d54ba7eb107ed934257f5fdaf34ce348276bcc3fa2fa1745fd9bb461f Assets/ImportedActors/Wear/FCO_Waist_Strappy_Male/Meshes/Kshishtof.mesh.meta
        // source-sha256: 04384f1ddaa5573fe0150e6ef36a0470a7792e81fd219bdd987664bc4c641c4b Assets/ImportedActors/Wear/FighterArmGuards/Meshes/Jana.mesh
        // source-sha256: f3ffa0917354a0811752ff73c92ec7c26389cf5eb76faf47878615ed71f53470 Assets/ImportedActors/Wear/FighterArmGuards/Meshes/Jana.mesh.meta
        // source-sha256: 4a567afa24a810c166e8cf2730137ad07db19ac8a5c9e4704fbccac904090186 Assets/ImportedActors/Wear/FighterBelt/Meshes/Jana.mesh
        // source-sha256: 3c133b07959b36ab7334ba1ff7bd28634c8390a9b62a6b856338b9f28f0138b3 Assets/ImportedActors/Wear/FighterBelt/Meshes/Jana.mesh.meta
        // source-sha256: 7241444cd322401baca486746f114dd4ac945fbc0070b9741021dfc8a5b004e6 Assets/ImportedActors/Wear/FighterFootwear/Meshes/Jana.mesh
        // source-sha256: fc838efa1de1fb6699d587fcf49e62a6757ab2135b9c11523927d3f6391d3bd3 Assets/ImportedActors/Wear/FighterFootwear/Meshes/Jana.mesh.meta
        // source-sha256: 982feba1c20109a643ea7da02fbb47482d99899c6fcf59144e0b2ae73295372b Assets/ImportedActors/Wear/FighterKeikogi/Meshes/Jana.mesh
        // source-sha256: a3089dadf3d949686e14aa852d2f1033abe3f8f211cafda237eecf87dc6a6a96 Assets/ImportedActors/Wear/FighterKeikogi/Meshes/Jana.mesh.meta
        // source-sha256: 291e3117cfb4d3f0ace826dd7588b22d3233618ac4a8e84b88b4314e8c40785e Assets/ImportedActors/Wear/FighterPants/Meshes/Jana.mesh
        // source-sha256: e191a62be4f4acccf75fa164714da3dac09ee55ec5c61169f4c5f435ee4b0d6d Assets/ImportedActors/Wear/FighterPants/Meshes/Jana.mesh.meta
        // source-sha256: a3f32a592387a3c5f02186d59a0fb386f26e52e5d06acdcd5b2f45361fe2fb9f Assets/ImportedActors/Wear/FighterTop/Meshes/Jana.mesh
        // source-sha256: a27eabe20cb11e32242fbed967bfec35a63f2002a2134ab7608d30a10c4688da Assets/ImportedActors/Wear/FighterTop/Meshes/Jana.mesh.meta
        // source-sha256: 9f6445b245af13396668ff01640f079d7e8b108b2cde1a1ec06ba30b53efe7bc Assets/ImportedActors/Wear/FitCropTop/Meshes/Jana.mesh
        // source-sha256: fc10e28760f842e3ce445a760e2a8a341d69abf7b2f49432e88c09b2adece7f3 Assets/ImportedActors/Wear/FitCropTop/Meshes/Jana.mesh.meta
        // source-sha256: 58c521ee658c751803c2ec97f32ca3b7416387e0afdf3904888a7b64e9fba950 Assets/ImportedActors/Wear/FitGloves/Meshes/Jana.mesh
        // source-sha256: 3efbe27d6ac4dc3bea19d2d5f1b6f5a10433b7013953ddaa55e2becb6660eae0 Assets/ImportedActors/Wear/FitGloves/Meshes/Jana.mesh.meta
        // source-sha256: 246ca5203d3a328c4bbcb296714952f52afe564d7230e309a5dbfe293c430ca2 Assets/ImportedActors/Wear/FitShorts/Meshes/Jana.mesh
        // source-sha256: b140ff2b86adb6843cc4090c12a747c85a62d6ec4cb64d1f070836937da60133 Assets/ImportedActors/Wear/FitShorts/Meshes/Jana.mesh.meta
        // source-sha256: 8cfcf631c63af1d692c43ca2cd8eb5910a286f5d59351aa7b108cdbecc8a1d93 Assets/ImportedActors/Wear/FitSocks/Meshes/Jana.mesh
        // source-sha256: 8f018457f045ed41ac571cb17bb8ca29efb3de033d4b4f18d3486484ad21af70 Assets/ImportedActors/Wear/FitSocks/Meshes/Jana.mesh.meta
        // source-sha256: 8ded165fb440abd1cd91f4f4bf33e9e57ef40ea8bbb83db673c2ab71902f7c4c Assets/ImportedActors/Wear/FlairBriefs/Meshes/Jana.mesh
        // source-sha256: 637c98ab3bd8d1aa36f4eced1d96731f0e6b8329a6d928f21cdd5a48dd7f11b6 Assets/ImportedActors/Wear/FlairBriefs/Meshes/Jana.mesh.meta
        // source-sha256: a44bf171ac8a5cc013ddda081c82510e737fdaf90f5c59723cd0bd49d370fb00 Assets/ImportedActors/Wear/FlairPumps/Meshes/Jana.mesh
        // source-sha256: ba8339d95c0853bfcfed8313f5f62e105b8b596dd56d36e9358fb25698a3dfcc Assets/ImportedActors/Wear/FlairPumps/Meshes/Jana.mesh.meta
        // source-sha256: a8450b0d3e710ccc255a46e82baefcc341fb815b4421884df45a22874cca3e82 Assets/ImportedActors/Wear/FlairSkirt/Meshes/Jana.mesh
        // source-sha256: 82f7675f624edd804e4b5538f8725d04536310628f992fb83e91c28dfecedf51 Assets/ImportedActors/Wear/FlairSkirt/Meshes/Jana.mesh.meta
        // source-sha256: 2c10ad9b4810b68f235104497182eba5d1dfe06d15eb9c07dcc9d88acb1c4a88 Assets/ImportedActors/Wear/FlairSweater/Meshes/Jana.mesh
        // source-sha256: fc16d2deed3c2e523d004f39ccc8416cc7407be024dec42294a794df8ec68f77 Assets/ImportedActors/Wear/FlairSweater/Meshes/Jana.mesh.meta
        // source-sha256: bba59ca7347e548131d1334261cfb65a73659b8e58f281ba0146df9831535c67 Assets/ImportedActors/Wear/FolkTop/Meshes/Jana.mesh
        // source-sha256: 4eeeef35899b07f72d382280fdea7fa342ed9a68460c01d9415f531b1b3ab890 Assets/ImportedActors/Wear/FolkTop/Meshes/Jana.mesh.meta
        // source-sha256: da21fab9c192675d733b89911de74c469bb125b69b133a9da7a9568da2592133 Assets/ImportedActors/Wear/IdolCropTop/Meshes/Jana.mesh
        // source-sha256: db8ad2ca52c37a944d64bdf92517ef3e392e9a1714dfd357126355062359a695 Assets/ImportedActors/Wear/IdolCropTop/Meshes/Jana.mesh.meta
        // source-sha256: dd8d9cd3ba1ac6954d42243b7f16f1a916f2e8f42b42cb769364cb42afcdf6bb Assets/ImportedActors/Wear/IdolLeggings/Meshes/Jana.mesh
        // source-sha256: 43b4ce41f64dfb8f430c4e30e8f1d9fd363613e3c1ee577320e03ae1af0d5a45 Assets/ImportedActors/Wear/IdolLeggings/Meshes/Jana.mesh.meta
        // source-sha256: efbf3e4ff80beb804edcd75c115bf671a016fdc91b560dc25e117bdb700f4fb8 Assets/ImportedActors/Wear/IdolSleeves/Meshes/Jana.mesh
        // source-sha256: f9512207e7b9e2407c574bc50539b0ad2a6b3709ada88cf0ee8b2eb9d3936d6a Assets/ImportedActors/Wear/IdolSleeves/Meshes/Jana.mesh.meta
        // source-sha256: 6f04c411fdd0ffb68260c2fde6085f6b079e1d2a6bbc1e5f167a2fc25c24a6bf Assets/ImportedActors/Wear/JaneSkirt/Meshes/Jana.mesh
        // source-sha256: c92199e0a9bdd9220daf3a99df7e1eeb8251807d81c46abdca00d7ffa053ad5f Assets/ImportedActors/Wear/JaneSkirt/Meshes/Jana.mesh.meta
        // source-sha256: b483849b30601de08ef69ddbf56b4402b94a5bf60a9555fde6882edbdb66dd49 Assets/ImportedActors/Wear/JaneTank/Meshes/Jana.mesh
        // source-sha256: 8baae9492fc269d3bd36e04529d2d629c86120ca640924714e8d6dcf747eaaaf Assets/ImportedActors/Wear/JaneTank/Meshes/Jana.mesh.meta
        // source-sha256: 2a9ecb281437f44ad8785d9a2a1d82b4c3dc62598e0f2a5fa6d671d9ed2321c3 Assets/ImportedActors/Wear/LaceBra/Meshes/Jana.mesh
        // source-sha256: d1624fb55ab38365d0bbe933aadb0e7cf3938f7ae4416a741b3120581a9b11d9 Assets/ImportedActors/Wear/LaceBra/Meshes/Jana.mesh.meta
        // source-sha256: c0b3c4503900121e7ebb05e15edd462ede72055a776962997e06f9104bc016e2 Assets/ImportedActors/Wear/LaceBriefs/Meshes/Jana.mesh
        // source-sha256: e061549f4135eaece389b0f3a90ddf3ef6da8bc04789e489bbc6eb7d72f29f8c Assets/ImportedActors/Wear/LaceBriefs/Meshes/Jana.mesh.meta
        // source-sha256: c53337ae47a823e158c13cc8ceec354b764ee58d37247205382f2d29857568e8 Assets/ImportedActors/Wear/LeatherBoots/Meshes/Jana.mesh
        // source-sha256: 8f08bd788529636f63b368588121cdc0b43a26c85c3d9017790a712b6e5fb87d Assets/ImportedActors/Wear/LeatherBoots/Meshes/Jana.mesh.meta
        // source-sha256: d7b34250256273aefbd7776c2601150dd3898d1336975eefb17ccfe1500d0f6d Assets/ImportedActors/Wear/LuxuryBikiniBriefs/Meshes/Jana.mesh
        // source-sha256: 0b73786888c0133df5c5d6dbf047041ca7f2c699e9318d41a40c3fd66c852bfc Assets/ImportedActors/Wear/LuxuryBikiniBriefs/Meshes/Jana.mesh.meta
        // source-sha256: cf09440b477f4101f03d73501eeced7151794c92108809159ff7e30fb836a7a4 Assets/ImportedActors/Wear/LuxuryBikiniTop/Meshes/Jana.mesh
        // source-sha256: d8d7c8da011565b358000a04d0528b4bb547a48f819be3005f21e03316b75691 Assets/ImportedActors/Wear/LuxuryBikiniTop/Meshes/Jana.mesh.meta
        // source-sha256: b0cb1b87f4bfea30ab07511b30a0f8c5a6d76ab9a21f5d6ac263d12aef9693bf Assets/ImportedActors/Wear/LuxuryBracelet/Meshes/Jana.mesh
        // source-sha256: bd02f5f2a133f8ab7bc3c45742686184ffe63ffa7e0ecba3910c19ac9d83de66 Assets/ImportedActors/Wear/LuxuryBracelet/Meshes/Jana.mesh.meta
        // source-sha256: a60e03556293a83c89fd78cd7695eb218b067f7403cd7ad9410ea24dddc21aea Assets/ImportedActors/Wear/LuxuryHeels/Meshes/Jana.mesh
        // source-sha256: a82e775a354e8612db414ae80ce4a300eb1deafe6d50db1a9e87e18daf957224 Assets/ImportedActors/Wear/LuxuryHeels/Meshes/Jana.mesh.meta
        // source-sha256: d6754644cce8e9363543f99004ef28388f0ba0e9b48a299b240844ec924d956a Assets/ImportedActors/Wear/LuxurySunglasses/Meshes/Jana.mesh
        // source-sha256: aedc06d2ffc9fcceb3d858d858d4c0f5276a31d8c722e883d2492fa410fc514b Assets/ImportedActors/Wear/LuxurySunglasses/Meshes/Jana.mesh.meta
        // source-sha256: ba71eac7447606ab2f3ea20190b842d24621bcbdf8d72dd6b721b47ed394c9e8 Assets/ImportedActors/Wear/NaughtySkirt/Meshes/Jana.mesh
        // source-sha256: d00dc138c88b85b82f552a519609343f2973edb82f2a450bc41eee81694d2fa8 Assets/ImportedActors/Wear/NaughtySkirt/Meshes/Jana.mesh.meta
        // source-sha256: f08ed919f0159bc02f33c2078a43ccc3b908b4a49c9da8f7f96ae4df55ef1b3c Assets/ImportedActors/Wear/NaughtySweater/Meshes/Jana.mesh
        // source-sha256: cf81c1cdec32a2c265fb73a4e1a9320bf5268d79765367544494acab6175698d Assets/ImportedActors/Wear/NaughtySweater/Meshes/Jana.mesh.meta
        // source-sha256: b673a80d1a437b80292a1c1f9330d27e4e23be8c46a32d385237ddf311e8662a Assets/ImportedActors/Wear/NerdBlouse/Meshes/Jana.mesh
        // source-sha256: 88cb8ec7f444e6a53aaca0f1d76640dd71bf3bf6f064348ac22f4965ff118705 Assets/ImportedActors/Wear/NerdBlouse/Meshes/Jana.mesh.meta
        // source-sha256: c26533888b29a8cb007a9e7ba0572ebad8d307b88ad0c1a39915cfed9e1fd5fa Assets/ImportedActors/Wear/NerdBowtie/Meshes/Jana.mesh
        // source-sha256: 6890f336e6de143b99cc7b62ad0c87a336af534d743a6cafd13b39e2cb8cd217 Assets/ImportedActors/Wear/NerdBowtie/Meshes/Jana.mesh.meta
        // source-sha256: 970d3e2b4cc8cc6046c7db183b15b60a3d97dae1e458728cc13a15ef455607a8 Assets/ImportedActors/Wear/NerdGlasses/Meshes/Jana.mesh
        // source-sha256: 2b8bab7a1aa4f8cff604d76e8a4d615749af674760d1793f4fefaf89e83c4de0 Assets/ImportedActors/Wear/NerdGlasses/Meshes/Jana.mesh.meta
        // source-sha256: e6248045df0aa45a100014c885d1ce8748a517023f958a8d52a1f7f8e5e1b3a7 Assets/ImportedActors/Wear/NerdKneeSocks/Meshes/Jana.mesh
        // source-sha256: a6c0dbbb18e6bd7827808f946603e7e08337af38032fe8d9e7dffc1627ff9c8f Assets/ImportedActors/Wear/NerdKneeSocks/Meshes/Jana.mesh.meta
        // source-sha256: 50468c0b72092e9c0550bd0b3f4926a4dfe8b944eb98d805fb5e13f5cf7a36e5 Assets/ImportedActors/Wear/NerdSneakers/Meshes/Jana.mesh
        // source-sha256: d72966aaabc849d07329e04f3b674596c0c9a3870a3d838334dd1f06d6dc3bf1 Assets/ImportedActors/Wear/NerdSneakers/Meshes/Jana.mesh.meta
        // source-sha256: eaa09e9efa67f87be272c0b6cfd81410d1b1a102087e4cb75fbcffe0e9410e75 Assets/ImportedActors/Wear/NerdTutu/Meshes/Jana.mesh
        // source-sha256: 9f6fd7f4e720dc1dbcfcc0b2d85ad722e8bb0252fc702a58ff66e60d5fc31ba3 Assets/ImportedActors/Wear/NerdTutu/Meshes/Jana.mesh.meta
        // source-sha256: ad9337aada97af4b03128db88c9fca662877dd60bbebc31c2a67718d4a2b4fc1 Assets/ImportedActors/Wear/NightDress/Meshes/Jana.mesh
        // source-sha256: 7fc9e67b6adba1f40ee2fbc0cf29927e6d0fc89fd18ea6d8a808b91eff55eeb5 Assets/ImportedActors/Wear/NightDress/Meshes/Jana.mesh.meta
        // source-sha256: 4ad396d3d352927eb058409291d94ee73fe23ba8056098746839357cf74c970f Assets/ImportedActors/Wear/NightNecklace/Meshes/Jana.mesh
        // source-sha256: e798557456cb7351a32d537ee55f8a623d3f3948d8748adf482614e7f19e2a2e Assets/ImportedActors/Wear/NightNecklace/Meshes/Jana.mesh.meta
        // source-sha256: dca700df47ba46b538118e272130be5679474269468a65e269a2f236d2fc0117 Assets/ImportedActors/Wear/OpenBackBra/Meshes/Jana.mesh
        // source-sha256: fe150c888d08dadc38dda21eb6499906c579648ee634a0d9e630d78eb3979ac7 Assets/ImportedActors/Wear/OpenBackBra/Meshes/Jana.mesh.meta
        // source-sha256: fbd3d3006d489d1b3c5220535f8533518ecbd9303d960d22d479c4bf735d672c Assets/ImportedActors/Wear/OpenBackBriefs/Meshes/Jana.mesh
        // source-sha256: f25fa01474145c4087606641106860b6245c81a5c7c0a4b2ef78e860f7374ae7 Assets/ImportedActors/Wear/OpenBackBriefs/Meshes/Jana.mesh.meta
        // source-sha256: 2d482467e3efd070e05a79683a285a50c93c8c66ed0848c8b9c52db28aa1d26f Assets/ImportedActors/Wear/OsirisBackpack/Meshes/Jana.mesh
        // source-sha256: 1ba2bd4fb35f5818a5cb76ee460aa9594b4a7681de9698f968ffec3d4ee6cd5e Assets/ImportedActors/Wear/OsirisBackpack/Meshes/Jana.mesh.meta
        // source-sha256: ada1e93a2b47c321448d392c19cad37f85c422b78f8b8663988dd8c2bf0b643a Assets/ImportedActors/Wear/OsirisBoots/Meshes/Jana.mesh
        // source-sha256: d67a27186b0201206c77936c891455f5cd846cb15f38c3b0b92de7f823ee1b96 Assets/ImportedActors/Wear/OsirisBoots/Meshes/Jana.mesh.meta
        // source-sha256: 6938020cd4bd8c463dc642e5703c8b26479359be2eff4768170a734d50317c56 Assets/ImportedActors/Wear/OsirisGloves/Meshes/Jana.mesh
        // source-sha256: 9c96d88f7f11c9950d927591e7e1cb88c243f67f228bc15b9edb494f3e7b3424 Assets/ImportedActors/Wear/OsirisGloves/Meshes/Jana.mesh.meta
        // source-sha256: e0c046f1b7f69a43f77b96d15250bfe14490adb8c79093e522f6dc7996b90f6a Assets/ImportedActors/Wear/OsirisHolsterBelt/Meshes/Jana.mesh
        // source-sha256: b0de1a99588c80b38df5a56e73789a0aea96d350ca25187203a475d81b83deef Assets/ImportedActors/Wear/OsirisHolsterBelt/Meshes/Jana.mesh.meta
        // source-sha256: 8ef4f493923ff6cd20686e6443305df062854637c1d030f21e67f78252cfdf45 Assets/ImportedActors/Wear/OsirisNecklace/Meshes/Jana.mesh
        // source-sha256: 4468e5ca7065faf72b70a56ba8a845dd2bbc4989b3991242541eb5ded6ebb987 Assets/ImportedActors/Wear/OsirisNecklace/Meshes/Jana.mesh.meta
        // source-sha256: 72e5b5c34cc7ded517c104c58332fbd7e4096f20ad581ce046fc8f76bc0087d1 Assets/ImportedActors/Wear/OsirisShorts/Meshes/Jana.mesh
        // source-sha256: 0b995bbc4f2df0dc485632200a9c4fac14a44f1d15acf3166203dc88e186d11a Assets/ImportedActors/Wear/OsirisShorts/Meshes/Jana.mesh.meta
        // source-sha256: f25ad4966870fe7afe62251ea261b059e4c35068fed9dcc5186fe40dd79da197 Assets/ImportedActors/Wear/OsirisTop/Meshes/Jana.mesh
        // source-sha256: 30bf269535a866c13397dfe0b27fdd2e24d9853d58bb713a241a5a133c21b599 Assets/ImportedActors/Wear/OsirisTop/Meshes/Jana.mesh.meta
        // source-sha256: 17b95eb86fdda37116548490c7d10654a757b4db31a60b42c019d023c8631376 Assets/ImportedActors/Wear/OverKneeSocks/Meshes/Jana.mesh
        // source-sha256: dcdadedba18d2c01790a407333299856a819dbd53ff54f58986698adf9d2f356 Assets/ImportedActors/Wear/OverKneeSocks/Meshes/Jana.mesh.meta
        // source-sha256: b06cb8590e0ddde4d197cb8d2e733bf50a27b4098514db650527e697683ad18f Assets/ImportedActors/Wear/PlainPanties/Meshes/Jana.mesh
        // source-sha256: 36f7350b6ebcbeefbc69dcabb39e3d13843b37078ca6d3db6de9d80fdd9f7dbc Assets/ImportedActors/Wear/PlainPanties/Meshes/Jana.mesh.meta
        // source-sha256: 36037e9940d1dab1153ab627983df6cb60211fded202a93f610a983d8e6b3ee2 Assets/ImportedActors/Wear/PrimalArmWraps/Meshes/Jana.mesh
        // source-sha256: f8c0d4ac31e3f7033e4c43957b9793717835b8d42b075d4c8c6380d0fc3a5285 Assets/ImportedActors/Wear/PrimalArmWraps/Meshes/Jana.mesh.meta
        // source-sha256: a9a1a2195a2900b58096dd3d935d715dc1564f3775ada92c8f9113cf33fe729e Assets/ImportedActors/Wear/PrimalBriefs/Meshes/Jana.mesh
        // source-sha256: bf4c2e2ec221f4df966f8700625e54fb3e89385c380d57f9c79298a24eb4baa8 Assets/ImportedActors/Wear/PrimalBriefs/Meshes/Jana.mesh.meta
        // source-sha256: 51fe00422bd74320bd1df9e8a9a33105cf36190c5cc6ef6de1aedd89d25895f1 Assets/ImportedActors/Wear/PrimalCollar/Meshes/Jana.mesh
        // source-sha256: 928a1ec3f955c4ed853c60f8960989ef7603355af9c29a141cb481a94824f2cc Assets/ImportedActors/Wear/PrimalCollar/Meshes/Jana.mesh.meta
        // source-sha256: cb91727fcc4b1e3c608fc531c80f88d4c65cc916a577de204a0e69fe58d194b0 Assets/ImportedActors/Wear/PrimalDress/Meshes/Jana.mesh
        // source-sha256: 31e911c7f0bf8277df6ff65c4778f481fa1fa5abef697b015fa43647793d9f8b Assets/ImportedActors/Wear/PrimalDress/Meshes/Jana.mesh.meta
        // source-sha256: 17c3de5e87490d7767280de53bd3f0f74863ec7be73817ebf4de8af310627764 Assets/ImportedActors/Wear/PrimalHeadband/Meshes/Jana.mesh
        // source-sha256: e4ef926ebabf062891ce81898a2e12e7a7acea2fdadb5307c485d0bd382d2649 Assets/ImportedActors/Wear/PrimalHeadband/Meshes/Jana.mesh.meta
        // source-sha256: 3b55407f88336a2a348afbe4895684f1541196e1dbcaf3447e5f8d9c623cda0d Assets/ImportedActors/Wear/PrimalSkirt/Meshes/Jana.mesh
        // source-sha256: 7cf9ab7e3e418a9b2f73fc0e3460a023617df9045a44210370ab8cee6551b676 Assets/ImportedActors/Wear/PrimalSkirt/Meshes/Jana.mesh.meta
        // source-sha256: 27abdae04b0e959dfaa769cea383f51d5ac9e556fe3160902c8178b099658e1f Assets/ImportedActors/Wear/PrimalTop/Meshes/Jana.mesh
        // source-sha256: a4b43a48ca662b76334275835a4daae3bf21cc37291bd897124a25bc48a1ae64 Assets/ImportedActors/Wear/PrimalTop/Meshes/Jana.mesh.meta
        // source-sha256: 087fe254d5b656ed468635e092c176f84fbc996c20a677f36bc5cdd1b8d3ee2d Assets/ImportedActors/Wear/PrimalWrapBoots/Meshes/Jana.mesh
        // source-sha256: b88d0779f557a4965934b8cf7375b9391d23e46af1d87e2d9af0a52604b242c8 Assets/ImportedActors/Wear/PrimalWrapBoots/Meshes/Jana.mesh.meta
        // source-sha256: 807717a4f096e433a81735576eada4f50b8e1f6a2f70520497d2eb8f8c0b348f Assets/ImportedActors/Wear/RangerBeltPouch/Meshes/Jana.mesh
        // source-sha256: ca77d6e69f3413ccb143d2312cf440ec9eaf5ade1addf7c7a0b6c38e9c1eb3bb Assets/ImportedActors/Wear/RangerBeltPouch/Meshes/Jana.mesh.meta
        // source-sha256: 281fa1d05a99e4afccf17ff88f59ea432cf7b7bc38f89ee4e141033619a8d2f2 Assets/ImportedActors/Wear/RangerBoots/Meshes/Jana.mesh
        // source-sha256: c3ea581e89929f6c64b842765218a4c7b44e28fe7de564a6a3e234f4290d2391 Assets/ImportedActors/Wear/RangerBoots/Meshes/Jana.mesh.meta
        // source-sha256: 62d59d622d1c349993d15f1588610341814d2d3d409e8d99f8e647b682389910 Assets/ImportedActors/Wear/RangerJacket/Meshes/Jana.mesh
        // source-sha256: 07e976669d7bcfbe8cb8fe8e422a6aafaa20e256754744cf38a47504510c8af4 Assets/ImportedActors/Wear/RangerJacket/Meshes/Jana.mesh.meta
        // source-sha256: cdbbd87d2bb2c3df3595db6777dec4bdd2cfd886562c590d729d85a5f00a4420 Assets/ImportedActors/Wear/RangerPants/Meshes/Jana.mesh
        // source-sha256: 93e4463b23d7c658a4d18b8c77f92a070735c6c6650069142845ea3ec830535b Assets/ImportedActors/Wear/RangerPants/Meshes/Jana.mesh.meta
        // source-sha256: 71cc5c49fd3e26a0f2866e7be58aa9f207c9e1726a3049d6037a6c9afb1c264a Assets/ImportedActors/Wear/RangerVest/Meshes/Jana.mesh
        // source-sha256: 0a0a1ba891778dc5f00d8074d08ad8d4a44df7f65725720759656adac6386edf Assets/ImportedActors/Wear/RangerVest/Meshes/Jana.mesh.meta
        // source-sha256: ca72820806f4910196937d3603ba423a728ad561f08b99509f1188b48eb447c0 Assets/ImportedActors/Wear/RealSneakers/Meshes/Jana.mesh
        // source-sha256: 9dff10b10eacccf0b524fcf02612a629cf3eb27d2fb67821b507b736a1265757 Assets/ImportedActors/Wear/RealSneakers/Meshes/Jana.mesh.meta
        // source-sha256: 93d43417dd1d5f464b622303e492940f6a438385465afa29043149f63fc12724 Assets/ImportedActors/Wear/ReikoOutfit/Meshes/Jana.mesh
        // source-sha256: 51cfb387e276a49bd2dc72494d3f3b4b9784da7e0c47fbc8470e970b712cd428 Assets/ImportedActors/Wear/ReikoOutfit/Meshes/Jana.mesh.meta
        // source-sha256: 02ba52eb58f857dbfb8e27773cc138d148bba9b953f571d3c08f1b46e2581290 Assets/ImportedActors/Wear/RiotBackpack/Meshes/Jana.mesh
        // source-sha256: 1411d2cf7b4b14ddbbf87f96de60a9ccddddc6b38d532b28b981a66ee71978a4 Assets/ImportedActors/Wear/RiotBackpack/Meshes/Jana.mesh.meta
        // source-sha256: 1e1bff36f480336d206a85f65ec11cb948e67ac4322649afaf97d0af7fbe8394 Assets/ImportedActors/Wear/RiotBlouse/Meshes/Jana.mesh
        // source-sha256: 71ba8f7880569e826d28d843e888f00e0aaed38762cc0cf1979bb5ee88afc5a1 Assets/ImportedActors/Wear/RiotBlouse/Meshes/Jana.mesh.meta
        // source-sha256: 9ee13de014e0215f39c33594cf329a111b61074f204867eb5c479b8d44ce43f1 Assets/ImportedActors/Wear/RiotBlouseWaist/Meshes/Jana.mesh
        // source-sha256: a92685b6638e4a17ca46810aaf9312ed8f4af11b745479f3c8f550aef8c79ae8 Assets/ImportedActors/Wear/RiotBlouseWaist/Meshes/Jana.mesh.meta
        // source-sha256: 3cd271eeeaf4bf96e6b25db2d5e3bf656ef5aa87f0c9f16323b36245f009a51e Assets/ImportedActors/Wear/RiotBoots/Meshes/Jana.mesh
        // source-sha256: 9dd2bfb636fb0dcf976cf9ffd21234b88bfa9614c8d056943a86f4f80f120f61 Assets/ImportedActors/Wear/RiotBoots/Meshes/Jana.mesh.meta
        // source-sha256: b2e8bddf868b56cd0c1beee4aa9bec8221f74bd699bbbadb41d116b55cd357bc Assets/ImportedActors/Wear/RiotBra/Meshes/Jana.mesh
        // source-sha256: 340fdb8c15193e767b5c13839483fd648d2db6d6aca6ca2da173f13a1984e43e Assets/ImportedActors/Wear/RiotBra/Meshes/Jana.mesh.meta
        // source-sha256: 4e3a7e77053414cb03c5eaeec7d5c12c69e3c6b967488dbf35f9921a4276914f Assets/ImportedActors/Wear/RiotCap/Meshes/Jana.mesh
        // source-sha256: 3c3872eb187fc668154851168315a5a38e50a8abaff1bf0613f2027b7ee551ee Assets/ImportedActors/Wear/RiotCap/Meshes/Jana.mesh.meta
        // source-sha256: c337624805e5ed051f22db2fbddc7be64557ea002f977f7bf24a4df45cae2dad Assets/ImportedActors/Wear/RiotShirt/Meshes/Jana.mesh
        // source-sha256: af739c2cc5da95ed7a076eab98e37c2647011bb7dfd6874a74418cc50cfb2324 Assets/ImportedActors/Wear/RiotShirt/Meshes/Jana.mesh.meta
        // source-sha256: 52e315bb396953f743503d799a0799f7e5b100baf9df764f19ca509494e6eb2e Assets/ImportedActors/Wear/RiotShorts/Meshes/Jana.mesh
        // source-sha256: e535dbbffc6ed28536caeb878ee75023fc248c99f30563b64ebddcd193f5f8ce Assets/ImportedActors/Wear/RiotShorts/Meshes/Jana.mesh.meta
        // source-sha256: c4eee9aa4283fc624016c45b34b09dbf4a7506a2bf9708116895f5c23d12b629 Assets/ImportedActors/Wear/RiotStockings/Meshes/Jana.mesh
        // source-sha256: b4404dc188ff5400bb041b1c22722dc5cbd480e43037aff82e48fd026321e561 Assets/ImportedActors/Wear/RiotStockings/Meshes/Jana.mesh.meta
        // source-sha256: b809fe4e032c73cfba7182fea31463ac0097b3ecc604fcb85ac2b79085312f62 Assets/ImportedActors/Wear/Sandals1/Meshes/Jana.mesh
        // source-sha256: 774b39c32b6390214f422060836c9031cb66153fd73c1c39fcf696803fad5130 Assets/ImportedActors/Wear/Sandals1/Meshes/Jana.mesh.meta
        // source-sha256: 1483423c3d908e22e5696e782a39fc3960cbc719250554753dfd540f2dfae3bd Assets/ImportedActors/Wear/Sandals2/Meshes/Jana.mesh
        // source-sha256: fcb9378cb08c7186023e6e03b7ea2cd550c54e30c9e1ff6af58ef0cd2725c34a Assets/ImportedActors/Wear/Sandals2/Meshes/Jana.mesh.meta
        // source-sha256: be3890764f7dee99ca6d95c9392bc83b2d42d6d74d0f0348a0c6bc7d89de6d0f Assets/ImportedActors/Wear/Sandals3/Meshes/Jana.mesh
        // source-sha256: bc5d47a928c6e842c323d47f601e5df9b0708ae7ee9d66851aa7f9ded09db51e Assets/ImportedActors/Wear/Sandals3/Meshes/Jana.mesh.meta
        // source-sha256: 110f38d273808986a217b282e8ffd703d6f8fafe06f2eec5f05f2cd88e63f669 Assets/ImportedActors/Wear/Sandals4/Meshes/Jana.mesh
        // source-sha256: d36ed3db5eb18a91e81d612610f2c51c560795eaf4f2cbb027176705c859cc68 Assets/ImportedActors/Wear/Sandals4/Meshes/Jana.mesh.meta
        // source-sha256: 31f2fdb6cda6286430d3fadae7278f86353eb48398f45e84a3aeac8695151924 Assets/ImportedActors/Wear/SkinnyCorset/Meshes/Jana.mesh
        // source-sha256: 9f747aee5af13477f0bf35f68e5a18f658a18d30b4fd2d29b2886a7d9c375b64 Assets/ImportedActors/Wear/SkinnyCorset/Meshes/Jana.mesh.meta
        // source-sha256: ff77ecc2ad108e0d58b25855c7f7bd2c36707e512d7424bca83f3e89e6575ac2 Assets/ImportedActors/Wear/SkinnyJeans/Meshes/Jana.mesh
        // source-sha256: 118eae47d0e8f6f7889dee8c70d53fb515fe337a329f4024532cd470da48ec4f Assets/ImportedActors/Wear/SkinnyJeans/Meshes/Jana.mesh.meta
        // source-sha256: 32c1bbd38eba02ba20744423f60b2634e7c7515190eca7f93af1a508d03f35f9 Assets/ImportedActors/Wear/SlipOns/Meshes/Jana.mesh
        // source-sha256: 6161a2f3195eaa28a24ff10ee5ac76b299504b62b43183fb05c7baa912f2fcdb Assets/ImportedActors/Wear/SlipOns/Meshes/Jana.mesh.meta
        // source-sha256: bde11de509a0bd50e7029eff27c6d6f0b027b6617d336ebd7a0184a589fa87d5 Assets/ImportedActors/Wear/SpookyStockings/Meshes/Jana.mesh
        // source-sha256: 3f007d4424abed2e90251b5e158a02f3442109f84ffb8e4618630580973425b8 Assets/ImportedActors/Wear/SpookyStockings/Meshes/Jana.mesh.meta
        // source-sha256: 69164b3afbe213c98ae2f4a9b542c87a3cb4564ff6d77d68b35f9b9de95d0113 Assets/ImportedActors/Wear/SportSneakers/Meshes/Jana.mesh
        // source-sha256: 4caf858269aa1be5a87b8cf53b6d785888551a34b1dfc5d288e81478c6f73613 Assets/ImportedActors/Wear/SportSneakers/Meshes/Jana.mesh.meta
        // source-sha256: 36fe314320934f821acf2a4970e2a9a0e79d54226db7db6af33d1484016ec424 Assets/ImportedActors/Wear/SportsBraJmr/Meshes/Jana.mesh
        // source-sha256: 6c8e79d5e1037fa847a6afc576dbca2f2024c6427741751c60b78af853b421ed Assets/ImportedActors/Wear/SportsBraJmr/Meshes/Jana.mesh.meta
        // source-sha256: 9ec698ffe505ab592ec5acf892374e3af1beb02c2898149f89d6937e3e03c3b8 Assets/ImportedActors/Wear/SportsBriefsJmr/Meshes/Jana.mesh
        // source-sha256: 4cb360249e1e734a88a327b62fa38dd1f948a4a5a80072094ab91f675cba86bd Assets/ImportedActors/Wear/SportsBriefsJmr/Meshes/Jana.mesh.meta
        // source-sha256: faeb85411c212d46094780d18c8f7259f3b105597eb0c2107eedd6924c54dbe3 Assets/ImportedActors/Wear/StarsBelt/Meshes/Jana.mesh
        // source-sha256: 3a679be6dcc2ec44de7f147547bc654ca7fef8038a53527afc9de9f0af673df9 Assets/ImportedActors/Wear/StarsBelt/Meshes/Jana.mesh.meta
        // source-sha256: e8e4b327b480f9cb91ddb8b005902c3e8288135cad65fa2337888c13d1b1e602 Assets/ImportedActors/Wear/StarsCap/Meshes/Jana.mesh
        // source-sha256: 74336c621734623b75675789055eb5cb911b25fee4cc13e4151714225de567d5 Assets/ImportedActors/Wear/StarsCap/Meshes/Jana.mesh.meta
        // source-sha256: ef1d6f2cc6737fc89adef8507482361f1dd5def0355b06974c1b40edb4faa6ff Assets/ImportedActors/Wear/StarsGloves/Meshes/Jana.mesh
        // source-sha256: eb214a5069d55c3c3ca416bfc5c61b71b16faaedda224ab28ea0f2e17664c4ec Assets/ImportedActors/Wear/StarsGloves/Meshes/Jana.mesh.meta
        // source-sha256: 63e203c69d8e674d3612a56714527a2bdeeb6e63d572e075621fe3a65fb49fee Assets/ImportedActors/Wear/StarsPants/Meshes/Jana.mesh
        // source-sha256: e460ad1ecff1fb60698aac70c6272222f625d0695a63d872dab4f209d88f5084 Assets/ImportedActors/Wear/StarsPants/Meshes/Jana.mesh.meta
        // source-sha256: 8b008a465b0e74c038a844ef430f1ec6e3f97270d8354879791195a5f8bf3205 Assets/ImportedActors/Wear/StarsTop/Meshes/Jana.mesh
        // source-sha256: acf1c3385aae53487635bc184d74619892d52b1fc1599cdde6fd8f20630afe36 Assets/ImportedActors/Wear/StarsTop/Meshes/Jana.mesh.meta
        // source-sha256: fc568976442b1a3f80d5a2a0d088489ef9c78d67bef178a4eb916ef36eff021d Assets/ImportedActors/Wear/StarsVest/Meshes/Jana.mesh
        // source-sha256: d68d5a1be5f12911e0fa1394f917b17fb2030d92f477ac4f27be5926aa279fa7 Assets/ImportedActors/Wear/StarsVest/Meshes/Jana.mesh.meta
        // source-sha256: 29cc1add809f9ff4f55cb7fc483d12d762d84961c68fd036d4f3fc4f110ac586 Assets/ImportedActors/Wear/StrappyBra/Meshes/Jana.mesh
        // source-sha256: a4810e50119ad5226d2e19ed23b12e41f8d8c0f84451cad59ebd55ac43839b0e Assets/ImportedActors/Wear/StrappyBra/Meshes/Jana.mesh.meta
        // source-sha256: eb21d9b8f03e4f8ee579f7770373cb80239be5671c59cdd68eb22d46709999aa Assets/ImportedActors/Wear/StrappyBriefs/Meshes/Jana.mesh
        // source-sha256: e7ebe610a8a082d58ba48643a2318cdf8074d93c0c5f7f8f034f240f3f2f4c1d Assets/ImportedActors/Wear/StrappyBriefs/Meshes/Jana.mesh.meta
        // source-sha256: e048dd85f3ec48d8852c3095e4c2e1369e068d7b14798d1ffe44329bf45d9927 Assets/ImportedActors/Wear/SummerShorts/Meshes/Jana.mesh
        // source-sha256: 6ca3d8e3aefc6f4822357d17b291e13cb0be3a02e981895154b6f36e92f71e8c Assets/ImportedActors/Wear/SummerShorts/Meshes/Jana.mesh.meta
        // source-sha256: e183f13400112ae09842681a8aa1cc2b00fe37de2e423069ce8fc06c8824cbb6 Assets/ImportedActors/Wear/SummerTShirt/Meshes/Jana.mesh
        // source-sha256: 02747071afb4ee7236c886c1142f28d6dd51559cec072d3a03877eb448b81b3d Assets/ImportedActors/Wear/SummerTShirt/Meshes/Jana.mesh.meta
        // source-sha256: baf9ba640e2ae6af4f04752578e9dd025888066f9c4df63deffb4779ac510ef9 Assets/ImportedActors/Wear/SummerTankTop/Meshes/Jana.mesh
        // source-sha256: e515546ca9b8176738ab18b426dcad0209db60c1d331d52733d6a1fe03f403d6 Assets/ImportedActors/Wear/SummerTankTop/Meshes/Jana.mesh.meta
        // source-sha256: bc6dce69ba08743f16c6cf08fdcbd6186066ea1ae1323bb2ead03d46718cb2fc Assets/ImportedActors/Wear/SweetyBabydoll/Meshes/Jana.mesh
        // source-sha256: 4371e74aaddd7400c5c2112b10e3cd33406d0942cd0976e79be38507cb1a14f8 Assets/ImportedActors/Wear/SweetyBabydoll/Meshes/Jana.mesh.meta
        // source-sha256: 5b2681b9a0ffa1cf13c521e83da6819bb32e43fb3a23d6a337f97a67f93e9df3 Assets/ImportedActors/Wear/SweetyPanty/Meshes/Jana.mesh
        // source-sha256: b1bf16e027b1d956c03ea73a6404d4a7fd395d040dcc88f0ca91ac4bbbac2a02 Assets/ImportedActors/Wear/SweetyPanty/Meshes/Jana.mesh.meta
        // source-sha256: 6bd4c5dfd6d24d503a203cca99e169a5e773be217ecb9cfdf674cb3a37a2243b Assets/ImportedActors/Wear/SwimsuitBriefs/Meshes/Jana.mesh
        // source-sha256: 26420cb01f8fa47de75a5367f619448e49b8147368f42ad5e70d1836bf911ea5 Assets/ImportedActors/Wear/SwimsuitBriefs/Meshes/Jana.mesh.meta
        // source-sha256: 6946c48b40aa7c681f74606539258e2cdf32d3e9fb3e45cb945b56ee9196c9c4 Assets/ImportedActors/Wear/SwimsuitTop/Meshes/Jana.mesh
        // source-sha256: f43baf801fbcc6ba82e5878c176772221f5c85e740992a004b426f2443925a11 Assets/ImportedActors/Wear/SwimsuitTop/Meshes/Jana.mesh.meta
        // source-sha256: 3cce96a83cb62c8b6f1ffe00f5a3777b09fad30802f7cb3b4cec2744c05e9cc0 Assets/ImportedActors/Wear/TekJacket/Meshes/Jana.mesh
        // source-sha256: f727c7960768ecacfe9c2748831a4a097c18659f17b0ecbe27642d5cab131cc6 Assets/ImportedActors/Wear/TekJacket/Meshes/Jana.mesh.meta
        // source-sha256: ed826f4b766ea675da39e43ed6c5c5ab92aff59367cfd7e244b639039a087ee6 Assets/ImportedActors/Wear/TekJacketTied/Meshes/Jana.mesh
        // source-sha256: 9fce189de287af920f44d786ff3b85e693d95e53ca102581fbec9f3f66ad7564 Assets/ImportedActors/Wear/TekJacketTied/Meshes/Jana.mesh.meta
        // source-sha256: 516bc9243404f6dd6eeee0c2e2d455f4e8d73a3faa0caa71bea4b0de320e6cb4 Assets/ImportedActors/Wear/TekYogaPants/Meshes/Jana.mesh
        // source-sha256: ec2987c0fe4f651944b8ed29381bf6a9ba73f0e881c6330c082dca054476907f Assets/ImportedActors/Wear/TekYogaPants/Meshes/Jana.mesh.meta
        // source-sha256: 8d020c4fce6959f4950f746d912a5fc96ee897c61ee5b52e50fdfb909ab1f54b Assets/ImportedActors/Wear/Teksportsbra/Meshes/Jana.mesh
        // source-sha256: d0f78003cb90d8be9602ec0ca3c74cef5ad49ee1682ffc7196660592fe36b789 Assets/ImportedActors/Wear/Teksportsbra/Meshes/Jana.mesh.meta
        // source-sha256: 42b2d951ee3c28d85c34e5560cc2682f241531f038f7352b842e6b2cfec38e3c Assets/ImportedActors/Wear/TodBabydoll/Meshes/Jana.mesh
        // source-sha256: 0724e0ab5955399a0c26de8d5451f45ba35866dafc469cbc06ca7be090236028 Assets/ImportedActors/Wear/TodBabydoll/Meshes/Jana.mesh.meta
        // source-sha256: 0363bad7972c3ed1e0bb38027211ea8718108a14a3e9f760cb31455bf160815c Assets/ImportedActors/Wear/TodGreaves/Meshes/Jana.mesh
        // source-sha256: 082bd5ffbf817285d26d8b878807fe370915056513d6b0a1579e8ea84b18c734 Assets/ImportedActors/Wear/TodGreaves/Meshes/Jana.mesh.meta
        // source-sha256: 723e4429ec78dbee6a32418143499c6b93a6f183fa800f0b3b4ea6758ac97c5b Assets/ImportedActors/Wear/TodLaceGloves/Meshes/Jana.mesh
        // source-sha256: d34850e50808ac59a989d787baf1bd6ec460aa3809d8f7ecd9365452ce53cd66 Assets/ImportedActors/Wear/TodLaceGloves/Meshes/Jana.mesh.meta
        // source-sha256: 0c721829ed9d28c001a102c2c52362f6195453ecb0328932cbcde48fde4d1bb3 Assets/ImportedActors/Wear/TodPanty/Meshes/Jana.mesh
        // source-sha256: f874101343151d25cc1f119d2bd253fa22a3f0051abc6941949f281f7f61e182 Assets/ImportedActors/Wear/TodPanty/Meshes/Jana.mesh.meta
        // source-sha256: deb6bf32ca62f9fcde7b2df15a50458abd693cc580e4479d19a420b37baddf15 Assets/ImportedActors/Wear/TodShoes/Meshes/Jana.mesh
        // source-sha256: 7370fbe5a39eb0eb7621c9bbbcbae6be232bfed321bebcbd3f50fb1729428f90 Assets/ImportedActors/Wear/TodShoes/Meshes/Jana.mesh.meta
        // source-sha256: b19a165136c7b1f6a365e0cfbb27c28e381399cf7daf117a9b595a718913b559 Assets/ImportedActors/Wear/TodStockings/Meshes/Jana.mesh
        // source-sha256: 8a44334381c7168abc27929fb2f348afa0d134d2c883f3ee9f97ec627ae496f9 Assets/ImportedActors/Wear/TodStockings/Meshes/Jana.mesh.meta
        // source-sha256: 3245254f124423c8578f2cf38d3ca1fb03761a5d578ec5512243e0fc1cfe059b Assets/ImportedActors/Wear/TodTop/Meshes/Jana.mesh
        // source-sha256: 90b914c99c2c663a5800ec880f3f96c9af4fdf25a7fe2d70c90fb7da7ba1177c Assets/ImportedActors/Wear/TodTop/Meshes/Jana.mesh.meta
        // source-sha256: c2eb8fdc49eadb58a92a8e6e6c9d1884f31a28092da70d12ba647bac77e424d9 Assets/ImportedActors/Wear/TonnyFlash/Meshes/Kshishtof.mesh
        // source-sha256: a0ac63d547616437fb6a77eed39b89633573206960c7a592ead9430ddacca509 Assets/ImportedActors/Wear/TonnyFlash/Meshes/Kshishtof.mesh.meta
        // source-sha256: dfc6b713ccfc397ea788683e0a06fa883b9378ee65e7a088fb0621c449f1d5c4 Assets/ImportedActors/Wear/VampHalter/Meshes/Jana.mesh
        // source-sha256: 661ad0c147c2cf32bc024b624bcac8136ab4c979a76d5fea722db0b55ab28d23 Assets/ImportedActors/Wear/VampHalter/Meshes/Jana.mesh.meta
        // source-sha256: 39af5b8718cecb4a0509d6b86a5de918237a9e080f4f1c0ea59d77bcb78ee7e0 Assets/ImportedActors/Wear/VampShorts/Meshes/Jana.mesh
        // source-sha256: a18effef7861da1bdd1bd526b71cbef0a520b058b4f0186f46f45ad429f06f6f Assets/ImportedActors/Wear/VampShorts/Meshes/Jana.mesh.meta
        // source-sha256: dac2cee9f18ef7093baf09b907e456cb17634f9ee497e2e4cb8c8ff4ca6abe62 Assets/ImportedActors/Wear/VaporBriefs/Meshes/Jana.mesh
        // source-sha256: 47773413ec631c30fbd5952cfdbfbb92d23a3565fd2954c0761634f4efe7fecf Assets/ImportedActors/Wear/VaporBriefs/Meshes/Jana.mesh.meta
        // source-sha256: 6684addb15c37f5ff8bf6d4d47caae6ced90e5954c9d9623e4e64bfe1bf36da4 Assets/ImportedActors/Wear/VaporTop/Meshes/Jana.mesh
        // source-sha256: b82eddf5af924c97e8800f5cb9f4ae696c9286f688f1b83284c3d222c6c63081 Assets/ImportedActors/Wear/VaporTop/Meshes/Jana.mesh.meta
        // source-sha256: a35830cf4506ff039eb4c76ddafc4eabde7bd383c7e43451be1abadcc2b4c7cc Assets/ImportedActors/Wear/WildBra/Meshes/Jana.mesh
        // source-sha256: 26c0a9cb97901f77785c8e521c69d51a72f2e09d25aa2eb2ab9009f3173057f5 Assets/ImportedActors/Wear/WildBra/Meshes/Jana.mesh.meta
        // source-sha256: 146a4aa6c03f736062d96c501759763e437b437dba5827bf549319f19c91e6ec Assets/ImportedActors/Wear/WildPanty/Meshes/Jana.mesh
        // source-sha256: b3f5759c66121177b1db89ad8c01ca2611f32a0e537a99d588b6fc3947df8b37 Assets/ImportedActors/Wear/WildPanty/Meshes/Jana.mesh.meta
        // source-sha256: ca23a7650d3dea257c4dd1c1badb66ef22c4da61b2333918bafeb6083ec348aa Assets/ImportedActors/Wear/WildSkirt/Meshes/Jana.mesh
        // source-sha256: 54b2e58b52acd543acd9ba58176860533c0cd63162536f112a69930d3bf8f69d Assets/ImportedActors/Wear/WildSkirt/Meshes/Jana.mesh.meta
        // source-sha256: c92d86a8fbe9124296762e437c83462b26e6a60e050c31d3562b68d37de3f9ff Assets/ImportedActors/Wear/YogaPants/Meshes/Jana.mesh
        // source-sha256: 3cd70e9a08bd826b7b7069133ac5d3653a368cc6d11b31e6432129d9f4bfb49a Assets/ImportedActors/Wear/YogaPants/Meshes/Jana.mesh.meta
        // source-sha256: f94936ce053e64c138d67ed13299390c333e114f101ceeb473d9062d922f789d Assets/ImportedActors/Wear/YogaTop/Meshes/Jana.mesh
        // source-sha256: 6558c415954baeda81668c6d95ad73d80df9f7b55d6d525fb206190043ff584f Assets/ImportedActors/Wear/YogaTop/Meshes/Jana.mesh.meta
        // source-sha256: 796e59538ba91024d4b60abe4f92899aa618983864dc947122686468b264b620 SimData/simdata.json
        Garments["FAO Harness Male"] = new("FAO Harness Male", new(-0.144108191f,0.009999998f,-0.1446111f,0.144108191f,0.03847684f,0.144610628f), 0f, garment:true);
        Garments["FCO Belt Male"] = new("FCO Belt Male", new(-0.129104063f,0.009999994f,-0.0931019858f,0.129104063f,0.0359712876f,0.09310163f), 0f, garment:true);
        Garments["FCO Boots Male"] = new("FCO Boots Male", new(-0.163287669f,0.0100000054f,-0.117842592f,0.163287669f,0.321781933f,0.117842592f), 0f, garment:true);
        Garments["FCO Gloves Male"] = new("FCO Gloves Male", new(-0.244256586f,0.0100000016f,-0.115448572f,0.244256586f,0.02744177f,0.115448095f), 0f, garment:true);
        Garments["FCO Knee Straps Male"] = new("FCO Knee Straps Male", new(-0.133114249f,0.009999998f,-0.0480481274f,0.133114249f,0.027587302f,0.04804801f), 0f, garment:true);
        Garments["FCO Legs Straps Male"] = new("FCO Legs Straps Male", new(-0.158842146f,0.009999998f,-0.07926269f,0.158842146f,0.0320456252f,0.07926245f), 0f, garment:true);
        Garments["FCO Pants Male"] = new("FCO Pants Male", new(-0.155332968f,0.009999998f,-0.357366562f,0.155332968f,0.0354201049f,0.357366323f), 0f, garment:true);
        Garments["FCO Waist Strappy Male"] = new("FCO Waist Strappy Male", new(-0.187118471f,0.009999998f,-0.105580352f,0.187118471f,0.0389655679f,0.105580114f), 0f, garment:true);
        Garments["TonnyFlash"] = new("TonnyFlash", new(-0.05926156f,0.01f,-0.111289091f,0.05926156f,0.0312178526f,0.111288615f), 0f, garment:true);
        Garments["clothing.ankleboots_amy"] = new("clothing.ankleboots_amy", new(-0.1492402f,0.0100000054f,-0.113828354f,0.1492402f,0.420566082f,0.113828354f), 0f, garment:true);
        Garments["clothing.armguards_fighter"] = new("clothing.armguards_fighter", new(-0.219305187f,0.009999998f,-0.103625327f,0.219305187f,0.0232136622f,0.103625089f), 0f, garment:true);
        Garments["clothing.armwraps_primal"] = new("clothing.armwraps_primal", new(-0.131679788f,0.009999998f,-0.0402340554f,0.131679788f,0.0248740017f,0.04023334f), 0f, garment:true);
        Garments["clothing.babydoll_sweety"] = new("clothing.babydoll_sweety", new(-0.1605893f,0.01f,-0.305807948f,0.1605893f,0.0414713547f,0.305807471f), 0f, garment:true);
        Garments["clothing.babydoll_tod"] = new("clothing.babydoll_tod", new(-0.17174001f,0.009999998f,-0.256130159f,0.17174001f,0.0381937623f,0.256129682f), 0f, garment:true);
        Garments["clothing.belt_anarchy"] = new("clothing.belt_anarchy", new(-0.137306258f,0.009999998f,-0.04728409f,0.137306258f,0.0320511162f,0.04728373f), 0f, garment:true);
        Garments["clothing.belt_cindy"] = new("clothing.belt_cindy", new(-0.219829872f,0.009999998f,-0.13082312f,0.219829872f,0.0392745063f,0.130822882f), 0f, garment:true);
        Garments["clothing.belt_fighter"] = new("clothing.belt_fighter", new(-0.115768552f,0.009999998f,-0.08809592f,0.115768552f,0.035432525f,0.08809544f), 0f, garment:true);
        Garments["clothing.belt_holster"] = new("clothing.belt_holster", new(-0.200358212f,0.0100000054f,-0.100100115f,0.200358212f,0.307819843f,0.100100115f), 0f, garment:true);
        Garments["clothing.belt_stars"] = new("clothing.belt_stars", new(-0.159135625f,0.0100000016f,-0.180492178f,0.159135625f,0.0352623463f,0.18049182f), 0f, garment:true);
        Garments["clothing.blouse_anarchy"] = new("clothing.blouse_anarchy", new(-0.1300287f,0.009999998f,-0.1677889f,0.1300287f,0.03691961f,0.167788416f), 0f, garment:true);
        Garments["clothing.blouse_nerd"] = new("clothing.blouse_nerd", new(-0.211630523f,0.009999998f,-0.221405551f,0.211630523f,0.0361937433f,0.221405074f), 0f, garment:true);
        Garments["clothing.blouse_riot"] = new("clothing.blouse_riot", new(-0.256436557f,0.009999998f,-0.278194815f,0.256436557f,0.03993518f,0.278194338f), 0f, garment:true);
        Garments["clothing.blouse_waist_riot"] = new("clothing.blouse_waist_riot", new(-0.198066711f,0.01f,-0.226345f,0.198066711f,0.04533136f,0.226344764f), 0f, garment:true);
        Garments["clothing.boots_alloy"] = new("clothing.boots_alloy", new(-0.150515765f,0.0100000054f,-0.1063823f,0.150515765f,0.443104684f,0.1063823f), 0f, garment:true);
        Garments["clothing.boots_anarchy"] = new("clothing.boots_anarchy", new(-0.156863481f,0.00999999f,-0.106238686f,0.156863481f,0.7139905f,0.106238686f), 0f, garment:true);
        Garments["clothing.boots_charm"] = new("clothing.boots_charm", new(-0.17078279f,0.00999999f,-0.139656663f,0.17078279f,0.7360917f,0.139656663f), 0f, garment:true);
        Garments["clothing.boots_cindy"] = new("clothing.boots_cindy", new(-0.163705155f,0.00999999f,-0.12154679f,0.163705155f,0.634100735f,0.12154679f), 0f, garment:true);
        Garments["clothing.boots_classic"] = new("clothing.boots_classic", new(-0.150591865f,0.0100000054f,-0.106906861f,0.150591865f,0.353836775f,0.106906861f), 0f, garment:true);
        Garments["clothing.boots_leather"] = new("clothing.boots_leather", new(-0.154185057f,0.009999998f,-0.100192636f,0.154185057f,0.171095014f,0.100192636f), 0f, garment:true);
        Garments["clothing.boots_osiris"] = new("clothing.boots_osiris", new(-0.154464483f,0.0100000054f,-0.111581214f,0.154464483f,0.3748266f,0.111581214f), 0f, garment:true);
        Garments["clothing.boots_ranger"] = new("clothing.boots_ranger", new(-0.152045771f,0.009999998f,-0.101694643f,0.152045771f,0.173155189f,0.101694643f), 0f, garment:true);
        Garments["clothing.boots_riot"] = new("clothing.boots_riot", new(-0.150271639f,0.009999998f,-0.104762077f,0.150271639f,0.207721889f,0.104762077f), 0f, garment:true);
        Garments["clothing.bowtie_nerd"] = new("clothing.bowtie_nerd", new(-0.0492698774f,0.009999999f,-0.0211989358f,0.0492698774f,0.0118775861f,0.02119822f), 0f, garment:true);
        Garments["clothing.bracelet_luxury"] = new("clothing.bracelet_luxury", new(-0.008101163f,0.01f,-0.0196596887f,0.008101163f,0.0163691361f,0.0196592119f), 0f, garment:true);
        Garments["clothing.cap_anarchy"] = new("clothing.cap_anarchy", new(-0.08145848f,0.009999998f,-0.106671572f,0.08145848f,0.13662526f,0.106671572f), 0f, garment:true);
        Garments["clothing.cap_riot"] = new("clothing.cap_riot", new(-0.07584693f,0.009999998f,-0.0896277f,0.07584693f,0.170241356f,0.0896277f), 0f, garment:true);
        Garments["clothing.cap_stars"] = new("clothing.cap_stars", new(-0.0869237259f,0.009999976f,-0.0983661339f,0.0869237259f,0.0918777138f,0.0983661339f), 0f, garment:true);
        Garments["clothing.collar_anarchy"] = new("clothing.collar_anarchy", new(-0.04945299f,0.01f,-0.0160549823f,0.04945299f,0.0218220931f,0.0160545055f), 0f, garment:true);
        Garments["clothing.collar_fur"] = new("clothing.collar_fur", new(-0.05780177f,0.01f,-0.201877072f,0.05780177f,0.04491371f,0.2018766f), 0f, garment:true);
        Garments["clothing.collar_studded_anarchy"] = new("clothing.collar_studded_anarchy", new(-0.0479391254f,0.009999998f,-0.037019033f,0.0479391254f,0.0216752142f,0.0370183177f), 0f, garment:true);
        Garments["clothing.corset_anarchy"] = new("clothing.corset_anarchy", new(-0.126487091f,0.009999998f,-0.188868523f,0.126487091f,0.036268197f,0.188868046f), 0f, garment:true);
        Garments["clothing.corset_skinny"] = new("clothing.corset_skinny", new(-0.126310647f,0.009999998f,-0.194560885f,0.126310647f,0.0342929065f,0.194560409f), 0f, garment:true);
        Garments["clothing.croptop_fit"] = new("clothing.croptop_fit", new(-0.126722515f,0.0100000016f,-0.118106142f,0.126722515f,0.0368253328f,0.118105665f), 0f, garment:true);
        Garments["clothing.croptop_idol"] = new("clothing.croptop_idol", new(-0.121559523f,0.009999998f,-0.124508373f,0.121559523f,0.03564468f,0.1245079f), 0f, garment:true);
        Garments["clothing.cuffs_anarchy"] = new("clothing.cuffs_anarchy", new(-0.210020125f,0.01f,-0.0377731659f,0.210020125f,0.0182905365f,0.03777281f), 0f, garment:true);
        Garments["clothing.dress_city"] = new("clothing.dress_city", new(-0.202616826f,0.009999998f,-0.319238722f,0.202616826f,0.0382051468f,0.319238245f), 0f, garment:true);
        Garments["clothing.dress_night"] = new("clothing.dress_night", new(-0.174866632f,0.01f,-0.214749873f,0.174866632f,0.041831322f,0.214749515f), 0f, garment:true);
        Garments["clothing.dress_primal"] = new("clothing.dress_primal", new(-0.1561137f,0.009999998f,-0.3294962f,0.1561137f,0.04075727f,0.329495728f), 0f, garment:true);
        Garments["clothing.footwear_fighter"] = new("clothing.footwear_fighter", new(-0.148239344f,0.00999999f,-0.09821037f,0.148239344f,0.241569579f,0.09821037f), 0f, garment:true);
        Garments["clothing.glasses_nerd"] = new("clothing.glasses_nerd", new(-0.061642617f,0.009999959f,-0.05735799f,0.061642617f,0.04660204f,0.05735799f), 0f, garment:true);
        Garments["clothing.gloves_biker"] = new("clothing.gloves_biker", new(-0.215777561f,0.01f,-0.0870504454f,0.215777561f,0.021928722f,0.08705009f), 0f, garment:true);
        Garments["clothing.gloves_cindy"] = new("clothing.gloves_cindy", new(-0.215941548f,0.01f,-0.08088387f,0.215941548f,0.0219716933f,0.08088351f), 0f, garment:true);
        Garments["clothing.gloves_classic"] = new("clothing.gloves_classic", new(-0.235049576f,0.01f,-0.08255861f,0.235049576f,0.0218732562f,0.08255837f), 0f, garment:true);
        Garments["clothing.gloves_deadly"] = new("clothing.gloves_deadly", new(-0.2138325f,0.009999998f,-0.218173161f,0.2138325f,0.02964329f,0.2181728f), 0f, garment:true);
        Garments["clothing.gloves_fit"] = new("clothing.gloves_fit", new(-0.211600229f,0.01f,-0.06911514f,0.211600229f,0.0208617877f,0.06911478f), 0f, garment:true);
        Garments["clothing.gloves_lace_tod"] = new("clothing.gloves_lace_tod", new(-0.228604913f,0.0100000016f,-0.09903162f,0.228604913f,0.0270522758f,0.09903114f), 0f, garment:true);
        Garments["clothing.gloves_long_anarchy"] = new("clothing.gloves_long_anarchy", new(-0.220010579f,0.009999998f,-0.110824339f,0.220010579f,0.0235378183f,0.110823862f), 0f, garment:true);
        Garments["clothing.gloves_osiris"] = new("clothing.gloves_osiris", new(-0.213865533f,0.01f,-0.05938147f,0.213865533f,0.0203810576f,0.0593811125f), 0f, garment:true);
        Garments["clothing.gloves_stars"] = new("clothing.gloves_stars", new(-0.218609914f,0.01f,-0.07234247f,0.218609914f,0.021670552f,0.07234211f), 0f, garment:true);
        Garments["clothing.gloves_strap_anarchy"] = new("clothing.gloves_strap_anarchy", new(-0.216429248f,0.009999998f,-0.1538755f,0.216429248f,0.0256619379f,0.153875142f), 0f, garment:true);
        Garments["clothing.greaves_tod"] = new("clothing.greaves_tod", new(-0.150459543f,0.01f,-0.1529329f,0.150459543f,0.0260824021f,0.152932838f), 0f, garment:true);
        Garments["clothing.halter_vamp"] = new("clothing.halter_vamp", new(-0.127768978f,0.009999998f,-0.226770386f,0.127768978f,0.0349025875f,0.226769909f), 0f, garment:true);
        Garments["clothing.headband_primal"] = new("clothing.headband_primal", new(-0.06600076f,0.0100000277f,-0.0778799653f,0.06600076f,0.121396504f,0.0778799653f), 0f, garment:true);
        Garments["clothing.heels_luxury"] = new("clothing.heels_luxury", new(-0.1470821f,0.009999998f,-0.09761898f,0.1470821f,0.157369733f,0.09761898f), 0f, garment:true);
        Garments["clothing.helmet_bull"] = new("clothing.helmet_bull", new(-0.0810375139f,0.009999998f,-0.100941166f,0.0810375139f,0.155670822f,0.100941166f), 0f, garment:true);
        Garments["clothing.helmet_carbon"] = new("clothing.helmet_carbon", new(-0.0860507339f,0.009999998f,-0.100941174f,0.0860507339f,0.175267637f,0.100941174f), 0f, garment:true);
        Garments["clothing.helmet_knight"] = new("clothing.helmet_knight", new(-0.08589825f,0.009999953f,-0.100941166f,0.08589825f,0.192148715f,0.100941166f), 0f, garment:true);
        Garments["clothing.helmet_m1"] = new("clothing.helmet_m1", new(-0.08430392f,0.009999976f,-0.100941166f,0.08430392f,0.197021976f,0.100941166f), 0f, garment:true);
        Garments["clothing.helmet_moto"] = new("clothing.helmet_moto", new(-0.100941174f,0.009999946f,-0.08948467f,0.100941174f,0.160912558f,0.08948467f), 0f, garment:true);
        Garments["clothing.helmet_racing"] = new("clothing.helmet_racing", new(-0.08126344f,0.009999998f,-0.100941174f,0.08126344f,0.204614371f,0.100941174f), 0f, garment:true);
        Garments["clothing.helmet_retro"] = new("clothing.helmet_retro", new(-0.100941174f,0.009999961f,-0.100489311f,0.100941174f,0.210978061f,0.100488834f), 0f, garment:true);
        Garments["clothing.helmet_space"] = new("clothing.helmet_space", new(-0.06627611f,0.009999998f,-0.100941189f,0.06627611f,0.115668468f,0.100941189f), 0f, garment:true);
        Garments["clothing.helmet_t1"] = new("clothing.helmet_t1", new(-0.0847238f,0.009999998f,-0.100941174f,0.0847238f,0.205664843f,0.100941174f), 0f, garment:true);
        Garments["clothing.helmet_tactical"] = new("clothing.helmet_tactical", new(-0.100941174f,0.009999976f,-0.0992897749f,0.100941174f,0.133600727f,0.0992897749f), 0f, garment:true);
        Garments["clothing.helmet_tactical_headset"] = new("clothing.helmet_tactical_headset", new(-0.100941189f,0.009999998f,-0.100580439f,0.100941189f,0.211160392f,0.100579962f), 0f, garment:true);
        Garments["clothing.helmet_vietnam"] = new("clothing.helmet_vietnam", new(-0.09644155f,0.009999998f,-0.100941166f,0.09644155f,0.1888802f,0.100941166f), 0f, garment:true);
        Garments["clothing.helmet_vintage"] = new("clothing.helmet_vintage", new(-0.0871829242f,0.0100000054f,-0.117561691f,0.0871829242f,0.2451229f,0.117561214f), 0f, garment:true);
        Garments["clothing.hipbelt_anarchy"] = new("clothing.hipbelt_anarchy", new(-0.146742865f,0.009999998f,-0.0490262657f,0.146742865f,0.0341011733f,0.0490260273f), 0f, garment:true);
        Garments["clothing.jacket_autumn"] = new("clothing.jacket_autumn", new(-0.2246686f,0.009999998f,-0.270919174f,0.2246686f,0.0391384959f,0.2709187f), 0f, garment:true);
        Garments["clothing.jacket_biker"] = new("clothing.jacket_biker", new(-0.220309511f,0.009999998f,-0.268340081f,0.220309511f,0.0373033956f,0.2683396f), 0f, garment:true);
        Garments["clothing.jacket_cindy"] = new("clothing.jacket_cindy", new(-0.230779067f,0.0100000016f,-0.2565149f,0.230779067f,0.03724532f,0.25651443f), 0f, garment:true);
        Garments["clothing.jacket_ranger"] = new("clothing.jacket_ranger", new(-0.136026114f,0.009999998f,-0.213287637f,0.136026114f,0.0369533747f,0.21328716f), 0f, garment:true);
        Garments["clothing.jacket_tek"] = new("clothing.jacket_tek", new(-0.279048741f,0.009999998f,-0.2578928f,0.279048741f,0.036587365f,0.2578923f), 0f, garment:true);
        Garments["clothing.jackettied_tek"] = new("clothing.jackettied_tek", new(-0.184841335f,0.0100000054f,-0.201652184f,0.184841335f,0.0711025447f,0.201651827f), 0f, garment:true);
        Garments["clothing.jeans_skinny"] = new("clothing.jeans_skinny", new(-0.154191509f,0.009999998f,-0.367409736f,0.154191509f,0.0343112573f,0.3674095f), 0f, garment:true);
        Garments["clothing.keikogi_fighter"] = new("clothing.keikogi_fighter", new(-0.15192236f,0.009999998f,-0.239407942f,0.15192236f,0.0388606638f,0.239407465f), 0f, garment:true);
        Garments["clothing.leather_pants"] = new("clothing.leather_pants", new(-0.150873587f,0.009999998f,-0.410664767f,0.150873587f,0.03537538f,0.410664529f), 0f, garment:true);
        Garments["clothing.leggings_idol"] = new("clothing.leggings_idol", new(-0.151006192f,0.009999998f,-0.408963084f,0.151006192f,0.0342125371f,0.408962846f), 0f, garment:true);
        Garments["clothing.leggings_yoga"] = new("clothing.leggings_yoga", new(-0.1523402f,0.0100000016f,-0.338622153f,0.1523402f,0.0336285457f,0.3386219f), 0f, garment:true);
        Garments["clothing.necklace_beads"] = new("clothing.necklace_beads", new(-0.06298023f,0.009999998f,-0.0445920564f,0.06298023f,0.0255092718f,0.04459158f), 0f, garment:true);
        Garments["clothing.necklace_osiris"] = new("clothing.necklace_osiris", new(-0.06060233f,0.009999998f,-0.0439993329f,0.06060233f,0.02551794f,0.0439988561f), 0f, garment:true);
        Garments["clothing.outfit_reiko"] = new("clothing.outfit_reiko", new(-0.235049531f,0.01f,-0.626517951f,0.235049531f,0.04354971f,0.6265176f), 0f, garment:true);
        Garments["clothing.pants_anarchy"] = new("clothing.pants_anarchy", new(-0.153447509f,0.009999998f,-0.172294214f,0.153447509f,0.0333237872f,0.172293976f), 0f, garment:true);
        Garments["clothing.pants_biker"] = new("clothing.pants_biker", new(-0.150873587f,0.009999998f,-0.410664767f,0.150873587f,0.03537538f,0.410664529f), 0f, garment:true);
        Garments["clothing.pants_fighter"] = new("clothing.pants_fighter", new(-0.15367727f,0.0100000016f,-0.217689186f,0.15367727f,0.034184888f,0.217688948f), 0f, garment:true);
        Garments["clothing.pants_ranger"] = new("clothing.pants_ranger", new(-0.153435856f,0.009999998f,-0.370240748f,0.153435856f,0.034334138f,0.3702405f), 0f, garment:true);
        Garments["clothing.pants_stars"] = new("clothing.pants_stars", new(-0.1517378f,0.0100000016f,-0.3367853f,0.1517378f,0.034677323f,0.336785048f), 0f, garment:true);
        Garments["clothing.pendant_amy"] = new("clothing.pendant_amy", new(-0.0539012551f,0.0100000016f,-0.03243057f,0.0539012551f,0.0235043168f,0.0324298553f), 0f, garment:true);
        Garments["clothing.pumps_flair"] = new("clothing.pumps_flair", new(-0.164454058f,0.009999998f,-0.10325624f,0.164454058f,0.156004131f,0.10325624f), 0f, garment:true);
        Garments["clothing.sandals_summer1"] = new("clothing.sandals_summer1", new(-0.15439795f,0.009999998f,-0.096705474f,0.15439795f,0.07203067f,0.096705474f), 0f, garment:true);
        Garments["clothing.sandals_summer2"] = new("clothing.sandals_summer2", new(-0.1535085f,0.0100000016f,-0.096705474f,0.1535085f,0.0928863f,0.096705474f), 0f, garment:true);
        Garments["clothing.sandals_summer3"] = new("clothing.sandals_summer3", new(-0.1535085f,0.0100000016f,-0.096705474f,0.1535085f,0.106662527f,0.096705474f), 0f, garment:true);
        Garments["clothing.sandals_summer4"] = new("clothing.sandals_summer4", new(-0.1535085f,0.0100000016f,-0.096705474f,0.1535085f,0.0949373245f,0.096705474f), 0f, garment:true);
        Garments["clothing.scarf_classic"] = new("clothing.scarf_classic", new(-0.0512042157f,0.009999998f,-0.133045763f,0.0512042157f,0.0329416841f,0.133045048f), 0f, garment:true);
        Garments["clothing.shirt_amy"] = new("clothing.shirt_amy", new(-0.215279058f,0.009999998f,-0.204529777f,0.215279058f,0.0363017768f,0.2045293f), 0f, garment:true);
        Garments["clothing.shirt_riot"] = new("clothing.shirt_riot", new(-0.1429905f,0.009999998f,-0.2142672f,0.1429905f,0.03638722f,0.214266717f), 0f, garment:true);
        Garments["clothing.shoes_tod"] = new("clothing.shoes_tod", new(-0.150950134f,0.00999999f,-0.10641592f,0.150950134f,0.122263879f,0.10641592f), 0f, garment:true);
        Garments["clothing.shorts_cindy"] = new("clothing.shorts_cindy", new(-0.144058868f,0.009999998f,-0.069151625f,0.144058868f,0.0347894f,0.06915139f), 0f, garment:true);
        Garments["clothing.shorts_classic"] = new("clothing.shorts_classic", new(-0.146141559f,0.009999998f,-0.07891923f,0.146141559f,0.03770273f,0.0789188743f), 0f, garment:true);
        Garments["clothing.shorts_deadly"] = new("clothing.shorts_deadly", new(-0.163246274f,0.009999998f,-0.127047688f,0.163246274f,0.03562343f,0.12704733f), 0f, garment:true);
        Garments["clothing.shorts_fit"] = new("clothing.shorts_fit", new(-0.138588861f,0.009999998f,-0.06247878f,0.138588861f,0.0323892f,0.0624784231f), 0f, garment:true);
        Garments["clothing.shorts_osiris"] = new("clothing.shorts_osiris", new(-0.150339663f,0.0100000016f,-0.07958674f,0.150339663f,0.0334851928f,0.0795865f), 0f, garment:true);
        Garments["clothing.shorts_riot"] = new("clothing.shorts_riot", new(-0.157580629f,0.009999998f,-0.09893139f,0.157580629f,0.0349681154f,0.09893103f), 0f, garment:true);
        Garments["clothing.shorts_summer"] = new("clothing.shorts_summer", new(-0.15138711f,0.009999998f,-0.09381831f,0.15138711f,0.03439276f,0.09381795f), 0f, garment:true);
        Garments["clothing.shorts_vamp"] = new("clothing.shorts_vamp", new(-0.1459004f,0.009999998f,-0.0836772f,0.1459004f,0.0340456329f,0.0836768448f), 0f, garment:true);
        Garments["clothing.skirt_alloy"] = new("clothing.skirt_alloy", new(-0.15264526f,0.009999998f,-0.190288916f,0.15264526f,0.03384527f,0.190288678f), 0f, garment:true);
        Garments["clothing.skirt_amy"] = new("clothing.skirt_amy", new(-0.198274583f,0.01f,-0.139077917f,0.198274583f,0.0460366756f,0.139077678f), 0f, garment:true);
        Garments["clothing.skirt_anarchy"] = new("clothing.skirt_anarchy", new(-0.1648003f,0.009999998f,-0.09913734f,0.1648003f,0.03813715f,0.0991369858f), 0f, garment:true);
        Garments["clothing.skirt_flair"] = new("clothing.skirt_flair", new(-0.19430314f,0.009999998f,-0.160017952f,0.19430314f,0.0397537947f,0.1600176f), 0f, garment:true);
        Garments["clothing.skirt_jane"] = new("clothing.skirt_jane", new(-0.2965855f,0.009999998f,-0.3750214f,0.2965855f,0.06730498f,0.37502116f), 0f, garment:true);
        Garments["clothing.skirt_naughty"] = new("clothing.skirt_naughty", new(-0.19321005f,0.0100000016f,-0.11000675f,0.19321005f,0.0432697423f,0.110006392f), 0f, garment:true);
        Garments["clothing.skirt_primal"] = new("clothing.skirt_primal", new(-0.143654332f,0.0100000016f,-0.09274446f,0.143654332f,0.03517512f,0.0927441046f), 0f, garment:true);
        Garments["clothing.skirt_wild"] = new("clothing.skirt_wild", new(-0.178270116f,0.0100000016f,-0.0683057159f,0.178270116f,0.04224233f,0.06830536f), 0f, garment:true);
        Garments["clothing.sleeves_idol"] = new("clothing.sleeves_idol", new(-0.215491414f,0.01f,-0.259655625f,0.215491414f,0.0291016828f,0.259655148f), 0f, garment:true);
        Garments["clothing.slipons_fads"] = new("clothing.slipons_fads", new(-0.1540348f,0.009999998f,-0.10504739f,0.1540348f,0.08345149f,0.10504739f), 0f, garment:true);
        Garments["clothing.sneakers_canvas"] = new("clothing.sneakers_canvas", new(-0.153364345f,0.009999998f,-0.109588295f,0.153364345f,0.103374951f,0.109588295f), 0f, garment:true);
        Garments["clothing.sneakers_nerd"] = new("clothing.sneakers_nerd", new(-0.152826548f,0.0100000016f,-0.101522639f,0.152826548f,0.09244232f,0.101522639f), 0f, garment:true);
        Garments["clothing.sneakers_sport"] = new("clothing.sneakers_sport", new(-0.1520076f,0.009999998f,-0.103456408f,0.1520076f,0.100534372f,0.103456408f), 0f, garment:true);
        Garments["clothing.suit_bandaid"] = new("clothing.suit_bandaid", new(-0.121751979f,0.009999998f,-0.2537065f,0.121751979f,0.0341854542f,0.253706038f), 0f, garment:true);
        Garments["clothing.sunglasses_luxury"] = new("clothing.sunglasses_luxury", new(-0.06195992f,0.009999998f,-0.0548098348f,0.06195992f,0.05620026f,0.0548098348f), 0f, garment:true);
        Garments["clothing.sweater_flair"] = new("clothing.sweater_flair", new(-0.2277326f,0.009999998f,-0.174393132f,0.2277326f,0.0364200547f,0.174392655f), 0f, garment:true);
        Garments["clothing.sweater_naughty"] = new("clothing.sweater_naughty", new(-0.214349359f,0.009999998f,-0.211987287f,0.214349359f,0.0368649f,0.21198681f), 0f, garment:true);
        Garments["clothing.tank_jane"] = new("clothing.tank_jane", new(-0.123795159f,0.009999998f,-0.165800512f,0.123795159f,0.03650832f,0.165800035f), 0f, garment:true);
        Garments["clothing.tanktop_summer"] = new("clothing.tanktop_summer", new(-0.13249962f,0.009999998f,-0.203948215f,0.13249962f,0.0362773836f,0.203947738f), 0f, garment:true);
        Garments["clothing.thighboots_amy"] = new("clothing.thighboots_amy", new(-0.148686871f,0.00999999f,-0.112238765f,0.148686871f,0.578261f,0.112238765f), 0f, garment:true);
        Garments["clothing.top_alloy"] = new("clothing.top_alloy", new(-0.214383f,0.009999998f,-0.19338803f,0.214383f,0.03660261f,0.193387553f), 0f, garment:true);
        Garments["clothing.top_anarchy"] = new("clothing.top_anarchy", new(-0.225153819f,0.009999998f,-0.187248632f,0.225153819f,0.03721139f,0.187248155f), 0f, garment:true);
        Garments["clothing.top_classic"] = new("clothing.top_classic", new(-0.12493331f,0.009999998f,-0.121626757f,0.12493331f,0.03654754f,0.12162628f), 0f, garment:true);
        Garments["clothing.top_deadly"] = new("clothing.top_deadly", new(-0.118852064f,0.009999998f,-0.158742875f,0.118852064f,0.0332435f,0.15874216f), 0f, garment:true);
        Garments["clothing.top_fighter"] = new("clothing.top_fighter", new(-0.1211979f,0.009999998f,-0.121340327f,0.1211979f,0.0344350636f,0.12133985f), 0f, garment:true);
        Garments["clothing.top_folk"] = new("clothing.top_folk", new(-0.120170861f,0.0100000016f,-0.17592977f,0.120170861f,0.03207163f,0.1759293f), 0f, garment:true);
        Garments["clothing.top_osiris"] = new("clothing.top_osiris", new(-0.129576817f,0.0100000016f,-0.210818753f,0.129576817f,0.03652528f,0.210818276f), 0f, garment:true);
        Garments["clothing.top_primal"] = new("clothing.top_primal", new(-0.1222833f,0.0100000016f,-0.12877965f,0.1222833f,0.03231019f,0.128778934f), 0f, garment:true);
        Garments["clothing.top_stars"] = new("clothing.top_stars", new(-0.213127673f,0.009999998f,-0.159538075f,0.213127673f,0.0365102142f,0.15953736f), 0f, garment:true);
        Garments["clothing.top_tod"] = new("clothing.top_tod", new(-0.2238662f,0.009999998f,-0.07935697f,0.2238662f,0.0251562521f,0.07935649f), 0f, garment:true);
        Garments["clothing.top_yoga"] = new("clothing.top_yoga", new(-0.121068232f,0.009999998f,-0.116624407f,0.121068232f,0.0369984172f,0.116623931f), 0f, garment:true);
        Garments["clothing.tshirt_big"] = new("clothing.tshirt_big", new(-0.223234758f,0.009999998f,-0.285309583f,0.223234758f,0.03896074f,0.2853091f), 0f, garment:true);
        Garments["clothing.tshirt_summer"] = new("clothing.tshirt_summer", new(-0.2357467f,0.009999998f,-0.1849989f,0.2357467f,0.03744133f,0.184998423f), 0f, garment:true);
        Garments["clothing.tutu_nerd"] = new("clothing.tutu_nerd", new(-0.22577f,0.01f,-0.157041624f,0.22577f,0.05048211f,0.157041267f), 0f, garment:true);
        Garments["clothing.vest_ranger"] = new("clothing.vest_ranger", new(-0.123354852f,0.009999998f,-0.178302f,0.123354852f,0.04031831f,0.178301528f), 0f, garment:true);
        Garments["clothing.vest_stars"] = new("clothing.vest_stars", new(-0.143149361f,0.009999998f,-0.127947673f,0.143149361f,0.0380389169f,0.1279472f), 0f, garment:true);
        Garments["clothing.wrapboots_primal"] = new("clothing.wrapboots_primal", new(-0.149569348f,0.0100000054f,-0.09890753f,0.149569348f,0.4103257f,0.09890753f), 0f, garment:true);
        Garments["clothing.yogapants_tek"] = new("clothing.yogapants_tek", new(-0.152304739f,0.0100000016f,-0.3275143f,0.152304739f,0.0340099744f,0.327514052f), 0f, garment:true);
        Garments["gear.backpack_osiris"] = new("gear.backpack_osiris", new(-0.128653631f,0.01000002f,-0.133461937f,0.128653631f,0.2595341f,0.133461937f), 0f, garment:true);
        Garments["gear.backpack_riot"] = new("gear.backpack_riot", new(-0.131723464f,0.0100000054f,-0.114889964f,0.131723464f,0.435140371f,0.114889964f), 0f, garment:true);
        Garments["gear.beltpouch_ranger"] = new("gear.beltpouch_ranger", new(-0.147054538f,0.009999998f,-0.1319866f,0.147054538f,0.0338844955f,0.131986246f), 0f, garment:true);
        Garments["underwear.bikini_briefs"] = new("underwear.bikini_briefs", new(-0.126348987f,0.009999998f,-0.074710086f,0.126348987f,0.0318752825f,0.07470973f), 0f, garment:true);
        Garments["underwear.bikini_top"] = new("underwear.bikini_top", new(-0.120251104f,0.009999998f,-0.1043819f,0.120251104f,0.0366918854f,0.10438142f), 0f, garment:true);
        Garments["underwear.bra_lace"] = new("underwear.bra_lace", new(-0.1211887f,0.009999998f,-0.116415262f,0.1211887f,0.0353671759f,0.116414785f), 0f, garment:true);
        Garments["underwear.bra_openback"] = new("underwear.bra_openback", new(-0.118599869f,0.009999998f,-0.145150185f,0.118599869f,0.03220787f,0.145149708f), 0f, garment:true);
        Garments["underwear.bra_riot"] = new("underwear.bra_riot", new(-0.120043389f,0.009999998f,-0.103475064f,0.120043389f,0.0356615037f,0.103474587f), 0f, garment:true);
        Garments["underwear.bra_strappy"] = new("underwear.bra_strappy", new(-0.11620868f,0.009999998f,-0.137846261f,0.11620868f,0.03577251f,0.137845784f), 0f, garment:true);
        Garments["underwear.bra_wild"] = new("underwear.bra_wild", new(-0.118949555f,0.009999998f,-0.121590659f,0.118949555f,0.036187984f,0.121590182f), 0f, garment:true);
        Garments["underwear.briefs_cindy"] = new("underwear.briefs_cindy", new(-0.127140716f,0.009999998f,-0.07194667f,0.127140716f,0.0318213776f,0.0719463155f), 0f, garment:true);
        Garments["underwear.briefs_flair"] = new("underwear.briefs_flair", new(-0.138875663f,0.009999998f,-0.0809911042f,0.138875663f,0.0345170945f,0.08099075f), 0f, garment:true);
        Garments["underwear.briefs_lace"] = new("underwear.briefs_lace", new(-0.1402641f,0.009999998f,-0.07402029f,0.1402641f,0.0325510949f,0.07401993f), 0f, garment:true);
        Garments["underwear.briefs_luxury"] = new("underwear.briefs_luxury", new(-0.150134251f,0.009999998f,-0.0832741857f,0.150134251f,0.0330288634f,0.08327383f), 0f, garment:true);
        Garments["underwear.briefs_openback"] = new("underwear.briefs_openback", new(-0.137009591f,0.0100000016f,-0.06737626f,0.137009591f,0.0317564f,0.0673759058f), 0f, garment:true);
        Garments["underwear.briefs_plain"] = new("underwear.briefs_plain", new(-0.137914255f,0.0100000016f,-0.0584662743f,0.137914255f,0.03303938f,0.0584660359f), 0f, garment:true);
        Garments["underwear.briefs_primal"] = new("underwear.briefs_primal", new(-0.1268879f,0.009999998f,-0.07394711f,0.1268879f,0.0325815231f,0.07394687f), 0f, garment:true);
        Garments["underwear.briefs_sport"] = new("underwear.briefs_sport", new(-0.119380854f,0.009999998f,-0.09685337f,0.119380854f,0.0338043272f,0.09685313f), 0f, garment:true);
        Garments["underwear.briefs_strappy"] = new("underwear.briefs_strappy", new(-0.1353021f,0.009999998f,-0.07977737f,0.1353021f,0.0328214541f,0.07977713f), 0f, garment:true);
        Garments["underwear.briefs_sweety"] = new("underwear.briefs_sweety", new(-0.11763341f,0.009999998f,-0.0938098356f,0.11763341f,0.0339212865f,0.09380948f), 0f, garment:true);
        Garments["underwear.briefs_teez"] = new("underwear.briefs_teez", new(-0.13314642f,0.009999998f,-0.074691385f,0.13314642f,0.032178022f,0.07469103f), 0f, garment:true);
        Garments["underwear.briefs_vapor"] = new("underwear.briefs_vapor", new(-0.121288963f,0.009999998f,-0.08751768f,0.121288963f,0.0320180357f,0.08751732f), 0f, garment:true);
        Garments["underwear.briefs_wild"] = new("underwear.briefs_wild", new(-0.133779526f,0.01f,-0.0605271235f,0.133779526f,0.030940095f,0.0605267659f), 0f, garment:true);
        Garments["underwear.kneesocks_nerd"] = new("underwear.kneesocks_nerd", new(-0.148072541f,0.0100000016f,-0.170599535f,0.148072541f,0.0340513326f,0.170599476f), 0f, garment:true);
        Garments["underwear.panty_tod"] = new("underwear.panty_tod", new(-0.132040471f,0.009999998f,-0.0897951648f,0.132040471f,0.0329055563f,0.08979481f), 0f, garment:true);
        Garments["underwear.socks_fit"] = new("underwear.socks_fit", new(-0.140892953f,0.01f,-0.08815035f,0.140892953f,0.0250324663f,0.08815031f), 0f, garment:true);
        Garments["underwear.sportsbra_jmr"] = new("underwear.sportsbra_jmr", new(-0.113248289f,0.0100000016f,-0.1181446f,0.113248289f,0.03633446f,0.118144125f), 0f, garment:true);
        Garments["underwear.sportsbra_tek"] = new("underwear.sportsbra_tek", new(-0.120679319f,0.0100000016f,-0.11177922f,0.120679319f,0.03597511f,0.111778744f), 0f, garment:true);
        Garments["underwear.stockings_overknee"] = new("underwear.stockings_overknee", new(-0.130681083f,0.009999998f,-0.213555381f,0.130681083f,0.0271387473f,0.213555261f), 0f, garment:true);
        Garments["underwear.stockings_riot"] = new("underwear.stockings_riot", new(-0.150863171f,0.009999998f,-0.4277976f,0.150863171f,0.0358791128f,0.427797347f), 0f, garment:true);
        Garments["underwear.stockings_spooky"] = new("underwear.stockings_spooky", new(-0.1505359f,0.009999998f,-0.319341451f,0.1505359f,0.0341005027f,0.319341272f), 0f, garment:true);
        Garments["underwear.stockings_tod"] = new("underwear.stockings_tod", new(-0.150565282f,0.009999998f,-0.157354116f,0.150565282f,0.03037332f,0.157353878f), 0f, garment:true);
        Garments["underwear.swimsuit_briefs"] = new("underwear.swimsuit_briefs", new(-0.138815984f,0.009999998f,-0.06761189f,0.138815984f,0.0327117741f,0.06761153f), 0f, garment:true);
        Garments["underwear.swimsuit_top"] = new("underwear.swimsuit_top", new(-0.124746166f,0.009999998f,-0.04928947f,0.124746166f,0.03339202f,0.04928899f), 0f, garment:true);
        Garments["underwear.thong_anarchy"] = new("underwear.thong_anarchy", new(-0.114929475f,0.01f,-0.0918865651f,0.114929475f,0.0296253171f,0.09188621f), 0f, garment:true);
        Garments["underwear.tights_deadly"] = new("underwear.tights_deadly", new(-0.151228517f,0.009999998f,-0.288610756f,0.151228517f,0.0306635723f,0.288610578f), 0f, garment:true);
        Garments["underwear.top_cindy"] = new("underwear.top_cindy", new(-0.113739535f,0.009999998f,-0.123586953f,0.113739535f,0.0318447426f,0.123586476f), 0f, garment:true);
        Garments["underwear.top_luxury"] = new("underwear.top_luxury", new(-0.118383259f,0.009999998f,-0.178487644f,0.118383259f,0.0331024453f,0.178487167f), 0f, garment:true);
        Garments["underwear.top_vapor"] = new("underwear.top_vapor", new(-0.122355178f,0.009999998f,-0.0506606f,0.122355178f,0.0346809477f,0.0506601222f), 0f, garment:true);
        GarmentPrototypes["FAO Harness Male"] = "FAO Harness Male";
        GarmentPrototypes["FCO Belt Male"] = "FCO Belt Male";
        GarmentPrototypes["FCO Boots Male"] = "FCO Boots Male";
        GarmentPrototypes["FCO Gloves Male"] = "FCO Gloves Male";
        GarmentPrototypes["FCO Knee Straps Male"] = "FCO Knee Straps Male";
        GarmentPrototypes["FCO Legs Straps Male"] = "FCO Legs Straps Male";
        GarmentPrototypes["FCO Pants Male"] = "FCO Pants Male";
        GarmentPrototypes["FCO Waist Strappy Male"] = "FCO Waist Strappy Male";
        GarmentPrototypes["TonnyFlash"] = "TonnyFlash";
        GarmentPrototypes["clothing.ankleboots_amy"] = "clothing.ankleboots_amy";
        GarmentPrototypes["clothing.armguards_fighter"] = "clothing.armguards_fighter";
        GarmentPrototypes["clothing.armwraps_primal"] = "clothing.armwraps_primal";
        GarmentPrototypes["clothing.armwraps_primal_sleeve1"] = "clothing.armwraps_primal";
        GarmentPrototypes["clothing.armwraps_primal_sleeve2"] = "clothing.armwraps_primal";
        GarmentPrototypes["clothing.armwraps_primal_sleeve3"] = "clothing.armwraps_primal";
        GarmentPrototypes["clothing.babydoll_sweety"] = "clothing.babydoll_sweety";
        GarmentPrototypes["clothing.babydoll_sweety_01babydoll"] = "clothing.babydoll_sweety";
        GarmentPrototypes["clothing.babydoll_sweety_02_babydoll"] = "clothing.babydoll_sweety";
        GarmentPrototypes["clothing.babydoll_sweety_03_babydoll"] = "clothing.babydoll_sweety";
        GarmentPrototypes["clothing.babydoll_sweety_04babydoll"] = "clothing.babydoll_sweety";
        GarmentPrototypes["clothing.babydoll_sweety_05babydoll"] = "clothing.babydoll_sweety";
        GarmentPrototypes["clothing.babydoll_sweety_06_babydoll"] = "clothing.babydoll_sweety";
        GarmentPrototypes["clothing.babydoll_sweety_07_babydoll"] = "clothing.babydoll_sweety";
        GarmentPrototypes["clothing.babydoll_tod"] = "clothing.babydoll_tod";
        GarmentPrototypes["clothing.belt_anarchy"] = "clothing.belt_anarchy";
        GarmentPrototypes["clothing.belt_cindy"] = "clothing.belt_cindy";
        GarmentPrototypes["clothing.belt_fighter"] = "clothing.belt_fighter";
        GarmentPrototypes["clothing.belt_holster"] = "clothing.belt_holster";
        GarmentPrototypes["clothing.belt_stars"] = "clothing.belt_stars";
        GarmentPrototypes["clothing.blouse_anarchy"] = "clothing.blouse_anarchy";
        GarmentPrototypes["clothing.blouse_nerd"] = "clothing.blouse_nerd";
        GarmentPrototypes["clothing.blouse_riot"] = "clothing.blouse_riot";
        GarmentPrototypes["clothing.blouse_riot_denim"] = "clothing.blouse_riot";
        GarmentPrototypes["clothing.blouse_riot_green"] = "clothing.blouse_riot";
        GarmentPrototypes["clothing.blouse_riot_squaresclear"] = "clothing.blouse_riot";
        GarmentPrototypes["clothing.blouse_waist_riot"] = "clothing.blouse_waist_riot";
        GarmentPrototypes["clothing.blouse_waist_riot_denim"] = "clothing.blouse_waist_riot";
        GarmentPrototypes["clothing.blouse_waist_riot_green"] = "clothing.blouse_waist_riot";
        GarmentPrototypes["clothing.blouse_waist_riot_squaresclear"] = "clothing.blouse_waist_riot";
        GarmentPrototypes["clothing.boots_alloy"] = "clothing.boots_alloy";
        GarmentPrototypes["clothing.boots_anarchy"] = "clothing.boots_anarchy";
        GarmentPrototypes["clothing.boots_anarchy_purple"] = "clothing.boots_anarchy";
        GarmentPrototypes["clothing.boots_anarchy_red"] = "clothing.boots_anarchy";
        GarmentPrototypes["clothing.boots_charm"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_latex_blue"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_latex_grey"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_latex_pink"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_latex_red"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_latex_white"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_leather_beige"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_leather_black"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_leather_blue"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_leather_brown"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_leather_grey"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_leather_red"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_velvet_beige"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_velvet_black"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_velvet_blue"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_velvet_brown"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_velvet_purple"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_charm_velvet_red"] = "clothing.boots_charm";
        GarmentPrototypes["clothing.boots_cindy"] = "clothing.boots_cindy";
        GarmentPrototypes["clothing.boots_classic"] = "clothing.boots_classic";
        GarmentPrototypes["clothing.boots_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_black_red_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_black_white_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_black_yellow_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_red_black_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_red_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_red_white_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_red_yellow_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_white_black_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_white_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_white_red_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_white_yellow_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_yellow_black_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_yellow_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_yellow_red_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_leather_lb_yellow_white_leather"] = "clothing.boots_leather";
        GarmentPrototypes["clothing.boots_osiris"] = "clothing.boots_osiris";
        GarmentPrototypes["clothing.boots_ranger"] = "clothing.boots_ranger";
        GarmentPrototypes["clothing.boots_riot"] = "clothing.boots_riot";
        GarmentPrototypes["clothing.bowtie_nerd"] = "clothing.bowtie_nerd";
        GarmentPrototypes["clothing.bracelet_luxury"] = "clothing.bracelet_luxury";
        GarmentPrototypes["clothing.cap_anarchy"] = "clothing.cap_anarchy";
        GarmentPrototypes["clothing.cap_anarchy_black"] = "clothing.cap_anarchy";
        GarmentPrototypes["clothing.cap_anarchy_brown"] = "clothing.cap_anarchy";
        GarmentPrototypes["clothing.cap_anarchy_purple"] = "clothing.cap_anarchy";
        GarmentPrototypes["clothing.cap_riot"] = "clothing.cap_riot";
        GarmentPrototypes["clothing.cap_riot_camel"] = "clothing.cap_riot";
        GarmentPrototypes["clothing.cap_riot_denim"] = "clothing.cap_riot";
        GarmentPrototypes["clothing.cap_riot_red"] = "clothing.cap_riot";
        GarmentPrototypes["clothing.cap_stars"] = "clothing.cap_stars";
        GarmentPrototypes["clothing.collar_anarchy"] = "clothing.collar_anarchy";
        GarmentPrototypes["clothing.collar_anarchy_brn"] = "clothing.collar_anarchy";
        GarmentPrototypes["clothing.collar_fur"] = "clothing.collar_fur";
        GarmentPrototypes["clothing.collar_fur_color01"] = "clothing.collar_fur";
        GarmentPrototypes["clothing.collar_fur_color02"] = "clothing.collar_fur";
        GarmentPrototypes["clothing.collar_fur_color03"] = "clothing.collar_fur";
        GarmentPrototypes["clothing.collar_fur_color04"] = "clothing.collar_fur";
        GarmentPrototypes["clothing.collar_fur_color05"] = "clothing.collar_fur";
        GarmentPrototypes["clothing.collar_fur_color06"] = "clothing.collar_fur";
        GarmentPrototypes["clothing.collar_studded_anarchy"] = "clothing.collar_studded_anarchy";
        GarmentPrototypes["clothing.collar_studded_anarchy_black"] = "clothing.collar_studded_anarchy";
        GarmentPrototypes["clothing.corset_anarchy"] = "clothing.corset_anarchy";
        GarmentPrototypes["clothing.corset_anarchy_red"] = "clothing.corset_anarchy";
        GarmentPrototypes["clothing.corset_skinny"] = "clothing.corset_skinny";
        GarmentPrototypes["clothing.croptop_fit"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_1_blackblue"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_1_blacklime"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_1_blackpink"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_1_blackwhite"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_1_blue"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_1_lime"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_1_pink"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_1_white"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_2_black"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_3_black"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_3_black2"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_3_blackgreen"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_3_blackmagenta"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_3_blackorange"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_3_magenta"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_3_orange"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_3_turquoise"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_3_white"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_4_batik1"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_4_batik2"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_4_batik3"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_4_batik4"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_fit_4_batik5"] = "clothing.croptop_fit";
        GarmentPrototypes["clothing.croptop_idol"] = "clothing.croptop_idol";
        GarmentPrototypes["clothing.croptop_idol_blue"] = "clothing.croptop_idol";
        GarmentPrototypes["clothing.cuffs_anarchy"] = "clothing.cuffs_anarchy";
        GarmentPrototypes["clothing.dress_city"] = "clothing.dress_city";
        GarmentPrototypes["clothing.dress_city_02_dress"] = "clothing.dress_city";
        GarmentPrototypes["clothing.dress_city_03_dress"] = "clothing.dress_city";
        GarmentPrototypes["clothing.dress_city_04_dress"] = "clothing.dress_city";
        GarmentPrototypes["clothing.dress_city_05_dress"] = "clothing.dress_city";
        GarmentPrototypes["clothing.dress_city_06_dress"] = "clothing.dress_city";
        GarmentPrototypes["clothing.dress_city_07_dress"] = "clothing.dress_city";
        GarmentPrototypes["clothing.dress_night"] = "clothing.dress_night";
        GarmentPrototypes["clothing.dress_night_red"] = "clothing.dress_night";
        GarmentPrototypes["clothing.dress_night_white"] = "clothing.dress_night";
        GarmentPrototypes["clothing.dress_primal"] = "clothing.dress_primal";
        GarmentPrototypes["clothing.dress_primal_color01"] = "clothing.dress_primal";
        GarmentPrototypes["clothing.dress_primal_color02"] = "clothing.dress_primal";
        GarmentPrototypes["clothing.dress_primal_color03"] = "clothing.dress_primal";
        GarmentPrototypes["clothing.dress_primal_color04"] = "clothing.dress_primal";
        GarmentPrototypes["clothing.dress_primal_color05"] = "clothing.dress_primal";
        GarmentPrototypes["clothing.dress_primal_color06"] = "clothing.dress_primal";
        GarmentPrototypes["clothing.footwear_fighter"] = "clothing.footwear_fighter";
        GarmentPrototypes["clothing.glasses_nerd"] = "clothing.glasses_nerd";
        GarmentPrototypes["clothing.gloves_biker"] = "clothing.gloves_biker";
        GarmentPrototypes["clothing.gloves_cindy"] = "clothing.gloves_cindy";
        GarmentPrototypes["clothing.gloves_classic"] = "clothing.gloves_classic";
        GarmentPrototypes["clothing.gloves_deadly"] = "clothing.gloves_deadly";
        GarmentPrototypes["clothing.gloves_fit"] = "clothing.gloves_fit";
        GarmentPrototypes["clothing.gloves_fit_blue"] = "clothing.gloves_fit";
        GarmentPrototypes["clothing.gloves_fit_blue2"] = "clothing.gloves_fit";
        GarmentPrototypes["clothing.gloves_lace_tod"] = "clothing.gloves_lace_tod";
        GarmentPrototypes["clothing.gloves_long_anarchy"] = "clothing.gloves_long_anarchy";
        GarmentPrototypes["clothing.gloves_osiris"] = "clothing.gloves_osiris";
        GarmentPrototypes["clothing.gloves_stars"] = "clothing.gloves_stars";
        GarmentPrototypes["clothing.gloves_strap_anarchy"] = "clothing.gloves_strap_anarchy";
        GarmentPrototypes["clothing.gloves_strap_anarchy_brn"] = "clothing.gloves_strap_anarchy";
        GarmentPrototypes["clothing.greaves_tod"] = "clothing.greaves_tod";
        GarmentPrototypes["clothing.halter_vamp"] = "clothing.halter_vamp";
        GarmentPrototypes["clothing.halter_vamp_blk_lea"] = "clothing.halter_vamp";
        GarmentPrototypes["clothing.halter_vamp_burgundy"] = "clothing.halter_vamp";
        GarmentPrototypes["clothing.halter_vamp_purple_lace"] = "clothing.halter_vamp";
        GarmentPrototypes["clothing.headband_primal"] = "clothing.headband_primal";
        GarmentPrototypes["clothing.headband_primal_2"] = "clothing.headband_primal";
        GarmentPrototypes["clothing.headband_primal_3"] = "clothing.headband_primal";
        GarmentPrototypes["clothing.headband_primal_4"] = "clothing.headband_primal";
        GarmentPrototypes["clothing.heels_luxury"] = "clothing.heels_luxury";
        GarmentPrototypes["clothing.heels_luxury_golden_color"] = "clothing.heels_luxury";
        GarmentPrototypes["clothing.heels_luxury_light_pink_color"] = "clothing.heels_luxury";
        GarmentPrototypes["clothing.heels_luxury_silver_color"] = "clothing.heels_luxury";
        GarmentPrototypes["clothing.heels_luxury_white_color"] = "clothing.heels_luxury";
        GarmentPrototypes["clothing.helmet_bull"] = "clothing.helmet_bull";
        GarmentPrototypes["clothing.helmet_carbon"] = "clothing.helmet_carbon";
        GarmentPrototypes["clothing.helmet_knight"] = "clothing.helmet_knight";
        GarmentPrototypes["clothing.helmet_m1"] = "clothing.helmet_m1";
        GarmentPrototypes["clothing.helmet_moto"] = "clothing.helmet_moto";
        GarmentPrototypes["clothing.helmet_racing"] = "clothing.helmet_racing";
        GarmentPrototypes["clothing.helmet_retro"] = "clothing.helmet_retro";
        GarmentPrototypes["clothing.helmet_space"] = "clothing.helmet_space";
        GarmentPrototypes["clothing.helmet_t1"] = "clothing.helmet_t1";
        GarmentPrototypes["clothing.helmet_tactical"] = "clothing.helmet_tactical";
        GarmentPrototypes["clothing.helmet_tactical_headset"] = "clothing.helmet_tactical_headset";
        GarmentPrototypes["clothing.helmet_vietnam"] = "clothing.helmet_vietnam";
        GarmentPrototypes["clothing.helmet_vintage"] = "clothing.helmet_vintage";
        GarmentPrototypes["clothing.hipbelt_anarchy"] = "clothing.hipbelt_anarchy";
        GarmentPrototypes["clothing.jacket_autumn"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material01"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material02"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material03"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material04"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material05"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material06"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material07"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material08"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material09"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material10"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material11"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material12"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material13"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material14"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material15"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material16"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material17"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_autumn_jacket_material18"] = "clothing.jacket_autumn";
        GarmentPrototypes["clothing.jacket_biker"] = "clothing.jacket_biker";
        GarmentPrototypes["clothing.jacket_cindy"] = "clothing.jacket_cindy";
        GarmentPrototypes["clothing.jacket_ranger"] = "clothing.jacket_ranger";
        GarmentPrototypes["clothing.jacket_tek"] = "clothing.jacket_tek";
        GarmentPrototypes["clothing.jackettied_tek"] = "clothing.jackettied_tek";
        GarmentPrototypes["clothing.jeans_skinny"] = "clothing.jeans_skinny";
        GarmentPrototypes["clothing.keikogi_fighter"] = "clothing.keikogi_fighter";
        GarmentPrototypes["clothing.leather_pants"] = "clothing.leather_pants";
        GarmentPrototypes["clothing.leggings_idol"] = "clothing.leggings_idol";
        GarmentPrototypes["clothing.leggings_idol_blue"] = "clothing.leggings_idol";
        GarmentPrototypes["clothing.leggings_yoga"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_01_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_02_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_03_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_04_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_05_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_06_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_07_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_08_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_09_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.leggings_yoga_10_pants"] = "clothing.leggings_yoga";
        GarmentPrototypes["clothing.necklace_beads"] = "clothing.necklace_beads";
        GarmentPrototypes["clothing.necklace_osiris"] = "clothing.necklace_osiris";
        GarmentPrototypes["clothing.outfit_reiko"] = "clothing.outfit_reiko";
        GarmentPrototypes["clothing.outfit_reiko_default"] = "clothing.outfit_reiko";
        GarmentPrototypes["clothing.pants_anarchy"] = "clothing.pants_anarchy";
        GarmentPrototypes["clothing.pants_anarchy_black"] = "clothing.pants_anarchy";
        GarmentPrototypes["clothing.pants_biker"] = "clothing.pants_biker";
        GarmentPrototypes["clothing.pants_fighter"] = "clothing.pants_fighter";
        GarmentPrototypes["clothing.pants_ranger"] = "clothing.pants_ranger";
        GarmentPrototypes["clothing.pants_stars"] = "clothing.pants_stars";
        GarmentPrototypes["clothing.pendant_amy"] = "clothing.pendant_amy";
        GarmentPrototypes["clothing.pumps_flair"] = "clothing.pumps_flair";
        GarmentPrototypes["clothing.sandals_summer1"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer1_style_01_black"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer1_style_01_cherry"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer1_style_01_red"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer1_style_01_salmon"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer1_style_01_silver"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer1_style_01_sky"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer1_style_01_tan"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer1_style_01_white"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer1_style_01_yellow"] = "clothing.sandals_summer1";
        GarmentPrototypes["clothing.sandals_summer2"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer2_style_02_black"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer2_style_02_cherry"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer2_style_02_red"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer2_style_02_salmon"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer2_style_02_silver"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer2_style_02_sky"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer2_style_02_tan"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer2_style_02_white"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer2_style_02_yellow"] = "clothing.sandals_summer2";
        GarmentPrototypes["clothing.sandals_summer3"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer3_style_03_black"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer3_style_03_cherry"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer3_style_03_red"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer3_style_03_salmon"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer3_style_03_silver"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer3_style_03_sky"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer3_style_03_tan"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer3_style_03_white"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer3_style_03_yellow"] = "clothing.sandals_summer3";
        GarmentPrototypes["clothing.sandals_summer4"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.sandals_summer4_style_04_black"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.sandals_summer4_style_04_cherry"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.sandals_summer4_style_04_red"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.sandals_summer4_style_04_salmon"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.sandals_summer4_style_04_silver"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.sandals_summer4_style_04_sky"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.sandals_summer4_style_04_tan"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.sandals_summer4_style_04_white"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.sandals_summer4_style_04_yellow"] = "clothing.sandals_summer4";
        GarmentPrototypes["clothing.scarf_classic"] = "clothing.scarf_classic";
        GarmentPrototypes["clothing.shirt_amy"] = "clothing.shirt_amy";
        GarmentPrototypes["clothing.shirt_amy_07"] = "clothing.shirt_amy";
        GarmentPrototypes["clothing.shirt_amy_08"] = "clothing.shirt_amy";
        GarmentPrototypes["clothing.shirt_riot"] = "clothing.shirt_riot";
        GarmentPrototypes["clothing.shirt_riot_greydark"] = "clothing.shirt_riot";
        GarmentPrototypes["clothing.shirt_riot_pink"] = "clothing.shirt_riot";
        GarmentPrototypes["clothing.shirt_riot_wine"] = "clothing.shirt_riot";
        GarmentPrototypes["clothing.shoes_tod"] = "clothing.shoes_tod";
        GarmentPrototypes["clothing.shorts_cindy"] = "clothing.shorts_cindy";
        GarmentPrototypes["clothing.shorts_classic"] = "clothing.shorts_classic";
        GarmentPrototypes["clothing.shorts_deadly"] = "clothing.shorts_deadly";
        GarmentPrototypes["clothing.shorts_fit"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_1_blackblue"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_1_blacklime"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_1_blackpink"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_1_blackwhite"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_2_black"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_3_black"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_3_blackgreen"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_3_blackmagenta"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_3_blackorange"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_3_magenta"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_3_orange"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_3_turquoise"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_3_white"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_4_batik1"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_4_batik2"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_4_batik3"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_4_batik4"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_4_batik5"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_fit_4_camouflage"] = "clothing.shorts_fit";
        GarmentPrototypes["clothing.shorts_osiris"] = "clothing.shorts_osiris";
        GarmentPrototypes["clothing.shorts_riot"] = "clothing.shorts_riot";
        GarmentPrototypes["clothing.shorts_riot_brown"] = "clothing.shorts_riot";
        GarmentPrototypes["clothing.shorts_riot_denimgrey"] = "clothing.shorts_riot";
        GarmentPrototypes["clothing.shorts_riot_green"] = "clothing.shorts_riot";
        GarmentPrototypes["clothing.shorts_summer"] = "clothing.shorts_summer";
        GarmentPrototypes["clothing.shorts_vamp"] = "clothing.shorts_vamp";
        GarmentPrototypes["clothing.shorts_vamp_blk_lea"] = "clothing.shorts_vamp";
        GarmentPrototypes["clothing.shorts_vamp_blk_lea_lace"] = "clothing.shorts_vamp";
        GarmentPrototypes["clothing.skirt_alloy"] = "clothing.skirt_alloy";
        GarmentPrototypes["clothing.skirt_amy"] = "clothing.skirt_amy";
        GarmentPrototypes["clothing.skirt_amy_04"] = "clothing.skirt_amy";
        GarmentPrototypes["clothing.skirt_amy_05"] = "clothing.skirt_amy";
        GarmentPrototypes["clothing.skirt_anarchy"] = "clothing.skirt_anarchy";
        GarmentPrototypes["clothing.skirt_anarchy_red"] = "clothing.skirt_anarchy";
        GarmentPrototypes["clothing.skirt_flair"] = "clothing.skirt_flair";
        GarmentPrototypes["clothing.skirt_flair_skirt_02"] = "clothing.skirt_flair";
        GarmentPrototypes["clothing.skirt_flair_skirt_03"] = "clothing.skirt_flair";
        GarmentPrototypes["clothing.skirt_jane"] = "clothing.skirt_jane";
        GarmentPrototypes["clothing.skirt_jane_blacks"] = "clothing.skirt_jane";
        GarmentPrototypes["clothing.skirt_jane_blues"] = "clothing.skirt_jane";
        GarmentPrototypes["clothing.skirt_jane_bluest"] = "clothing.skirt_jane";
        GarmentPrototypes["clothing.skirt_jane_lavendar"] = "clothing.skirt_jane";
        GarmentPrototypes["clothing.skirt_jane_reds"] = "clothing.skirt_jane";
        GarmentPrototypes["clothing.skirt_jane_redst"] = "clothing.skirt_jane";
        GarmentPrototypes["clothing.skirt_jane_rosey"] = "clothing.skirt_jane";
        GarmentPrototypes["clothing.skirt_jane_roseyt"] = "clothing.skirt_jane";
        GarmentPrototypes["clothing.skirt_naughty"] = "clothing.skirt_naughty";
        GarmentPrototypes["clothing.skirt_naughty_skirt_2"] = "clothing.skirt_naughty";
        GarmentPrototypes["clothing.skirt_naughty_skirt_3"] = "clothing.skirt_naughty";
        GarmentPrototypes["clothing.skirt_naughty_skirt_4"] = "clothing.skirt_naughty";
        GarmentPrototypes["clothing.skirt_naughty_skirt_5"] = "clothing.skirt_naughty";
        GarmentPrototypes["clothing.skirt_naughty_skirt_6"] = "clothing.skirt_naughty";
        GarmentPrototypes["clothing.skirt_primal"] = "clothing.skirt_primal";
        GarmentPrototypes["clothing.skirt_primal_1"] = "clothing.skirt_primal";
        GarmentPrototypes["clothing.skirt_primal_2"] = "clothing.skirt_primal";
        GarmentPrototypes["clothing.skirt_primal_3"] = "clothing.skirt_primal";
        GarmentPrototypes["clothing.skirt_wild"] = "clothing.skirt_wild";
        GarmentPrototypes["clothing.sleeves_idol"] = "clothing.sleeves_idol";
        GarmentPrototypes["clothing.slipons_fads"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_checks"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_graffiti"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_ladybugs"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_navy"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_pink"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_plaid"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_planets"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_red"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_skulls"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.slipons_fads_white"] = "clothing.slipons_fads";
        GarmentPrototypes["clothing.sneakers_canvas"] = "clothing.sneakers_canvas";
        GarmentPrototypes["clothing.sneakers_canvas_rs5_mat02"] = "clothing.sneakers_canvas";
        GarmentPrototypes["clothing.sneakers_canvas_rs5_mat03"] = "clothing.sneakers_canvas";
        GarmentPrototypes["clothing.sneakers_nerd"] = "clothing.sneakers_nerd";
        GarmentPrototypes["clothing.sneakers_nerd_nc_sneakers_pink"] = "clothing.sneakers_nerd";
        GarmentPrototypes["clothing.sneakers_nerd_nc_sneakers_purple"] = "clothing.sneakers_nerd";
        GarmentPrototypes["clothing.sneakers_sport"] = "clothing.sneakers_sport";
        GarmentPrototypes["clothing.sneakers_sport_cream_white"] = "clothing.sneakers_sport";
        GarmentPrototypes["clothing.sneakers_sport_lilac_purple"] = "clothing.sneakers_sport";
        GarmentPrototypes["clothing.suit_bandaid"] = "clothing.suit_bandaid";
        GarmentPrototypes["clothing.suit_bandaid_bs_bluemesh"] = "clothing.suit_bandaid";
        GarmentPrototypes["clothing.suit_bandaid_bs_blueshine"] = "clothing.suit_bandaid";
        GarmentPrototypes["clothing.suit_bandaid_bs_pinkmesh"] = "clothing.suit_bandaid";
        GarmentPrototypes["clothing.suit_bandaid_bs_pinkshine"] = "clothing.suit_bandaid";
        GarmentPrototypes["clothing.suit_bandaid_bs_purplemesh"] = "clothing.suit_bandaid";
        GarmentPrototypes["clothing.suit_bandaid_bs_purpleshine"] = "clothing.suit_bandaid";
        GarmentPrototypes["clothing.sunglasses_luxury"] = "clothing.sunglasses_luxury";
        GarmentPrototypes["clothing.sunglasses_luxury_animal_print_color"] = "clothing.sunglasses_luxury";
        GarmentPrototypes["clothing.sunglasses_luxury_burgundy_color"] = "clothing.sunglasses_luxury";
        GarmentPrototypes["clothing.sunglasses_luxury_light_peach_color"] = "clothing.sunglasses_luxury";
        GarmentPrototypes["clothing.sunglasses_luxury_rainbow_color"] = "clothing.sunglasses_luxury";
        GarmentPrototypes["clothing.sunglasses_luxury_sunset_color"] = "clothing.sunglasses_luxury";
        GarmentPrototypes["clothing.sunglasses_luxury_with_color"] = "clothing.sunglasses_luxury";
        GarmentPrototypes["clothing.sweater_flair"] = "clothing.sweater_flair";
        GarmentPrototypes["clothing.sweater_flair_sweater_02"] = "clothing.sweater_flair";
        GarmentPrototypes["clothing.sweater_flair_sweater_03"] = "clothing.sweater_flair";
        GarmentPrototypes["clothing.sweater_naughty"] = "clothing.sweater_naughty";
        GarmentPrototypes["clothing.sweater_naughty_sweater_2"] = "clothing.sweater_naughty";
        GarmentPrototypes["clothing.sweater_naughty_sweater_3"] = "clothing.sweater_naughty";
        GarmentPrototypes["clothing.sweater_naughty_sweater_4"] = "clothing.sweater_naughty";
        GarmentPrototypes["clothing.sweater_naughty_sweater_5"] = "clothing.sweater_naughty";
        GarmentPrototypes["clothing.sweater_naughty_sweater_6"] = "clothing.sweater_naughty";
        GarmentPrototypes["clothing.tank_jane"] = "clothing.tank_jane";
        GarmentPrototypes["clothing.tank_jane_black"] = "clothing.tank_jane";
        GarmentPrototypes["clothing.tank_jane_blues"] = "clothing.tank_jane";
        GarmentPrototypes["clothing.tank_jane_lavendar"] = "clothing.tank_jane";
        GarmentPrototypes["clothing.tank_jane_reds"] = "clothing.tank_jane";
        GarmentPrototypes["clothing.tank_jane_rosey"] = "clothing.tank_jane";
        GarmentPrototypes["clothing.tanktop_summer"] = "clothing.tanktop_summer";
        GarmentPrototypes["clothing.tanktop_summer_i13sw_tank_02"] = "clothing.tanktop_summer";
        GarmentPrototypes["clothing.tanktop_summer_i13sw_tank_03"] = "clothing.tanktop_summer";
        GarmentPrototypes["clothing.tanktop_summer_i13sw_tank_04"] = "clothing.tanktop_summer";
        GarmentPrototypes["clothing.tanktop_summer_i13sw_tank_05"] = "clothing.tanktop_summer";
        GarmentPrototypes["clothing.thighboots_amy"] = "clothing.thighboots_amy";
        GarmentPrototypes["clothing.top_alloy"] = "clothing.top_alloy";
        GarmentPrototypes["clothing.top_anarchy"] = "clothing.top_anarchy";
        GarmentPrototypes["clothing.top_anarchy_bra_purple"] = "clothing.top_anarchy";
        GarmentPrototypes["clothing.top_anarchy_bra_red"] = "clothing.top_anarchy";
        GarmentPrototypes["clothing.top_anarchy_red"] = "clothing.top_anarchy";
        GarmentPrototypes["clothing.top_classic"] = "clothing.top_classic";
        GarmentPrototypes["clothing.top_deadly"] = "clothing.top_deadly";
        GarmentPrototypes["clothing.top_fighter"] = "clothing.top_fighter";
        GarmentPrototypes["clothing.top_folk"] = "clothing.top_folk";
        GarmentPrototypes["clothing.top_osiris"] = "clothing.top_osiris";
        GarmentPrototypes["clothing.top_primal"] = "clothing.top_primal";
        GarmentPrototypes["clothing.top_primal_1"] = "clothing.top_primal";
        GarmentPrototypes["clothing.top_primal_2"] = "clothing.top_primal";
        GarmentPrototypes["clothing.top_primal_3"] = "clothing.top_primal";
        GarmentPrototypes["clothing.top_stars"] = "clothing.top_stars";
        GarmentPrototypes["clothing.top_tod"] = "clothing.top_tod";
        GarmentPrototypes["clothing.top_yoga"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_01_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_02_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_03_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_04_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_05_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_06_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_07_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_08_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_09_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.top_yoga_10_top"] = "clothing.top_yoga";
        GarmentPrototypes["clothing.tshirt_big"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_bt_charcoal"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_bt_dusty_blue"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_bt_dusty_pink"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_bt_dusty_purple"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_bt_grey"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_bt_red"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_bt_sky_blue"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_bt_white"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_american"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_born_to_ride"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_california"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_denim"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_eagles"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_heartbreaker"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_lost_angels"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_queen"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_racing"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_big_tshirt_saints"] = "clothing.tshirt_big";
        GarmentPrototypes["clothing.tshirt_summer"] = "clothing.tshirt_summer";
        GarmentPrototypes["clothing.tshirt_summer_i13sw_tshirt_02"] = "clothing.tshirt_summer";
        GarmentPrototypes["clothing.tshirt_summer_i13sw_tshirt_03"] = "clothing.tshirt_summer";
        GarmentPrototypes["clothing.tshirt_summer_i13sw_tshirt_04"] = "clothing.tshirt_summer";
        GarmentPrototypes["clothing.tshirt_summer_i13sw_tshirt_05"] = "clothing.tshirt_summer";
        GarmentPrototypes["clothing.tutu_nerd"] = "clothing.tutu_nerd";
        GarmentPrototypes["clothing.vest_ranger"] = "clothing.vest_ranger";
        GarmentPrototypes["clothing.vest_ranger_green"] = "clothing.vest_ranger";
        GarmentPrototypes["clothing.vest_ranger_packs"] = "clothing.vest_ranger";
        GarmentPrototypes["clothing.vest_stars"] = "clothing.vest_stars";
        GarmentPrototypes["clothing.wrapboots_primal"] = "clothing.wrapboots_primal";
        GarmentPrototypes["clothing.wrapboots_primal_1"] = "clothing.wrapboots_primal";
        GarmentPrototypes["clothing.wrapboots_primal_2"] = "clothing.wrapboots_primal";
        GarmentPrototypes["clothing.wrapboots_primal_3"] = "clothing.wrapboots_primal";
        GarmentPrototypes["clothing.yogapants_tek"] = "clothing.yogapants_tek";
        GarmentPrototypes["clothing.yogapants_tek_yoga_01_black_mesh"] = "clothing.yogapants_tek";
        GarmentPrototypes["clothing.yogapants_tek_yoga_02_black"] = "clothing.yogapants_tek";
        GarmentPrototypes["clothing.yogapants_tek_yoga_03_black_red"] = "clothing.yogapants_tek";
        GarmentPrototypes["clothing.yogapants_tek_yoga_04_black_orange"] = "clothing.yogapants_tek";
        GarmentPrototypes["clothing.yogapants_tek_yoga_05_black_pink"] = "clothing.yogapants_tek";
        GarmentPrototypes["clothing.yogapants_tek_yoga_06_black_blue"] = "clothing.yogapants_tek";
        GarmentPrototypes["clothing.yogapants_tek_yoga_07_black_plain"] = "clothing.yogapants_tek";
        GarmentPrototypes["clothing.yogapants_tek_yoga_08_white_plain"] = "clothing.yogapants_tek";
        GarmentPrototypes["gear.backpack_osiris"] = "gear.backpack_osiris";
        GarmentPrototypes["gear.backpack_riot"] = "gear.backpack_riot";
        GarmentPrototypes["gear.backpack_riot_denim"] = "gear.backpack_riot";
        GarmentPrototypes["gear.backpack_riot_grey"] = "gear.backpack_riot";
        GarmentPrototypes["gear.backpack_riot_leather"] = "gear.backpack_riot";
        GarmentPrototypes["gear.beltpouch_ranger"] = "gear.beltpouch_ranger";
        GarmentPrototypes["underwear.bikini_briefs"] = "underwear.bikini_briefs";
        GarmentPrototypes["underwear.bikini_briefs_mat01"] = "underwear.bikini_briefs";
        GarmentPrototypes["underwear.bikini_briefs_mat02"] = "underwear.bikini_briefs";
        GarmentPrototypes["underwear.bikini_briefs_mat03"] = "underwear.bikini_briefs";
        GarmentPrototypes["underwear.bikini_briefs_mat04"] = "underwear.bikini_briefs";
        GarmentPrototypes["underwear.bikini_briefs_mat05"] = "underwear.bikini_briefs";
        GarmentPrototypes["underwear.bikini_top"] = "underwear.bikini_top";
        GarmentPrototypes["underwear.bikini_top_mat01"] = "underwear.bikini_top";
        GarmentPrototypes["underwear.bikini_top_mat02"] = "underwear.bikini_top";
        GarmentPrototypes["underwear.bikini_top_mat03"] = "underwear.bikini_top";
        GarmentPrototypes["underwear.bikini_top_mat04"] = "underwear.bikini_top";
        GarmentPrototypes["underwear.bikini_top_mat05"] = "underwear.bikini_top";
        GarmentPrototypes["underwear.bra_lace"] = "underwear.bra_lace";
        GarmentPrototypes["underwear.bra_lace_dream_lace_3_bra_black"] = "underwear.bra_lace";
        GarmentPrototypes["underwear.bra_openback"] = "underwear.bra_openback";
        GarmentPrototypes["underwear.bra_openback_bra_2"] = "underwear.bra_openback";
        GarmentPrototypes["underwear.bra_openback_bra_3"] = "underwear.bra_openback";
        GarmentPrototypes["underwear.bra_openback_bra_4"] = "underwear.bra_openback";
        GarmentPrototypes["underwear.bra_openback_bra_5"] = "underwear.bra_openback";
        GarmentPrototypes["underwear.bra_openback_bra_6"] = "underwear.bra_openback";
        GarmentPrototypes["underwear.bra_riot"] = "underwear.bra_riot";
        GarmentPrototypes["underwear.bra_riot_blue"] = "underwear.bra_riot";
        GarmentPrototypes["underwear.bra_riot_pink"] = "underwear.bra_riot";
        GarmentPrototypes["underwear.bra_riot_red"] = "underwear.bra_riot";
        GarmentPrototypes["underwear.bra_strappy"] = "underwear.bra_strappy";
        GarmentPrototypes["underwear.bra_strappy_mat01"] = "underwear.bra_strappy";
        GarmentPrototypes["underwear.bra_strappy_mat02"] = "underwear.bra_strappy";
        GarmentPrototypes["underwear.bra_strappy_mat03"] = "underwear.bra_strappy";
        GarmentPrototypes["underwear.bra_strappy_mat04"] = "underwear.bra_strappy";
        GarmentPrototypes["underwear.bra_wild"] = "underwear.bra_wild";
        GarmentPrototypes["underwear.briefs_cindy"] = "underwear.briefs_cindy";
        GarmentPrototypes["underwear.briefs_flair"] = "underwear.briefs_flair";
        GarmentPrototypes["underwear.briefs_flair_panty_02"] = "underwear.briefs_flair";
        GarmentPrototypes["underwear.briefs_flair_panty_03"] = "underwear.briefs_flair";
        GarmentPrototypes["underwear.briefs_lace"] = "underwear.briefs_lace";
        GarmentPrototypes["underwear.briefs_lace_dream_lace_3_panty_black"] = "underwear.briefs_lace";
        GarmentPrototypes["underwear.briefs_lace_dream_lace_3_panty_blue1"] = "underwear.briefs_lace";
        GarmentPrototypes["underwear.briefs_lace_dream_lace_3_panty_blue2"] = "underwear.briefs_lace";
        GarmentPrototypes["underwear.briefs_luxury"] = "underwear.briefs_luxury";
        GarmentPrototypes["underwear.briefs_luxury_02_cherry_panties"] = "underwear.briefs_luxury";
        GarmentPrototypes["underwear.briefs_luxury_03_white_panties"] = "underwear.briefs_luxury";
        GarmentPrototypes["underwear.briefs_luxury_04_black_panties"] = "underwear.briefs_luxury";
        GarmentPrototypes["underwear.briefs_luxury_05_golden_panties"] = "underwear.briefs_luxury";
        GarmentPrototypes["underwear.briefs_openback"] = "underwear.briefs_openback";
        GarmentPrototypes["underwear.briefs_openback_panties_2"] = "underwear.briefs_openback";
        GarmentPrototypes["underwear.briefs_openback_panties_3"] = "underwear.briefs_openback";
        GarmentPrototypes["underwear.briefs_openback_panties_4"] = "underwear.briefs_openback";
        GarmentPrototypes["underwear.briefs_openback_panties_5"] = "underwear.briefs_openback";
        GarmentPrototypes["underwear.briefs_openback_panties_6"] = "underwear.briefs_openback";
        GarmentPrototypes["underwear.briefs_plain"] = "underwear.briefs_plain";
        GarmentPrototypes["underwear.briefs_plain_mat01"] = "underwear.briefs_plain";
        GarmentPrototypes["underwear.briefs_plain_mat02"] = "underwear.briefs_plain";
        GarmentPrototypes["underwear.briefs_plain_mat03"] = "underwear.briefs_plain";
        GarmentPrototypes["underwear.briefs_plain_mat04"] = "underwear.briefs_plain";
        GarmentPrototypes["underwear.briefs_plain_shima01"] = "underwear.briefs_plain";
        GarmentPrototypes["underwear.briefs_plain_shima02"] = "underwear.briefs_plain";
        GarmentPrototypes["underwear.briefs_plain_shima03"] = "underwear.briefs_plain";
        GarmentPrototypes["underwear.briefs_plain_shima04"] = "underwear.briefs_plain";
        GarmentPrototypes["underwear.briefs_primal"] = "underwear.briefs_primal";
        GarmentPrototypes["underwear.briefs_primal_panty1"] = "underwear.briefs_primal";
        GarmentPrototypes["underwear.briefs_primal_panty2"] = "underwear.briefs_primal";
        GarmentPrototypes["underwear.briefs_primal_panty3"] = "underwear.briefs_primal";
        GarmentPrototypes["underwear.briefs_sport"] = "underwear.briefs_sport";
        GarmentPrototypes["underwear.briefs_sport_panties_2"] = "underwear.briefs_sport";
        GarmentPrototypes["underwear.briefs_sport_panties_3"] = "underwear.briefs_sport";
        GarmentPrototypes["underwear.briefs_sport_panties_4"] = "underwear.briefs_sport";
        GarmentPrototypes["underwear.briefs_sport_panties_5"] = "underwear.briefs_sport";
        GarmentPrototypes["underwear.briefs_sport_panties_6"] = "underwear.briefs_sport";
        GarmentPrototypes["underwear.briefs_strappy"] = "underwear.briefs_strappy";
        GarmentPrototypes["underwear.briefs_strappy_mat01"] = "underwear.briefs_strappy";
        GarmentPrototypes["underwear.briefs_strappy_mat02"] = "underwear.briefs_strappy";
        GarmentPrototypes["underwear.briefs_strappy_mat03"] = "underwear.briefs_strappy";
        GarmentPrototypes["underwear.briefs_strappy_mat04"] = "underwear.briefs_strappy";
        GarmentPrototypes["underwear.briefs_sweety"] = "underwear.briefs_sweety";
        GarmentPrototypes["underwear.briefs_sweety_01panty"] = "underwear.briefs_sweety";
        GarmentPrototypes["underwear.briefs_sweety_02_panty"] = "underwear.briefs_sweety";
        GarmentPrototypes["underwear.briefs_sweety_03_panty"] = "underwear.briefs_sweety";
        GarmentPrototypes["underwear.briefs_sweety_04panty"] = "underwear.briefs_sweety";
        GarmentPrototypes["underwear.briefs_sweety_05panty"] = "underwear.briefs_sweety";
        GarmentPrototypes["underwear.briefs_sweety_06panty"] = "underwear.briefs_sweety";
        GarmentPrototypes["underwear.briefs_sweety_07panty"] = "underwear.briefs_sweety";
        GarmentPrototypes["underwear.briefs_sweety_08panty"] = "underwear.briefs_sweety";
        GarmentPrototypes["underwear.briefs_teez"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_teez_bt_panty_charcoal"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_teez_bt_panty_dark_grey"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_teez_bt_panty_dusty_blue"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_teez_bt_panty_dusty_pink"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_teez_bt_panty_dusty_purple"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_teez_bt_panty_grey"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_teez_bt_panty_red"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_teez_bt_panty_sky_blue"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_teez_bt_panty_white"] = "underwear.briefs_teez";
        GarmentPrototypes["underwear.briefs_vapor"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot01"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot02"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot03"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot04"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot05"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot06"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot07"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot08"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot09"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot10"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot11"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_vapor_bot12"] = "underwear.briefs_vapor";
        GarmentPrototypes["underwear.briefs_wild"] = "underwear.briefs_wild";
        GarmentPrototypes["underwear.kneesocks_nerd"] = "underwear.kneesocks_nerd";
        GarmentPrototypes["underwear.kneesocks_nerd_nc_sock_left_argyle"] = "underwear.kneesocks_nerd";
        GarmentPrototypes["underwear.kneesocks_nerd_nc_sock_left_polka_dots"] = "underwear.kneesocks_nerd";
        GarmentPrototypes["underwear.kneesocks_nerd_nc_sock_right_hearts"] = "underwear.kneesocks_nerd";
        GarmentPrototypes["underwear.kneesocks_nerd_nc_sock_right_polka_dots"] = "underwear.kneesocks_nerd";
        GarmentPrototypes["underwear.panty_tod"] = "underwear.panty_tod";
        GarmentPrototypes["underwear.socks_fit"] = "underwear.socks_fit";
        GarmentPrototypes["underwear.socks_fit_blue"] = "underwear.socks_fit";
        GarmentPrototypes["underwear.socks_fit_blue2"] = "underwear.socks_fit";
        GarmentPrototypes["underwear.sportsbra_jmr"] = "underwear.sportsbra_jmr";
        GarmentPrototypes["underwear.sportsbra_jmr_bra_2"] = "underwear.sportsbra_jmr";
        GarmentPrototypes["underwear.sportsbra_jmr_bra_3"] = "underwear.sportsbra_jmr";
        GarmentPrototypes["underwear.sportsbra_jmr_bra_4"] = "underwear.sportsbra_jmr";
        GarmentPrototypes["underwear.sportsbra_jmr_bra_5"] = "underwear.sportsbra_jmr";
        GarmentPrototypes["underwear.sportsbra_jmr_bra_6"] = "underwear.sportsbra_jmr";
        GarmentPrototypes["underwear.sportsbra_tek"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_01_apply_black_bottom_trim"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_01_apply_black_trim"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_01_apply_white_bottom_trim"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_01_apply_white_trim"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_02_black"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_03_orange_black"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_04_red_black"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_05_black_pink"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_06_blue_black"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_07_blue_black_orange"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.sportsbra_tek_bra_08_white"] = "underwear.sportsbra_tek";
        GarmentPrototypes["underwear.stockings_overknee"] = "underwear.stockings_overknee";
        GarmentPrototypes["underwear.stockings_overknee_over_knee_2"] = "underwear.stockings_overknee";
        GarmentPrototypes["underwear.stockings_overknee_over_knee_3"] = "underwear.stockings_overknee";
        GarmentPrototypes["underwear.stockings_overknee_over_knee_4"] = "underwear.stockings_overknee";
        GarmentPrototypes["underwear.stockings_overknee_over_knee_5"] = "underwear.stockings_overknee";
        GarmentPrototypes["underwear.stockings_overknee_over_knee_6"] = "underwear.stockings_overknee";
        GarmentPrototypes["underwear.stockings_riot"] = "underwear.stockings_riot";
        GarmentPrototypes["underwear.stockings_riot_clear"] = "underwear.stockings_riot";
        GarmentPrototypes["underwear.stockings_spooky"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_01"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_02"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_03"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_04"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_05"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_06"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_07"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_08"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_09"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_10"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_11"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_13"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_14"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_15"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_16"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_17"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_spooky_18"] = "underwear.stockings_spooky";
        GarmentPrototypes["underwear.stockings_tod"] = "underwear.stockings_tod";
        GarmentPrototypes["underwear.swimsuit_briefs"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_2"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_3"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_4"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_5"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_6"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_black"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_blue"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_green"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_pink"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_red"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_briefs_bottom_white"] = "underwear.swimsuit_briefs";
        GarmentPrototypes["underwear.swimsuit_top"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_blue"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_green"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_pink"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_red"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_1"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_2"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_3"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_4"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_5"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_6"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_black"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_blue"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_green"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_pink"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_red"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_top_white"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.swimsuit_top_white"] = "underwear.swimsuit_top";
        GarmentPrototypes["underwear.thong_anarchy"] = "underwear.thong_anarchy";
        GarmentPrototypes["underwear.thong_anarchy_purple"] = "underwear.thong_anarchy";
        GarmentPrototypes["underwear.tights_deadly"] = "underwear.tights_deadly";
        GarmentPrototypes["underwear.top_cindy"] = "underwear.top_cindy";
        GarmentPrototypes["underwear.top_luxury"] = "underwear.top_luxury";
        GarmentPrototypes["underwear.top_luxury_02_cherry_bra"] = "underwear.top_luxury";
        GarmentPrototypes["underwear.top_luxury_03_white_bra"] = "underwear.top_luxury";
        GarmentPrototypes["underwear.top_luxury_04_black_bra"] = "underwear.top_luxury";
        GarmentPrototypes["underwear.top_luxury_05_golden_bra"] = "underwear.top_luxury";
        GarmentPrototypes["underwear.top_vapor"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_01"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_02"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_03"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_04"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_05"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_06"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_07"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_08"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_09"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_10"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_11"] = "underwear.top_vapor";
        GarmentPrototypes["underwear.top_vapor_12"] = "underwear.top_vapor";
        foreach(var profile in Profiles.Values) MaximumRadiusXZ=Math.Max(MaximumRadiusXZ,profile.FullBounds.RadiusXZ);
        foreach(var profile in Garments.Values) MaximumRadiusXZ=Math.Max(MaximumRadiusXZ,profile.FullBounds.RadiusXZ);
    }
    public static bool IsGarment(string id) => GarmentPrototypes.ContainsKey(id);
    public static int Capacity(string id) => InventoryState.IsStackable(id)?InventoryState.StackSizeFor(id):1;
    public static bool TryGet(string id,bool garment,out GroundPileProfile profile)
    {
        // These maps are built once by the type initializer, then only read.
        // No query-time registration, locks, global content scans or mutation.
        profile = null;
        if (garment || IsGarment(id))
            return GarmentPrototypes.TryGetValue(id,out var prototype) && Garments.TryGetValue(prototype,out profile);
        return Profiles.TryGetValue(id,out profile);
    }

        public static float TargetWorldSize(string definitionId)
        {
            var r = HexSpatialMath.HexRadius;
            // §54.2: PalmTreeFactory returns before generic fitting; palm_final
            // keeps its authored 1:1 dimensions.
            if (definitionId.Contains("tree")) return r * 2.2f;
            if (definitionId.Contains("bed")) return r * 0.95f;
            // A meat chunk reads bigger than a coconut half — 1.5× the standard
            // food size. Ground, hand and the roasting spit all share this.
            if (definitionId == "food.meat_raw" || definitionId == "food.meat_cooked") return r * 0.18f;
            if (definitionId.StartsWith("food.")) return r * 0.12f;
            // Spec §54.2: a log/stick is a full palm-trunk segment long (big, like
            // Stranded Deep) — measured by its long axis; the crown and leaf are
            // sized to sit with the palm.
            if (definitionId == "resource.log" || definitionId == "resource.stick") return r * 0.7f;
            // §119.1: доска — распущенное бревно (saw.log даёт две штуки), а не
            // ручной инструмент. На общей ручке для resource.* (0.216) она лежала
            // в траве щепкой втрое короче бревна: модель рисовалась, но игрок её
            // не находил. 0.5 — заметно меньше бревна и всё же доска.
            if (definitionId == "resource.board") return r * 0.5f;
            if (definitionId == "resource.palm_crown") return r * 0.7f;
            if (definitionId == "resource.palm_leaf") return r * 0.55f;
            // The spear is a long two-handed weapon — much longer than a hand tool.
            if (definitionId == "tool.spear") return r * 0.9f;
            // A rope coil is a small bundle — slightly smaller than a coconut
            // half (food.* renders at 0.12), not tool-sized.
            if (definitionId == "resource.rope") return r * 0.10f;
            // A lighter is a tiny pocket object — 1/3 of the standard tool size,
            // applied to BOTH ground and hand (0.216 / 3).
            if (definitionId == "tool.lighter") return r * 0.072f;
            // A one-litre bottle is shorter than a hand tool. Keep this in the
            // shared fit table so the ground and hand cannot drift apart again.
            if (definitionId == "tool.bottle") return r * 0.18f;
            // Tools & resources: 0.216 = the standard hand/ground tool size
            // (was 0.18; +20% after in-hand testing, applied to BOTH paths).
            if (definitionId.StartsWith("tool.") || definitionId.StartsWith("resource.")) return r * 0.216f;
            // Small carried items (item.bandage…) are pocket-sized, like food.
            // Without this they fell to the 0.6 default and a bandage roll
            // rendered campfire-big on the ground and in hand.
            if (definitionId.StartsWith("item.")) return r * 0.12f;
            // Bug #341: med.splint is a small carried medical prop. The med.* id
            // used to miss every category and fall through to the 0.6 default,
            // making the one-metre source mesh five times too large everywhere.
            if (definitionId == "med.splint") return r * 0.12f;
            if (definitionId == "campfire.spot") return r * 0.55f;
            if (definitionId == "grave.npc") return r * 0.35f;
            if (definitionId == "rock.boulder") return r * 0.45f;
            // §35.5B/§54.15: station.drying_rack and station.water_collector are
            // NOT sized here — drying_rack_final / water_collector_final are
            // authored 1:1 like the beds and rendered via BedAssembly, whose
            // renderer branch returns before FitObjectPrefab ever runs. Adding
            // a factor here would be dead code today and a double-scale the
            // day that branch changes.
            if (definitionId == "forest.deadfall" ||
                definitionId == "construction.site") return r * 0.7f;
            return r * 0.6f;
        }

}
}
