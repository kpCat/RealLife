using System;

namespace RealLife.Simulation
{
    internal static class SimulationTime
    {
        public const int TicksPerSecond = 20;
        public static int MinuteOfDay(long tick) => (int)((8 * 60 + tick / (TicksPerSecond * 4)) % (24 * 60));
        public static long Advance(long tick) => tick < long.MaxValue - 1 ? tick + 1 : tick;
    }

    internal static class PlayerPose
    {
        public static bool ValidLook(float yaw, float pitch) =>
            WorldFrame.IsFinite(yaw) && yaw >= 0 && yaw < 360 &&
            WorldFrame.IsFinite(pitch) && pitch >= -85 && pitch <= 85;
    }

    [Serializable]
    public sealed class RegionSnapshot
    {
        public int Version;
        public string WorldId, PackageId, PackageRevision;
        public int PackageFormat;
        public GlobalPosition Player;
        public float Yaw, Pitch;
        public long Tick;
        public int BuildingGenerator, InteriorFloor;
        public string InteriorId, InventoryFingerprint;
        public string[] OpenDoors;
        public InventoryDelta Inventory;
    }

    public enum DoorActionResult { Applied, UnknownTarget, OutOfReach, Obstructed }

    public sealed class RegionSession
    {
        public const int SnapshotVersion = 3;
        private const int LegacySnapshotVersion = 2;
        private readonly RegionWorld world;
        private readonly IInventoryCapacityProvider capacity;
        private readonly Inventory inventory;
        private readonly string inventoryFingerprint;
        private GlobalPosition player;

        public RegionSession(RegionWorld world, GeoPoint spawn, float yaw, NeighborhoodCatalog catalog = null,
            IInventoryCapacityProvider capacity = null)
        {
            this.world = world;
            this.capacity = capacity;
            Buildings = new Buildings(world);
            inventory = RegionInventory.Create(Buildings, catalog);
            inventoryFingerprint = RegionInventory.Fingerprint(catalog);
            if (!world.IsSafeStart(spawn) || !world.TryGround(spawn, out var ground) ||
                !SetPlayerPose(new LocalPoint(spawn.X, ground + 0.08, spawn.Z), yaw, 0))
                throw new ArgumentException("Invalid region spawn.");
        }

        public string WorldId => $"earth:{world.PackageId}";
        public Buildings Buildings { get; }
        public string PlayerInventoryId => RegionInventory.PlayerId;
        public LocalPoint PlayerLocal => world.Frame.ToLocal(player);
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public long Tick { get; private set; }
        public int MinuteOfDay => SimulationTime.MinuteOfDay(Tick);
        public void AdvanceTick() => Tick = SimulationTime.Advance(Tick);

        public bool SetPlayerPose(LocalPoint position, float yaw, float pitch)
        {
            if (!ValidPose(position, yaw, pitch)) return false;
            player = world.Frame.ToGlobal(position);
            Yaw = yaw;
            Pitch = pitch;
            return true;
        }

        public InventoryContainerView GetInventory(string id) => inventory.GetContainer(id, capacity);

        public bool CanUseAnchor(string id) => Buildings.TryGetAnchor(id, out var anchor) &&
            (InReach(anchor.Position) || InReach(anchor.ContainerPosition));

        public InventoryMoveResult MoveItems(InventoryMove command, string focusedAnchorId)
            => inventory.Move(command, Access(focusedAnchorId), capacity);

        public InventoryMoveFailure CheckMove(InventoryMove command, string focusedAnchorId)
            => inventory.CheckMove(command, Access(focusedAnchorId), capacity);

        private RegionInventoryAccess Access(string anchor) => new RegionInventoryAccess(CanUseAnchor(anchor) ? anchor : null);

        public DoorActionResult ToggleDoor(string id)
        {
            if (!Buildings.TryGetDoor(id, out var door)) return DoorActionResult.UnknownTarget;
            var feet = PlayerLocal;
            if (!InReach(door.GetAim(Buildings.IsDoorOpen(id)))) return DoorActionResult.OutOfReach;
            if (door.ContainsSwing(feet)) return DoorActionResult.Obstructed;
            return Buildings.TryToggleDoor(id, out _) ? DoorActionResult.Applied : DoorActionResult.UnknownTarget;
        }

        public RegionSnapshot Capture()
        {
            var position = PlayerLocal;
            var floor = 0;
            Buildings.TryLocate(position, true, out var interior);
            interior?.TryLocate(position, true, out floor);
            return new RegionSnapshot
            {
                Version = SnapshotVersion, WorldId = WorldId, PackageId = world.PackageId,
                PackageFormat = RegionWorld.PackageFormat, PackageRevision = world.Revision, Player = player,
                Yaw = Yaw, Pitch = Pitch, Tick = Tick, BuildingGenerator = Buildings.GeneratorVersion,
                InteriorId = interior?.Id, InteriorFloor = floor, OpenDoors = Buildings.CaptureOpenDoors(),
                InventoryFingerprint = inventoryFingerprint,
                Inventory = inventory.Capture()
            };
        }

        public bool Matches(RegionSnapshot snapshot) => snapshot != null &&
            (snapshot.Version == LegacySnapshotVersion || snapshot.Version == SnapshotVersion &&
                snapshot.BuildingGenerator == Buildings.GeneratorVersion && snapshot.InventoryFingerprint == inventoryFingerprint) &&
            snapshot.WorldId == WorldId && snapshot.PackageId == world.PackageId &&
            snapshot.PackageFormat == RegionWorld.PackageFormat && snapshot.PackageRevision == world.Revision;

        public bool CanRestore(RegionSnapshot snapshot) => TryGetRestorePosition(snapshot, out _);

        public bool TryGetRestorePosition(RegionSnapshot snapshot, out LocalPoint position)
        {
            position = default;
            if (!Matches(snapshot) || snapshot.Tick < 0 || snapshot.Tick == long.MaxValue || !snapshot.Player.IsFinite)
                return false;
            position = world.Frame.ToLocal(snapshot.Player);
            if (!ValidCell(position, snapshot.Yaw, snapshot.Pitch)) return false;
            var flat = new GeoPoint(position.X, position.Z);
            if (snapshot.Version == LegacySnapshotVersion)
            {
                if (!ValidOutdoorPose(position, flat, legacy: true)) return false;
                // Old poses can occupy terrain now covered by the shallow entrance ramp.
                if (TryEntrySupport(position, out var support) && position.Y < support)
                    position = new LocalPoint(position.X, support + .04, position.Z);
                return Buildings.IsPoseClearOfDoors(position, Array.Empty<string>());
            }

            if (!Buildings.ValidateOpenDoors(snapshot.OpenDoors) || !inventory.CanRestore(snapshot.Inventory) ||
                !Buildings.IsPoseClearOfDoors(position, snapshot.OpenDoors)) return false;
            if (string.IsNullOrEmpty(snapshot.InteriorId))
                return snapshot.InteriorFloor == 0 && ValidOutdoorPose(position, flat, legacy: false);
            return Buildings.TryGet(snapshot.InteriorId, out var plan) &&
                plan.TryLocate(position, true, out var floor) && floor == snapshot.InteriorFloor;
        }

        public bool Restore(RegionSnapshot snapshot)
        {
            if (!TryGetRestorePosition(snapshot, out var position)) return false;
            Buildings.RestoreOpenDoors(snapshot.Version == LegacySnapshotVersion ? Array.Empty<string>() : snapshot.OpenDoors);
            inventory.Restore(snapshot.Version == LegacySnapshotVersion ? new InventoryDelta() : snapshot.Inventory);
            player = snapshot.Version == LegacySnapshotVersion && position.Y != world.Frame.ToLocal(snapshot.Player).Y
                ? world.Frame.ToGlobal(position) : snapshot.Player;
            Yaw = snapshot.Yaw;
            Pitch = snapshot.Pitch;
            Tick = snapshot.Tick;
            return true;
        }

        private bool ValidPose(LocalPoint p, float yaw, float pitch)
        {
            if (!ValidCell(p, yaw, pitch)) return false;
            if (Buildings.TryLocate(p, false, out _)) return true;
            var flat = new GeoPoint(p.X, p.Z);
            return world.TryGround(flat, out var ground) &&
                p.Y >= ground - .4 && p.Y <= ground + 1.5;
        }

        private bool ValidCell(LocalPoint p, float yaw, float pitch)
        {
            if (!p.IsFinite || !PlayerPose.ValidLook(yaw, pitch)) return false;
            var flat = new GeoPoint(p.X, p.Z);
            return world.DistanceToOutline(flat) >= .4 && world.TryChunk(world.CellAt(flat), out _);
        }

        private bool ValidOutdoorPose(LocalPoint p, GeoPoint flat, bool legacy)
        {
            if (!world.IsOpen(flat, .05) || !world.TryGround(flat, out var ground)) return false;
            if (!legacy && TryEntrySupport(p, out var support)) ground = support;
            return legacy ? p.Y >= ground - .4 && p.Y <= ground + 1.5 : p.Y >= ground - .13 && p.Y <= ground + .35;
        }

        private bool TryEntrySupport(LocalPoint position, out double height)
        {
            foreach (var plan in Buildings.Plans)
            {
                var local = plan.ToLocal(position);
                if (local.Z > 0 || local.Z < -BuildingPlan.EntryRun || Math.Abs(local.X) > .81) continue;
                height = plan.GroundFloor + (plan.LandingGround - plan.GroundFloor) * (-local.Z / BuildingPlan.EntryRun);
                return true;
            }
            height = 0;
            return false;
        }

        private bool InReach(LocalPoint target)
        {
            var p = PlayerLocal;
            var dx = target.X - p.X;
            var dy = target.Y - p.Y - 1.63;
            var dz = target.Z - p.Z;
            return dx * dx + dy * dy + dz * dz <= 2.8 * 2.8;
        }

        private sealed class RegionInventoryAccess : IInventoryAccess
        {
            private readonly string anchor;

            public RegionInventoryAccess(string anchor) => this.anchor = anchor;

            public bool CanAccess(string id) => id == RegionInventory.PlayerId || anchor != null && id == anchor;
        }
    }
}
