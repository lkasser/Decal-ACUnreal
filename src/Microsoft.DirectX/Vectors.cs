using Numerics = System.Numerics;

namespace Microsoft.DirectX
{
    /// <summary>A point or direction on a plane, as Managed DirectX had it.</summary>
    public struct Vector2
    {
        public float X;
        public float Y;

        public Vector2(float valueX, float valueY)
        {
            X = valueX;
            Y = valueY;
        }

        /// <summary>
        /// Moves this point by the matrix, as D3DX's TransformCoord did: as (X, Y, 0, 1), divided
        /// back by what becomes of the 1 - which, for the transforms plugins build, stays 1.
        /// </summary>
        public void TransformCoordinate(Matrix sourceMatrix) => this = TransformCoordinate(this, sourceMatrix);

        public static Vector2 TransformCoordinate(Vector2 source, Matrix sourceMatrix)
        {
            float x = source.X * sourceMatrix.M11 + source.Y * sourceMatrix.M21 + sourceMatrix.M41;
            float y = source.X * sourceMatrix.M12 + source.Y * sourceMatrix.M22 + sourceMatrix.M42;
            float w = source.X * sourceMatrix.M14 + source.Y * sourceMatrix.M24 + sourceMatrix.M44;
            return new Vector2(x / w, y / w);
        }
    }

    /// <summary>A point or direction in space, as Managed DirectX had it.</summary>
    public struct Vector3
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3(float valueX, float valueY, float valueZ)
        {
            X = valueX;
            Y = valueY;
            Z = valueZ;
        }

        internal Numerics.Vector3 ToNumerics() => new Numerics.Vector3(X, Y, Z);
    }

    /// <summary>A plane, as ax + by + cz + d = 0: what a plugin reflects a drawing in.</summary>
    public struct Plane
    {
        public float A;
        public float B;
        public float C;
        public float D;

        public Plane(float valuePointA, float valuePointB, float valuePointC, float valuePointD)
        {
            A = valuePointA;
            B = valuePointB;
            C = valuePointC;
            D = valuePointD;
        }

        /// <summary>The plane through three points, facing the way they wind, as D3DX's PlaneFromPoints made it.</summary>
        public static Plane FromPoints(Vector3 point1, Vector3 point2, Vector3 point3)
        {
            Numerics.Plane plane = Numerics.Plane.CreateFromVertices(point1.ToNumerics(), point2.ToNumerics(), point3.ToNumerics());
            return new Plane(plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.D);
        }

        internal Numerics.Plane ToNumerics() => new Numerics.Plane(A, B, C, D);
    }
}
