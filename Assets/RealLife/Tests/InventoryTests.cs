using System;
using NUnit.Framework;
using RealLife.Simulation;

namespace RealLife.Tests
{
    public sealed class InventoryTests
    {
        private const string Player = "actor:player";
        private const string Resident = "actor:resident";
        private const string Cupboard = "room:cupboard";
        private const string Anchor = "outside:bench";
        private const string Books = "world:books";
        private const string OtherBooks = "world:other-books";
        private const string Food = "test:food.meal";
        private const string Stock = "world:meals";

        [Test]
        public void TransferChecksAccessAndMergesOnlyCompatibleStacksWithinTheirLimit()
        {
            var inventory = CreateInventory();
            var access = new Access { Closed = Cupboard };
            var take = new InventoryMove(Cupboard, Books, 3, Player);
            Assert.That(inventory.CheckMove(take, access), Is.EqualTo(InventoryMoveFailure.Inaccessible));
            Assert.That(inventory.Move(take, access).Failure, Is.EqualTo(InventoryMoveFailure.Inaccessible));
            Assert.That(inventory.GetContainer(Cupboard).Stacks[0].Quantity, Is.EqualTo(6));
            Assert.That(inventory.GetContainer(Player).Stacks, Is.Empty);
            var beforeCheck = inventory.Capture();

            access.Closed = null;
            Assert.That(inventory.CheckMove(take, access), Is.EqualTo(InventoryMoveFailure.None));
            Assert.That(inventory.GetContainer(Cupboard).Stacks[0].Quantity, Is.EqualTo(6));
            Assert.That(inventory.GetContainer(Player).Stacks, Is.Empty);
            var afterCheck = inventory.Capture();
            Assert.That(afterCheck.Stacks, Is.Empty);
            Assert.That(afterCheck.NextSplitId, Is.EqualTo(beforeCheck.NextSplitId));

            access.Closed = Player;
            Assert.That(inventory.Move(take, access).Failure, Is.EqualTo(InventoryMoveFailure.Inaccessible));
            access.Closed = null;
            var split = inventory.Move(take, access);
            Assert.That(split.Succeeded, Is.True);
            var part = inventory.GetContainer(Player).Stacks[0];
            Assert.That(part.InstanceId, Is.EqualTo(split.InstanceId));
            Assert.That(part.OriginId, Is.EqualTo(Books));
            Assert.That(part.Quantity, Is.EqualTo(3));
            var incompatible = new InventoryMove(Player, part.InstanceId, 1, Resident, OtherBooks);
            Assert.That(inventory.Move(incompatible, access).Failure, Is.EqualTo(InventoryMoveFailure.IncompatibleStack));
            var overLimit = new InventoryMove(Resident, OtherBooks, 3, Cupboard, "world:signed-books");
            Assert.That(inventory.Move(overLimit, access).Failure, Is.EqualTo(InventoryMoveFailure.StackLimit));
            Assert.That(inventory.GetContainer(Resident).Stacks[0].Quantity, Is.EqualTo(7));

            var putBack = new InventoryMove(Player, part.InstanceId, 3, Cupboard, Books);
            access.Closed = Cupboard;
            Assert.That(inventory.Move(putBack, access).Failure, Is.EqualTo(InventoryMoveFailure.Inaccessible));
            Assert.That(inventory.GetContainer(Player).Stacks[0].Quantity, Is.EqualTo(3));
            access.Closed = null;
            var returned = inventory.Move(putBack, access);
            Assert.That(returned.InstanceId, Is.EqualTo(Books));
            Assert.That(inventory.GetContainer(Player).Stacks, Is.Empty);
            Assert.That(inventory.GetContainer(Cupboard).Stacks[0].Quantity, Is.EqualTo(6));
            Assert.That(inventory.Capture().Stacks, Is.Empty);

            Assert.That(inventory.Move(new InventoryMove(Resident, OtherBooks, 5, Player), access).Succeeded, Is.True);
            var merged = inventory.Move(new InventoryMove(Cupboard, "world:signed-books", 6, Resident, OtherBooks), access);
            Assert.That(merged.InstanceId, Is.EqualTo(OtherBooks));
            var restored = CreateInventory();
            Assert.That(restored.Restore(inventory.Capture()), Is.True);
            Assert.That(restored.GetContainer(Cupboard).Stacks.Count, Is.EqualTo(1));
            Assert.That(restored.GetContainer(Resident).Stacks[0].Quantity, Is.EqualTo(8));
        }

        [Test]
        public void ShrinkingCapacityRetainsContentsAndPermitsNeutralMergesAndRemovals()
        {
            var inventory = CreateInventory();
            var access = new Access();
            var capacity = new Capacity();
            var moved = inventory.Move(new InventoryMove(Cupboard, Books, 4, Player), access, capacity);
            var split = inventory.Move(new InventoryMove(Player, moved.InstanceId, 2, Player), access, capacity);
            Assert.That(split.Succeeded, Is.True);
            var beforeShrink = inventory.GetContainer(Player, capacity);
            var load = new InventoryMove(Cupboard, Books, 1, Player, moved.InstanceId);
            Assert.That(inventory.CheckMove(load, access, capacity), Is.EqualTo(InventoryMoveFailure.None));
            capacity.PlayerCapacity = new InventoryCapacity(1, 100);
            var view = inventory.GetContainer(Player, capacity);
            Assert.That(view.Stacks.Count, Is.EqualTo(2));
            Assert.That(view.TotalWeightGrams, Is.EqualTo(400));
            Assert.That(view.Capacity.MaxSlots, Is.EqualTo(1));
            Assert.That(inventory.CheckMove(load, access, capacity), Is.EqualTo(InventoryMoveFailure.Capacity));
            Assert.That(inventory.Move(load, access, capacity).Failure, Is.EqualTo(InventoryMoveFailure.Capacity));
            Assert.That(inventory.Move(new InventoryMove(Player, split.InstanceId, 2, Player, moved.InstanceId), access, capacity)
                .Succeeded, Is.True);
            Assert.That(beforeShrink.Stacks.Count, Is.EqualTo(2));

            var loadSeparate = new InventoryMove(Cupboard, Books, 1, Player);
            capacity.PlayerCapacity = new InventoryCapacity(0, 600);
            Assert.That(inventory.CheckMove(loadSeparate, access, capacity), Is.EqualTo(InventoryMoveFailure.Capacity));
            capacity.PlayerCapacity = new InventoryCapacity(4, 0);
            Assert.That(inventory.CheckMove(load, access, capacity), Is.EqualTo(InventoryMoveFailure.Capacity));
            capacity.PlayerCapacity = new InventoryCapacity(0, 0);
            Assert.That(inventory.GetContainer(Player, capacity).TotalWeightGrams, Is.EqualTo(400));

            var drop = new InventoryMove(Player, moved.InstanceId, 1, Anchor);
            capacity.AnchorCapacity = new InventoryCapacity(0, InventoryCapacity.Unlimited);
            Assert.That(inventory.GetContainer(Anchor, capacity).Capacity.MaxSlots, Is.Zero);
            Assert.That(inventory.CheckMove(drop, access, capacity), Is.EqualTo(InventoryMoveFailure.Capacity));
            capacity.AnchorCapacity = new InventoryCapacity(InventoryCapacity.Unlimited, InventoryCapacity.Unlimited);
            Assert.That(inventory.GetContainer(Anchor, capacity).Capacity.MaxSlots, Is.EqualTo(1));
            Assert.That(inventory.Move(drop, access, capacity).Succeeded, Is.True);
            Assert.That(inventory.CheckMove(drop, access, capacity), Is.EqualTo(InventoryMoveFailure.Capacity));
            Assert.That(inventory.Move(drop, access, capacity).Failure, Is.EqualTo(InventoryMoveFailure.Capacity));
            Assert.That(inventory.GetContainer(Player, capacity).TotalWeightGrams, Is.EqualTo(300));
            var restored = CreateInventory();
            Assert.That(restored.Restore(inventory.Capture()), Is.True);
            Assert.That(restored.GetContainer(Player, capacity).TotalWeightGrams, Is.EqualTo(300));
            Assert.That(restored.GetContainer(Anchor).Stacks[0].Quantity, Is.EqualTo(1));
        }

        [Test]
        public void RestoreValidatesTheCompleteDeltaAndKeepsSplitIdsStable()
        {
            var inventory = CreateInventory();
            var access = new Access();
            var command = new InventoryMove(Cupboard, Books, 2, Player);
            var first = inventory.Move(command, access);
            var save = inventory.Capture();
            var restored = CreateInventory();
            Assert.That(restored.CanRestore(save), Is.True);
            Assert.That(restored.Restore(save), Is.True);
            Assert.That(restored.GetContainer(Player).Stacks[0].InstanceId, Is.EqualTo(first.InstanceId));
            var second = inventory.Move(command, access);
            Assert.That(restored.Move(command, access).InstanceId, Is.EqualTo(second.InstanceId));
            Assert.That(second.InstanceId, Is.Not.EqualTo(first.InstanceId));

            var valid = save.Stacks;
            var original = Array.Find(valid, stack => stack.InstanceId == Books);
            var derived = Array.Find(valid, stack => stack.InstanceId == first.InstanceId);
            save.Stacks = new[] { original, derived, derived };
            Assert.That(restored.Restore(save), Is.False);
            save.Stacks = valid;
            original.Quantity++;
            Assert.That(restored.Restore(save), Is.False);
            original.Quantity--;
            derived.InstanceId = OtherBooks + "~1";
            Assert.That(restored.Restore(save), Is.False);
            derived.InstanceId = first.InstanceId;
            derived.ContainerId = "room:absent";
            Assert.That(restored.Restore(save), Is.False);
            Assert.That(restored.GetContainer(Player).TotalWeightGrams, Is.EqualTo(400));
            Assert.That(restored.GetContainer(Cupboard).Stacks[0].Quantity, Is.EqualTo(2));
        }

        [Test]
        public void ConsumptionValidatesActorAccessAndPreservesTheLedgerAcrossRestore()
        {
            var inventory = CreateFoodInventory();
            var access = new Access();
            var moved = inventory.Move(new InventoryMove(Cupboard, Stock, 2, Resident), access);
            var use = new InventoryUse(Resident, Resident, moved.InstanceId, 12);
            access.Closed = Resident;
            Assert.That(inventory.Consume(use, access).Failure, Is.EqualTo(InventoryUseFailure.Inaccessible));
            Assert.That(inventory.GetContainer(Resident).Stacks[0].Quantity, Is.EqualTo(2));

            access.Closed = null;
            Assert.That(inventory.Consume(use, access).Kind, Is.EqualTo(InventoryUseKind.Food));
            Assert.That(inventory.GetContainer(Resident).Stacks[0].Quantity, Is.EqualTo(1));
            var save = inventory.Capture();
            var restored = CreateFoodInventory();
            Assert.That(restored.Restore(save, currentTick: 11, currentDay: 0), Is.False);
            Assert.That(restored.Restore(save, currentTick: 12, currentDay: 0), Is.True);
            Assert.That(restored.Consume(use, access).Failure, Is.EqualTo(InventoryUseFailure.DuplicateAction));
            Assert.That(restored.GetContainer(Resident).Stacks[0].Quantity, Is.EqualTo(1));
            Assert.That(restored.GetContainer(Cupboard).Stacks[0].Quantity, Is.EqualTo(1));

            save.Uses[0].Quantity++;
            Assert.That(restored.Restore(save), Is.False);
            Assert.That(restored.GetContainer(Resident).Stacks[0].Quantity, Is.EqualTo(1));
            Assert.That(restored.Consume(new InventoryUse(Player, Cupboard, Stock, 13), access).Succeeded, Is.True);
            Assert.That(restored.GetContainer(Cupboard).Stacks, Is.Empty);
            Assert.That(CreateFoodInventory().Restore(restored.Capture()), Is.True);
        }

        [Test]
        public void DailyDeliveryCannotDuplicateMovedStackOrRepeatAfterRestore()
        {
            var inventory = CreateFoodInventory();
            var access = new Access();
            var moved = inventory.Move(new InventoryMove(Cupboard, Stock, 3, Resident), access);
            Assert.That(moved.InstanceId, Is.EqualTo(Stock));
            var delivery = inventory.DeliverDaily(Stock, 1);
            Assert.That(delivery, Is.EqualTo(2));
            var shelf = inventory.GetContainer(Cupboard).Stacks[0];
            Assert.That(shelf.InstanceId, Is.Not.EqualTo(Stock));
            Assert.That(shelf.OriginId, Is.EqualTo(Stock));
            Assert.That(inventory.DeliverDaily(Stock, 1), Is.Zero);

            var save = inventory.Capture();
            var restored = CreateFoodInventory();
            Assert.That(restored.Restore(save, currentTick: 0, currentDay: 0), Is.False);
            Assert.That(restored.Restore(save, currentTick: 0, currentDay: 1), Is.True);
            Assert.That(restored.DeliverDaily(Stock, 1), Is.Zero);
            Assert.That(restored.DeliverDaily(Stock, 2), Is.EqualTo(1));
            Assert.That(restored.GetContainer(Cupboard).Stacks[0].Quantity, Is.EqualTo(3));
            Assert.That(restored.GetContainer(Resident).Stacks[0].Quantity, Is.EqualTo(3));

            save.Deliveries[0].Quantity++;
            Assert.That(restored.Restore(save), Is.False);
            Assert.That(restored.GetContainer(Cupboard).Stacks[0].Quantity, Is.EqualTo(3));
        }

        private static Inventory CreateFoodInventory() => new Inventory(
            new[] { new InventoryItemDefinition { DefinitionId = Food, DisplayName = "Обед",
                Category = "Еда", UnitWeightGrams = 200, MaxStack = 3, UseKind = InventoryUseKind.Food } },
            new[] { Container(Player), Container(Resident), Container(Cupboard) },
            new[] { new InventorySeed { InstanceId = Stock, DefinitionId = Food, ContainerId = Cupboard, Quantity = 3 } },
            new[] { new InventoryDailySupply { OriginId = Stock, DailyLimit = 2, TargetQuantity = 3 } });

        private static Inventory CreateInventory() => new Inventory(
            new[] { new InventoryItemDefinition { DefinitionId = "test:book.paperback", DisplayName = "Книга",
                Category = "Книги", UnitWeightGrams = 100, MaxStack = 8 } },
            new[]
            {
                Container(Player), Container(Resident), Container(Cupboard),
                new InventoryContainerDefinition { ContainerId = Anchor, DisplayName = "Скамья", SingleStack = true }
            },
            new[]
            {
                Seed(Books, Cupboard, 6), Seed(OtherBooks, Resident, 7, "signed"),
                Seed("world:signed-books", Cupboard, 6, "signed")
            });

        private static InventoryContainerDefinition Container(string id) =>
            new InventoryContainerDefinition { ContainerId = id, DisplayName = id, ActorInventory = id == Player || id == Resident };

        private static InventorySeed Seed(string id, string container, int quantity, string variantId = null) =>
            new InventorySeed { InstanceId = id, DefinitionId = "test:book.paperback", ContainerId = container,
                Quantity = quantity, VariantId = variantId };

        private sealed class Access : IInventoryAccess
        {
            public string Closed;
            public bool CanAccess(string containerId) => containerId != Closed;
        }

        private sealed class Capacity : IInventoryCapacityProvider
        {
            public InventoryCapacity PlayerCapacity = new InventoryCapacity(4, 600);
            public InventoryCapacity AnchorCapacity = new InventoryCapacity(1, InventoryCapacity.Unlimited);

            public InventoryCapacity GetCapacity(string containerId, InventoryCapacity defaults) =>
                containerId == Player ? PlayerCapacity : containerId == Anchor ? AnchorCapacity : defaults;
        }
    }
}
