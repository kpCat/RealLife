using System;
using System.Collections.Generic;
using System.Linq;

namespace RealLife.Simulation
{
    public readonly struct ResidentWaypoint
    {
        public LocalPoint Position { get; }
        public string BuildingId { get; }
        public string RoomId { get; }

        internal ResidentWaypoint(LocalPoint position, string buildingId, string roomId) =>
            (Position, BuildingId, RoomId) = (position, buildingId, roomId);
    }

    public readonly struct ResidentStep
    {
        public double Length { get; }
        public string DoorId { get; }

        internal ResidentStep(double length, string doorId) => (Length, DoorId) = (length, doorId);
    }

    public sealed class ResidentNavigation
    {
        private const double RoadSpacing = 12, GroundOffset = .065, StructureOffset = .025;
        private const double OutdoorStep = .55, OutdoorClearance = .55;
        private readonly RegionWorld world;
        private readonly Buildings buildings;
        private readonly List<Node> nodes = new List<Node>();
        private readonly Dictionary<string, int> approaches = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]> rest = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]> work = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]> outside = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]> anchors = new Dictionary<string, int[]>(StringComparer.Ordinal);

        public ResidentNavigation(RegionWorld world, Buildings buildings)
        {
            this.world = world;
            this.buildings = buildings;
            var roads = BuildRoads();
            var roadNodeCount = nodes.Count;
            foreach (var building in buildings.Plans.OrderBy(plan => plan.Id, StringComparer.Ordinal))
            {
                if (!ConnectApproach(building, roads, out var approach, out var road)) continue;
                approaches.Add(building.Id, approach);
                outside.Add(building.Id, new[] { road });
                BuildInterior(building, approach);
            }
            SetComponents();
            foreach (var pair in approaches)
                outside[pair.Key] = OutdoorDestinations(pair.Value, outside[pair.Key][0], roadNodeCount);
        }

        public bool IsNode(int id) => (uint)id < (uint)nodes.Count;
        public ResidentWaypoint GetNode(int id) => nodes[id].Waypoint;

        public bool IsReachable(int from, int to) => IsNode(from) && IsNode(to) &&
            nodes[from].Component == nodes[to].Component;

        public bool TryRest(string buildingId, int variant, out int node) =>
            TryPlace(rest, buildingId, variant, out node);

        public bool TryWork(string buildingId, int variant, out int node) =>
            TryPlace(work, buildingId, variant, out node);

        public bool TryOutside(string buildingId, int variant, out int node) =>
            TryPlace(outside, buildingId, variant, out node);

        public bool TryFindOutdoor(GeoPoint point, double maxDistance, out int node) =>
            TryFindOutdoor(point, maxDistance, -1, out node);

        public bool TryFindOutdoor(GeoPoint point, double maxDistance, int connectedTo, out int node)
        {
            node = -1;
            if (!(maxDistance >= 0) || !world.TryGround(point, out _)) return false;
            var candidates = Enumerable.Range(0, nodes.Count)
                .Where(id => GetNode(id).BuildingId == null && (connectedTo < 0 || IsReachable(id, connectedTo)))
                .Select(id => (Id: id, Distance: FlatDistance(point, Flat(GetNode(id).Position))))
                .Where(item => item.Distance <= maxDistance).OrderBy(item => item.Distance);
            foreach (var candidate in candidates)
            {
                if (!ClearOutside(point, Flat(GetNode(candidate.Id).Position))) continue;
                node = candidate.Id;
                return true;
            }
            return false;
        }

        public bool TryAnchor(string anchorId, out int node)
        {
            node = -1;
            return anchorId != null && anchors.TryGetValue(anchorId, out var candidates) &&
                buildings.TryGetAnchor(anchorId, out var anchor) && TryReachable(anchor.BuildingId, candidates, 0, out node);
        }

        private bool TryPlace(Dictionary<string, int[]> places, string id, int variant, out int node)
        {
            node = -1;
            return id != null && places.TryGetValue(id, out var options) && TryReachable(id, options, variant, out node);
        }

        private bool TryReachable(string id, int[] options, int variant, out int node)
        {
            node = -1;
            var approach = approaches[id];
            var start = (int)((uint)variant % (uint)options.Length);
            for (var offset = 0; offset < options.Length; offset++)
            {
                var candidate = options[(start + offset) % options.Length];
                if (!IsReachable(approach, candidate)) continue;
                node = candidate;
                return true;
            }
            return false;
        }

        public bool TryRoute(int from, int to, out int[] route)
        {
            route = Array.Empty<int>();
            if (!IsReachable(from, to)) return false;
            if (from == to)
            {
                route = new[] { from };
                return true;
            }

            var distance = new double[nodes.Count];
            var previous = new int[nodes.Count];
            Array.Fill(distance, double.PositiveInfinity);
            distance[from] = 0;
            var open = new SortedSet<(double Cost, int Id)> { (Estimate(from), from) };
            while (open.Count > 0)
            {
                var current = open.Min;
                open.Remove(current);
                if (current.Id == to)
                {
                    var path = new List<int> { to };
                    for (var id = to; id != from;) path.Add(id = previous[id]);
                    path.Reverse();
                    route = path.ToArray();
                    return true;
                }
                foreach (var edge in nodes[current.Id].Edges)
                {
                    var candidate = distance[current.Id] + edge.Step.Length;
                    if (candidate >= distance[edge.To]) continue;
                    var estimate = Estimate(edge.To);
                    open.Remove((distance[edge.To] + estimate, edge.To));
                    distance[edge.To] = candidate;
                    previous[edge.To] = current.Id;
                    open.Add((candidate + estimate, edge.To));
                }
            }
            return false;

            double Estimate(int fromNode) => FlatDistance(GetNode(fromNode).Position, GetNode(to).Position);
        }

        public bool TryStep(int from, int to, out ResidentStep step)
        {
            if (TryEdge(from, to, out var edge))
            {
                step = edge.Step;
                return true;
            }
            step = default;
            return false;
        }

        public LocalPoint Position(int from, int to, double progress)
        {
            var a = GetNode(from).Position;
            if (from == to || progress <= 0) return a;
            var b = GetNode(to).Position;
            if (progress >= 1) return b;
            var position = Lerp(a, b, progress);
            if (!TryEdge(from, to, out var edge)) throw new ArgumentException("Unknown walking step.");
            if (edge.Surface == Surface.Ground && world.TryGround(Flat(position), out var y))
                return new LocalPoint(position.X, y + GroundOffset, position.Z);
            if (edge.Surface == Surface.EntryDoor && buildings.TryGet(edge.BuildingId, out var building))
                return EntryHeight(building, position);
            return position;
        }

        public string BuildingAt(int from, int to, double progress)
        {
            if (from == to || progress <= 0) return GetNode(from).BuildingId;
            if (progress >= 1) return GetNode(to).BuildingId;
            return TryEdge(from, to, out var edge) ? edge.BuildingId : null;
        }

        public string RoomAt(int from, int to, double progress)
        {
            var a = GetNode(from);
            if (from == to || progress <= 0) return a.RoomId;
            var b = GetNode(to);
            if (progress >= 1) return b.RoomId;
            if (a.RoomId == b.RoomId) return a.RoomId;
            if (TryEdge(from, to, out var edge) && buildings.TryGetDoor(edge.Step.DoorId, out var door) &&
                buildings.TryGet(door.BuildingId, out var building))
            {
                var start = DoorNormal(door, building.ToLocal(a.Position));
                var end = DoorNormal(door, building.ToLocal(b.Position));
                return progress < start / (start - end) ? a.RoomId : b.RoomId;
            }
            return progress < .5 ? a.RoomId : b.RoomId;
        }

        private List<RoadLink> BuildRoads()
        {
            var byPosition = new Dictionary<(double X, double Z), int>();
            var links = new List<RoadLink>();
            foreach (var span in world.Chunks.SelectMany(chunk => chunk.Roads)
                .OrderBy(road => road.Id, StringComparer.Ordinal).ThenBy(road => road.Part))
            {
                var count = Math.Max(1, (int)Math.Ceiling(FlatDistance(span.A, span.B) / RoadSpacing));
                var a = span.A;
                for (var i = 1; i <= count; i++)
                {
                    var b = i == count ? span.B : Lerp(span.A, span.B, i / (double)count);
                    if (FlatDistance(a, b) > .015 && ClearOutside(a, b))
                    {
                        var from = RoadNode(a);
                        var to = RoadNode(b);
                        if (Link(from, to, Surface.Ground)) links.Add(new RoadLink(from, to));
                    }
                    a = b;
                }
            }

            // Baked spans normally share exact endpoints. Join only short, clear seams.
            const double seam = .75;
            var nearby = new Dictionary<(int X, int Z), List<int>>();
            for (var id = 0; id < nodes.Count; id++)
            {
                var point = Flat(GetNode(id).Position);
                var bucket = ((int)Math.Floor(point.X / seam), (int)Math.Floor(point.Z / seam));
                for (var dz = -1; dz <= 1; dz++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (nearby.TryGetValue((bucket.Item1 + dx, bucket.Item2 + dz), out var neighbors))
                            foreach (var neighbor in neighbors)
                            {
                                var target = Flat(GetNode(neighbor).Position);
                                if (FlatDistance(point, target) <= seam && ClearOutside(point, target))
                                    Link(id, neighbor, Surface.Ground);
                            }
                if (!nearby.TryGetValue(bucket, out var current)) nearby.Add(bucket, current = new List<int>());
                current.Add(id);
            }
            return links;

            int RoadNode(GeoPoint point)
            {
                var key = (point.X, point.Z);
                if (!byPosition.TryGetValue(key, out var id))
                    byPosition.Add(key, id = AddNode(OnGround(point)));
                return id;
            }
        }

        private bool ConnectApproach(BuildingPlan building, List<RoadLink> roads, out int approach, out int road)
        {
            approach = road = -1;
            var origin = Flat(building.Approach);
            var candidates = roads.Select(link => (Link: link, Point: Project(origin,
                Flat(GetNode(link.A).Position), Flat(GetNode(link.B).Position))))
                .Select(candidate => (candidate.Link, candidate.Point, Distance: FlatDistance(origin, candidate.Point)))
                .Where(candidate => candidate.Distance <= 48)
                .OrderBy(candidate => candidate.Distance);
            foreach (var candidate in candidates)
            {
                if (!ClearOutside(origin, candidate.Point)) continue;
                var a = candidate.Link.A;
                var b = candidate.Link.B;
                if (FlatDistance(Flat(GetNode(a).Position), candidate.Point) < .015) road = a;
                else if (FlatDistance(Flat(GetNode(b).Position), candidate.Point) < .015) road = b;
                else
                {
                    road = AddNode(OnGround(candidate.Point));
                    Link(a, road, Surface.Ground);
                    Link(road, b, Surface.Ground);
                }
                if (candidate.Distance < .015) approach = road;
                else
                {
                    approach = AddNode(OnGround(origin));
                    Link(approach, road, Surface.Ground);
                }
                return true;
            }
            return false;
        }

        private int[] OutdoorDestinations(int approach, int road, int roadNodeCount)
        {
            var origin = GetNode(approach).Position;
            var options = Enumerable.Range(0, roadNodeCount).Where(id => IsReachable(road, id))
                .Select(id => (Id: id, Distance: FlatDistance(origin, GetNode(id).Position)))
                .Where(item => item.Distance >= 8 && item.Distance <= 55).OrderBy(item => item.Distance);
            var result = new List<int>();
            foreach (var option in options)
            {
                if (result.Any(id => FlatDistance(GetNode(id).Position, GetNode(option.Id).Position) < 4)) continue;
                result.Add(option.Id);
                if (result.Count == 8) break;
            }
            if (result.Count == 0) result.Add(road);
            return result.ToArray();
        }

        private bool ClearOutside(GeoPoint a, GeoPoint b)
        {
            var distance = FlatDistance(a, b);
            var count = Math.Max(1, (int)Math.Ceiling(distance / OutdoorStep));
            var previousHeight = 0.0;
            for (var i = 0; i <= count; i++)
            {
                var point = Lerp(a, b, i / (double)count);
                if (!world.TryGround(point, out var height) || !world.IsOpen(point, OutdoorClearance) ||
                    i > 0 && Math.Abs(height - previousHeight) > distance / count * .65 + .015) return false;
                previousHeight = height;
            }
            return true;
        }

        private LocalPoint OnGround(GeoPoint point)
        {
            world.TryGround(point, out var height);
            return new LocalPoint(point.X, height + GroundOffset, point.Z);
        }

        private void BuildInterior(BuildingPlan building, int approach)
        {
            var rooms = building.Rooms.ToDictionary(room => room.Id, _ => new List<int>(), StringComparer.Ordinal);
            var bedPlaces = new List<int>();
            var chairPlaces = new List<int>();
            var counterPlaces = new List<int>();
            foreach (var door in building.Doors) BuildDoor(door);

            foreach (var room in building.Rooms)
            {
                const double spacing = .85;
                var columns = Math.Max(1, (int)Math.Ceiling((room.Right - room.Left) / spacing));
                var rows = Math.Max(1, (int)Math.Ceiling((room.Back - room.Front) / spacing));
                for (var row = 0; row < rows; row++)
                    for (var column = 0; column < columns; column++)
                    {
                        var x = room.Left + (room.Right - room.Left) * (column + .5) / columns;
                        var z = room.Front + (room.Back - room.Front) * (row + .5) / rows;
                        AddRoomNode(room.Id, AtFloor(room.Floor, x, z));
                    }
            }

            foreach (var piece in building.Furniture)
            {
                var room = building.Rooms.First(candidate => candidate.Floor == piece.Floor &&
                    Inside(candidate, piece.X, piece.Z));
                var halfWidth = piece.Width / 2 + .36;
                var halfDepth = piece.Depth / 2 + .36;
                var adjacent = new[] { (0.0, halfDepth), (halfWidth, 0.0), (-halfWidth, 0.0), (0.0, -halfDepth),
                    (-halfWidth, halfDepth), (halfWidth, halfDepth), (-halfWidth, -halfDepth), (halfWidth, -halfDepth) };
                var furniturePlaces = new List<int>();
                foreach (var (dx, dz) in adjacent)
                {
                    var id = AddRoomNode(room.Id, AtFloor(room.Floor, piece.X + dx, piece.Z + dz));
                    if (id < 0) continue;
                    furniturePlaces.Add(id);
                }
                if (furniturePlaces.Count == 0) continue;
                if (piece.Anchor != null) anchors.Add(piece.Anchor.Id, furniturePlaces.ToArray());
                if (piece.Kind == BuildingFurnitureKind.Bed) bedPlaces.AddRange(furniturePlaces);
                if (piece.Kind == BuildingFurnitureKind.Chair) chairPlaces.AddRange(furniturePlaces);
                if (piece.Kind == BuildingFurnitureKind.Counter && room.Role == BuildingRoomRole.Shop)
                    counterPlaces.AddRange(furniturePlaces);
            }

            if (building.Stairs != null) BuildStairs(building.Stairs);
            foreach (var room in building.Rooms)
            {
                var available = rooms[room.Id];
                for (var i = 0; i < available.Count; i++)
                    for (var j = i + 1; j < available.Count; j++)
                    {
                        var a = GetNode(available[i]).Position;
                        var b = GetNode(available[j]).Position;
                        if (FlatDistance(a, b) <= 2.6 && ClearFloor(building, a, b, room.Floor))
                            Link(available[i], available[j], Surface.Structure, building.Id);
                    }
            }

            var restRooms = building.Rooms.Where(room => room.Role == BuildingRoomRole.Bedroom ||
                room.Role == BuildingRoomRole.Living).OrderByDescending(room => room.Floor);
            var places = bedPlaces.Distinct().Concat(chairPlaces.Distinct())
                .Concat(restRooms.SelectMany(room => rooms[room.Id].Skip(rooms[room.Id].Count / 3).Take(3)))
                .Distinct().ToArray();
            if (places.Length > 0) rest.Add(building.Id, places);
            if (building.Kind == BuildingKind.PublicPremises)
            {
                var shopFloor = building.Rooms.Where(room => room.Role == BuildingRoomRole.Shop)
                    .SelectMany(room => rooms[room.Id].Skip(rooms[room.Id].Count / 3).Take(6));
                var stations = new List<int>();
                foreach (var id in counterPlaces.Concat(shopFloor))
                    if (!stations.Any(station => FlatDistance(GetNode(station).Position, GetNode(id).Position) < .72))
                        stations.Add(id);
                if (stations.Count > 0) work.Add(building.Id, stations.ToArray());
            }

            LocalPoint AtFloor(int floor, double x, double z) => building.ToWorld(x,
                floor * BuildingPlan.StoreyHeight + StructureOffset, z);

            int AddRoomNode(string roomId, LocalPoint point)
            {
                var room = building.Rooms.First(item => item.Id == roomId);
                var local = building.ToLocal(point);
                if (!Inside(room, local.X, local.Z) || !ClearPose(building, point, room.Floor)) return -1;
                var ids = rooms[roomId];
                foreach (var id in ids)
                    if (FlatDistance(GetNode(id).Position, point) < .035) return id;
                var node = AddNode(point, building.Id, roomId);
                ids.Add(node);
                return node;
            }

            void BuildStairs(BuildingStairs stairs)
            {
                var low = AddRoomNode(stairs.LowerRoomId, AtFloor(0, 0, BuildingStairs.Bottom - .30));
                var high = AddRoomNode(stairs.UpperRoomId, AtFloor(1, 0, BuildingStairs.Top + .34));
                if (low < 0 || high < 0) return;
                // The centre line follows the same continuous support as the physical stair collider.
                var foot = AddNode(AtFloor(0, 0, BuildingStairs.Bottom), building.Id, stairs.LowerRoomId);
                var crest = AddNode(AtFloor(1, 0, BuildingStairs.Top), building.Id, stairs.UpperRoomId);
                Link(low, foot, Surface.Structure, building.Id);
                Link(foot, crest, Surface.Structure, building.Id);
                Link(crest, high, Surface.Structure, building.Id);
            }

            void BuildDoor(BuildingDoor door)
            {
                var floor = (int)(door.Y / BuildingPlan.StoreyHeight);
                var swingSide = door.AlongX ? -door.Swing : door.Swing;
                foreach (var along in new[] { 0.0, .12, -.12 })
                {
                    // Begin the traversal outside the leaf's swept area on either side.
                    var negative = DoorStand(-1);
                    var positive = DoorStand(1);
                    if (door.ContainsSwing(negative) || door.ContainsSwing(positive) ||
                        !ClearDoor(building, door, negative, positive, floor)) continue;
                    var positiveRoom = SideRoom(positive);
                    var negativeRoom = door.Exterior ? null : SideRoom(negative);
                    if (positiveRoom == null || !door.Exterior && negativeRoom == null) continue;
                    var inside = AddRoomNode(positiveRoom, positive);
                    if (inside < 0) continue;
                    int other;
                    if (door.Exterior)
                    {
                        other = AddNode(negative, building.Id);
                        var toe = AddNode(building.ToWorld(0, building.LandingGround - building.GroundFloor + StructureOffset,
                            -BuildingPlan.EntryRun), building.Id);
                        Link(approach, toe, Surface.Ground);
                        Link(toe, other, Surface.Structure, building.Id);
                    }
                    else other = AddRoomNode(negativeRoom, negative);
                    if (other < 0) continue;
                    Link(other, inside, door.Exterior ? Surface.EntryDoor : Surface.Structure, building.Id, door.Id);
                    return;

                    LocalPoint DoorStand(int sign)
                    {
                        var normal = sign == swingSide ? 1.43 : .45;
                        var x = door.X + (door.AlongX ? along : sign * normal);
                        var z = door.Z + (door.AlongX ? sign * normal : along);
                        var point = AtFloor(floor, x, z);
                        return door.Exterior && sign < 0 ? EntryHeight(building, point) : point;
                    }

                    string SideRoom(LocalPoint point)
                    {
                        var local = building.ToLocal(point);
                        return building.Rooms.FirstOrDefault(candidate => (candidate.Id == door.RoomA ||
                            candidate.Id == door.RoomB) && Inside(candidate, local.X, local.Z))?.Id;
                    }
                }
            }
        }

        private static bool ClearFloor(BuildingPlan building, LocalPoint a, LocalPoint b, int floor)
        {
            var samples = Math.Max(1, (int)Math.Ceiling(FlatDistance(a, b) / .16));
            for (var i = 1; i < samples; i++)
                if (!ClearPose(building, Lerp(a, b, i / (double)samples), floor)) return false;
            return true;
        }

        private static bool ClearDoor(BuildingPlan building, BuildingDoor door, LocalPoint a, LocalPoint b, int floor)
        {
            var samples = Math.Max(1, (int)Math.Ceiling(FlatDistance(a, b) / .12));
            for (var i = 0; i <= samples; i++)
            {
                var point = Lerp(a, b, i / (double)samples);
                if (door.Exterior && building.ToLocal(point).Z < -.12)
                {
                    point = EntryHeight(building, point);
                    if (!building.TryLocate(point, false, out _) || !ClearLeaves(building, point, door.Id)) return false;
                }
                else if (!ClearPose(building, door.Exterior ? EntryHeight(building, point) : point, floor, door.Id))
                    return false;
            }
            return true;
        }

        private static bool ClearPose(BuildingPlan building, LocalPoint point, int floor, string traversal = null) =>
            building.TryLocate(point, true, out var support) && support == floor && ClearLeaves(building, point, traversal);

        private static bool ClearLeaves(BuildingPlan building, LocalPoint point, string traversal = null)
        {
            foreach (var door in building.Doors)
                if (door.Obstructs(point, true) || door.Id != traversal && door.Obstructs(point, false)) return false;
            return true;
        }

        private static LocalPoint EntryHeight(BuildingPlan building, LocalPoint point)
        {
            var local = building.ToLocal(point);
            var rise = (building.LandingGround - building.GroundFloor) * Math.Clamp(-local.Z / BuildingPlan.EntryRun, 0, 1);
            return building.ToWorld(local.X, rise + StructureOffset, local.Z);
        }

        private static bool Inside(BuildingRoom room, double x, double z) =>
            x > room.Left && x < room.Right && z > room.Front && z < room.Back;

        private static double DoorNormal(BuildingDoor door, LocalPoint point) =>
            door.AlongX ? point.Z - door.Z : point.X - door.X;

        private int AddNode(LocalPoint position, string building = null, string room = null)
        {
            var index = nodes.Count;
            nodes.Add(new Node(new ResidentWaypoint(position, building, room)));
            return index;
        }

        private bool Link(int from, int to, Surface surface, string building = null, string door = null)
        {
            if (from == to || TryEdge(from, to, out _)) return false;
            var a = GetNode(from).Position;
            var b = GetNode(to).Position;
            var flat = FlatDistance(a, b);
            var y = b.Y - a.Y;
            var step = new ResidentStep(Math.Max(.005, Math.Sqrt(flat * flat + y * y)), door);
            nodes[from].Edges.Add(new Edge(to, step, surface, building));
            nodes[to].Edges.Add(new Edge(from, step, surface, building));
            return true;
        }

        private bool TryEdge(int from, int to, out Edge edge)
        {
            if (IsNode(from))
                foreach (var candidate in nodes[from].Edges)
                    if (candidate.To == to)
                    {
                        edge = candidate;
                        return true;
                    }
            edge = default;
            return false;
        }

        private void SetComponents()
        {
            var open = new Stack<int>();
            for (var root = 0; root < nodes.Count; root++)
            {
                if (nodes[root].Component >= 0) continue;
                nodes[root].Component = root;
                open.Push(root);
                while (open.Count > 0)
                    foreach (var edge in nodes[open.Pop()].Edges)
                    {
                        if (nodes[edge.To].Component >= 0) continue;
                        nodes[edge.To].Component = root;
                        open.Push(edge.To);
                    }
            }
        }

        private static GeoPoint Flat(LocalPoint point) => new GeoPoint(point.X, point.Z);
        private static double FlatDistance(LocalPoint a, LocalPoint b) => FlatDistance(Flat(a), Flat(b));
        private static double FlatDistance(GeoPoint a, GeoPoint b)
        {
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        private static GeoPoint Lerp(GeoPoint a, GeoPoint b, double t) => new GeoPoint(
            a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);

        private static LocalPoint Lerp(LocalPoint a, LocalPoint b, double t) => new LocalPoint(
            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        private static GeoPoint Project(GeoPoint point, GeoPoint a, GeoPoint b)
        {
            var x = b.X - a.X;
            var z = b.Z - a.Z;
            return Lerp(a, b, Math.Clamp(((point.X - a.X) * x + (point.Z - a.Z) * z) / (x * x + z * z), 0, 1));
        }

        private sealed class Node
        {
            public readonly ResidentWaypoint Waypoint;
            public readonly List<Edge> Edges = new List<Edge>();
            public int Component = -1;
            public Node(ResidentWaypoint waypoint) => Waypoint = waypoint;
        }

        private enum Surface { Ground, Structure, EntryDoor }

        private readonly struct Edge
        {
            public readonly int To;
            public readonly ResidentStep Step;
            public readonly Surface Surface;
            public readonly string BuildingId;
            public Edge(int to, ResidentStep step, Surface surface, string buildingId) =>
                (To, Step, Surface, BuildingId) = (to, step, surface, buildingId);
        }

        private readonly struct RoadLink
        {
            public readonly int A, B;
            public RoadLink(int a, int b) => (A, B) = (a, b);
        }
    }
}
