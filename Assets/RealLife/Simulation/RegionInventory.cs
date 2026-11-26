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

        public static string Fingerprint(NeighborhoodCatalog catalog)
        {
            if (catalog == null) return "empty";
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
            writer.Flush();
            stream.Position = 0;
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static Inventory Create(Buildings buildings, NeighborhoodCatalog catalog)
        {
            var containers = new List<InventoryContainerDefinition>
            {
                new InventoryContainerDefinition
                {
                    ContainerId = PlayerId, DisplayName = "С собой", MaxSlots = 24, MaxWeightGrams = 18000
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
            return new Inventory(catalog?.Items ?? Array.Empty<InventoryItemDefinition>(), containers, seeds);
        }

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
