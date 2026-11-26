using System;
using System.Linq;
using NUnit.Framework;
using RealLife.Simulation;
using UnityEngine;

namespace RealLife.Tests
{
    public sealed class PopulationTests
    {
        [Test]
        public void ResidentsMoveAndUseTheirOwnInventoryWithoutAnObserverAndRestoreInFlight()
        {
            var world = BuildingTests.Fixture(connectRoads: true);
            var session = new RegionSession(world, world.Route[1], 0, Catalog());
            var fresh = session.Capture();
            Assert.That(fresh.Residents.Length, Is.GreaterThanOrEqualTo(3));
            Assert.That(fresh.Residents.Select(person => person.Id).Distinct().Count(), Is.EqualTo(fresh.Residents.Length));
            var person = session.Residents.First(value => value.BuildingId == null);
            var originalFood = session.GetInventory(person.Id).Stacks.First();
            var refused = session.MoveItems(new InventoryMove(person.Id, originalFood.InstanceId, 1,
                session.PlayerInventoryId), null);
            Assert.That(refused.Failure, Is.EqualTo(InventoryMoveFailure.Inaccessible));
            Assert.That(session.Talk(person.Id, ConversationTopic.Greeting).Applied, Is.False);

            var near = new LocalPoint(person.Position.X, person.Position.Y, person.Position.Z - 1);
            Assert.That(session.SetPlayerPose(near, 0, 0), Is.True);
            Assert.That(session.Talk(person.Id, ConversationTopic.Greeting).Encounters, Is.EqualTo(1));
            Assert.That(session.Talk(person.Id, ConversationTopic.Name).Text, Does.Contain(person.Name));
            Assert.That(session.Restore(fresh), Is.True);
            Assert.That(session.Capture().Acquaintances, Is.Empty);
            Assert.That(session.SetPlayerPose(near, 0, 0), Is.True);
            Assert.That(session.Talk(person.Id, ConversationTopic.Greeting).Applied, Is.True);
            Assert.That(world.TryGround(world.Route[1], out var y), Is.True);
            Assert.That(session.SetPlayerPose(new LocalPoint(world.Route[1].X, y + .08, world.Route[1].Z), 0, 0), Is.True);

            RegionSnapshot saved = null;
            for (var tick = 0; tick < 2800; tick++)
            {
                session.AdvanceTick();
                if (tick > 80 && session.Residents.Any(value => value.Walking) &&
                    session.Residents.Sum(value => value.InventoryUses) > 0)
                {
                    saved = session.Capture();
                    break;
                }
            }
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved.Inventory.Uses, Is.Not.Empty);
            Assert.That(saved.Residents.Any(value => value.Progress > 0), Is.True);
            Assert.That(saved.Acquaintances.Single().ResidentId, Is.EqualTo(person.Id));
            saved = JsonUtility.FromJson<RegionSnapshot>(JsonUtility.ToJson(saved));

            var resumed = new RegionSession(world, world.Route[0], 90, Catalog());
            Assert.That(resumed.Restore(saved), Is.True);
            Assert.That(resumed.Tick, Is.EqualTo(saved.Tick));
            CollectionAssert.AreEqual(session.Residents.Select(value => value.Id), resumed.Residents.Select(value => value.Id));
            for (var tick = 0; tick < 30; tick++)
            {
                session.AdvanceTick();
                resumed.AdvanceTick();
            }
            for (var i = 0; i < session.Residents.Count; i++)
            {
                Assert.That(resumed.Residents[i].Position, Is.EqualTo(session.Residents[i].Position));
                Assert.That(resumed.Residents[i].Thirst, Is.EqualTo(session.Residents[i].Thirst));
            }
            var validTick = resumed.Tick;
            saved.Residents[0].Position = default;
            Assert.That(resumed.Restore(saved), Is.False);
            Assert.That(resumed.Tick, Is.EqualTo(validTick));
            Assert.That(resumed.Capture().Acquaintances.Single().ResidentId, Is.EqualTo(person.Id));
        }

        [Test]
        public void ResidentsUseTheSharedContainerAndConsumptionRulesAtTheirDestination()
        {
            var world = BuildingTests.Fixture(connectRoads: true);
            var session = new RegionSession(world, world.Route[1], 0, Catalog());
            var placements = session.Buildings.Plans.SelectMany(plan => plan.Anchors)
                .Where(anchor => anchor.Kind == BuildingAnchorKind.Storage)
                .Select(anchor => (Anchor: anchor, Stock: session.GetInventory(anchor.Id).Stacks
                    .FirstOrDefault(stack => stack.UseKind == InventoryUseKind.Drink)))
                .Where(place => place.Stock.InstanceId != null).ToArray();
            var traveler = session.Residents.First(person => person.BuildingId == null);
            var storage = placements.First().Anchor;
            var stackId = placements.First().Stock.InstanceId;
            Assert.That(session.MoveItems(traveler.Id, new InventoryMove(storage.Id, stackId, 1, traveler.Id), storage.Id).Failure,
                Is.EqualTo(InventoryMoveFailure.Inaccessible));

            for (var tick = 0; tick < 6500; tick++)
            {
                session.AdvanceTick();
                foreach (var person in session.Residents)
                {
                    if (person.RoomId == null || person.Traveling || person.Intent != ResidentActivity.Work) continue;
                    foreach (var place in placements)
                    {
                        var anchor = place.Anchor;
                        if (person.RoomId != anchor.RoomId) continue;
                        var before = session.GetInventory(anchor.Id).Stacks.Single(stack => stack.InstanceId == place.Stock.InstanceId);
                        var move = session.MoveItems(person.Id, new InventoryMove(anchor.Id, before.InstanceId, 1, person.Id), anchor.Id);
                        if (!move.Succeeded) continue;
                        var used = session.UseItem(new InventoryUse(person.Id, person.Id, move.InstanceId, session.Tick));
                        Assert.That(used.Kind, Is.EqualTo(InventoryUseKind.Drink));
                        Assert.That(session.GetInventory(person.Id).Stacks.Any(stack => stack.InstanceId == move.InstanceId), Is.False);
                        Assert.That(session.GetInventory(anchor.Id).Stacks.Single(stack => stack.InstanceId == before.InstanceId).Quantity,
                            Is.EqualTo(before.Quantity - 1));
                        Assert.That(session.Capture().Residents.Single(value => value.Id == person.Id).Thirst,
                            Is.LessThan(person.Thirst));
                        return;
                    }
                }
            }
            Assert.Fail("No working resident arrived at a provision container in the connected fixture.");
        }

        [Test]
        public void V3KeepsTheOldInteriorPoseAndMovedStackWhileInstallingProvisionsOnce()
        {
            const string oldFingerprint = "589e68740b83e24e9043b5ee7fac0b0c5aaf6ce15433856db7be1d7b9b4ab55e";
            var world = BuildingTests.Fixture(connectRoads: true);
            var session = new RegionSession(world, world.Route[0], 0, Catalog());
            var house = session.Buildings.Plans.Single(plan => plan.Kind == BuildingKind.TwoStoreyHouse);
            var anchor = house.Anchors.First(value => value.Role == BuildingRoomRole.Bedroom);
            var item = anchor.Id + "/item";
            var upstairs = house.StairsTop;
            var v3 = new RegionSnapshot
            {
                Version = 3, WorldId = session.WorldId, PackageId = world.PackageId,
                PackageFormat = RegionWorld.PackageFormat, PackageRevision = world.Revision,
                Player = world.Frame.ToGlobal(upstairs), Yaw = 126, Pitch = 11, Tick = 9112,
                BuildingGenerator = Buildings.GeneratorVersion, InteriorId = house.Id, InteriorFloor = 1,
                OpenDoors = new[] { house.Doors[0].Id }, InventoryFingerprint = oldFingerprint,
                Inventory = new InventoryDelta { Stacks = new[]
                {
                    new InventoryStackDelta { InstanceId = item, ContainerId = session.PlayerInventoryId, Quantity = 4 }
                }}
            };

            Assert.That(session.Restore(v3), Is.True);
            var upgraded = session.Capture();
            Assert.That(upgraded.Version, Is.EqualTo(4));
            Assert.That(upgraded.Player, Is.EqualTo(v3.Player));
            Assert.That(upgraded.InteriorFloor, Is.EqualTo(1));
            Assert.That(upgraded.Tick, Is.EqualTo(v3.Tick));
            Assert.That(session.Buildings.IsDoorOpen(v3.OpenDoors[0]), Is.True);
            Assert.That(session.GetInventory(session.PlayerInventoryId).Stacks.Single().InstanceId, Is.EqualTo(item));
            Assert.That(session.GetInventory(anchor.Id).Stacks.Any(stack => stack.InstanceId == item), Is.False);
            var inventoryId = upgraded.Residents[0].Id;
            var initial = session.GetInventory(inventoryId).Stacks.Sum(stack => stack.Quantity);
            Assert.That(initial, Is.GreaterThan(0));
            Assert.That(session.Restore(upgraded), Is.True);
            Assert.That(session.GetInventory(inventoryId).Stacks.Sum(stack => stack.Quantity), Is.EqualTo(initial));
            Assert.That(session.GetInventory(session.PlayerInventoryId).Stacks.Single().Quantity, Is.EqualTo(4));

            v3.Inventory.Stacks[0].Quantity++;
            Assert.That(session.Restore(v3), Is.False);
            Assert.That(session.GetInventory(session.PlayerInventoryId).Stacks.Single().Quantity, Is.EqualTo(4));
        }

        private static NeighborhoodCatalog Catalog() => new NeighborhoodCatalog
        {
            Items = new[] { new InventoryItemDefinition
            {
                DefinitionId = "test:house.tool", DisplayName = "Мелок", Category = "Вещи", UnitWeightGrams = 100, MaxStack = 10
            }},
            Furnishings = new[] { new FurnishingItem
            {
                RoomRole = "Bedroom", DefinitionId = "test:house.tool", Quantity = 4
            }},
            Provisions = new[]
            {
                new InventoryItemDefinition { DefinitionId = "test:food.bread", DisplayName = "Хлеб", Category = "Еда",
                    UnitWeightGrams = 100, MaxStack = 40, UseKind = InventoryUseKind.Food },
                new InventoryItemDefinition { DefinitionId = "test:food.water", DisplayName = "Вода", Category = "Напитки",
                    UnitWeightGrams = 250, MaxStack = 40, UseKind = InventoryUseKind.Drink }
            }
        };
    }
}
