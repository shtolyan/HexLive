using System;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime.Blueprints
{
    public static class BuiltInBuildingBlueprints
    {
        public static BuildingBlueprintDraft Hut1Hex()
        {
            var draft = new BuildingBlueprintDraft { BlueprintId = "hut_1hex" };
            var floors = Enumerable.Range(0, 6)
                .Select(sector => new FloorSectorKey(TileCoord.Zero, sector)).ToArray();
            Require(BlueprintEditorCommands.CreateRoom(draft, floors));

            // The approved hut keeps broad windows on two opposite sides and
            // uses the only portal-capable (middle) 0.5-wu segment for its door.
            foreach (var edge in new[] { 1, 5 })
            {
                var edgeSegments = EdgeSegments(TileCoord.Zero, edge);
                foreach (var segment in edgeSegments)
                    Require(BlueprintEditorCommands.PlaceOpening(draft, segment, BlueprintElementKind.Window));
            }
            Require(BlueprintEditorCommands.PlaceOpening(
                draft, EdgeSegments(TileCoord.Zero, 3)[1], BlueprintElementKind.Door));

            for (var corner = 0; corner < 6; corner++)
                Require(BlueprintEditorCommands.AddSupport(draft, BlueprintGeometry.HexCorner(TileCoord.Zero, corner)));
            for (var sector = 0; sector < 6; sector++)
                Require(BlueprintEditorCommands.AddRoofSector(draft, new RoofSectorKey(TileCoord.Zero, sector)));

            // Integer anchors are intentionally derived from the existing
            // production layout. Visual offsets remain factory responsibility.
            // §120.3 r2: yaw-шаги кроватей повторяют канонический кит
            // (+180°/+240° к повороту дома) — спящая лежит головой к очагу.
            PlaceNearest(draft, ContentIds.BedBasic, -0.974279f, 0f, 3);
            PlaceNearest(draft, ContentIds.BedBasic, 0.487139f, 0.84375f, 4);
            PlaceNearest(draft, "furniture.hearth", -0.3248f, 0.5625f, 0);
            // ⭐ #178: рама чертежа ЗЕРКАЛЬНА киту по X (кит нумерует бухты
            // против часовой, чертёж — по часовой), а координаты кита сюда
            // скопировали как есть. Авторское место шкафа оказалось на
            // ДВЕРНОЙ стене (отсюда r2-комментарий «интерьер недостижим»),
            // «противоположную стену» заняла кровать с новым yaw-шагом, и
            // PlaceNearest молча увёз шкаф в ЦЕНТР комнаты — 0.375 wu от
            // очага, внутрь тлеющего диска (0.55R = 0.825 wu); аптечка,
            // выводимая от шкафа, легла на центр пола. Правильное место —
            // китовское, отражённое в раму чертежа (x → −x: −0.3248,−0.9375 →
            // +0.3248,−0.9375), со сдвигом на один шаг решётки вдоль той же
            // стены, чтобы держать контракт «≥ 1 wu от двери» (точное зеркало
            // даёт 0.992). Стена та же (0.325 wu, как у кита), от очага
            // 1.635 wu. yawStep 5 — лицом в комнату (2 смотрит в стену).
            PlaceNearest(draft, "furniture.wardrobe", 0.6495190f, -0.75f, 5);
            draft.Normalize();
            return draft;
        }

        public static BuildSegmentKey[] EdgeSegments(TileCoord tile, int edge)
        {
            return BlueprintGeometry.SplitLine(
                BlueprintGeometry.HexCorner(tile, edge),
                BlueprintGeometry.HexCorner(tile, edge + 1)).ToArray();
        }

        private static void PlaceNearest(
            BuildingBlueprintDraft draft, string definitionId, float x, float z, int yawStep)
        {
            var candidates = HexPointLayout.GetInteriorTemplates()
                .OrderBy(template =>
                {
                    var dx = template.Offset.X - x;
                    var dz = template.Offset.Y - z;
                    return dx * dx + dz * dz;
                }).ToArray();
            for (var yawOffset = 0; yawOffset < 6; yawOffset++)
            {
                foreach (var candidate in candidates)
                {
                    var result = BlueprintEditorCommands.PlaceFurniture(
                        draft, definitionId, TileCoord.Zero, candidate.Slot, yawStep + yawOffset);
                    if (result.Succeeded) return;
                }
            }
            throw new InvalidOperationException($"No collision-free junction for built-in {definitionId}.");
        }

        private static void Require(BlueprintCommandResult result)
        {
            if (!result.Succeeded) throw new InvalidOperationException(result.Message);
        }
    }
}
