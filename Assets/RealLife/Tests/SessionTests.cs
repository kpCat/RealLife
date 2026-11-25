using NUnit.Framework;
using RealLife.Simulation;

namespace RealLife.Tests
{
    public sealed class SessionTests
    {
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
