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
            if (!IsFinite(latitude) || !IsFinite(longitude) || !IsFinite(height) ||
                Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
                throw new ArgumentException("Invalid WGS84 origin.");

            var lat = latitude * Math.PI / 180.0;
            var lon = longitude * Math.PI / 180.0;
            var sinLat = Math.Sin(lat);
            var cosLat = Math.Cos(lat);
            var sinLon = Math.Sin(lon);
            var cosLon = Math.Cos(lon);
            var radius = SemiMajorAxis / Math.Sqrt(1 - EccentricitySquared * sinLat * sinLat);

            origin = new GlobalPosition(
                (radius + height) * cosLat * cosLon,
                (radius + height) * cosLat * sinLon,
                (radius * (1 - EccentricitySquared) + height) * sinLat);
            east = new GlobalPosition(-sinLon, cosLon, 0);
            up = new GlobalPosition(cosLat * cosLon, cosLat * sinLon, sinLat);
            north = new GlobalPosition(-sinLat * cosLon, -sinLat * sinLon, cosLat);
        }

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
}
