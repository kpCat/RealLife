using System;
using System.Collections.Generic;
using System.Linq;

namespace RealLife.Simulation
{
    public sealed class Buildings
    {
        public const int GeneratorVersion = 1;
        private const int PerKind = 5;
        private readonly Dictionary<string, BuildingPlan> plans = new Dictionary<string, BuildingPlan>(StringComparer.Ordinal);
        private readonly Dictionary<string, BuildingDoor> doors = new Dictionary<string, BuildingDoor>(StringComparer.Ordinal);
        private readonly Dictionary<string, BuildingAnchor> anchors = new Dictionary<string, BuildingAnchor>(StringComparer.Ordinal);
        private readonly HashSet<string> openDoors = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyList<BuildingPlan> Plans { get; }

        public Buildings(RegionWorld world)
        {
            var candidates = Candidates(world).OrderBy(candidate => candidate.RouteDistance)
                .ThenBy(candidate => candidate.Footprint.Id, StringComparer.Ordinal).ToArray();
            var owners = new HashSet<CellKey>();
            var selected = new List<BuildingPlan>();
            Select(BuildingKind.TwoStoreyHouse, candidate => candidate.Width >= 8.6 && candidate.Depth >= 9.5 &&
                candidate.Footprint.Height >= 5, true);
            Select(BuildingKind.PublicPremises, candidate => candidate.Width >= 7.2 && candidate.Depth >= 7.5, false);
            Select(BuildingKind.SingleStoreyHouse, _ => true, false);
            Plans = selected.AsReadOnly();

            void Select(BuildingKind kind, Func<Candidate, bool> eligible, bool twoStorey)
            {
                var count = 0;
                foreach (var candidate in candidates)
                {
                    if (!eligible(candidate) || !owners.Add(candidate.Owner)) continue;
                    var storeys = twoStorey ? 2 : 1;
                    var plan = new BuildingPlan(world.PackageId, candidate.Footprint.Id, candidate.Owner, kind,
                        candidate.Footprint.Style, candidate.Width, candidate.Depth, candidate.Origin, candidate.RightX,
                        candidate.RightZ, candidate.GroundFloor, candidate.Lowest - .35, candidate.EntryGround,
                        Math.Max(BuildingPlan.StoreyHeight * storeys + .24,
                            Math.Min(candidate.Footprint.Height, BuildingPlan.StoreyHeight * storeys + 1.35)));
                    BuildingLayouts.Furnish(plan);
                    plans.Add(plan.Id, plan);
                    selected.Add(plan);
                    foreach (var door in plan.Doors) doors.Add(door.Id, door);
                    foreach (var anchor in plan.Anchors) anchors.Add(anchor.Id, anchor);
                    if (++count == PerKind) return;
                }
            }
        }

        public bool TryGet(string id, out BuildingPlan plan)
        {
            plan = null;
            return id != null && plans.TryGetValue(id, out plan);
        }

        public bool TryGetDoor(string id, out BuildingDoor door)
        {
            door = null;
            return id != null && doors.TryGetValue(id, out door);
        }

        public bool TryGetAnchor(string id, out BuildingAnchor anchor)
        {
            anchor = null;
            return id != null && anchors.TryGetValue(id, out anchor);
        }

        public bool TryLocate(LocalPoint position, bool strict, out BuildingPlan plan)
        {
            foreach (var candidate in Plans)
                if (candidate.TryLocate(position, strict, out _))
                {
                    plan = candidate;
                    return true;
                }
            plan = null;
            return false;
        }

        public bool IsDoorOpen(string id) => openDoors.Contains(id);

        public bool TryToggleDoor(string id, out bool isOpen)
        {
            isOpen = false;
            if (!TryGetDoor(id, out _)) return false;
            if (!openDoors.Remove(id))
            {
                openDoors.Add(id);
                isOpen = true;
            }
            return true;
        }

        public string[] CaptureOpenDoors() => openDoors.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        public bool ValidateOpenDoors(string[] ids)
        {
            if (ids == null || ids.Length > doors.Count) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return Array.TrueForAll(ids, id => TryGetDoor(id, out _) && seen.Add(id));
        }

        public bool RestoreOpenDoors(string[] ids)
        {
            if (!ValidateOpenDoors(ids)) return false;
            openDoors.Clear();
            openDoors.UnionWith(ids);
            return true;
        }

        public bool IsPoseClearOfDoors(LocalPoint pose, string[] openDoorIds)
        {
            foreach (var plan in Plans)
            {
                if (plan.DistanceToFootprint(pose) > .35) continue;
                foreach (var door in plan.Doors)
                    if (door.Obstructs(pose, openDoorIds != null && Array.IndexOf(openDoorIds, door.Id) >= 0))
                        return false;
            }
            return true;
        }

        private static IEnumerable<Candidate> Candidates(RegionWorld world)
        {
            foreach (var chunk in world.Chunks)
            {
                var owner = world.Key(chunk.X, chunk.Y);
                var nearbyRoads = world.Nearby(owner, 1).SelectMany(key =>
                {
                    world.TryChunk(key, out var current);
                    return current.Roads;
                }).ToArray();
                foreach (var footprint in chunk.Buildings)
                {
                    var polygon = footprint.Footprint;
                    if (polygon.Length != 4 || footprint.Height > 8.5) continue;
                    var area = 0.0;
                    for (var i = 0; i < 4; i++)
                        area += polygon[i].X * polygon[(i + 1) % 4].Z - polygon[i].Z * polygon[(i + 1) % 4].X;
                    var step = area > 0 ? 1 : 3;
                    Candidate best = null;
                    var bestScore = double.PositiveInfinity;
                    for (var i = 0; i < 4; i++)
                    {
                        var a = polygon[i];
                        var b = polygon[(i + step) % 4];
                        var c = polygon[(i + step * 2) % 4];
                        var d = polygon[(i + step * 3) % 4];
                        var width = Distance(a, b);
                        if (width < 6.4 || width > 20) continue;
                        var ux = (b.X - a.X) / width;
                        var uz = (b.Z - a.Z) / width;
                        var origin = new GeoPoint((a.X + b.X) / 2, (a.Z + b.Z) / 2);
                        GeoPoint At(double x, double z) => new GeoPoint(origin.X + ux * x - uz * z, origin.Z + uz * x + ux * z);
                        var depth = ((c.Z + d.Z - a.Z - b.Z) * ux - (c.X + d.X - a.X - b.X) * uz) / 2;
                        if (depth < 6.6 || depth > 21 || Distance(At(width / 2, depth), c) > .42 ||
                            Distance(At(-width / 2, depth), d) > .42)
                            continue;

                        var highest = double.NegativeInfinity;
                        var lowest = double.PositiveInfinity;
                        for (var row = 0; row <= 4; row++)
                            for (var col = 0; col <= 4; col++)
                            {
                                if (!world.TryGround(At((col / 4.0 - .5) * width, depth * row / 4.0), out var terrain))
                                    highest = double.PositiveInfinity;
                                highest = Math.Max(highest, terrain);
                                lowest = Math.Min(lowest, terrain);
                            }
                        var floor = highest + .18;
                        var entry = At(0, -BuildingPlan.EntryRun);
                        if (highest - lowest > .75 || world.DistanceToOutline(entry) < 3 ||
                            !world.TryGround(entry, out var entryGround) || Math.Abs(floor - entryGround) > .76)
                            continue;
                        var gradeOpen = true;
                        for (var t = 0.0; t < 1; t += .25)
                        {
                            if (!world.TryGround(At(0, -BuildingPlan.EntryRun * t), out var ground) ||
                                ground > floor + (entryGround - floor) * t + .055)
                                gradeOpen = false;
                        }
                        if (!gradeOpen) continue;
                        var approach = At(0, -BuildingPlan.EntryRun - .30);
                        var road = nearbyRoads.Select(span => Closest(approach, span.A, span.B))
                            .OrderBy(point => Distance(approach, point)).Take(5)
                            .Where(point => Distance(approach, point) <= 48 && PathClear(world, approach, point))
                            .Select(point => (GeoPoint?)point).FirstOrDefault();
                        if (!road.HasValue) continue;
                        var score = Distance(approach, road.Value) + Math.Abs(floor - entryGround) * 8;
                        if (score >= bestScore) continue;
                        bestScore = score;
                        best = new Candidate
                        {
                            Owner = owner, Footprint = footprint, Origin = origin, Width = width, Depth = depth, Lowest = lowest,
                            GroundFloor = floor, EntryGround = entryGround, RightX = ux, RightZ = uz,
                            RouteDistance = RouteDistance(world.Route, approach)
                        };
                    }
                    if (best != null) yield return best;
                }
            }
        }

        private static bool PathClear(RegionWorld world, GeoPoint source, GeoPoint destination)
        {
            var length = Distance(source, destination);
            var count = Math.Max(1, (int)Math.Ceiling(length / .65));
            for (var i = 0; i <= count; i++)
            {
                var t = i / (double)count;
                var p = new GeoPoint(source.X + (destination.X - source.X) * t, source.Z + (destination.Z - source.Z) * t);
                if (!world.IsOpen(p, .55)) return false;
            }
            return length < .1 || world.TryGround(source, out _) && world.TryGround(destination, out _);
        }

        private static double RouteDistance(IReadOnlyList<GeoPoint> route, GeoPoint point)
        {
            var distance = double.PositiveInfinity;
            for (var i = 1; i < route.Count; i++)
                distance = Math.Min(distance, Distance(point, Closest(point, route[i - 1], route[i])));
            return distance;
        }

        private static GeoPoint Closest(GeoPoint p, GeoPoint a, GeoPoint b)
        {
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var length = dx * dx + dz * dz;
            var t = length > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / length, 0, 1) : 0;
            return new GeoPoint(a.X + t * dx, a.Z + t * dz);
        }

        private static double Distance(GeoPoint a, GeoPoint b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        private sealed class Candidate
        {
            public CellKey Owner;
            public BuildingFootprint Footprint;
            public GeoPoint Origin;
            public double GroundFloor, Lowest, EntryGround, RightX, RightZ, Width, Depth, RouteDistance;
        }
    }
}
