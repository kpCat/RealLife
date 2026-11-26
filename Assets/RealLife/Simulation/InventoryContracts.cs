using System;
using System.Collections.Generic;

namespace RealLife.Simulation
{
    [Serializable]
    public sealed class InventoryItemDefinition
    {
        public string DefinitionId;
        public string DisplayName;
        public string Category;
        public string Appearance;
        public int UnitWeightGrams;
        public int MaxStack;
        public InventoryUseKind UseKind;
    }

    [Serializable]
    public sealed class InventoryContainerDefinition
    {
        public string ContainerId;
        public string DisplayName;
        public int MaxSlots = InventoryCapacity.Unlimited;
        public long MaxWeightGrams = InventoryCapacity.Unlimited;
        public bool SingleStack;
        public bool ActorInventory;
    }

    [Serializable]
    public sealed class InventorySeed
    {
        public string InstanceId;
        public string DefinitionId;
        public string ContainerId;
        public int Quantity;
        public string VariantId;
    }

    public sealed class InventoryDailySupply
    {
        public string OriginId;
        public int DailyLimit;
        public int TargetQuantity;
    }

    public readonly struct InventoryCapacity
    {
        public const int Unlimited = -1;

        public int MaxSlots { get; }
        public long MaxWeightGrams { get; }

        public InventoryCapacity(int maxSlots, long maxWeightGrams)
        {
            if (maxSlots < Unlimited) throw new ArgumentOutOfRangeException(nameof(maxSlots));
            if (maxWeightGrams < Unlimited) throw new ArgumentOutOfRangeException(nameof(maxWeightGrams));
            (MaxSlots, MaxWeightGrams) = (maxSlots, maxWeightGrams);
        }
    }

    public interface IInventoryAccess
    {
        bool CanAccess(string containerId);
    }

    public interface IInventoryCapacityProvider
    {
        InventoryCapacity GetCapacity(string containerId, InventoryCapacity defaults);
    }

    public readonly struct InventoryMove
    {
        public string SourceContainerId { get; }
        public string InstanceId { get; }
        public int Quantity { get; }
        public string DestinationContainerId { get; }
        public string TargetInstanceId { get; }

        public InventoryMove(string sourceContainerId, string instanceId, int quantity,
            string destinationContainerId, string targetInstanceId = null) =>
            (SourceContainerId, InstanceId, Quantity, DestinationContainerId, TargetInstanceId) =
            (sourceContainerId, instanceId, quantity, destinationContainerId, targetInstanceId);
    }

    public enum InventoryMoveFailure
    {
        None, UnknownContainer, Inaccessible, UnknownStack, InvalidQuantity, InvalidTarget,
        IncompatibleStack, StackLimit, Capacity, IdentityLimit
    }

    public readonly struct InventoryMoveResult
    {
        public bool Succeeded => InstanceId != null;
        public InventoryMoveFailure Failure { get; }
        public string InstanceId { get; }

        internal InventoryMoveResult(InventoryMoveFailure failure, string instanceId = null) =>
            (Failure, InstanceId) = (failure, instanceId);
    }

    public enum InventoryUseKind { None, Food, Drink }

    public readonly struct InventoryUse
    {
        public string ActorId { get; }
        public string ContainerId { get; }
        public string InstanceId { get; }
        public long ActionTick { get; }

        public InventoryUse(string actorId, string containerId, string instanceId, long actionTick) =>
            (ActorId, ContainerId, InstanceId, ActionTick) = (actorId, containerId, instanceId, actionTick);
    }

    public enum InventoryUseFailure
    {
        None, UnknownContainer, Inaccessible, UnknownStack, NotConsumable, InvalidAction, DuplicateAction, QuantityLimit
    }

    public readonly struct InventoryUseResult
    {
        public bool Succeeded => Kind != InventoryUseKind.None;
        public InventoryUseFailure Failure { get; }
        public InventoryUseKind Kind { get; }

        internal InventoryUseResult(InventoryUseFailure failure, InventoryUseKind kind = InventoryUseKind.None) =>
            (Failure, Kind) = (failure, kind);
    }

    public readonly struct InventoryStackView
    {
        public string InstanceId { get; }
        public string OriginId { get; }
        public string DefinitionId { get; }
        public string DisplayName { get; }
        public string Category { get; }
        public string Appearance { get; }
        public int Quantity { get; }
        public int MaxStack { get; }
        public string VariantId { get; }
        public InventoryUseKind UseKind { get; }

        internal InventoryStackView(string instanceId, string originId, InventoryItemDefinition definition,
            int quantity, string variantId)
        {
            (InstanceId, OriginId, DefinitionId, DisplayName, Category) =
                (instanceId, originId, definition.DefinitionId, definition.DisplayName, definition.Category);
            (Quantity, MaxStack, VariantId) = (quantity, definition.MaxStack, variantId);
            Appearance = definition.Appearance;
            UseKind = definition.UseKind;
        }
    }

    public sealed class InventoryContainerView
    {
        public string ContainerId { get; }
        public string DisplayName { get; }
        public IReadOnlyList<InventoryStackView> Stacks { get; }
        public InventoryCapacity Capacity { get; }
        public long TotalWeightGrams { get; }

        internal InventoryContainerView(string containerId, string displayName, IReadOnlyList<InventoryStackView> stacks,
            InventoryCapacity capacity, long totalWeightGrams) =>
            (ContainerId, DisplayName, Stacks, Capacity, TotalWeightGrams) =
            (containerId, displayName, stacks, capacity, totalWeightGrams);
    }

    [Serializable]
    public sealed class InventoryDelta
    {
        public long NextSplitId = 1;
        public InventoryStackDelta[] Stacks = Array.Empty<InventoryStackDelta>();
        public InventoryUseDelta[] Uses = Array.Empty<InventoryUseDelta>();
        public InventoryDeliveryDelta[] Deliveries = Array.Empty<InventoryDeliveryDelta>();
    }

    [Serializable]
    public sealed class InventoryStackDelta
    {
        // Zero quantity and no container mean that the base instance has no placement.
        public string InstanceId;
        public string ContainerId;
        public int Quantity;
    }

    [Serializable]
    public sealed class InventoryUseDelta
    {
        public string ActorId, DefinitionId, VariantId;
        public long LastTick, Quantity;
        public InventoryUseKind Kind;
    }

    [Serializable]
    public sealed class InventoryDeliveryDelta
    {
        public string OriginId;
        public long LastDay, Quantity;
    }
}
