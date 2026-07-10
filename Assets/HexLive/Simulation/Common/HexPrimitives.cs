using System;

namespace HexLive.Simulation.Common
{
    public readonly struct TileCoord : IEquatable<TileCoord>
    {
        public TileCoord(int q, int r)
        {
            Q = q;
            R = r;
        }

        public int Q { get; }

        public int R { get; }

        public static TileCoord Zero => new TileCoord(0, 0);

        public bool Equals(TileCoord other) => Q == other.Q && R == other.R;

        public override bool Equals(object obj) => obj is TileCoord other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Q, R);

        public override string ToString() => string.Format("{0},{1}", Q, R);

        public static bool operator ==(TileCoord left, TileCoord right) => left.Equals(right);

        public static bool operator !=(TileCoord left, TileCoord right) => !left.Equals(right);
    }

    public readonly struct HexDirection : IEquatable<HexDirection>
    {
        public HexDirection(int dq, int dr)
        {
            DQ = dq;
            DR = dr;
        }

        public int DQ { get; }

        public int DR { get; }

        public static readonly HexDirection East = new HexDirection(1, 0);
        public static readonly HexDirection NorthEast = new HexDirection(1, -1);
        public static readonly HexDirection NorthWest = new HexDirection(0, -1);
        public static readonly HexDirection West = new HexDirection(-1, 0);
        public static readonly HexDirection SouthWest = new HexDirection(-1, 1);
        public static readonly HexDirection SouthEast = new HexDirection(0, 1);

        // Must be declared after the named directions: static initializers run
        // in textual order, and an earlier declaration would capture zeros.
        private static readonly HexDirection[] AllDirections =
        {
            East,
            NorthEast,
            NorthWest,
            West,
            SouthWest,
            SouthEast
        };

        public static ReadOnlySpan<HexDirection> All => AllDirections;

        public bool Equals(HexDirection other)
        {
            return DQ == other.DQ && DR == other.DR;
        }

        public override bool Equals(object obj)
        {
            return obj is HexDirection other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(DQ, DR);
        }

        public static bool operator ==(HexDirection left, HexDirection right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(HexDirection left, HexDirection right)
        {
            return !left.Equals(right);
        }
    }
}
