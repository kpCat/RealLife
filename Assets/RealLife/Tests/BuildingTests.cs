using System;
using System.Linq;
using NUnit.Framework;
using RealLife.Simulation;

namespace RealLife.Tests
{
    public sealed class BuildingTests
    {
        [Test]
        public void LegacyPoseUnderNewEntryRampPreservesHorizontalLocationAndUsesNewSupport()
        {
            var world = Fixture();
            var session = new RegionSession(world, world.Route[0], 35);
            var plan = session.Buildings.Plans.First();
            var ramp = plan.ToWorld(0, 0, -BuildingPlan.EntryRun / 2);
            var legacy = session.Capture();
            legacy.Version = 2;
            legacy.Yaw = 125;
            legacy.Tick = 72;
            legacy.Player = world.Frame.ToGlobal(new LocalPoint(ramp.X, 0, ramp.Z));

            Assert.That(session.Restore(legacy), Is.True);
            Assert.That(session.PlayerLocal.X, Is.EqualTo(ramp.X).Within(1e-8));
            Assert.That(session.PlayerLocal.Z, Is.EqualTo(ramp.Z).Within(1e-8));
            Assert.That(session.PlayerLocal.Y, Is.GreaterThan(.08));
            Assert.That(session.Yaw, Is.EqualTo(125));
            Assert.That(session.Tick, Is.EqualTo(72));
            Assert.That(session.CanRestore(session.Capture()), Is.True);
        }

        [Test]
        public void GeneratesStableConnectedPlansForEligibleFootprints()
        {
            var world = Fixture();
            var first = new Buildings(world);
            var second = new Buildings(world);
            CollectionAssert.AreEquivalent((BuildingKind[])Enum.GetValues(typeof(BuildingKind)),
                first.Plans.Select(plan => plan.Kind));
            Assert.That(first.Plans.Select(plan => plan.Owner).Distinct().Count(), Is.EqualTo(3));
            CollectionAssert.AreEqual(first.Plans.SelectMany(plan => plan.Doors).Select(door => door.Id),
                second.Plans.SelectMany(plan => plan.Doors).Select(door => door.Id));
            Assert.That(first.TryGet("osm:way:4", out _), Is.False);
            foreach (var plan in first.Plans)
            {
                Assert.That(plan.Rooms.Count, Is.GreaterThan(1));
                Assert.That(plan.Doors[0].Exterior, Is.True);
                Assert.That(plan.Doors.Skip(1).All(door => door.RoomA != door.RoomB), Is.True);
            }
        }

        [Test]
        public void RestoresDoorChangesAtomicallyAndRejectsDuplicateOrUnknownIds()
        {
            var world = Fixture();
            var first = new Buildings(world);
            var door = first.Plans[0].Doors[0];
            Assert.That(first.TryToggleDoor(door.Id, out var open), Is.True);
            Assert.That(open, Is.True);
            var second = new Buildings(world);
            Assert.That(second.RestoreOpenDoors(first.CaptureOpenDoors()), Is.True);
            Assert.That(second.RestoreOpenDoors(new[] { door.Id, door.Id }), Is.False);
            Assert.That(second.RestoreOpenDoors(new[] { "building.missing" }), Is.False);
            Assert.That(second.IsDoorOpen(door.Id), Is.True);
            Assert.That(second.RestoreOpenDoors(Array.Empty<string>()), Is.True);
            Assert.That(second.IsDoorOpen(door.Id), Is.False);
        }

        [Test]
        public void LocatesStairsAndUpperFloorAndRejectsInvalidSupport()
        {
            var buildings = new Buildings(Fixture());
            var plan = buildings.Plans.Single(candidate => candidate.Kind == BuildingKind.TwoStoreyHouse);
            var top = plan.ToLocal(plan.StairsTop);
            Assert.That(plan.TryLocate(plan.StairsTop, true, out var floor), Is.True);
            Assert.That(floor, Is.EqualTo(1));
            Assert.That(plan.TryLocate(plan.ToWorld(0, 0, top.Z), true, out floor), Is.True);
            Assert.That(floor, Is.Zero);
            Assert.That(plan.TryLocate(plan.ToWorld(0, 1.2, top.Z), true, out _), Is.False);
            Assert.That(plan.TryLocate(plan.ToWorld(plan.Width / 2, 0, 1.4), true, out _), Is.False);
            Assert.That(buildings.IsPoseClearOfDoors(plan.Entrance, Array.Empty<string>()), Is.False);
            Assert.That(buildings.IsPoseClearOfDoors(plan.Entrance, new[] { plan.Doors[0].Id }), Is.True);
        }

        [Test]
        public void RestoreUsesTheClearSupportAtTheSavedHeight()
        {
            var buildings = new Buildings(Fixture());
            var plan = buildings.Plans.Single(candidate => candidate.Kind == BuildingKind.TwoStoreyHouse);
            Assert.That(plan.TryLocate(plan.ToWorld(0, 0, 6.65), true, out var floor), Is.True);
            Assert.That(floor, Is.Zero);
            Assert.That(plan.TryLocate(plan.ToWorld(0, BuildingPlan.StoreyHeight, 1.90), true, out floor), Is.True);
            Assert.That(floor, Is.EqualTo(1));
            Assert.That(plan.TryLocate(plan.ToWorld(0, 0, 4), true, out _), Is.False);
            Assert.That(plan.TryLocate(plan.ToWorld(plan.Width / 2 - .29, 0, 2), true, out _), Is.False);
            Assert.That(plan.TryLocate(plan.ToWorld(0, .22, 1), true, out _), Is.False);
            Assert.That(buildings.IsPoseClearOfDoors(plan.ToWorld(0, 0, -.26), Array.Empty<string>()), Is.False);
            Assert.That(buildings.IsPoseClearOfDoors(plan.ToWorld(0, 0, -.31), Array.Empty<string>()), Is.True);
        }

        private static RegionWorld Fixture()
        {
            const int level = 16, center = 32768;
            var frame = new WorldFrame(0, 0, 0);
            CellKey Key(int offset) => new CellKey(0, level, center + offset, center);
            GeoPoint Point(CellKey key, double x, double y)
            {
                var point = frame.ProjectDirection(key.DirectionAt(x, y));
                return new GeoPoint(point.X, point.Z);
            }
            RegionChunk Chunk(int offset, double width, double depth, float height, bool rectangle = true)
            {
                var key = Key(offset);
                var p = Point(key, .45, .42);
                var origin = new GeoPoint(p.X - width / 2, p.Z);
                var ends = new[]
                {
                    origin, new GeoPoint(origin.X + width, origin.Z), new GeoPoint(origin.X + width, origin.Z + depth),
                    new GeoPoint(origin.X + (rectangle ? 0 : 4), origin.Z + depth)
                };
                return new RegionChunk
                {
                    X = key.X, Y = key.Y, Heights = new float[9],
                    Roads = new[] { new RoadSpan { Id = $"way:{offset}", Width = 4,
                        A = new GeoPoint(p.X - 15, p.Z - 10), B = new GeoPoint(p.X + 15, p.Z - 10) } },
                    Buildings = new[] { new BuildingFootprint { Id = $"osm:way:{offset + 1}", Height = height,
                        Footprint = ends, Triangles = new[] { 0, 1, 2, 0, 2, 3 } } }
                };
            }
            return new RegionWorld(new RegionDefinition
            {
                Format = 1, GridVersion = 1, Level = level, MinX = center, MinY = center,
                MaxX = center + 4, MaxY = center + 1, Steps = 2, PackageId = "region.buildings",
                Title = "Buildings fixture", Revision = "r1",
                Outline = new[] { Point(Key(0), 0, 0), Point(Key(3), 1, 0), Point(Key(3), 1, 1), Point(Key(0), 0, 1) },
                Route = new[] { Point(Key(0), .45, .35), Point(Key(3), .45, .35) },
                Chunks = new[] { Chunk(0, 6.9, 7.1, 3.4f), Chunk(1, 10, 12, 6.3f), Chunk(2, 11, 10, 3.4f),
                    Chunk(3, 11, 10, 3.4f, false) },
                Sources = new[] { new RegionSource(), new RegionSource(), new RegionSource() }
            });
        }
    }
}
