using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RealLife.Simulation
{
    public sealed class Inventory
    {
        private const char SplitSeparator = '~';
        private readonly Dictionary<string, InventoryItemDefinition> items =
            new Dictionary<string, InventoryItemDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, ContainerRules> rules = new Dictionary<string, ContainerRules>(StringComparer.Ordinal);
        private readonly Dictionary<string, Origin> origins = new Dictionary<string, Origin>(StringComparer.Ordinal);
        private readonly Dictionary<(string DefinitionId, string VariantId), long> baseTotals =
            new Dictionary<(string DefinitionId, string VariantId), long>();
        private readonly Dictionary<string, DailySupply> supplies = new Dictionary<string, DailySupply>(StringComparer.Ordinal);
        private Dictionary<string, ContainerContents> contents;
        private Dictionary<(string ActorId, string DefinitionId, string VariantId), (long LastTick, long Quantity)> uses =
            new Dictionary<(string, string, string), (long, long)>();
        private Dictionary<string, (long LastDay, long Quantity)> deliveries =
            new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        private long nextSplitId = 1;

        public Inventory(IEnumerable<InventoryItemDefinition> definitions,
            IEnumerable<InventoryContainerDefinition> containers, IEnumerable<InventorySeed> seeds,
            IEnumerable<InventoryDailySupply> dailySupplies = null)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            if (containers == null) throw new ArgumentNullException(nameof(containers));
            if (seeds == null) throw new ArgumentNullException(nameof(seeds));

            foreach (var definition in definitions)
            {
                if (definition == null || !ValidDefinitionId(definition.DefinitionId) ||
                    string.IsNullOrWhiteSpace(definition.DisplayName) || string.IsNullOrWhiteSpace(definition.Category) ||
                    definition.UnitWeightGrams < 0 || definition.MaxStack <= 0 ||
                    !Enum.IsDefined(typeof(InventoryUseKind), definition.UseKind) || items.ContainsKey(definition.DefinitionId))
                    throw new ArgumentException("Invalid or duplicate item definition.", nameof(definitions));

                items.Add(definition.DefinitionId, new InventoryItemDefinition
                {
                    DefinitionId = definition.DefinitionId, DisplayName = definition.DisplayName, Category = definition.Category,
                    UnitWeightGrams = definition.UnitWeightGrams, MaxStack = definition.MaxStack,
                    Appearance = definition.Appearance, UseKind = definition.UseKind
                });
            }

            foreach (var container in containers)
            {
                if (container == null || !ValidId(container.ContainerId) || string.IsNullOrWhiteSpace(container.DisplayName) ||
                    container.MaxSlots < InventoryCapacity.Unlimited || container.MaxWeightGrams < InventoryCapacity.Unlimited ||
                    rules.ContainsKey(container.ContainerId))
                    throw new ArgumentException("Invalid or duplicate inventory container.", nameof(containers));

                rules.Add(container.ContainerId, new ContainerRules(container));
            }

            contents = EmptyContents();
            long totalWeight = 0;
            foreach (var seed in seeds)
            {
                if (seed == null || !ValidId(seed.InstanceId) || seed.ContainerId == null || seed.DefinitionId == null ||
                    !contents.TryGetValue(seed.ContainerId, out var container) ||
                    !items.TryGetValue(seed.DefinitionId, out var definition) ||
                    !ValidQuantity(seed.Quantity, definition) ||
                    (!string.IsNullOrEmpty(seed.VariantId) && !ValidId(seed.VariantId)) || origins.ContainsKey(seed.InstanceId))
                    throw new ArgumentException("Invalid or duplicate initial inventory stack.", nameof(seeds));

                var origin = new Origin(seed, definition);
                var stack = origin.InitialStack;
                if (totalWeight > long.MaxValue - stack.WeightGrams ||
                    (container.Rules.SingleStack && container.Stacks.Count != 0))
                    throw new ArgumentException("Invalid initial inventory placement or weight.", nameof(seeds));

                origins.Add(seed.InstanceId, origin);
                container.Put(stack);
                totalWeight += stack.WeightGrams;
                baseTotals.TryGetValue(origin.Kind, out var quantity);
                baseTotals[origin.Kind] = quantity + seed.Quantity;
            }

            if (dailySupplies == null) return;
            foreach (var supply in dailySupplies)
            {
                if (supply?.OriginId == null || !origins.TryGetValue(supply.OriginId, out var origin) ||
                    origin.Definition.UseKind == InventoryUseKind.None || supply.DailyLimit <= 0 ||
                    supply.TargetQuantity < supply.DailyLimit || supply.TargetQuantity > origin.Definition.MaxStack ||
                    !supplies.TryAdd(supply.OriginId, new DailySupply(origin, supply.DailyLimit, supply.TargetQuantity)))
                    throw new ArgumentException("Invalid or duplicate daily inventory supply.", nameof(dailySupplies));
            }
        }

        public InventoryContainerView GetContainer(string containerId, IInventoryCapacityProvider capacityProvider = null)
        {
            if (containerId == null || !contents.TryGetValue(containerId, out var container)) return null;

            var capacity = container.Rules.ResolveCapacity(capacityProvider);
            return new InventoryContainerView(containerId, container.Rules.DisplayName, container.ProjectStacks(), capacity,
                container.TotalWeightGrams);
        }

        public InventoryMoveFailure CheckMove(InventoryMove command, IInventoryAccess access,
            IInventoryCapacityProvider capacityProvider = null) =>
            ValidateMove(command, access, capacityProvider, out _, out _, out _, out _);

        public InventoryMoveResult Move(InventoryMove command, IInventoryAccess access,
            IInventoryCapacityProvider capacityProvider = null)
        {
            var failure = ValidateMove(command, access, capacityProvider,
                out var source, out var destination, out var stack, out var target);
            if (failure != InventoryMoveFailure.None) return new InventoryMoveResult(failure);

            var wholeStack = command.Quantity == stack.Quantity;
            if (wholeStack) source.Remove(stack);
            else source.Put(stack.WithQuantity(stack.Quantity - command.Quantity));

            if (command.TargetInstanceId != null)
            {
                destination.Put(target.WithQuantity(target.Quantity + command.Quantity));
                return new InventoryMoveResult(InventoryMoveFailure.None, target.InstanceId);
            }

            var instanceId = wholeStack ? stack.InstanceId : SplitId(stack.Origin.InstanceId, nextSplitId++);
            destination.Put(new Stack(instanceId, stack.Origin, command.Quantity));
            return new InventoryMoveResult(InventoryMoveFailure.None, instanceId);
        }

        public InventoryUseFailure CheckUse(InventoryUse command, IInventoryAccess access)
            => ValidateUse(command, access, out _, out _, out _, out _);

        public InventoryUseResult Consume(InventoryUse command, IInventoryAccess access)
        {
            var failure = ValidateUse(command, access, out var container, out var stack, out var key, out var prior);
            if (failure != InventoryUseFailure.None) return new InventoryUseResult(failure);

            if (stack.Quantity == 1) container.Remove(stack);
            else container.Put(stack.WithQuantity(stack.Quantity - 1));
            uses[key] = (command.ActionTick, prior + 1);
            return new InventoryUseResult(InventoryUseFailure.None, stack.Origin.Definition.UseKind);
        }

        private InventoryUseFailure ValidateUse(InventoryUse command, IInventoryAccess access,
            out ContainerContents container, out Stack stack,
            out (string ActorId, string DefinitionId, string VariantId) key, out long consumed)
        {
            container = null;
            stack = default;
            key = default;
            consumed = 0;
            if (!IsActorInventory(command.ActorId) ||
                command.ActionTick < 0 || command.ActionTick == long.MaxValue)
                return InventoryUseFailure.InvalidAction;
            if (command.ContainerId == null || !contents.TryGetValue(command.ContainerId, out container))
                return InventoryUseFailure.UnknownContainer;
            if (access == null || !access.CanAccess(command.ActorId) || !access.CanAccess(command.ContainerId))
                return InventoryUseFailure.Inaccessible;
            if (command.InstanceId == null || !container.Stacks.TryGetValue(command.InstanceId, out stack))
                return InventoryUseFailure.UnknownStack;
            if (stack.Origin.Definition.UseKind == InventoryUseKind.None)
                return InventoryUseFailure.NotConsumable;

            key = (command.ActorId, stack.Origin.Kind.DefinitionId, stack.Origin.Kind.VariantId);
            if (!uses.TryGetValue(key, out var prior)) return InventoryUseFailure.None;
            if (command.ActionTick <= prior.LastTick) return InventoryUseFailure.DuplicateAction;
            consumed = prior.Quantity;
            return consumed < long.MaxValue ? InventoryUseFailure.None : InventoryUseFailure.QuantityLimit;
        }

        internal void DeliverAllDaily(long day, IInventoryCapacityProvider capacity)
        {
            foreach (var id in supplies.Keys) DeliverDaily(id, day, capacity);
        }

        public int DeliverDaily(string originId, long day, IInventoryCapacityProvider capacityProvider = null)
        {
            if (day <= 0 || originId == null || !supplies.TryGetValue(originId, out var supply)) return 0;
            deliveries.TryGetValue(originId, out var prior);
            if (day <= prior.LastDay) return 0;

            var origin = supply.Origin;
            var destination = contents[origin.ContainerId];
            long inStock = 0;
            Stack target = default;
            foreach (var stack in destination.Stacks.Values)
            {
                if (stack.Origin.Kind != origin.Kind) continue;
                inStock += stack.Quantity;
                if (target.InstanceId == null || StringComparer.Ordinal.Compare(stack.InstanceId, target.InstanceId) < 0)
                    target = stack;
            }

            var quantity = Math.Min(supply.DailyLimit, supply.TargetQuantity - inStock);
            if (quantity <= 0) return 0;
            var capacity = destination.Rules.ResolveCapacity(capacityProvider);
            var weightLimit = capacity.MaxWeightGrams == InventoryCapacity.Unlimited ? long.MaxValue : capacity.MaxWeightGrams;
            if (origin.Definition.UnitWeightGrams > 0)
                quantity = Math.Min(quantity, (weightLimit - destination.TotalWeightGrams) / origin.Definition.UnitWeightGrams);
            if (quantity <= 0 || prior.Quantity > long.MaxValue - quantity) return 0;

            if (target.InstanceId != null)
                destination.Put(target.WithQuantity(target.Quantity + (int)quantity));
            else
            {
                if (nextSplitId == long.MaxValue ||
                    (capacity.MaxSlots != InventoryCapacity.Unlimited && destination.Stacks.Count >= capacity.MaxSlots))
                    return 0;
                var id = SplitId(origin.InstanceId, nextSplitId++);
                destination.Put(new Stack(id, origin, (int)quantity));
            }
            deliveries[originId] = (day, prior.Quantity + quantity);
            return (int)quantity;
        }

        private InventoryMoveFailure ValidateMove(InventoryMove command, IInventoryAccess access,
            IInventoryCapacityProvider capacityProvider, out ContainerContents source, out ContainerContents destination,
            out Stack stack, out Stack target)
        {
            source = destination = null;
            stack = target = default;
            if (command.SourceContainerId == null || !contents.TryGetValue(command.SourceContainerId, out source) ||
                command.DestinationContainerId == null || !contents.TryGetValue(command.DestinationContainerId, out destination))
                return InventoryMoveFailure.UnknownContainer;

            var sameContainer = source == destination;
            if (access == null || !access.CanAccess(command.SourceContainerId) ||
                (!sameContainer && !access.CanAccess(command.DestinationContainerId)))
                return InventoryMoveFailure.Inaccessible;
            if (command.InstanceId == null || !source.Stacks.TryGetValue(command.InstanceId, out stack))
                return InventoryMoveFailure.UnknownStack;
            if (command.Quantity <= 0 || command.Quantity > stack.Quantity)
                return InventoryMoveFailure.InvalidQuantity;

            var wholeStack = command.Quantity == stack.Quantity;
            var merging = command.TargetInstanceId != null;
            if (merging)
            {
                if (command.TargetInstanceId == command.InstanceId ||
                    !destination.Stacks.TryGetValue(command.TargetInstanceId, out target))
                    return InventoryMoveFailure.InvalidTarget;
                if (target.Origin.Kind != stack.Origin.Kind)
                    return InventoryMoveFailure.IncompatibleStack;
                if (command.Quantity > target.Origin.Definition.MaxStack - target.Quantity)
                    return InventoryMoveFailure.StackLimit;
            }
            else if (sameContainer && wholeStack)
            {
                return InventoryMoveFailure.InvalidTarget;
            }

            var capacity = destination.Rules.ResolveCapacity(capacityProvider);
            var weightAdded = sameContainer ? 0 : (long)stack.Origin.Definition.UnitWeightGrams * command.Quantity;
            if ((!merging && capacity.MaxSlots != InventoryCapacity.Unlimited && destination.Stacks.Count >= capacity.MaxSlots) ||
                (weightAdded > 0 && capacity.MaxWeightGrams != InventoryCapacity.Unlimited &&
                    weightAdded > capacity.MaxWeightGrams - destination.TotalWeightGrams))
                return InventoryMoveFailure.Capacity;

            if (!merging && !wholeStack && nextSplitId == long.MaxValue)
                return InventoryMoveFailure.IdentityLimit;
            return InventoryMoveFailure.None;
        }

        public InventoryDelta Capture()
        {
            var changes = new List<InventoryStackDelta>();
            var remainingBaseIds = new HashSet<string>(origins.Keys, StringComparer.Ordinal);
            foreach (var pair in contents)
                foreach (var stack in pair.Value.Stacks.Values)
                {
                    if (remainingBaseIds.Remove(stack.InstanceId) && stack.Origin.ContainerId == pair.Key &&
                        stack.Origin.Quantity == stack.Quantity)
                        continue;

                    changes.Add(new InventoryStackDelta { InstanceId = stack.InstanceId, ContainerId = pair.Key,
                        Quantity = stack.Quantity });
                }

            foreach (var id in remainingBaseIds)
                changes.Add(new InventoryStackDelta { InstanceId = id });

            changes.Sort((left, right) => StringComparer.Ordinal.Compare(left.InstanceId, right.InstanceId));
            return new InventoryDelta
            {
                Stacks = changes.ToArray(), NextSplitId = nextSplitId,
                Uses = uses.OrderBy(pair => pair.Key.ActorId, StringComparer.Ordinal)
                    .ThenBy(pair => pair.Key.DefinitionId, StringComparer.Ordinal).ThenBy(pair => pair.Key.VariantId, StringComparer.Ordinal)
                    .Select(pair => new InventoryUseDelta
                    {
                        ActorId = pair.Key.ActorId, DefinitionId = pair.Key.DefinitionId, VariantId = pair.Key.VariantId,
                        Kind = items[pair.Key.DefinitionId].UseKind, LastTick = pair.Value.LastTick, Quantity = pair.Value.Quantity
                    }).ToArray(),
                Deliveries = deliveries.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new InventoryDeliveryDelta
                    {
                        OriginId = pair.Key, LastDay = pair.Value.LastDay, Quantity = pair.Value.Quantity
                    }).ToArray()
            };
        }

        public bool CanRestore(InventoryDelta delta, long currentTick = long.MaxValue, long currentDay = long.MaxValue) =>
            TryBuildContents(delta, currentTick, currentDay, out _, out _, out _);

        public bool Restore(InventoryDelta delta, long currentTick = long.MaxValue, long currentDay = long.MaxValue)
        {
            if (!TryBuildContents(delta, currentTick, currentDay, out var restored, out var restoredUses, out var restoredDeliveries))
                return false;
            contents = restored;
            uses = restoredUses;
            deliveries = restoredDeliveries;
            nextSplitId = delta.NextSplitId;
            return true;
        }

        private bool TryBuildContents(InventoryDelta delta, long currentTick, long currentDay,
            out Dictionary<string, ContainerContents> restored,
            out Dictionary<(string ActorId, string DefinitionId, string VariantId), (long LastTick, long Quantity)> restoredUses,
            out Dictionary<string, (long LastDay, long Quantity)> restoredDeliveries)
        {
            restored = null;
            restoredUses = null;
            restoredDeliveries = null;
            if (delta == null || delta.NextSplitId <= 0 || delta.Stacks == null || currentTick < 0 || currentDay < 0)
                return false;

            var remaining = new Dictionary<(string DefinitionId, string VariantId), long>(baseTotals);
            if (!TryReadLedger(delta, currentTick, currentDay, remaining, out restoredUses, out restoredDeliveries))
                return false;

            var candidate = EmptyContents();
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var splitIds = new HashSet<long>();
            bool Place(string containerId, Stack stack)
            {
                if (containerId == null || !candidate.TryGetValue(containerId, out var container) ||
                    (container.Rules.SingleStack && container.Stacks.Count != 0) ||
                    stack.WeightGrams > long.MaxValue - container.TotalWeightGrams)
                    return false;

                var available = remaining[stack.Origin.Kind];
                if (available < stack.Quantity) return false;
                remaining[stack.Origin.Kind] = available - stack.Quantity;
                container.Put(stack);
                return true;
            }

            foreach (var change in delta.Stacks)
            {
                if (change == null || string.IsNullOrEmpty(change.InstanceId) || !changed.Add(change.InstanceId)) return false;

                var isBase = origins.TryGetValue(change.InstanceId, out var origin);
                if (change.Quantity == 0)
                {
                    if (!isBase || !string.IsNullOrEmpty(change.ContainerId)) return false;
                    continue;
                }

                if (!isBase && (!TrySplitOrigin(change.InstanceId, delta.NextSplitId, out origin, out var sequence) ||
                    !splitIds.Add(sequence)))
                    return false;
                if (!ValidQuantity(change.Quantity, origin.Definition) ||
                    !Place(change.ContainerId, new Stack(change.InstanceId, origin, change.Quantity)))
                    return false;
            }

            foreach (var origin in origins.Values)
                if (!changed.Contains(origin.InstanceId) && !Place(origin.ContainerId, origin.InitialStack))
                    return false;
            foreach (var quantity in remaining.Values)
                if (quantity != 0) return false;

            restored = candidate;
            return true;
        }

        private bool TryReadLedger(InventoryDelta delta, long currentTick, long currentDay,
            Dictionary<(string DefinitionId, string VariantId), long> available,
            out Dictionary<(string ActorId, string DefinitionId, string VariantId), (long LastTick, long Quantity)> readUses,
            out Dictionary<string, (long LastDay, long Quantity)> readDeliveries)
        {
            readUses = new Dictionary<(string, string, string), (long, long)>();
            readDeliveries = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
            foreach (var entry in delta.Deliveries ?? Array.Empty<InventoryDeliveryDelta>())
            {
                if (entry?.OriginId == null || !supplies.TryGetValue(entry.OriginId, out var source) ||
                    entry.LastDay <= 0 || entry.LastDay > currentDay || entry.Quantity <= 0 ||
                    entry.Quantity > (decimal)entry.LastDay * source.DailyLimit ||
                    !readDeliveries.TryAdd(entry.OriginId, (entry.LastDay, entry.Quantity)))
                    return false;

                var quantity = available[source.Origin.Kind];
                if (entry.Quantity > long.MaxValue - quantity) return false;
                available[source.Origin.Kind] = quantity + entry.Quantity;
            }

            foreach (var entry in delta.Uses ?? Array.Empty<InventoryUseDelta>())
            {
                if (entry == null || !IsActorInventory(entry.ActorId) || entry.DefinitionId == null ||
                    !items.TryGetValue(entry.DefinitionId, out var item) ||
                    item.UseKind == InventoryUseKind.None || entry.Kind != item.UseKind ||
                    entry.LastTick < 0 || entry.LastTick == long.MaxValue || entry.LastTick > currentTick ||
                    entry.Quantity <= 0 || entry.Quantity > entry.LastTick + 1)
                    return false;

                var variant = string.IsNullOrEmpty(entry.VariantId) ? null : entry.VariantId;
                var kind = (entry.DefinitionId, variant);
                if (!available.TryGetValue(kind, out var quantity) || quantity < entry.Quantity ||
                    !readUses.TryAdd((entry.ActorId, entry.DefinitionId, variant), (entry.LastTick, entry.Quantity)))
                    return false;
                available[kind] = quantity - entry.Quantity;
            }
            return true;
        }

        private bool IsActorInventory(string id) => id != null && rules.TryGetValue(id, out var rule) && rule.ActorInventory;

        private bool TrySplitOrigin(string id, long nextId, out Origin origin, out long sequence)
        {
            origin = null;
            sequence = 0;
            var separator = id.IndexOf(SplitSeparator);
            return separator > 0 && origins.TryGetValue(id.Substring(0, separator), out origin) &&
                long.TryParse(id.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence) &&
                sequence > 0 && sequence < nextId && SplitId(origin.InstanceId, sequence) == id;
        }

        private Dictionary<string, ContainerContents> EmptyContents()
        {
            var result = new Dictionary<string, ContainerContents>(rules.Count, StringComparer.Ordinal);
            foreach (var pair in rules)
                result.Add(pair.Key, new ContainerContents(pair.Value));
            return result;
        }

        private static string SplitId(string originId, long id) =>
            originId + SplitSeparator + id.ToString(CultureInfo.InvariantCulture);

        private static bool ValidQuantity(int quantity, InventoryItemDefinition definition) =>
            quantity > 0 && quantity <= definition.MaxStack;

        private static bool ValidId(string id) => id != null && Regex.IsMatch(id,
            @"\A[a-zA-Z0-9][a-zA-Z0-9.:/_-]*\z", RegexOptions.CultureInvariant);

        private static bool ValidDefinitionId(string id) => id != null && Regex.IsMatch(id,
            @"\A[a-z0-9][a-z0-9_-]*:[a-z0-9][a-z0-9_-]*(?:\.[a-z0-9][a-z0-9_-]*)+\z", RegexOptions.CultureInvariant);

        private readonly struct DailySupply
        {
            public readonly Origin Origin;
            public readonly int DailyLimit, TargetQuantity;

            public DailySupply(Origin origin, int dailyLimit, int targetQuantity) =>
                (Origin, DailyLimit, TargetQuantity) = (origin, dailyLimit, targetQuantity);
        }

        private sealed class Origin
        {
            public readonly string InstanceId;
            public readonly string ContainerId;
            public readonly int Quantity;
            public readonly InventoryItemDefinition Definition;
            public readonly (string DefinitionId, string VariantId) Kind;

            public Origin(InventorySeed seed, InventoryItemDefinition definition)
            {
                (InstanceId, ContainerId, Quantity, Definition) = (seed.InstanceId, seed.ContainerId, seed.Quantity, definition);
                Kind = (definition.DefinitionId, string.IsNullOrEmpty(seed.VariantId) ? null : seed.VariantId);
            }

            public Stack InitialStack => new Stack(InstanceId, this, Quantity);
        }

        private readonly struct Stack
        {
            public readonly string InstanceId;
            public readonly Origin Origin;
            public readonly int Quantity;

            public Stack(string instanceId, Origin origin, int quantity) =>
                (InstanceId, Origin, Quantity) = (instanceId, origin, quantity);

            public long WeightGrams => (long)Origin.Definition.UnitWeightGrams * Quantity;
            public Stack WithQuantity(int quantity) => new Stack(InstanceId, Origin, quantity);
            public InventoryStackView Project() => new InventoryStackView(InstanceId, Origin.InstanceId,
                Origin.Definition, Quantity, Origin.Kind.VariantId);
        }

        private sealed class ContainerRules
        {
            public readonly string Id;
            public readonly string DisplayName;
            public readonly bool SingleStack;
            public readonly bool ActorInventory;
            private readonly InventoryCapacity defaults;

            public ContainerRules(InventoryContainerDefinition definition)
            {
                (Id, DisplayName, SingleStack, ActorInventory) =
                    (definition.ContainerId, definition.DisplayName, definition.SingleStack, definition.ActorInventory);
                defaults = LimitSlots(new InventoryCapacity(definition.MaxSlots, definition.MaxWeightGrams));
            }

            public InventoryCapacity ResolveCapacity(IInventoryCapacityProvider provider) =>
                provider == null ? defaults : LimitSlots(provider.GetCapacity(Id, defaults));

            private InventoryCapacity LimitSlots(InventoryCapacity capacity) =>
                SingleStack && capacity.MaxSlots != 0 ? new InventoryCapacity(1, capacity.MaxWeightGrams) : capacity;
        }

        private sealed class ContainerContents
        {
            public readonly ContainerRules Rules;
            public readonly Dictionary<string, Stack> Stacks = new Dictionary<string, Stack>(StringComparer.Ordinal);
            public long TotalWeightGrams { get; private set; }
            private IReadOnlyList<InventoryStackView> projection;

            public ContainerContents(ContainerRules rules) => Rules = rules;

            public void Put(Stack stack)
            {
                if (Stacks.TryGetValue(stack.InstanceId, out var replaced))
                    TotalWeightGrams -= replaced.WeightGrams;
                Stacks[stack.InstanceId] = stack;
                TotalWeightGrams += stack.WeightGrams;
                projection = null;
            }

            public void Remove(Stack stack)
            {
                Stacks.Remove(stack.InstanceId);
                TotalWeightGrams -= stack.WeightGrams;
                projection = null;
            }

            public IReadOnlyList<InventoryStackView> ProjectStacks()
            {
                if (projection != null) return projection;

                var stacks = new List<InventoryStackView>(Stacks.Count);
                foreach (var stack in Stacks.Values)
                    stacks.Add(stack.Project());
                stacks.Sort((left, right) => StringComparer.Ordinal.Compare(left.InstanceId, right.InstanceId));
                projection = stacks.AsReadOnly();
                return projection;
            }
        }
    }
}
