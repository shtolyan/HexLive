using System;

namespace HexLive.Simulation.Common
{
    public readonly struct EntityId : IEquatable<EntityId>
    {
        public EntityId(int value)
        {
            Value = value;
        }

        public int Value { get; }

        public bool Equals(EntityId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is EntityId other && Equals(other);

        public override int GetHashCode() => Value;

        public override string ToString() => Value.ToString();

        public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);

        public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);
    }

    public readonly struct ObjectId : IEquatable<ObjectId>
    {
        public ObjectId(int value)
        {
            Value = value;
        }

        public int Value { get; }

        public bool Equals(ObjectId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is ObjectId other && Equals(other);

        public override int GetHashCode() => Value;

        public override string ToString() => Value.ToString();

        public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);

        public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);
    }

    public readonly struct FragmentId : IEquatable<FragmentId>
    {
        public FragmentId(int value)
        {
            Value = value;
        }

        public int Value { get; }

        public bool Equals(FragmentId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is FragmentId other && Equals(other);

        public override int GetHashCode() => Value;

        public override string ToString() => Value.ToString();

        public static bool operator ==(FragmentId left, FragmentId right) => left.Equals(right);

        public static bool operator !=(FragmentId left, FragmentId right) => !left.Equals(right);
    }

    public readonly struct JunctionId : IEquatable<JunctionId>
    {
        public JunctionId(int value)
        {
            Value = value;
        }

        public int Value { get; }

        public bool Equals(JunctionId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is JunctionId other && Equals(other);

        public override int GetHashCode() => Value;

        public override string ToString() => Value.ToString();

        public static bool operator ==(JunctionId left, JunctionId right) => left.Equals(right);

        public static bool operator !=(JunctionId left, JunctionId right) => !left.Equals(right);
    }
}
