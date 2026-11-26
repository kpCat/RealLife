using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RealLife.Simulation
{
    public sealed class Inventory
    {
        private const char SplitSeparator = '~';
        private readonly Dictionary<string, ContainerRules> rules = new Dictionary<string, ContainerRules>(StringComparer.Ordinal);
        private readonly Dictionary<string, Origin> origins = new Dictionary<string, Origin>(StringComparer.Ordinal);
        private readonly Dictionary<(string DefinitionId, string VariantId), long> baseTotals =
            new Dictionary<(string DefinitionId, string VariantId), long>();
        private Dictionary<string, ContainerContents> contents;
        private long nextSplitId = 1;

        public Inventory(IEnumerable<InventoryItemDefinition> definitions,
            IEnumerable<InventoryContainerDefinition> containers, IEnumerable<InventorySeed> seeds)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            if (containers == null) throw new ArgumentNullException(nameof(containers));
            if (seeds == null) throw new ArgumentNullException(nameof(seeds));

            var items = new Dictionary<string, InventoryItemDefinition>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (definition == null || !ValidDefinitionId(definition.DefinitionId) ||
                    string.IsNullOrWhiteSpace(definition.DisplayName) || string.IsNullOrWhiteSpace(definition.Category) ||
                    definition.UnitWeightGrams < 0 || definition.MaxStack <= 0 || items.ContainsKey(definition.DefinitionId))
                    throw new ArgumentException("Invalid or duplicate item definition.", nameof(definitions));

                items.Add(definition.DefinitionId, new InventoryItemDefinition
                {
                    DefinitionId = definition.DefinitionId, DisplayName = definition.DisplayName, Category = definition.Category,
                    UnitWeightGrams = definition.UnitWeightGrams, MaxStack = definition.MaxStack,
                    Appearance = definition.Appearance
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
            return new InventoryDelta { Stacks = changes.ToArray(), NextSplitId = nextSplitId };
        }

        public bool CanRestore(InventoryDelta delta) => TryBuildContents(delta, out _);

        public bool Restore(InventoryDelta delta)
        {
            if (!TryBuildContents(delta, out var restored)) return false;
            contents = restored;
            nextSplitId = delta.NextSplitId;
            return true;
        }

        private bool TryBuildContents(InventoryDelta delta, out Dictionary<string, ContainerContents> restored)
        {
            restored = null;
            if (delta == null || delta.NextSplitId <= 0 || delta.Stacks == null) return false;

            var candidate = EmptyContents();
            var remaining = new Dictionary<(string DefinitionId, string VariantId), long>(baseTotals);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var splitIds = new HashSet<long>();
            bool Place(string containerId, Stack stack)
            {
                if (containerId == null || !candidate.TryGetValue(containerId, out var container) ||
                    (container.Rules.SingleStack && container.Stacks.Count != 0))
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
            private readonly InventoryCapacity defaults;

            public ContainerRules(InventoryContainerDefinition definition)
            {
                (Id, DisplayName, SingleStack) = (definition.ContainerId, definition.DisplayName, definition.SingleStack);
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
