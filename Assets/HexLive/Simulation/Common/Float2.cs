using System;

namespace HexLive.Simulation.Common
{

public readonly struct Float2 : IEquatable<Float2>
{
    public Float2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }

    public float Y { get; }

    public static Float2 Zero => new(0f, 0f);

    public bool Equals(Float2 other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is Float2 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public override string ToString() => $"({X:0.###}, {Y:0.###})";

    public static Float2 operator +(Float2 left, Float2 right) => new(left.X + right.X, left.Y + right.Y);

    public static Float2 operator -(Float2 left, Float2 right) => new(left.X - right.X, left.Y - right.Y);

    public static Float2 operator *(Float2 value, float scalar) => new(value.X * scalar, value.Y * scalar);
}

}
