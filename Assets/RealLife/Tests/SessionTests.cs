using System;
using NUnit.Framework;
using RealLife.Simulation;

namespace RealLife.Tests
{
    public sealed class SessionTests
    {
        [TestCase(47.088, 28.868, 137.2)]
        [TestCase(0, 180, -30)]
        [TestCase(89.9999, -120, 1200)]
        public void GeodeticCoordinatesRoundTrip(double latitude, double longitude, double height)
        {
            var restored = WorldFrame.ToGeodetic(WorldFrame.ToGlobal(latitude, longitude, height));
            Assert.That(restored.Latitude, Is.EqualTo(latitude).Within(1e-9));
            Assert.That(restored.Longitude, Is.EqualTo(longitude).Within(1e-9));
            Assert.That(restored.Height, Is.EqualTo(height).Within(1e-5));
        }

        [Test]
        public void CellKeyHasStableFacesAndSharedChildEdges()
        {
            var east = CellKey.FromGlobal(WorldFrame.ToGlobal(0, 0, 100), 16);
            Assert.That(east, Is.EqualTo(new CellKey(0, 16, 32768, 32768)));
            Assert.That(east.Parent, Is.EqualTo(new CellKey(0, 15, 16384, 16384)));
            Assert.That(CellKey.FromGlobal(WorldFrame.ToGlobal(0, 90, 0), 16).Face, Is.EqualTo(2));
            Assert.That(CellKey.FromGlobal(WorldFrame.ToGlobal(-90, 0, 0), 16).Face, Is.EqualTo(5));
            var neighbor = new CellKey(0, 16, 32769, 32768);
            var edge = east.DirectionAt(1, 0.5);
            Assert.That(edge.X, Is.EqualTo(neighbor.DirectionAt(0, 0.5).X));
            Assert.That(edge.Y, Is.EqualTo(neighbor.DirectionAt(0, 0.5).Y));
            Assert.That(edge.Z, Is.EqualTo(neighbor.DirectionAt(0, 0.5).Z));
        }

        [Test]
        public void RegionTerrainSharesItsPhysicalEdgeAndRejectsDamagedPackages()
        {
            var data = CreateRegion();
            var world = new RegionWorld(data);
            var left = world.Key(data.MinX, data.MinY);
            var right = world.Key(data.MinX + 1, data.MinY);
            for (var j = 0; j <= world.Steps; j++)
            {
                var a = world.TerrainVertex(left, data.Chunks[0], world.Steps, j);
                var b = world.TerrainVertex(right, data.Chunks[1], 0, j);
                Assert.That(a.X, Is.EqualTo(b.X));
                Assert.That(a.Y, Is.EqualTo(b.Y));
                Assert.That(a.Z, Is.EqualTo(b.Z));
                Assert.That(world.TryGround(new GeoPoint(a.X, a.Z), out var sampled), Is.True);
                Assert.That(sampled, Is.EqualTo(a.Y).Within(1e-7));
            }
            data.Chunks[1].Heights[0] += 1;
            Assert.Throws<ArgumentException>(() => new RegionWorld(data));
        }

        [Test]
        public void RegionSaveRejectsDifferentPackageAndRestoresGlobalPoseAndTick()
        {
            var data = CreateRegion();
            var world = new RegionWorld(data);
            var session = new RegionSession(world, data.Route[0], 137);
            session.AdvanceTick();
            var saved = session.Capture();
            var target = new RegionSession(world, data.Route[1], 10);
            saved.PackageRevision = "other";
            Assert.That(target.Restore(saved), Is.False);
            Assert.That(target.Yaw, Is.EqualTo(10));
            Assert.That(target.Tick, Is.Zero);
            saved.PackageRevision = data.Revision;
            Assert.That(target.Restore(saved), Is.True);
            Assert.That(target.PlayerLocal.X, Is.EqualTo(data.Route[0].X).Within(1e-8));
            Assert.That(target.PlayerLocal.Z, Is.EqualTo(data.Route[0].Z).Within(1e-8));
            Assert.That(target.Yaw, Is.EqualTo(137));
            Assert.That(target.Tick, Is.EqualTo(1));

            var boundary = world.Frame.ProjectDirection(world.Key(data.MaxX - 1, data.MinY).DirectionAt(1, .5));
            var justOutside = new GeoPoint(boundary.X + .0005, boundary.Z);
            Assert.That(world.TryGround(justOutside, out var edgeHeight), Is.True);
            saved.Player = world.Frame.ToGlobal(new LocalPoint(justOutside.X, edgeHeight, justOutside.Z));
            Assert.That(target.Restore(saved), Is.False);
            Assert.That(target.PlayerLocal.X, Is.EqualTo(data.Route[0].X).Within(1e-8));
        }

        [Test]
        public void LegacyRegionSnapshotRestoresPoseAndUpgradesOnCapture()
        {
            var data = CreateRegion();
            var world = new RegionWorld(data);
            var origin = data.Route[0];
            Assert.That(world.TryGround(origin, out var height), Is.True);
            var legacy = new RegionSnapshot
            {
                Version = 2, WorldId = "earth:region.fixture", PackageId = "region.fixture", PackageFormat = 1,
                PackageRevision = "r1", Player = world.Frame.ToGlobal(new LocalPoint(origin.X, height + .08, origin.Z)),
                Yaw = 137, Pitch = 11, Tick = 260
            };
            var session = new RegionSession(world, data.Route[1], 0);

            Assert.That(session.Restore(legacy), Is.True);
            var upgraded = session.Capture();
            Assert.That(upgraded.Version, Is.EqualTo(3));
            Assert.That(upgraded.Player.X, Is.EqualTo(legacy.Player.X));
            Assert.That(upgraded.Player.Y, Is.EqualTo(legacy.Player.Y));
            Assert.That(upgraded.Player.Z, Is.EqualTo(legacy.Player.Z));
            Assert.That(upgraded.Yaw, Is.EqualTo(137));
            Assert.That(upgraded.Pitch, Is.EqualTo(11));
            Assert.That(upgraded.Tick, Is.EqualTo(260));
            upgraded.InventoryFingerprint = "different-catalog";
            Assert.That(session.Restore(upgraded), Is.False);
            Assert.That(session.Tick, Is.EqualTo(260));
        }

        private static RegionDefinition CreateRegion()
        {
            const int level = 16, center = 32768;
            var frame = new WorldFrame(0, 0, 0);
            var left = new CellKey(0, level, center, center);
            var right = new CellKey(0, level, center + 1, center);
            GeoPoint Point(CellKey key, double x, double y)
            {
                var point = frame.ProjectDirection(key.DirectionAt(x, y));
                return new GeoPoint(point.X, point.Z);
            }
            RegionChunk Chunk(CellKey key, float offset)
            {
                var heights = new float[9];
                for (var i = 0; i < heights.Length; i++) heights[i] = offset + i % 3 + i / 3;
                return new RegionChunk { X = key.X, Y = key.Y, Heights = heights,
                    Roads = Array.Empty<RoadSpan>(), Buildings = Array.Empty<BuildingFootprint>() };
            }
            return new RegionDefinition
            {
                Format = 1, GridVersion = 1, Level = level, MinX = center, MinY = center, MaxX = center + 2, MaxY = center + 1,
                PackageId = "region.fixture", Title = "Тестовый регион", Revision = "r1", Steps = 2,
                Outline = new[] { Point(left, 0, 0), Point(right, 1, 0), Point(right, 1, 1), Point(left, 0, 1) },
                Route = new[] { Point(left, .5, .5), Point(right, .5, .5) },
                Chunks = new[] { Chunk(left, 0), Chunk(right, 2) },
                Sources = new[] { new RegionSource(), new RegionSource(), new RegionSource() }
            };
        }

        [Test]
        public void DistrictFrameUsesWgs84AndRoundTripsLocalMetres()
        {
            var equator = new WorldFrame(0, 0, 0);
            Assert.That(equator.ToGlobal(new LocalPoint(3, 4, 5)).X, Is.EqualTo(6378141).Within(1e-8));
            Assert.That(equator.ToGlobal(new LocalPoint(3, 4, 5)).Y, Is.EqualTo(3).Within(1e-8));
            Assert.That(equator.ToGlobal(new LocalPoint(3, 4, 5)).Z, Is.EqualTo(5).Within(1e-8));

            var frame = new WorldFrame(47.04, 28.86, 92);
            var local = frame.ToLocal(frame.ToGlobal(new LocalPoint(-17.2, 1.5, 31.1)));
            Assert.That(local.X, Is.EqualTo(-17.2).Within(1e-8));
            Assert.That(local.Y, Is.EqualTo(1.5).Within(1e-8));
            Assert.That(local.Z, Is.EqualTo(31.1).Within(1e-8));
        }

        [Test]
        public void ItemHasOnePlacementAndActionsRequireAValidTargetInReach()
        {
            var game = new GameSession(CreateDistrict());
            Assert.That(game.Apply(GameAction.TakeItem, "district:other"), Is.EqualTo(ActionResult.UnknownTarget));
            Assert.That(game.Apply(GameAction.TakeItem, game.ItemId), Is.EqualTo(ActionResult.OutOfReach));
            Assert.That(game.SetPlayerPose(new LocalPoint(1, 0, 9), 20, 3), Is.True);
            Assert.That(game.Apply(GameAction.TakeItem, game.ItemId), Is.EqualTo(ActionResult.Applied));
            Assert.That(game.IsCarryingItem, Is.True);
            Assert.That(game.Apply(GameAction.TakeItem, game.ItemId), Is.EqualTo(ActionResult.Unavailable));
            Assert.That(game.Apply(GameAction.ReturnItem, game.ItemId), Is.EqualTo(ActionResult.Applied));
            Assert.That(game.IsCarryingItem, Is.False);
        }

        [Test]
        public void RestoreValidatesTheWholeSnapshotBeforeApplyingIt()
        {
            var game = new GameSession(CreateDistrict());
            game.SetPlayerPose(new LocalPoint(1, 0, 4), 20, 3);
            Assert.That(game.Apply(GameAction.ToggleDoor, game.DoorId), Is.EqualTo(ActionResult.Applied));
            game.SetPlayerPose(new LocalPoint(1, 0, 9), 20, 3);
            game.Apply(GameAction.TakeItem, game.ItemId);
            game.AdvanceTick();
            var snapshot = game.Capture();
            var damaged = game.Capture();
            damaged.DistrictRevision++;

            game.Apply(GameAction.ReturnItem, game.ItemId);
            game.SetPlayerPose(new LocalPoint(1, 0, 4), 0, 0);
            game.Apply(GameAction.ToggleDoor, game.DoorId);
            Assert.That(game.Restore(damaged), Is.False);
            Assert.That(game.IsDoorOpen || game.IsCarryingItem, Is.False);
            Assert.That(game.Restore(snapshot), Is.True);
            Assert.That(game.IsDoorOpen && game.IsCarryingItem, Is.True);
            Assert.That(game.Tick, Is.EqualTo(1));
            Assert.That(game.Yaw, Is.EqualTo(20));
            Assert.That(game.Pitch, Is.EqualTo(3));
            Assert.That(game.PlayerLocal.Z, Is.EqualTo(9).Within(1e-8));
        }

        private static DistrictDefinition CreateDistrict() => new DistrictDefinition
        {
            Id = "district:test.yard",
            Title = "Тестовый двор",
            Revision = 1,
            Latitude = 47.04,
            Longitude = 28.86,
            Bounds = new Area(-10, -10, 10, 20),
            Building = new Area(-4, 5, 4, 15),
            Door = new DoorDefinition
            {
                Id = "district:test.yard.door",
                Hinge = new LocalPoint(-0.6, 0, 5),
                Width = 1.2f,
                Height = 2.1f
            },
            Item = new ItemDefinition
            {
                Id = "district:test.yard.lantern",
                DefinitionId = "core:item.lantern",
                Name = "Фонарь",
                Home = new LocalPoint(1, 1, 10)
            }
        };
    }
}
