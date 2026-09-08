using System;

namespace HexLive.Simulation.Common
{
    /// <summary>
    /// §156: адрес чанка — квадрата <c>S×S</c> в axial-координатах тайлов.
    /// Чисто пространственная решётка: ничего, кроме «какие тайлы рядом», она не
    /// значит.
    /// <para>
    /// Отдельный тип, а не <see cref="FragmentId"/>: фрагмент несёт семантику
    /// связности (<c>Fragment.Links</c>, входы/выходы) и фактически мёртв — весь
    /// мир живёт во <c>FragmentId(1)</c>. Нагрузив его вторым смыслом, мы получили
    /// бы ось, которая означает то одно, то другое.
    /// </para>
    /// </summary>
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public ChunkCoord(int cq, int cr)
        {
            Cq = cq;
            Cr = cr;
        }

        public int Cq { get; }

        public int Cr { get; }

        public bool Equals(ChunkCoord other) => Cq == other.Cq && Cr == other.Cr;

        public override bool Equals(object obj) => obj is ChunkCoord other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Cq, Cr);

        public override string ToString() => string.Format("{0},{1}", Cq, Cr);

        public static bool operator ==(ChunkCoord left, ChunkCoord right) => left.Equals(right);

        public static bool operator !=(ChunkCoord left, ChunkCoord right) => !left.Equals(right);
    }
}
