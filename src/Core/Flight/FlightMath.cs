using System;

namespace LivingSystems.Core.Flight
{
    /// <summary>
    /// Minimal vector math with Unity's conventions (left-handed, Y up, Z forward), so the adapter can
    /// copy x/y/z(/w) straight into UnityEngine types. Core stays free of Unity.
    /// </summary>
    public struct Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 Forward = new Vec3(0, 0, 1);
        public static readonly Vec3 Up = new Vec3(0, 1, 0);
        public static readonly Vec3 Right = new Vec3(1, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(float s, Vec3 a) => a * s;
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Normalized { get { var l = Length; return l > 1e-6f ? this / l : Zero; } }

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>Same length direction, shortened to at most <paramref name="max"/>.</summary>
        public Vec3 ClampLength(float max) { var l = Length; return l > max && l > 0 ? this * (max / l) : this; }

        public override string ToString() => $"({X:0.0},{Y:0.0},{Z:0.0})";
    }

    public struct Quat
    {
        public float X, Y, Z, W;
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public static Vec3 operator *(Quat q, Vec3 v)
        {
            var u = new Vec3(q.X, q.Y, q.Z);
            var t = 2f * Vec3.Cross(u, v);
            return v + q.W * t + Vec3.Cross(u, t);
        }

        public Quat Inverse => new Quat(-X, -Y, -Z, W);   // unit quaternions only

        public Quat Normalized
        {
            get
            {
                var l = (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                return l > 1e-9f ? new Quat(X / l, Y / l, Z / l, W / l) : Identity;
            }
        }

        public Vec3 Forward => this * Vec3.Forward;
        public Vec3 Up => this * Vec3.Up;
        public Vec3 Right => this * Vec3.Right;

        /// <summary>Rotation by a rotation vector (axis × angle in radians).</summary>
        public static Quat FromRotationVector(Vec3 r)
        {
            var angle = r.Length;
            if (angle < 1e-9f) return Identity;
            var axis = r / angle;
            var s = (float)Math.Sin(angle / 2);
            return new Quat(axis.X * s, axis.Y * s, axis.Z * s, (float)Math.Cos(angle / 2));
        }

        /// <summary>Inverse of <see cref="FromRotationVector"/>, shortest way round (angle ≤ π).</summary>
        public Vec3 ToRotationVector()
        {
            var q = W < 0 ? new Quat(-X, -Y, -Z, -W) : this;
            var s = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            if (s < 1e-9f) return Vec3.Zero;
            var angle = 2f * (float)Math.Atan2(s, q.W);
            return new Vec3(q.X, q.Y, q.Z) * (angle / s);
        }

        /// <summary>Unity's Quaternion.LookRotation: forward = <paramref name="fwd"/>, up as close to <paramref name="up"/> as possible.</summary>
        public static Quat LookRotation(Vec3 fwd, Vec3 up)
        {
            var f = fwd.Normalized;
            if (f.Length < 0.5f) return Identity;
            var r = Vec3.Cross(up, f).Normalized;
            if (r.Length < 0.5f)   // up parallel to forward: pick any perpendicular
                r = Vec3.Cross(Math.Abs(f.Y) < 0.9f ? Vec3.Up : Vec3.Right, f).Normalized;
            var u = Vec3.Cross(f, r);
            // rotation matrix columns r, u, f → quaternion
            float m00 = r.X, m01 = u.X, m02 = f.X, m10 = r.Y, m11 = u.Y, m12 = f.Y, m20 = r.Z, m21 = u.Z, m22 = f.Z;
            float tr = m00 + m11 + m22;
            Quat q;
            if (tr > 0)
            {
                var s = (float)Math.Sqrt(tr + 1) * 2;
                q = new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                var s = (float)Math.Sqrt(1 + m00 - m11 - m22) * 2;
                q = new Quat(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                var s = (float)Math.Sqrt(1 + m11 - m00 - m22) * 2;
                q = new Quat((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                var s = (float)Math.Sqrt(1 + m22 - m00 - m11) * 2;
                q = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
            return q.Normalized;
        }

        /// <summary>Angle between two rotations, degrees.</summary>
        public static float AngleDeg(Quat a, Quat b) => (a.Inverse * b).ToRotationVector().Length * 57.29578f;
    }
}
