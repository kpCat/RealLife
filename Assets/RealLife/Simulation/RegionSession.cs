using System;
using System.Collections.Generic;

namespace RealLife.Simulation
{
    internal static class SimulationTime
    {
        public const int TicksPerSecond = 20;
        public const int TicksPerMinute = TicksPerSecond * 4;
        private static long TotalMinutes(long tick) => 8 * 60 + tick / TicksPerMinute;
        public static int MinuteOfDay(long tick) => (int)(TotalMinutes(tick) % (24 * 60));
        public static long Day(long tick) => TotalMinutes(tick) / (24 * 60);
        public static long After(long tick, int duration) => Math.Min(tick, long.MaxValue - 1 - duration) + duration;
        public static long Advance(long tick) => After(tick, 1);
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
        public int BuildingGenerator, PopulationGenerator, InteriorFloor;
        public string InteriorId, InventoryFingerprint;
        public string[] OpenDoors;
        public InventoryDelta Inventory;
        public ResidentSnapshot[] Residents;
        public AcquaintanceSnapshot[] Acquaintances;
        public float Hunger, Thirst;
    }

    public enum DoorActionResult { Applied, UnknownTarget, OutOfReach, Obstructed, AlreadyInState }

    public sealed class RegionSession
    {
        public const int SnapshotVersion = 4;
        private const int InteriorSnapshotVersion = 3;
        private const int LegacySnapshotVersion = 2;
        private readonly RegionWorld world;
        private readonly IInventoryCapacityProvider capacity;
        private readonly Inventory inventory, legacyInventory;
        private readonly string inventoryFingerprint, legacyFingerprint;
        private readonly Population population;
        private readonly ResidentSocial social = new ResidentSocial();
        private GlobalPosition player;

        public RegionSession(RegionWorld world, GeoPoint spawn, float yaw, NeighborhoodCatalog catalog = null,
            IInventoryCapacityProvider capacity = null, bool enableResidents = true, int residentsPerHome = 3)
        {
            if (residentsPerHome < 1 || residentsPerHome > 4) throw new ArgumentOutOfRangeException(nameof(residentsPerHome));
            this.world = world;
            this.capacity = capacity;
            Buildings = new Buildings(world);
            legacyInventory = RegionInventory.Create(Buildings, catalog);
            legacyFingerprint = RegionInventory.Fingerprint(catalog);
            population = new Population(world, Buildings, enableResidents, residentsPerHome);
            inventory = RegionInventory.Create(Buildings, catalog, population.InventoryIds);
            inventoryFingerprint = RegionInventory.Fingerprint(catalog, includeProvisions: true);
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
        public long Day => SimulationTime.Day(Tick);
        public float Hunger { get; private set; }
        public float Thirst { get; private set; }
        public int PresentationRevision { get; private set; }
        public IReadOnlyList<ResidentViewData> Residents => population.Actors.Views;

        public void AdvanceTick(Func<string, LocalPoint, LocalPoint, bool> stepAllowed = null)
        {
            var previous = Tick;
            Tick = SimulationTime.Advance(Tick);
            if (Tick == previous) return;
            if (Day != SimulationTime.Day(previous))
            {
                RegionInventory.Restock(inventory, Day, capacity);
                Changed();
            }
            if (Tick % SimulationTime.TicksPerSecond == 0)
            {
                Hunger = Math.Min(1, Hunger + .00082f);
                Thirst = Math.Min(1, Thirst + .0011f);
            }
            population.Step(this, stepAllowed);
        }

        public bool SetPlayerPose(LocalPoint position, float yaw, float pitch)
        {
            if (!ValidPose(position, yaw, pitch)) return false;
            player = world.Frame.ToGlobal(position);
            Yaw = yaw;
            Pitch = pitch;
            return true;
        }

        public InventoryContainerView GetInventory(string id) => inventory.GetContainer(id, capacity);

        public bool CanUseAnchor(string id) => CanUseAnchor(PlayerActor(), id);

        public InventoryMoveResult MoveItems(InventoryMove command, string focusedAnchorId)
            => MoveItems(PlayerInventoryId, command, focusedAnchorId);

        public InventoryMoveResult MoveItems(string actorId, InventoryMove command, string focusedAnchorId)
        {
            if (!TryActor(actorId, out var actor)) return new InventoryMoveResult(InventoryMoveFailure.Inaccessible);
            var result = inventory.Move(command, Access(actor, focusedAnchorId), capacity);
            if (result.Succeeded) Changed();
            return result;
        }

        public InventoryMoveFailure CheckMove(InventoryMove command, string focusedAnchorId)
            => inventory.CheckMove(command, Access(PlayerActor(), focusedAnchorId), capacity);

        public InventoryUseResult UseItem(InventoryUse command, string focusedAnchorId = null)
        {
            if (command.ActionTick != Tick) return new InventoryUseResult(InventoryUseFailure.InvalidAction);
            if (!TryActor(command.ActorId, out var actor)) return new InventoryUseResult(InventoryUseFailure.Inaccessible);
            var result = inventory.Consume(command, Access(actor, focusedAnchorId));
            if (!result.Succeeded) return result;
            const float relief = .48f;
            if (actor.Person == null)
            {
                if (result.Kind == InventoryUseKind.Food) Hunger = Math.Max(0, Hunger - relief);
                else Thirst = Math.Max(0, Thirst - relief);
            }
            else
            {
                if (result.Kind == InventoryUseKind.Food) actor.Person.Hunger = Math.Max(0, actor.Person.Hunger - relief);
                else actor.Person.Thirst = Math.Max(0, actor.Person.Thirst - relief);
                if (actor.Person.Uses < int.MaxValue) actor.Person.Uses++;
            }
            Changed();
            return result;
        }

        private bool CanUseAnchor(ActorContext actor, string id)
        {
            if (!Buildings.TryGetAnchor(id, out var anchor)) return false;
            if (actor.Person != null &&
                (actor.RoomId != anchor.RoomId || anchor.BuildingId != actor.Person.HomeId &&
                    (!Buildings.TryGet(anchor.BuildingId, out var plan) || plan.Kind != BuildingKind.PublicPremises)))
                return false;
            return InReach(actor.Feet, anchor.Position) || InReach(actor.Feet, anchor.ContainerPosition);
        }

        private RegionInventoryAccess Access(ActorContext actor, string anchor) =>
            new RegionInventoryAccess(actor.Id, CanUseAnchor(actor, anchor) ? anchor : null);

        public DoorActionResult ToggleDoor(string id) => SetDoor(PlayerInventoryId, id, !Buildings.IsDoorOpen(id));

        public DoorActionResult SetDoor(string actorId, string id, bool open)
        {
            if (!Buildings.TryGetDoor(id, out var door) || !TryActor(actorId, out var actor))
                return DoorActionResult.UnknownTarget;
            var wasOpen = Buildings.IsDoorOpen(id);
            if (wasOpen == open) return DoorActionResult.AlreadyInState;
            if (!InReach(actor.Feet, door.GetAim(wasOpen))) return DoorActionResult.OutOfReach;
            if (door.ContainsSwing(PlayerLocal)) return DoorActionResult.Obstructed;
            foreach (var person in population.Actors.People)
                if (door.ContainsSwing(person.Position)) return DoorActionResult.Obstructed;
            if (!Buildings.TryToggleDoor(id, out _)) return DoorActionResult.UnknownTarget;
            Changed();
            return DoorActionResult.Applied;
        }

        public ConversationResult Talk(string residentId, ConversationTopic topic)
        {
            var person = population.Actors.Find(residentId);
            if (person == null || Population.DistanceSquared(person.Position, PlayerLocal) > 3.2 * 3.2 ||
                Math.Abs(person.Position.Y - PlayerLocal.Y) >= 1.15)
                return new ConversationResult(false, "Подойдите ближе, чтобы поговорить.");
            foreach (var view in Residents)
                if (view.Id == residentId) return social.Talk(person, view, topic, Tick);
            return default;
        }

        private void Changed() => PresentationRevision = unchecked(PresentationRevision + 1);

        private ActorContext PlayerActor() => new ActorContext(PlayerInventoryId, PlayerLocal, null, null);

        private bool TryActor(string id, out ActorContext actor)
        {
            if (id == PlayerInventoryId)
            {
                actor = PlayerActor();
                return true;
            }
            var person = population.Actors.Find(id);
            actor = person == null ? default : new ActorContext(person.Id, person.Position, person, population.RoomOf(person));
            return person != null;
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
                PopulationGenerator = Population.GeneratorVersion,
                InteriorId = interior?.Id, InteriorFloor = floor, OpenDoors = Buildings.CaptureOpenDoors(),
                InventoryFingerprint = inventoryFingerprint, Inventory = inventory.Capture(),
                Residents = population.Capture(), Acquaintances = social.Capture(), Hunger = Hunger, Thirst = Thirst
            };
        }

        public bool Matches(RegionSnapshot snapshot) => snapshot != null &&
            snapshot.WorldId == WorldId && snapshot.PackageId == world.PackageId &&
            snapshot.PackageFormat == RegionWorld.PackageFormat && snapshot.PackageRevision == world.Revision &&
            (snapshot.Version switch
            {
                LegacySnapshotVersion => true,
                InteriorSnapshotVersion => snapshot.BuildingGenerator == Buildings.GeneratorVersion &&
                    snapshot.InventoryFingerprint == legacyFingerprint,
                SnapshotVersion => snapshot.BuildingGenerator == Buildings.GeneratorVersion &&
                    snapshot.PopulationGenerator == Population.GeneratorVersion &&
                    snapshot.InventoryFingerprint == inventoryFingerprint,
                _ => false
            });

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

            if (!Buildings.ValidateOpenDoors(snapshot.OpenDoors) || !CanRestoreContents(snapshot) ||
                !Buildings.IsPoseClearOfDoors(position, snapshot.OpenDoors)) return false;
            if (string.IsNullOrEmpty(snapshot.InteriorId))
                return snapshot.InteriorFloor == 0 && ValidOutdoorPose(position, flat, legacy: false);
            return Buildings.TryGet(snapshot.InteriorId, out var plan) &&
                plan.TryLocate(position, true, out var floor) && floor == snapshot.InteriorFloor;
        }

        private bool CanRestoreContents(RegionSnapshot snapshot)
        {
            if (snapshot.Version == InteriorSnapshotVersion && !legacyInventory.CanRestore(snapshot.Inventory, 0, 0))
                return false;
            if (!inventory.CanRestore(snapshot.Inventory, snapshot.Tick, SimulationTime.Day(snapshot.Tick)))
                return false;
            if (snapshot.Version == InteriorSnapshotVersion) return true;

            return snapshot.Inventory.Uses != null && snapshot.Inventory.Deliveries != null &&
                WorldFrame.IsFinite(snapshot.Hunger) && snapshot.Hunger >= 0 && snapshot.Hunger <= 1 &&
                WorldFrame.IsFinite(snapshot.Thirst) && snapshot.Thirst >= 0 && snapshot.Thirst <= 1 &&
                ResidentSocial.CanRestore(snapshot.Acquaintances, population.Actors, snapshot.Tick) &&
                population.CanRestore(snapshot.Residents, Buildings, snapshot.OpenDoors, snapshot.Tick) &&
                ConsumptionMatches(snapshot.Residents, snapshot.Inventory.Uses);
        }

        private static bool ConsumptionMatches(ResidentSnapshot[] people, InventoryUseDelta[] ledger)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in ledger)
            {
                counts.TryGetValue(entry.ActorId, out var prior);
                counts[entry.ActorId] = prior + (int)Math.Min(int.MaxValue - prior, entry.Quantity);
            }
            foreach (var person in people)
            {
                counts.TryGetValue(person.Id, out var count);
                if (person.Uses != count) return false;
            }
            return true;
        }

        public bool Restore(RegionSnapshot snapshot)
        {
            if (!TryGetRestorePosition(snapshot, out var position)) return false;
            var oldOutdoor = snapshot.Version == LegacySnapshotVersion;
            Buildings.RestoreOpenDoors(oldOutdoor ? Array.Empty<string>() : snapshot.OpenDoors);
            inventory.Restore(oldOutdoor ? new InventoryDelta() : snapshot.Inventory, snapshot.Tick, SimulationTime.Day(snapshot.Tick));
            player = oldOutdoor && position.Y != world.Frame.ToLocal(snapshot.Player).Y
                ? world.Frame.ToGlobal(position) : snapshot.Player;
            Yaw = snapshot.Yaw;
            Pitch = snapshot.Pitch;
            Tick = snapshot.Tick;
            if (snapshot.Version == SnapshotVersion)
            {
                population.Restore(snapshot.Residents);
                social.Restore(snapshot.Acquaintances);
                (Hunger, Thirst) = (snapshot.Hunger, snapshot.Thirst);
            }
            else
            {
                population.Reset(Tick);
                social.Restore(null);
                (Hunger, Thirst) = (0, 0);
            }
            Changed();
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

        private static bool InReach(LocalPoint feet, LocalPoint target)
        {
            var dx = target.X - feet.X;
            var dy = target.Y - feet.Y - 1.63;
            var dz = target.Z - feet.Z;
            return dx * dx + dy * dy + dz * dz <= 2.8 * 2.8;
        }

        private readonly struct ActorContext
        {
            public readonly string Id, RoomId;
            public readonly LocalPoint Feet;
            public readonly Resident Person;

            public ActorContext(string id, LocalPoint feet, Resident person, string roomId) =>
                (Id, Feet, Person, RoomId) = (id, feet, person, roomId);
        }

        private sealed class RegionInventoryAccess : IInventoryAccess
        {
            private readonly string actor, anchor;

            public RegionInventoryAccess(string actor, string anchor) => (this.actor, this.anchor) = (actor, anchor);

            public bool CanAccess(string id) => id == actor || anchor != null && id == anchor;
        }
    }
}
