using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime.Blueprints
{
    public enum BlueprintElementKind
    {
        Support,
        FloorSector,
        Wall,
        Window,
        Door,
        RoofSector
    }

    public enum BlueprintElementOrigin
    {
        Manual,
        RoomBoundary
    }

    public readonly struct HexBuildNodeKey : IEquatable<HexBuildNodeKey>, IComparable<HexBuildNodeKey>
    {
        public HexBuildNodeKey(int q, int r)
        {
            Q = q;
            R = r;
        }

        public int Q { get; }
        public int R { get; }

        public int CompareTo(HexBuildNodeKey other)
        {
            var q = Q.CompareTo(other.Q);
            return q != 0 ? q : R.CompareTo(other.R);
        }

        public bool Equals(HexBuildNodeKey other) => Q == other.Q && R == other.R;
        public override bool Equals(object obj) => obj is HexBuildNodeKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Q, R);
        public override string ToString() => $"{Q},{R}";
        public static bool operator ==(HexBuildNodeKey left, HexBuildNodeKey right) => left.Equals(right);
        public static bool operator !=(HexBuildNodeKey left, HexBuildNodeKey right) => !left.Equals(right);
        public static HexBuildNodeKey operator +(HexBuildNodeKey left, HexBuildNodeKey right) =>
            new HexBuildNodeKey(left.Q + right.Q, left.R + right.R);
    }

    public readonly struct BuildSegmentKey : IEquatable<BuildSegmentKey>, IComparable<BuildSegmentKey>
    {
        public BuildSegmentKey(HexBuildNodeKey a, HexBuildNodeKey b)
        {
            if (a.CompareTo(b) <= 0)
            {
                A = a;
                B = b;
            }
            else
            {
                A = b;
                B = a;
            }
        }

        public HexBuildNodeKey A { get; }
        public HexBuildNodeKey B { get; }

        public int CompareTo(BuildSegmentKey other)
        {
            var a = A.CompareTo(other.A);
            return a != 0 ? a : B.CompareTo(other.B);
        }

        public bool Equals(BuildSegmentKey other) => A == other.A && B == other.B;
        public override bool Equals(object obj) => obj is BuildSegmentKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(A, B);
        public override string ToString() => $"{A}>{B}";
        public static bool operator ==(BuildSegmentKey left, BuildSegmentKey right) => left.Equals(right);
        public static bool operator !=(BuildSegmentKey left, BuildSegmentKey right) => !left.Equals(right);
    }

    public readonly struct FloorSectorKey : IEquatable<FloorSectorKey>, IComparable<FloorSectorKey>
    {
        public FloorSectorKey(TileCoord hex, int sector)
        {
            Hex = hex;
            Sector = BlueprintGeometry.NormalizeSector(sector);
        }

        public TileCoord Hex { get; }
        public int Sector { get; }

        public int CompareTo(FloorSectorKey other)
        {
            var q = Hex.Q.CompareTo(other.Hex.Q);
            if (q != 0) return q;
            var r = Hex.R.CompareTo(other.Hex.R);
            return r != 0 ? r : Sector.CompareTo(other.Sector);
        }

        public bool Equals(FloorSectorKey other) => Hex == other.Hex && Sector == other.Sector;
        public override bool Equals(object obj) => obj is FloorSectorKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Hex, Sector);
        public override string ToString() => $"{Hex.Q},{Hex.R}:{Sector}";
        public static bool operator ==(FloorSectorKey left, FloorSectorKey right) => left.Equals(right);
        public static bool operator !=(FloorSectorKey left, FloorSectorKey right) => !left.Equals(right);
    }

    public readonly struct RoofSectorKey : IEquatable<RoofSectorKey>, IComparable<RoofSectorKey>
    {
        public RoofSectorKey(TileCoord hex, int sector)
        {
            Hex = hex;
            Sector = BlueprintGeometry.NormalizeSector(sector);
        }

        public TileCoord Hex { get; }
        public int Sector { get; }

        public int CompareTo(RoofSectorKey other)
        {
            var q = Hex.Q.CompareTo(other.Hex.Q);
            if (q != 0) return q;
            var r = Hex.R.CompareTo(other.Hex.R);
            return r != 0 ? r : Sector.CompareTo(other.Sector);
        }

        public bool Equals(RoofSectorKey other) => Hex == other.Hex && Sector == other.Sector;
        public override bool Equals(object obj) => obj is RoofSectorKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Hex, Sector);
        public override string ToString() => $"{Hex.Q},{Hex.R}:{Sector}";
        public static bool operator ==(RoofSectorKey left, RoofSectorKey right) => left.Equals(right);
        public static bool operator !=(RoofSectorKey left, RoofSectorKey right) => !left.Equals(right);
    }

    public readonly struct JunctionKey : IEquatable<JunctionKey>, IComparable<JunctionKey>
    {
        public JunctionKey(int xKey, int yKey)
        {
            XKey = xKey;
            YKey = yKey;
        }

        public int XKey { get; }
        public int YKey { get; }

        public int CompareTo(JunctionKey other)
        {
            var x = XKey.CompareTo(other.XKey);
            return x != 0 ? x : YKey.CompareTo(other.YKey);
        }

        public bool Equals(JunctionKey other) => XKey == other.XKey && YKey == other.YKey;
        public override bool Equals(object obj) => obj is JunctionKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(XKey, YKey);
        public override string ToString() => $"{XKey},{YKey}";
        public static bool operator ==(JunctionKey left, JunctionKey right) => left.Equals(right);
        public static bool operator !=(JunctionKey left, JunctionKey right) => !left.Equals(right);
    }

    public sealed class BlueprintElementData
    {
        public string Id = string.Empty;
        public BlueprintElementKind Kind;
        public BlueprintElementOrigin Origin;
        public int RoomId;
        public HexBuildNodeKey Node;
        public BuildSegmentKey Segment;
        public FloorSectorKey FloorSector;
        public RoofSectorKey RoofSector;

        public BlueprintElementData Clone() => new BlueprintElementData
        {
            Id = Id,
            Kind = Kind,
            Origin = Origin,
            RoomId = RoomId,
            Node = Node,
            Segment = Segment,
            FloorSector = FloorSector,
            RoofSector = RoofSector
        };
    }

    public sealed class FurniturePlacementData
    {
        public string Id = string.Empty;
        public string DefinitionId = string.Empty;
        public int TileQ;
        public int TileR;
        public int JunctionSlot;
        public int YawStep;

        public TileCoord Tile => new TileCoord(TileQ, TileR);

        public JunctionKey PrimaryJunction
        {
            get
            {
                var templates = HexPointLayout.GetInteriorTemplates();
                if (JunctionSlot < 0 || JunctionSlot >= templates.Count)
                    return default;
                var pair = HexPointLayout.GetJunctionKeyPair(Tile, templates[JunctionSlot].SubAxial);
                return new JunctionKey(pair.xKey, pair.yKey);
            }
        }

        public FurniturePlacementData Clone() => new FurniturePlacementData
        {
            Id = Id,
            DefinitionId = DefinitionId,
            TileQ = TileQ,
            TileR = TileR,
            JunctionSlot = JunctionSlot,
            YawStep = BlueprintGeometry.NormalizeSector(YawStep)
        };
    }

    public sealed class BuildingBlueprintDraft
    {
        public const int CurrentVersion = 2;

        public int Version = CurrentVersion;
        public string BlueprintId = "draft";
        public int NextElementId = 1;
        public int NextRoomId = 1;
        /// <summary>§120.8: комнаты БЕЗ автоконтура стен («Пол без стен» — терраса,
    /// настил, фундамент под будущие стены). Обычная комната перестраивает
    /// границу при каждом изменении; открытая — только снимает её. Поле в JSON
    /// опционально (отсутствует = пусто), поэтому версия формата не растёт.</summary>
    public List<int> OpenRooms = new();

    public List<BlueprintElementData> Elements = new List<BlueprintElementData>();
        public List<FurniturePlacementData> Furniture = new List<FurniturePlacementData>();

        public BuildingBlueprintDraft Clone()
        {
            return new BuildingBlueprintDraft
            {
                Version = Version,
                BlueprintId = BlueprintId,
                NextElementId = NextElementId,
                NextRoomId = NextRoomId,
                Elements = Elements.Select(element => element.Clone()).ToList(),
                Furniture = Furniture.Select(item => item.Clone()).ToList(),
                OpenRooms = new List<int>(OpenRooms)
            };
        }

        public string AllocateElementId() => $"e{NextElementId++:D5}";
        public string AllocateFurnitureId() => $"f{NextElementId++:D5}";
        public int AllocateRoomId() => NextRoomId++;

        public void Normalize()
        {
            Version = CurrentVersion;
            Elements.Sort(BlueprintElementComparer.Instance);
            Furniture.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
            foreach (var item in Furniture) item.YawStep = BlueprintGeometry.NormalizeSector(item.YawStep);
            // Только живые комнаты, отсортированно — стабильный byte-diff JSON.
            var openRooms = OpenRooms.Distinct()
                .Where(roomId => Elements.Any(element =>
                    element.Kind == BlueprintElementKind.FloorSector && element.RoomId == roomId))
                .OrderBy(roomId => roomId).ToList();
            OpenRooms.Clear();
            OpenRooms.AddRange(openRooms);
        }

        private sealed class BlueprintElementComparer : IComparer<BlueprintElementData>
        {
            public static readonly BlueprintElementComparer Instance = new BlueprintElementComparer();

            public int Compare(BlueprintElementData left, BlueprintElementData right)
            {
                if (ReferenceEquals(left, right)) return 0;
                if (left == null) return -1;
                if (right == null) return 1;
                var kind = left.Kind.CompareTo(right.Kind);
                if (kind != 0) return kind;
                var room = left.RoomId.CompareTo(right.RoomId);
                if (room != 0) return room;
                return string.CompareOrdinal(left.Id, right.Id);
            }
        }
    }

    public sealed class BlueprintValidationIssue
    {
        public BlueprintValidationIssue(string code, string message, string elementId = "")
        {
            Code = code;
            Message = message;
            ElementId = elementId ?? string.Empty;
        }

        public string Code { get; }
        public string Message { get; }
        public string ElementId { get; }
    }

    public sealed class BlueprintValidationResult
    {
        private readonly List<BlueprintValidationIssue> _issues = new List<BlueprintValidationIssue>();

        public IReadOnlyList<BlueprintValidationIssue> Issues => _issues;
        public bool IsValid => _issues.Count == 0;

        internal void Add(string code, string message, string elementId = "") =>
            _issues.Add(new BlueprintValidationIssue(code, message, elementId));
    }
}
