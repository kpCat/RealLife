using System;
using System.Collections.Generic;

namespace RealLife.Simulation
{
    [Serializable]
    public struct Area
    {
        public double MinX;
        public double MinZ;
        public double MaxX;
        public double MaxZ;

        public Area(double minX, double minZ, double maxX, double maxZ) =>
            (MinX, MinZ, MaxX, MaxZ) = (minX, minZ, maxX, maxZ);

        public bool Contains(LocalPoint point) =>
            point.X >= MinX && point.X <= MaxX && point.Z >= MinZ && point.Z <= MaxZ;

        public bool IsValid => WorldFrame.IsFinite(MinX) && WorldFrame.IsFinite(MinZ) &&
            WorldFrame.IsFinite(MaxX) && WorldFrame.IsFinite(MaxZ) && MinX < MaxX && MinZ < MaxZ;
    }

    public enum SurfaceKind { Asphalt = 1, Sidewalk, Path, Soil }
    public enum PropKind { Tree = 1, Planter, Bench, Workbench, Shelf, Crate, Streetlight }

    [Serializable]
    public sealed class SurfaceDefinition
    {
        public SurfaceKind Kind;
        public Area Area;
    }

    [Serializable]
    public sealed class PropDefinition
    {
        public PropKind Kind;
        public LocalPoint Position;
        public float Yaw;
    }

    [Serializable]
    public sealed class DoorDefinition
    {
        public string Id;
        public LocalPoint Hinge;
        public float Width;
        public float Height;
    }

    [Serializable]
    public sealed class ItemDefinition
    {
        public string Id;
        public string DefinitionId;
        public string Name;
        public LocalPoint Home;
    }

    [Serializable]
    public sealed class DistrictDefinition
    {
        public string Id;
        public string Title;
        public int Revision;
        public double Latitude;
        public double Longitude;
        public double Height;
        public Area Bounds;
        public LocalPoint Spawn;
        public Area Building;
        public DoorDefinition Door;
        public ItemDefinition Item;
        public SurfaceDefinition[] Surfaces;
        public PropDefinition[] Props;

        public void Validate()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (Revision < 1 || string.IsNullOrWhiteSpace(Title) || !Register(Id, ids) ||
                !Bounds.IsValid || !Inside(Bounds, Building) || !Spawn.IsFinite || !Bounds.Contains(Spawn))
                throw new ArgumentException("Invalid district identity, bounds or spawn.");

            if (Door == null || !Register(Door.Id, ids) || !Door.Hinge.IsFinite ||
                !Building.Contains(Door.Hinge) || Door.Hinge.Z != Building.MinZ || Door.Hinge.Y != 0 ||
                !WorldFrame.IsFinite(Door.Width) || !WorldFrame.IsFinite(Door.Height) ||
                Door.Width <= 0 || Door.Height <= 0 || Door.Hinge.X + Door.Width >= Building.MaxX)
                throw new ArgumentException("Invalid district entrance.");

            if (Item == null || !Register(Item.Id, ids) || !HasNamespace(Item.DefinitionId) ||
                string.IsNullOrWhiteSpace(Item.Name) || !Item.Home.IsFinite || !Building.Contains(Item.Home))
                throw new ArgumentException("Invalid item definition or placement.");

            foreach (var surface in Surfaces ?? Array.Empty<SurfaceDefinition>())
                if (surface == null || !Enum.IsDefined(typeof(SurfaceKind), surface.Kind) || !Inside(Bounds, surface.Area))
                    throw new ArgumentException("Invalid district surface.");

            foreach (var prop in Props ?? Array.Empty<PropDefinition>())
                if (prop == null || !Enum.IsDefined(typeof(PropKind), prop.Kind) || !prop.Position.IsFinite ||
                    !Bounds.Contains(prop.Position) || !WorldFrame.IsFinite(prop.Yaw))
                    throw new ArgumentException("Invalid district prop.");
        }

        private static bool Inside(Area outer, Area inner) => inner.IsValid &&
            outer.Contains(new LocalPoint(inner.MinX, 0, inner.MinZ)) &&
            outer.Contains(new LocalPoint(inner.MaxX, 0, inner.MaxZ));

        private static bool Register(string id, HashSet<string> ids) => HasNamespace(id) && ids.Add(id);

        private static bool HasNamespace(string id) => !string.IsNullOrWhiteSpace(id) &&
            id.IndexOf(':') > 0 && id[id.Length - 1] != ':';
    }
}
