using System;

namespace RealLife.Simulation
{
    [Serializable]
    public struct LocalPoint
    {
        public double X;
        public double Y;
        public double Z;

        public LocalPoint(double x, double y, double z) => (X, Y, Z) = (x, y, z);

        public bool IsFinite => WorldFrame.IsFinite(X) && WorldFrame.IsFinite(Y) && WorldFrame.IsFinite(Z);
    }

    [Serializable]
    public struct GlobalPosition
    {
        public double X;
        public double Y;
        public double Z;

        public GlobalPosition(double x, double y, double z) => (X, Y, Z) = (x, y, z);
        public bool IsFinite => WorldFrame.IsFinite(X) && WorldFrame.IsFinite(Y) && WorldFrame.IsFinite(Z);
    }

    public readonly struct GeodeticPosition
    {
        public readonly double Latitude;
        public readonly double Longitude;
        public readonly double Height;

        public GeodeticPosition(double latitude, double longitude, double height) =>
            (Latitude, Longitude, Height) = (latitude, longitude, height);
    }

    public sealed class WorldFrame
    {
        private const double SemiMajorAxis = 6378137.0;
        private const double Flattening = 1.0 / 298.257223563;
        private const double EccentricitySquared = Flattening * (2 - Flattening);

        private readonly GlobalPosition origin;
        private readonly GlobalPosition east;
        private readonly GlobalPosition up;
        private readonly GlobalPosition north;

        public WorldFrame(double latitude, double longitude, double height)
        {
            origin = ToGlobal(latitude, longitude, height);
            var lat = latitude * Math.PI / 180.0;
            var lon = longitude * Math.PI / 180.0;
            var sinLat = Math.Sin(lat);
            var cosLat = Math.Cos(lat);
            var sinLon = Math.Sin(lon);
            var cosLon = Math.Cos(lon);
            east = new GlobalPosition(-sinLon, cosLon, 0);
            up = new GlobalPosition(cosLat * cosLon, cosLat * sinLon, sinLat);
            north = new GlobalPosition(-sinLat * cosLon, -sinLat * sinLon, cosLat);
        }

        public static GlobalPosition ToGlobal(double latitude, double longitude, double height)
        {
            if (!IsFinite(latitude) || !IsFinite(longitude) || !IsFinite(height) ||
                Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
                throw new ArgumentException("Invalid WGS84 coordinates.");

            var lat = latitude * Math.PI / 180;
            var lon = longitude * Math.PI / 180;
            var sin = Math.Sin(lat);
            var radius = SemiMajorAxis / Math.Sqrt(1 - EccentricitySquared * sin * sin);
            var horizontal = (radius + height) * Math.Cos(lat);
            return new GlobalPosition(horizontal * Math.Cos(lon), horizontal * Math.Sin(lon),
                (radius * (1 - EccentricitySquared) + height) * sin);
        }

        public static GeodeticPosition ToGeodetic(GlobalPosition point)
        {
            if (!point.IsFinite || (point.X == 0 && point.Y == 0 && point.Z == 0))
                throw new ArgumentException("Invalid ECEF coordinates.", nameof(point));

            var horizontal = Math.Sqrt(point.X * point.X + point.Y * point.Y);
            var lat = Math.Atan2(point.Z, horizontal * (1 - EccentricitySquared));
            for (var iteration = 0; iteration < 8; iteration++)
            {
                var sin = Math.Sin(lat);
                var radius = SemiMajorAxis / Math.Sqrt(1 - EccentricitySquared * sin * sin);
                var next = Math.Atan2(point.Z + EccentricitySquared * radius * sin, horizontal);
                if (Math.Abs(next - lat) < 1e-15) break;
                lat = next;
            }
            var sinLat = Math.Sin(lat);
            var height = horizontal * Math.Cos(lat) + point.Z * sinLat -
                SemiMajorAxis * Math.Sqrt(1 - EccentricitySquared * sinLat * sinLat);
            return new GeodeticPosition(lat * 180 / Math.PI, Math.Atan2(point.Y, point.X) * 180 / Math.PI, height);
        }

        // Intersect an Earth-centred ray with the anchor's local horizontal plane.
        public LocalPoint ProjectDirection(GlobalPosition direction)
        {
            var scale = Dot(origin, up) / Dot(direction, up);
            return ToLocal(new GlobalPosition(direction.X * scale, direction.Y * scale, direction.Z * scale));
        }

        private static double Dot(GlobalPosition a, GlobalPosition b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public GlobalPosition ToGlobal(LocalPoint point) => new GlobalPosition(
            origin.X + point.X * east.X + point.Y * up.X + point.Z * north.X,
            origin.Y + point.X * east.Y + point.Y * up.Y + point.Z * north.Y,
            origin.Z + point.X * east.Z + point.Y * up.Z + point.Z * north.Z);

        public LocalPoint ToLocal(GlobalPosition point)
        {
            var x = point.X - origin.X;
            var y = point.Y - origin.Y;
            var z = point.Z - origin.Z;
            return new LocalPoint(
                x * east.X + y * east.Y + z * east.Z,
                x * up.X + y * up.Y + z * up.Z,
                x * north.X + y * north.Y + z * north.Z);
        }

        internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    // Grid v1 uses ECEF axes, dominant-axis ties in X/Y/Z order, and half-open cells.
    public readonly struct CellKey : IEquatable<CellKey>
    {
        public const int CurrentGridVersion = 1;
        public int GridVersion => CurrentGridVersion;
        public readonly int Face, Level, X, Y;

        public CellKey(int face, int level, int x, int y)
        {
            if (face < 0 || face > 5 || level < 0 || level > 26 || x < 0 || y < 0 ||
                x >= (1 << level) || y >= (1 << level))
                throw new ArgumentOutOfRangeException(nameof(x), "Invalid cube-sphere cell.");
            (Face, Level, X, Y) = (face, level, x, y);
        }

        public CellKey Parent => Level == 0 ? this : new CellKey(Face, Level - 1, X / 2, Y / 2);

        public static CellKey FromGlobal(GlobalPosition point, int level) => Locate(point, level, out _, out _);

        internal static CellKey Locate(GlobalPosition point, int level, out double gridX, out double gridY)
        {
            if (!point.IsFinite || level < 0 || level > 26 || (point.X == 0 && point.Y == 0 && point.Z == 0))
                throw new ArgumentException("Invalid cube-sphere lookup.");
            var x = Math.Abs(point.X);
            var y = Math.Abs(point.Y);
            var z = Math.Abs(point.Z);
            int face;
            double u, v;
            if (x >= y && x >= z) { face = point.X >= 0 ? 0 : 1; u = point.Y / x; v = point.Z / x; }
            else if (y >= z) { face = point.Y >= 0 ? 2 : 3; u = point.X / y; v = point.Z / y; }
            else { face = point.Z >= 0 ? 4 : 5; u = point.X / z; v = point.Y / z; }
            var size = 1 << level;
            gridX = (u + 1) * 0.5 * size;
            gridY = (v + 1) * 0.5 * size;
            return new CellKey(face, level, Math.Min((int)gridX, size - 1), Math.Min((int)gridY, size - 1));
        }

        public GlobalPosition DirectionAt(double x, double y)
        {
            var u = 2 * (X + x) / (1 << Level) - 1;
            var v = 2 * (Y + y) / (1 << Level) - 1;
            return Face switch
            {
                0 => new GlobalPosition(1, u, v), 1 => new GlobalPosition(-1, u, v),
                2 => new GlobalPosition(u, 1, v), 3 => new GlobalPosition(u, -1, v),
                4 => new GlobalPosition(u, v, 1), _ => new GlobalPosition(u, v, -1)
            };
        }

        public bool Equals(CellKey other) => Face == other.Face && Level == other.Level && X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is CellKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(GridVersion, Face, Level, X, Y);
        public override string ToString() => $"{GridVersion}:{Face}:{Level}:{X}:{Y}";
    }
}
