using System;
using System.Globalization;
using AC.Host.World;

namespace Decal.Adapter.Wrappers
{
    /// <summary>A place on Dereth's map, in the north/south and east/west the radar shows.</summary>
    public class CoordsObject
    {
        public CoordsObject(double northSouth, double eastWest)
        {
            NorthSouth = northSouth;
            EastWest = eastWest;
        }

        public double NorthSouth { get; }

        public double EastWest { get; }

        /// <summary>
        /// The map coordinates of a position: each landblock is 192 units, and a map unit is 240,
        /// counted from 101.95 in from the corner - the numbers the radar has always used.
        /// </summary>
        internal static CoordsObject From(Location location)
        {
            double x = ((location.LandblockCell >> 24) & 0xFF) * 192.0 + location.X;
            double y = ((location.LandblockCell >> 16) & 0xFF) * 192.0 + location.Y;
            return new CoordsObject(y / 240.0 - 101.95, x / 240.0 - 101.95);
        }

        /// <summary>In map units, as the crow flies.</summary>
        public double DistanceToCoords(CoordsObject destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            double ns = destination.NorthSouth - NorthSouth;
            double ew = destination.EastWest - EastWest;
            return Math.Sqrt(ns * ns + ew * ew);
        }

        /// <summary>In degrees clockwise from north, as the compass reads.</summary>
        public double AngleToCoords(CoordsObject destination) => AngleToCoordsRadians(destination) * 180.0 / Math.PI;

        public double AngleToCoordsRadians(CoordsObject destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            double angle = Math.Atan2(destination.EastWest - EastWest, destination.NorthSouth - NorthSouth);
            return angle < 0 ? angle + 2 * Math.PI : angle;
        }

        public override string ToString() => ToString("0.0");

        /// <summary>"42.1N, 33.6E", with the numbers in the format given.</summary>
        public string ToString(string numberFormat)
            => Math.Abs(NorthSouth).ToString(numberFormat, CultureInfo.InvariantCulture) + (NorthSouth >= 0 ? "N" : "S") + ", "
             + Math.Abs(EastWest).ToString(numberFormat, CultureInfo.InvariantCulture) + (EastWest >= 0 ? "E" : "W");

        public override bool Equals(object obj) => Equals(obj as CoordsObject);

        public bool Equals(CoordsObject obj) => obj is not null && obj.NorthSouth == NorthSouth && obj.EastWest == EastWest;

        public override int GetHashCode() => HashCode.Combine(NorthSouth, EastWest);

        public static bool operator ==(CoordsObject a, CoordsObject b) => a is null ? b is null : a.Equals(b);

        public static bool operator !=(CoordsObject a, CoordsObject b) => !(a == b);
    }

    public class Vector3Object
    {
        public Vector3Object(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }

        public double Y { get; }

        public double Z { get; }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X}, {Y}, {Z})");

        public override bool Equals(object obj) => Equals(obj as Vector3Object);

        public bool Equals(Vector3Object obj) => obj is not null && obj.X == X && obj.Y == Y && obj.Z == Z;

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        public static bool operator ==(Vector3Object a, Vector3Object b) => a is null ? b is null : a.Equals(b);

        public static bool operator !=(Vector3Object a, Vector3Object b) => !(a == b);
    }

    public class Vector4Object
    {
        public Vector4Object(double w, double x, double y, double z)
        {
            W = w;
            X = x;
            Y = y;
            Z = z;
        }

        public double W { get; }

        public double X { get; }

        public double Y { get; }

        public double Z { get; }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({W}, {X}, {Y}, {Z})");

        public override bool Equals(object obj) => Equals(obj as Vector4Object);

        public bool Equals(Vector4Object obj) => obj is not null && obj.W == W && obj.X == X && obj.Y == Y && obj.Z == Z;

        public override int GetHashCode() => HashCode.Combine(W, X, Y, Z);

        public static bool operator ==(Vector4Object a, Vector4Object b) => a is null ? b is null : a.Equals(b);

        public static bool operator !=(Vector4Object a, Vector4Object b) => !(a == b);
    }
}
