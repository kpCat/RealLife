using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace RealLife.Simulation
{
    [Serializable]
    public sealed class NeighborhoodCatalog
    {
        public InventoryItemDefinition[] Items;
        public FurnishingItem[] Furnishings;
        public InventoryItemDefinition[] Provisions;
    }

    [Serializable]
    public sealed class FurnishingItem
    {
        public string RoomRole;
        public string DefinitionId;
        public string VariantId;
        public int Quantity;
    }

    internal static class RegionInventory
    {
        public const string PlayerId = "earth:actor.player";
        private const int ProvisionsRevision = 1;
        private const int PersonalQuantity = 2, HouseholdQuantity = 8, PublicQuantity = 32;

        public static string Fingerprint(NeighborhoodCatalog catalog, bool includeProvisions = false)
        {
            if (catalog == null) return includeProvisions ? $"empty-v4-{ProvisionsRevision}" : "empty";
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(catalog.Items.Length);
            foreach (var item in catalog.Items.OrderBy(item => item.DefinitionId, StringComparer.Ordinal))
            {
                writer.Write(item.DefinitionId);
                writer.Write(item.UnitWeightGrams);
                writer.Write(item.MaxStack);
            }
            writer.Write(catalog.Furnishings.Length);
            foreach (var furnishing in catalog.Furnishings)
            {
                writer.Write(furnishing.RoomRole);
                writer.Write(furnishing.DefinitionId);
                writer.Write(furnishing.VariantId ?? "");
                writer.Write(furnishing.Quantity);
            }
            if (includeProvisions)
            {
                writer.Write(ProvisionsRevision);
                var provisions = catalog.Provisions ?? Array.Empty<InventoryItemDefinition>();
                writer.Write(provisions.Length);
                foreach (var item in provisions.OrderBy(item => item.DefinitionId, StringComparer.Ordinal))
                {
                    writer.Write(item.DefinitionId);
                    writer.Write(item.UnitWeightGrams);
                    writer.Write(item.MaxStack);
                    writer.Write((int)item.UseKind);
                }
            }
            writer.Flush();
            stream.Position = 0;
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static Inventory Create(Buildings buildings, NeighborhoodCatalog catalog, IReadOnlyList<string> residentIds = null)
        {
            var containers = new List<InventoryContainerDefinition>
            {
                new InventoryContainerDefinition
                {
                    ContainerId = PlayerId, DisplayName = "С собой", MaxSlots = 24, MaxWeightGrams = 18000, ActorInventory = true
                }
            };
            var seeds = new List<InventorySeed>();
            var choices = new Dictionary<string, List<FurnishingItem>>(StringComparer.Ordinal);
            if (catalog != null)
            {
                foreach (var item in catalog.Furnishings)
                {
                    if (!choices.TryGetValue(item.RoomRole, out var options))
                        choices.Add(item.RoomRole, options = new List<FurnishingItem>());
                    options.Add(item);
                }
            }

            foreach (var plan in buildings.Plans)
                foreach (var anchor in plan.Anchors)
                {
                    var surface = anchor.Kind == BuildingAnchorKind.Surface;
                    containers.Add(new InventoryContainerDefinition
                    {
                        ContainerId = anchor.Id, DisplayName = surface ? "Поверхность" : "Хранилище",
                        MaxSlots = anchor.Capacity, MaxWeightGrams = surface ? 4000 : 24000, SingleStack = surface
                    });
                    if (!choices.TryGetValue(anchor.Role.ToString(), out var options)) continue;
                    var item = options[Variant(anchor.Id, options.Count)];
                    seeds.Add(new InventorySeed
                    {
                        InstanceId = anchor.Id + "/item", ContainerId = anchor.Id, DefinitionId = item.DefinitionId,
                        VariantId = item.VariantId, Quantity = item.Quantity
                    });
                }
            var items = catalog?.Items ?? Array.Empty<InventoryItemDefinition>();
            if (residentIds == null) return new Inventory(items, containers, seeds);

            var provisions = catalog?.Provisions ?? Array.Empty<InventoryItemDefinition>();
            var supplies = new List<InventoryDailySupply>();
            foreach (var id in residentIds)
            {
                containers.Add(new InventoryContainerDefinition
                {
                    ContainerId = id, DisplayName = "Личные вещи", MaxSlots = 8, MaxWeightGrams = 5000, ActorInventory = true
                });
                SeedProvisions(id, PersonalQuantity, replenish: false);
            }

            // No new stacks are placed on single-stack surfaces, or in legacy inventories.
            foreach (var plan in buildings.Plans)
            {
                var storage = ProvisionSource(plan);
                if (storage == null) continue;
                var publicStore = plan.Kind == BuildingKind.PublicPremises;
                SeedProvisions(storage.Id, publicStore ? PublicQuantity : HouseholdQuantity, publicStore);
            }
            return new Inventory(items.Concat(provisions), containers, seeds, supplies);

            void SeedProvisions(string containerId, int desiredQuantity, bool replenish)
            {
                foreach (var item in provisions)
                {
                    var id = containerId + "/provision/" + item.DefinitionId;
                    var quantity = Math.Min(desiredQuantity, item.MaxStack);
                    seeds.Add(new InventorySeed
                    {
                        InstanceId = id, ContainerId = containerId, DefinitionId = item.DefinitionId, Quantity = quantity
                    });
                    if (replenish) supplies.Add(new InventoryDailySupply
                    {
                        OriginId = id, DailyLimit = quantity, TargetQuantity = quantity
                    });
                }
            }
        }

        public static BuildingAnchor ProvisionSource(BuildingPlan plan) =>
            plan.Anchors.Where(anchor => anchor.Kind == BuildingAnchorKind.Storage)
                .OrderBy(anchor => anchor.Role == BuildingRoomRole.Kitchen ? 0 : 1).FirstOrDefault();

        public static void Restock(Inventory inventory, long day, IInventoryCapacityProvider capacity) =>
            inventory.DeliverAllDaily(day, capacity);

        private static int Variant(string id, int count)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var letter in id) hash = (hash ^ letter) * 16777619;
                return (int)(hash % (uint)count);
            }
        }
    }
}
