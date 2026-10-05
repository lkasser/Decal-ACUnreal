using Numerics = System.Numerics;

namespace Microsoft.DirectX
{
    /// <summary>
    /// A 4x4 transform as Managed DirectX had it: row vectors, so a point is multiplied on the
    /// left, the translation is in the fourth row, and in a product the left matrix acts first.
    /// </summary>
    /// <remarks>
    /// Plugins built transforms with these to place what they drew - Integrator2's map turns
    /// mouse positions into map coordinates with them as well as its drawing - so the arithmetic
    /// is real even though nothing here draws. Each of the makers (<see cref="Translate"/>,
    /// <see cref="RotateZ"/>...) replaces the whole matrix, as D3DX's did; a plugin combines them
    /// with <see cref="Multiply(Matrix)"/>. The sums are System.Numerics', which keeps DirectX's
    /// conventions.
    /// </remarks>
    public struct Matrix
    {
        public float M11;
        public float M12;
        public float M13;
        public float M14;
        public float M21;
        public float M22;
        public float M23;
        public float M24;
        public float M31;
        public float M32;
        public float M33;
        public float M34;
        public float M41;
        public float M42;
        public float M43;
        public float M44;

        public static Matrix Identity => From(Numerics.Matrix4x4.Identity);

        /// <summary>This matrix times another: this transform, then that one.</summary>
        public void Multiply(Matrix source) => this = Multiply(this, source);

        public static Matrix Multiply(Matrix left, Matrix right) => From(Numerics.Matrix4x4.Multiply(left.ToNumerics(), right.ToNumerics()));

        /// <summary>Makes this its own inverse. One with no inverse is left as it was, as D3DX left it.</summary>
        public void Invert()
        {
            if (Numerics.Matrix4x4.Invert(ToNumerics(), out Numerics.Matrix4x4 inverse))
                this = From(inverse);
        }

        public void Translate(float x, float y, float z) => this = From(Numerics.Matrix4x4.CreateTranslation(x, y, z));

        public void Scale(float x, float y, float z) => this = From(Numerics.Matrix4x4.CreateScale(x, y, z));

        public void RotateX(float angle) => this = From(Numerics.Matrix4x4.CreateRotationX(angle));

        public void RotateY(float angle) => this = From(Numerics.Matrix4x4.CreateRotationY(angle));

        public void RotateZ(float angle) => this = From(Numerics.Matrix4x4.CreateRotationZ(angle));

        /// <summary>Makes this a reflection in the plane, which need not be normalised.</summary>
        public void Reflect(Plane plane) => this = From(Numerics.Matrix4x4.CreateReflection(plane.ToNumerics()));

        internal Numerics.Matrix4x4 ToNumerics()
            => new Numerics.Matrix4x4(M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44);

        internal static Matrix From(Numerics.Matrix4x4 m)
            => new Matrix
            {
                M11 = m.M11, M12 = m.M12, M13 = m.M13, M14 = m.M14,
                M21 = m.M21, M22 = m.M22, M23 = m.M23, M24 = m.M24,
                M31 = m.M31, M32 = m.M32, M33 = m.M33, M34 = m.M34,
                M41 = m.M41, M42 = m.M42, M43 = m.M43, M44 = m.M44,
            };
    }
}
