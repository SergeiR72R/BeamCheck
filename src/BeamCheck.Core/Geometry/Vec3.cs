using System;
using System.Globalization;

namespace BeamCheck.Core.Geometry
{
    /// <summary>Immutable 3D vector/point. Model coordinates are always millimetres.</summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double k) => new Vec3(a.X * k, a.Y * k, a.Z * k);
        public static Vec3 operator *(double k, Vec3 a) => a * k;
        public static Vec3 operator /(Vec3 a, double k) => new Vec3(a.X / k, a.Y / k, a.Z / k);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double LengthXY => Math.Sqrt(X * X + Y * Y);

        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public double DotXY(Vec3 o) => X * o.X + Y * o.Y;

        public Vec3 Normalized()
        {
            double l = Length;
            return l < 1e-12 ? Zero : this / l;
        }

        /// <summary>Projection onto the XY plane (Z = 0), normalized.</summary>
        public Vec3 PlanDirection()
        {
            double l = LengthXY;
            return l < 1e-12 ? Zero : new Vec3(X / l, Y / l, 0);
        }

        public Vec3 WithZ(double z) => new Vec3(X, Y, z);

        public static double DistanceXY(Vec3 a, Vec3 b) => (a - b).LengthXY;

        public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;

        public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => unchecked((X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode());

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#}, {2:0.#})", X, Y, Z);
    }

    public static class PlanGeometry
    {
        /// <summary>
        /// Projects point <paramref name="p"/> onto the plan line a→b.
        /// Returns parameter t (0 at a, 1 at b) and the perpendicular plan distance.
        /// </summary>
        public static double ProjectXY(Vec3 a, Vec3 b, Vec3 p, out double distance)
        {
            var ab = (b - a).WithZ(0);
            double len2 = ab.DotXY(ab);
            if (len2 < 1e-12)
            {
                distance = Vec3.DistanceXY(a, p);
                return 0;
            }

            double t = (p - a).DotXY(ab) / len2;
            var foot = a + (b - a) * t;
            distance = Vec3.DistanceXY(foot, p);
            return t;
        }
    }
}
