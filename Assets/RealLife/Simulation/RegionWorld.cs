using System;
using System.Collections.Generic;

namespace RealLife.Simulation
{
    [Serializable]
    public struct GeoPoint
    {
        public double X, Z;
        public GeoPoint(double x, double z) => (X, Z) = (x, z);
    }

    [Serializable]
    public sealed class RoadSpan
    {
        public string Id;
        public int Part;
        public GeoPoint A, B;
        public float Width;
        public bool Trail;
        public int Sidewalk;
    }

    [Serializable]
    public sealed class BuildingFootprint
    {
        public string Id;
        public GeoPoint[] Footprint;
        public int[] Triangles;
        public float Height;
        public int Style;
    }

    [Serializable]
    public sealed class RegionChunk
    {
        public int X, Y;
        public float[] Heights;
        public RoadSpan[] Roads;
        public BuildingFootprint[] Buildings;
    }

    [Serializable]
    public sealed class RegionSource
    {
        public string Name, Sha256;
    }

    [Serializable]
    public sealed class RegionDefinition
    {
        public int Format, GridVersion, Face, Level, MinX, MinY, MaxX, MaxY, Steps;
        public string PackageId, Revision, Title, Coordinates, Attribution, OsmLatestEdit;
        public double Latitude, Longitude, Height, South, West, North, East;
        public GeoPoint[] Outline, Route;
        public RegionSource[] Sources;
        public RegionChunk[] Chunks;
    }

    public sealed class RegionWorld
    {
        public const int PackageFormat = 1;
        private readonly RegionDefinition data;
        private readonly Dictionary<CellKey, RegionChunk> chunks;

        public RegionWorld(RegionDefinition definition)
        {
            data = definition ?? throw new ArgumentNullException(nameof(definition));
            if (data.Format != PackageFormat || data.GridVersion != CellKey.CurrentGridVersion ||
                data.Level < 13 || data.Level > 20 || data.MaxX <= data.MinX || data.MaxY <= data.MinY ||
                data.MaxX - data.MinX > 64 || data.MaxY - data.MinY > 64 ||
                data.Steps < 2 || data.Steps > 64 || data.Outline?.Length != 4 || data.Route?.Length < 2 ||
                data.Route == null || data.Chunks?.Length != (data.MaxX - data.MinX) * (data.MaxY - data.MinY) ||
                !ValidPackageId(data.PackageId) || string.IsNullOrEmpty(data.Revision) || string.IsNullOrEmpty(data.Title) ||
                data.Sources == null || data.Sources.Length < 3)
                throw new ArgumentException("Invalid or unsupported region package.");

            Frame = new WorldFrame(data.Latitude, data.Longitude, data.Height);
            var width = data.Steps + 1;
            var buildingIds = new HashSet<string>(StringComparer.Ordinal);
            var roadParts = new HashSet<(string, int)>();
            chunks = new Dictionary<CellKey, RegionChunk>(data.Chunks.Length);
            foreach (var chunk in data.Chunks)
            {
                if (chunk == null || !InGrid(chunk.X, chunk.Y) || chunk.Heights?.Length != width * width ||
                    chunk.Roads == null || chunk.Buildings == null || !chunks.TryAdd(Key(chunk.X, chunk.Y), chunk))
                    throw new ArgumentException("Invalid or duplicate region cell.");
                foreach (var height in chunk.Heights)
                    if (!WorldFrame.IsFinite(height)) throw new ArgumentException("Invalid terrain height.");
                foreach (var road in chunk.Roads)
                {
                    if (road == null || string.IsNullOrEmpty(road.Id) || !roadParts.Add((road.Id, road.Part)) ||
                        !Finite(road.A) || !Finite(road.B) || !WorldFrame.IsFinite(road.Width) || road.Width <= 0)
                        throw new ArgumentException("Invalid or duplicate road span.");
                }
                foreach (var building in chunk.Buildings)
                {
                    if (building == null || string.IsNullOrEmpty(building.Id) || !buildingIds.Add(building.Id) ||
                        building.Footprint == null || building.Footprint.Length < 3 || building.Triangles == null ||
                        building.Triangles.Length != (building.Footprint.Length - 2) * 3 ||
                        !WorldFrame.IsFinite(building.Height) || building.Height < 2 || building.Style < 0)
                        throw new ArgumentException("Invalid or duplicate building footprint.");
                    foreach (var point in building.Footprint)
                        if (!Finite(point)) throw new ArgumentException("Invalid footprint vertex.");
                    foreach (var index in building.Triangles)
                        if (index < 0 || index >= building.Footprint.Length) throw new ArgumentException("Invalid roof index.");
                }
            }
            foreach (var pair in chunks) ValidateEdges(pair.Key, pair.Value);
        }

        public WorldFrame Frame { get; }
        public string PackageId => data.PackageId;
        public string Revision => data.Revision;
        public string Title => data.Title;
        public string Attribution => data.Attribution;
        public GeodeticPosition Anchor => new GeodeticPosition(data.Latitude, data.Longitude, data.Height);
        public IReadOnlyList<GeoPoint> Outline => data.Outline;
        public IReadOnlyList<GeoPoint> Route => data.Route;
        public IEnumerable<RegionChunk> Chunks => data.Chunks;
        public int Steps => data.Steps;

        public bool Covers(double latitude, double longitude) =>
            chunks.ContainsKey(CellKey.FromGlobal(WorldFrame.ToGlobal(latitude, longitude, 0), data.Level));

        public CellKey Key(int x, int y) => new CellKey(data.Face, data.Level, x, y);

        public CellKey CellAt(GeoPoint point) =>
            CellKey.FromGlobal(Frame.ToGlobal(new LocalPoint(point.X, 0, point.Z)), data.Level);

        public bool TryChunk(CellKey key, out RegionChunk chunk) => chunks.TryGetValue(key, out chunk);

        public IEnumerable<CellKey> Nearby(CellKey center, int radius)
        {
            for (var ring = 0; ring <= radius; ring++)
                for (var y = center.Y - ring; y <= center.Y + ring; y++)
                    for (var x = center.X - ring; x <= center.X + ring; x++)
                        if (Math.Max(Math.Abs(x - center.X), Math.Abs(y - center.Y)) == ring && InGrid(x, y))
                            yield return Key(x, y);
        }

        public LocalPoint TerrainVertex(CellKey key, RegionChunk chunk, int i, int j)
        {
            var p = Frame.ProjectDirection(key.DirectionAt((double)i / Steps, (double)j / Steps));
            return new LocalPoint(p.X, chunk.Heights[j * (Steps + 1) + i], p.Z);
        }

        public bool TryGround(GeoPoint point, out double height)
        {
            height = 0;
            if (!Finite(point)) return false;
            var key = CellKey.Locate(Frame.ToGlobal(new LocalPoint(point.X, 0, point.Z)), data.Level, out var x, out var y);
            // Permit rounding at the outer edge for road and coverage vertices.
            if (key.Face != data.Face || x < data.MinX - 1e-5 || y < data.MinY - 1e-5 ||
                x > data.MaxX + 1e-5 || y > data.MaxY + 1e-5)
                return false;
            key = Key(Math.Clamp(key.X, data.MinX, data.MaxX - 1), Math.Clamp(key.Y, data.MinY, data.MaxY - 1));
            var s = Math.Clamp((x - key.X) * Steps, 0, Steps);
            var t = Math.Clamp((y - key.Y) * Steps, 0, Steps);
            var i = Math.Min((int)s, Steps - 1);
            var j = Math.Min((int)t, Steps - 1);
            var chunk = chunks[key];
            var a = TerrainVertex(key, chunk, i, j);
            var b = TerrainVertex(key, chunk, i + 1, j + 1);
            var c = s - i >= t - j ? TerrainVertex(key, chunk, i + 1, j) : TerrainVertex(key, chunk, i, j + 1);
            var determinant = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
            var u = ((point.X - a.X) * (c.Z - a.Z) - (point.Z - a.Z) * (c.X - a.X)) / determinant;
            var v = ((b.X - a.X) * (point.Z - a.Z) - (b.Z - a.Z) * (point.X - a.X)) / determinant;
            height = a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
            return true;
        }

        public bool IsSafeStart(GeoPoint point)
        {
            if (!TryGround(point, out _) || DistanceToOutline(point) < 2) return false;
            return IsOpen(point, 0.65);
        }

        internal bool IsOpen(GeoPoint point, double margin)
        {
            foreach (var key in Nearby(CellAt(point), 1))
                foreach (var building in chunks[key].Buildings)
                    if (NearFootprint(point, building.Footprint, margin))
                        return false;
            return true;
        }

        public double DistanceToOutline(GeoPoint point)
        {
            var nearest = double.PositiveInfinity;
            for (var i = 0; i < data.Outline.Length; i++)
                nearest = Math.Min(nearest, DistanceSquared(point, data.Outline[i], data.Outline[(i + 1) % data.Outline.Length]));
            return Math.Sqrt(nearest);
        }

        private static bool NearFootprint(GeoPoint point, GeoPoint[] polygon, double margin)
        {
            var inside = false;
            for (var i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                if (DistanceSquared(point, a, b) < margin * margin) return true;
                if ((a.Z > point.Z) != (b.Z > point.Z) && point.X < (b.X - a.X) * (point.Z - a.Z) / (b.Z - a.Z) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        private static double DistanceSquared(GeoPoint p, GeoPoint a, GeoPoint b)
        {
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var length = dx * dx + dz * dz;
            var t = length > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / length, 0, 1) : 0;
            var x = p.X - a.X - dx * t;
            var z = p.Z - a.Z - dz * t;
            return x * x + z * z;
        }

        private bool InGrid(int x, int y) => x >= data.MinX && x < data.MaxX && y >= data.MinY && y < data.MaxY;
        private static bool Finite(GeoPoint p) => WorldFrame.IsFinite(p.X) && WorldFrame.IsFinite(p.Z);
        private static bool ValidPackageId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 64 && id[0] >= 'a' && id[0] <= 'z' &&
            Array.TrueForAll(id.ToCharArray(), c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '.' || c == '-');

        private void ValidateEdges(CellKey key, RegionChunk cell)
        {
            var width = Steps + 1;
            if (key.X + 1 < data.MaxX && TryChunk(Key(key.X + 1, key.Y), out var east))
                for (var i = 0; i < width; i++)
                    if (cell.Heights[i * width + Steps] != east.Heights[i * width])
                        throw new ArgumentException("Terrain cells disagree at their shared X edge.");
            if (key.Y + 1 < data.MaxY && TryChunk(Key(key.X, key.Y + 1), out var north))
                for (var i = 0; i < width; i++)
                    if (cell.Heights[Steps * width + i] != north.Heights[i])
                        throw new ArgumentException("Terrain cells disagree at their shared Y edge.");
        }
    }
}
