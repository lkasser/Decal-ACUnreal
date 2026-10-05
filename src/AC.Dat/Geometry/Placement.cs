using System;
using System.Numerics;

namespace AC.Dat.Geometry
{
    /// <summary>
    /// Where something stands and which way it faces, in the frame of whatever holds it: a part
    /// in its object, an object in its landblock, a cell in its landblock.
    /// </summary>
    public readonly struct Placement
    {
        public static readonly Placement Identity = new Placement(Vector3.Zero, Quaternion.Identity);

        public Placement(Vector3 origin, Quaternion orientation)
        {
            Origin = origin;
            Orientation = orientation == default ? Quaternion.Identity : Quaternion.Normalize(orientation);
        }

        public Vector3 Origin { get; }

        public Quaternion Orientation { get; }

        /// <summary>A point given in this frame, in the frame holding it.</summary>
        public Vector3 ToOuter(Vector3 local) => Origin + Vector3.Transform(local, Orientation);

        /// <summary>A point of the frame holding this one, in this frame.</summary>
        public Vector3 ToInner(Vector3 outer) => Vector3.Transform(outer - Origin, Quaternion.Conjugate(Orientation));

        /// <summary>
        /// This placement within <paramref name="outer"/>, its offset scaled by <paramref name="scale"/>
        /// first: how the game places an object's parts, whose offsets grow with the object.
        /// </summary>
        public Placement Within(Placement outer, float scale = 1f)
            => new Placement(outer.ToOuter(Origin * scale), outer.Orientation * Orientation);

        /// <summary>
        /// The same place turned to face <paramref name="degrees"/> clockwise from north, keeping the
        /// small lean towards it that the game keeps: its set_heading, as scenery is turned.
        /// </summary>
        public Placement Heading(double degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            Matrix4x4 m = Matrix4x4.CreateFromQuaternion(Orientation);
            Vector3 towards = new Vector3((float)Math.Sin(radians), (float)Math.Cos(radians), m.M23 + m.M13);
            float length = towards.Length();
            if (length < 0.0002f)
                return this;

            towards /= length;
            double heading = (450.0 - Math.Atan2(towards.Y, towards.X) * 180.0 / Math.PI) % 360.0;
            Quaternion turn = Quaternion.CreateFromYawPitchRoll((float)Math.Asin(towards.Z), 0f, (float)(-heading * Math.PI / 180.0));
            return new Placement(Origin, turn);
        }

        public override string ToString() => $"{Origin} {Orientation}";
    }
}
