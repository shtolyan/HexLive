using System;

namespace HexLive.Simulation.Runtime.Blueprints
{
    /// <summary>
    /// Building plans the game may raise, frozen into the assembly.
    ///
    /// The constructor writes a player's draft to
    /// <c>persistentDataPath/HexLive/BlueprintDrafts/*.json</c>, which is a
    /// DRAFT: it lives on one machine, outside the repository, and a headless
    /// run or a server has no access to it. A plan colonists actually build has
    /// to be committed data, exactly like the canonical hut is committed code.
    /// The promotion is a copy of the draft's own JSON — no hand-retyped
    /// coordinates, so display rounding can never become new geometry.
    /// </summary>
    public static class CommittedBuildingPlans
    {
        public const string PlayerHutId = "hut_player_v1";

        private static BuildingBlueprintDraft _playerHut;
        private static BuildingElementBlueprint[] _playerHutModules;

        /// <summary>The player-authored three-hex house, approved 2026-08-15.</summary>
        public static BuildingBlueprintDraft PlayerHut =>
            _playerHut ??= Parse(PlayerHutJson);

        public static System.Collections.Generic.IReadOnlyList<BuildingElementBlueprint> PlayerHutModules =>
            _playerHutModules ??= System.Linq.Enumerable.ToArray(
                BlueprintBuildingPlan.Modules(PlayerHut));

        public static BuildingBlueprintDraft ById(string blueprintId) =>
            blueprintId == PlayerHutId ? PlayerHut : null;

        private static BuildingBlueprintDraft Parse(string json)
        {
            if (!BuildingBlueprintJson.TryDeserialize(json, out var draft, out var error))
                throw new InvalidOperationException($"Committed building plan is unreadable: {error}");
            return draft;
        }

        private const string PlayerHutJson =
        @"{""version"":2,""blueprintId"":""hut_player_v1"",""nextElementId"":183,""nextRoomId"":2,""elements"":[{""id"":""e00" +
        @"025"",""kind"":""support"",""origin"":""manual"",""roomId"":0,""node"":{""q"":0,""r"":3}},{""id"":""e00026"",""kind"":""supp" +
        @"ort"",""origin"":""manual"",""roomId"":0,""node"":{""q"":3,""r"":0}},{""id"":""e00027"",""kind"":""support"",""origin"":""ma" +
        @"nual"",""roomId"":0,""node"":{""q"":3,""r"":-3}},{""id"":""e00028"",""kind"":""support"",""origin"":""manual"",""roomId"":0" +
        @",""node"":{""q"":0,""r"":-3}},{""id"":""e00029"",""kind"":""support"",""origin"":""manual"",""roomId"":0,""node"":{""q"":-3," +
        @"""r"":0}},{""id"":""e00030"",""kind"":""support"",""origin"":""manual"",""roomId"":0,""node"":{""q"":-3,""r"":3}},{""id"":""e" +
        @"00172"",""kind"":""support"",""origin"":""roomBoundary"",""roomId"":1,""node"":{""q"":0,""r"":6}},{""id"":""e00138"",""kin" +
        @"d"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSector"":{""q"":0,""r"":0,""sector"":0}},{""id"":""e00139""" +
        @",""kind"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSector"":{""q"":0,""r"":0,""sector"":1}},{""id"":""e0" +
        @"0140"",""kind"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSector"":{""q"":0,""r"":0,""sector"":2}},{""id" +
        @""":""e00141"",""kind"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSector"":{""q"":0,""r"":0,""sector"":3}}" +
        @",{""id"":""e00142"",""kind"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSector"":{""q"":0,""r"":0,""sector" +
        @""":4}},{""id"":""e00143"",""kind"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSector"":{""q"":0,""r"":0,""s" +
        @"ector"":5}},{""id"":""e00144"",""kind"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSector"":{""q"":0,""r""" +
        @":1,""sector"":3}},{""id"":""e00145"",""kind"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSector"":{""q"":" +
        @"-1,""r"":1,""sector"":2}},{""id"":""e00146"",""kind"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSector""" +
        @":{""q"":0,""r"":1,""sector"":4}},{""id"":""e00147"",""kind"":""floorSector"",""origin"":""manual"",""roomId"":1,""floorSe" +
        @"ctor"":{""q"":-1,""r"":1,""sector"":1}},{""id"":""e00071"",""kind"":""wall"",""origin"":""manual"",""roomId"":0,""segment""" +
        @":{""a"":{""q"":-1,""r"":-2},""b"":{""q"":0,""r"":-3}}},{""id"":""e00181"",""kind"":""wall"",""origin"":""manual"",""roomId"":0" +
        @",""segment"":{""a"":{""q"":-3,""r"":4},""b"":{""q"":-3,""r"":5}}},{""id"":""e00182"",""kind"":""wall"",""origin"":""manual"",""" +
        @"roomId"":0,""segment"":{""a"":{""q"":3,""r"":1},""b"":{""q"":3,""r"":2}}},{""id"":""e00148"",""kind"":""wall"",""origin"":""ro" +
        @"omBoundary"",""roomId"":1,""segment"":{""a"":{""q"":-3,""r"":0},""b"":{""q"":-3,""r"":1}}},{""id"":""e00149"",""kind"":""wal" +
        @"l"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":-3,""r"":0},""b"":{""q"":-2,""r"":-1}}},{""id"":""e00" +
        @"150"",""kind"":""wall"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":-3,""r"":1},""b"":{""q"":-3,""r"":" +
        @"2}}},{""id"":""e00153"",""kind"":""wall"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":-3,""r"":5},""" +
        @"b"":{""q"":-3,""r"":6}}},{""id"":""e00154"",""kind"":""wall"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{" +
        @"""q"":-3,""r"":6},""b"":{""q"":-2,""r"":6}}},{""id"":""e00156"",""kind"":""wall"",""origin"":""roomBoundary"",""roomId"":1,""" +
        @"segment"":{""a"":{""q"":-1,""r"":6},""b"":{""q"":0,""r"":6}}},{""id"":""e00157"",""kind"":""wall"",""origin"":""roomBoundary" +
        @""",""roomId"":1,""segment"":{""a"":{""q"":0,""r"":-3},""b"":{""q"":1,""r"":-3}}},{""id"":""e00158"",""kind"":""wall"",""origin" +
        @""":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":0,""r"":6},""b"":{""q"":1,""r"":5}}},{""id"":""e00161"",""kind"":""" +
        @"wall"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":2,""r"":-3},""b"":{""q"":3,""r"":-3}}},{""id"":""e" +
        @"00162"",""kind"":""wall"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":2,""r"":4},""b"":{""q"":3,""r"":" +
        @"3}}},{""id"":""e00163"",""kind"":""wall"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":3,""r"":-3},""" +
        @"b"":{""q"":3,""r"":-2}}},{""id"":""e00164"",""kind"":""wall"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{" +
        @"""q"":3,""r"":-2},""b"":{""q"":3,""r"":-1}}},{""id"":""e00168"",""kind"":""wall"",""origin"":""roomBoundary"",""roomId"":1,""" +
        @"segment"":{""a"":{""q"":3,""r"":2},""b"":{""q"":3,""r"":3}}},{""id"":""e00072"",""kind"":""window"",""origin"":""manual"",""ro" +
        @"omId"":0,""segment"":{""a"":{""q"":-3,""r"":2},""b"":{""q"":-3,""r"":3}}},{""id"":""e00151"",""kind"":""window"",""origin"":""" +
        @"roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":-3,""r"":3},""b"":{""q"":-3,""r"":4}}},{""id"":""e00155"",""kind"":""w" +
        @"indow"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":-2,""r"":6},""b"":{""q"":-1,""r"":6}}},{""id"":""" +
        @"e00159"",""kind"":""window"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":1,""r"":-3},""b"":{""q"":2," +
        @"""r"":-3}}},{""id"":""e00160"",""kind"":""window"",""origin"":""roomBoundary"",""roomId"":1,""segment"":{""a"":{""q"":1,""r" +
        @""":5},""b"":{""q"":2,""r"":4}}},{""id"":""e00165"",""kind"":""window"",""origin"":""roomBoundary"",""roomId"":1,""segment""" +
        @":{""a"":{""q"":3,""r"":-1},""b"":{""q"":3,""r"":0}}},{""id"":""e00166"",""kind"":""window"",""origin"":""roomBoundary"",""roo" +
        @"mId"":1,""segment"":{""a"":{""q"":3,""r"":0},""b"":{""q"":3,""r"":1}}},{""id"":""e00073"",""kind"":""door"",""origin"":""manua" +
        @"l"",""roomId"":0,""segment"":{""a"":{""q"":-2,""r"":-1},""b"":{""q"":-1,""r"":-2}}},{""id"":""e00031"",""kind"":""roofSector" +
        @""",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":0,""r"":0,""sector"":0}},{""id"":""e00032"",""kind"":""roofSec" +
        @"tor"",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":0,""r"":0,""sector"":1}},{""id"":""e00033"",""kind"":""roof" +
        @"Sector"",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":0,""r"":0,""sector"":2}},{""id"":""e00034"",""kind"":""r" +
        @"oofSector"",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":0,""r"":0,""sector"":3}},{""id"":""e00035"",""kind""" +
        @":""roofSector"",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":0,""r"":0,""sector"":4}},{""id"":""e00036"",""ki" +
        @"nd"":""roofSector"",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":0,""r"":0,""sector"":5}},{""id"":""e00176""," +
        @"""kind"":""roofSector"",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":0,""r"":1,""sector"":3}},{""id"":""e0017" +
        @"7"",""kind"":""roofSector"",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":-1,""r"":1,""sector"":1}},{""id"":""e" +
        @"00178"",""kind"":""roofSector"",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":-1,""r"":1,""sector"":2}},{""id" +
        @""":""e00179"",""kind"":""roofSector"",""origin"":""manual"",""roomId"":0,""roofSector"":{""q"":0,""r"":1,""sector"":4}}]," +
        @"""furniture"":[{""id"":""f00037"",""definitionId"":""bed.basic"",""tileQ"":-1,""tileR"":1,""junctionSlot"":1,""yawSte" +
        @"p"":0},{""id"":""f00038"",""definitionId"":""bed.basic"",""tileQ"":0,""tileR"":1,""junctionSlot"":23,""yawStep"":1},{" +
        @"""id"":""f00039"",""definitionId"":""furniture.hearth"",""tileQ"":0,""tileR"":0,""junctionSlot"":36,""yawStep"":2},{" +
        @"""id"":""f00040"",""definitionId"":""furniture.wardrobe"",""tileQ"":0,""tileR"":0,""junctionSlot"":2,""yawStep"":5}," +
        @"{""id"":""f00180"",""definitionId"":""bed.basic"",""tileQ"":0,""tileR"":0,""junctionSlot"":14,""yawStep"":0}]}";
    }
}
