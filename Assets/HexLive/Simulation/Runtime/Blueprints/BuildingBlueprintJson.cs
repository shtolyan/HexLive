using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime.Blueprints
{
    public static class BuildingBlueprintJson
    {
        public static string Serialize(BuildingBlueprintDraft source, bool pretty = true)
        {
            var draft = source?.Clone() ?? throw new ArgumentNullException(nameof(source));
            draft.Normalize();
            var newline = pretty ? "\n" : string.Empty;
            var i1 = pretty ? "  " : string.Empty;
            var i2 = pretty ? "    " : string.Empty;
            var i3 = pretty ? "      " : string.Empty;
            var space = pretty ? " " : string.Empty;
            var sb = new StringBuilder();
            sb.Append('{').Append(newline);
            Property(sb, i1, "version", draft.Version.ToString(CultureInfo.InvariantCulture), false, newline, space);
            Property(sb, i1, "blueprintId", Quote(draft.BlueprintId), false, newline, space);
            Property(sb, i1, "nextElementId", draft.NextElementId.ToString(CultureInfo.InvariantCulture), false, newline, space);
            Property(sb, i1, "nextRoomId", draft.NextRoomId.ToString(CultureInfo.InvariantCulture), false, newline, space);
            sb.Append(i1).Append("\"elements\":").Append(space).Append('[');
            if (draft.Elements.Count > 0) sb.Append(newline);
            for (var index = 0; index < draft.Elements.Count; index++)
            {
                var element = draft.Elements[index];
                sb.Append(i2).Append('{').Append(newline);
                Property(sb, i3, "id", Quote(element.Id), false, newline, space);
                Property(sb, i3, "kind", Quote(ToToken(element.Kind)), false, newline, space);
                Property(sb, i3, "origin", Quote(ToToken(element.Origin)), false, newline, space);
                Property(sb, i3, "roomId", element.RoomId.ToString(CultureInfo.InvariantCulture), false, newline, space);
                switch (element.Kind)
                {
                    case BlueprintElementKind.Support:
                        Property(sb, i3, "node", Node(element.Node, space), true, newline, space);
                        break;
                    case BlueprintElementKind.FloorSector:
                        Property(sb, i3, "floorSector", Sector(element.FloorSector.Hex, element.FloorSector.Sector, space), true, newline, space);
                        break;
                    case BlueprintElementKind.RoofSector:
                        Property(sb, i3, "roofSector", Sector(element.RoofSector.Hex, element.RoofSector.Sector, space), true, newline, space);
                        break;
                    default:
                        Property(sb, i3, "segment", Segment(element.Segment, space), true, newline, space);
                        break;
                }
                sb.Append(i2).Append('}');
                if (index + 1 < draft.Elements.Count) sb.Append(',');
                sb.Append(newline);
            }
            sb.Append(i1).Append(']').Append(',').Append(newline);
            sb.Append(i1).Append("\"furniture\":").Append(space).Append('[');
            if (draft.Furniture.Count > 0) sb.Append(newline);
            for (var index = 0; index < draft.Furniture.Count; index++)
            {
                var item = draft.Furniture[index];
                sb.Append(i2).Append('{').Append(newline);
                Property(sb, i3, "id", Quote(item.Id), false, newline, space);
                Property(sb, i3, "definitionId", Quote(item.DefinitionId), false, newline, space);
                Property(sb, i3, "tileQ", item.TileQ.ToString(CultureInfo.InvariantCulture), false, newline, space);
                Property(sb, i3, "tileR", item.TileR.ToString(CultureInfo.InvariantCulture), false, newline, space);
                Property(sb, i3, "junctionSlot", item.JunctionSlot.ToString(CultureInfo.InvariantCulture), false, newline, space);
                Property(sb, i3, "yawStep", item.YawStep.ToString(CultureInfo.InvariantCulture), true, newline, space);
                sb.Append(i2).Append('}');
                if (index + 1 < draft.Furniture.Count) sb.Append(',');
                sb.Append(newline);
            }
            sb.Append(i1).Append(']').Append(newline).Append('}');
            return sb.ToString();
        }

        public static bool TryDeserialize(string json, out BuildingBlueprintDraft draft, out string error)
        {
            draft = null;
            error = string.Empty;
            if (MiniJson.Parse(json) is not Dictionary<string, object> root)
            {
                error = "JSON не является объектом.";
                return false;
            }
            try
            {
                draft = new BuildingBlueprintDraft
                {
                    Version = Int(root, "version"),
                    BlueprintId = String(root, "blueprintId"),
                    NextElementId = Int(root, "nextElementId"),
                    NextRoomId = Int(root, "nextRoomId")
                };
                if (draft.Version != BuildingBlueprintDraft.CurrentVersion)
                    throw new FormatException($"Версия {draft.Version} не поддерживается.");
                foreach (var map in Objects(root, "elements")) draft.Elements.Add(ParseElement(map));
                foreach (var map in Objects(root, "furniture")) draft.Furniture.Add(ParseFurniture(map));
                draft.Normalize();
                var validation = BlueprintValidator.Validate(draft);
                if (!validation.IsValid) throw new FormatException(validation.Issues[0].Message);
                return true;
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or KeyNotFoundException)
            {
                error = exception.Message;
                draft = null;
                return false;
            }
        }

        private static BlueprintElementData ParseElement(Dictionary<string, object> map)
        {
            var kind = ParseKind(String(map, "kind"));
            var element = new BlueprintElementData
            {
                Id = String(map, "id"),
                Kind = kind,
                Origin = ParseOrigin(String(map, "origin")),
                RoomId = Int(map, "roomId")
            };
            switch (kind)
            {
                case BlueprintElementKind.Support:
                    element.Node = ParseNode(Object(map, "node"));
                    break;
                case BlueprintElementKind.FloorSector:
                    var floor = Object(map, "floorSector");
                    element.FloorSector = new FloorSectorKey(
                        new TileCoord(Int(floor, "q"), Int(floor, "r")), Int(floor, "sector"));
                    break;
                case BlueprintElementKind.RoofSector:
                    var roof = Object(map, "roofSector");
                    element.RoofSector = new RoofSectorKey(
                        new TileCoord(Int(roof, "q"), Int(roof, "r")), Int(roof, "sector"));
                    break;
                default:
                    var segment = Object(map, "segment");
                    element.Segment = new BuildSegmentKey(
                        ParseNode(Object(segment, "a")), ParseNode(Object(segment, "b")));
                    break;
            }
            return element;
        }

        private static FurniturePlacementData ParseFurniture(Dictionary<string, object> map) =>
            new FurniturePlacementData
            {
                Id = String(map, "id"),
                DefinitionId = String(map, "definitionId"),
                TileQ = Int(map, "tileQ"),
                TileR = Int(map, "tileR"),
                JunctionSlot = Int(map, "junctionSlot"),
                YawStep = Int(map, "yawStep")
            };

        private static void Property(
            StringBuilder sb, string indent, string name, string value, bool last,
            string newline, string space)
        {
            sb.Append(indent).Append(Quote(name)).Append(':').Append(space).Append(value);
            if (!last) sb.Append(',');
            sb.Append(newline);
        }

        private static string Node(HexBuildNodeKey node, string space) =>
            $"{{\"q\":{space}{node.Q},\"r\":{space}{node.R}}}";

        private static string Segment(BuildSegmentKey segment, string space) =>
            $"{{\"a\":{space}{Node(segment.A, space)},\"b\":{space}{Node(segment.B, space)}}}";

        private static string Sector(TileCoord hex, int sector, string space) =>
            $"{{\"q\":{space}{hex.Q},\"r\":{space}{hex.R},\"sector\":{space}{sector}}}";

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            var sb = new StringBuilder(value.Length + 2).Append('"');
            foreach (var character in value)
            {
                sb.Append(character switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => character.ToString()
                });
            }
            return sb.Append('"').ToString();
        }

        private static string ToToken(BlueprintElementKind kind) => kind switch
        {
            BlueprintElementKind.Support => "support",
            BlueprintElementKind.FloorSector => "floorSector",
            BlueprintElementKind.Wall => "wall",
            BlueprintElementKind.Window => "window",
            BlueprintElementKind.Door => "door",
            BlueprintElementKind.RoofSector => "roofSector",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        private static string ToToken(BlueprintElementOrigin origin) =>
            origin == BlueprintElementOrigin.RoomBoundary ? "roomBoundary" : "manual";

        private static BlueprintElementKind ParseKind(string token) => token switch
        {
            "support" => BlueprintElementKind.Support,
            "floorSector" => BlueprintElementKind.FloorSector,
            "wall" => BlueprintElementKind.Wall,
            "window" => BlueprintElementKind.Window,
            "door" => BlueprintElementKind.Door,
            "roofSector" => BlueprintElementKind.RoofSector,
            _ => throw new FormatException($"Неизвестный kind '{token}'.")
        };

        private static BlueprintElementOrigin ParseOrigin(string token) => token switch
        {
            "manual" => BlueprintElementOrigin.Manual,
            "roomBoundary" => BlueprintElementOrigin.RoomBoundary,
            _ => throw new FormatException($"Неизвестный origin '{token}'.")
        };

        private static HexBuildNodeKey ParseNode(Dictionary<string, object> map) =>
            new HexBuildNodeKey(Int(map, "q"), Int(map, "r"));

        private static IEnumerable<Dictionary<string, object>> Objects(
            Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value is not List<object> list)
                throw new FormatException($"Поле '{key}' должно быть массивом.");
            return list.Select(item => item as Dictionary<string, object> ??
                throw new FormatException($"Элемент '{key}' должен быть объектом."));
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) && value is Dictionary<string, object> result
                ? result
                : throw new FormatException($"Поле '{key}' должно быть объектом.");

        private static int Int(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value is not double number ||
                double.IsNaN(number) || double.IsInfinity(number) || Math.Truncate(number) != number ||
                number < int.MinValue || number > int.MaxValue)
                throw new FormatException($"Поле '{key}' должно быть целым числом.");
            return (int)number;
        }

        private static string String(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) && value is string text
                ? text
                : throw new FormatException($"Поле '{key}' должно быть строкой.");
    }
}
