using System;
using System.Collections.Generic;

namespace RealLife.Simulation
{
    public enum BuildingKind { SingleStoreyHouse, TwoStoreyHouse, PublicPremises }
    public enum BuildingRoomRole { Kitchen, Living, Bedroom, Shop, Storeroom, Hall }
    public enum BuildingAnchorKind { Surface, Storage }
    public enum BuildingFurnitureKind { Counter, Table, Shelves, Bed, Chair }

    public sealed class BuildingRoom
    {
        public string Id { get; }
        public BuildingRoomRole Role { get; }
        public int Floor { get; }
        public double Left { get; }
        public double Right { get; }
        public double Front { get; }
        public double Back { get; }

        internal BuildingRoom(string id, BuildingRoomRole role, int floor, double left, double right, double front, double back)
        {
            (Id, Role, Floor) = (id, role, floor);
            (Left, Right, Front, Back) = (left, right, front, back);
        }
    }

    public sealed class BuildingDoor
    {
        public const double Width = 1.2, Height = 2.18;
        public const double LeafWidth = 1.145, LeafThickness = .09;
        private readonly BuildingPlan plan;
        public string Id { get; }
        public string BuildingId => plan.Id;
        public string RoomA { get; }
        public string RoomB { get; }
        public bool Exterior => RoomA == null;
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public bool AlongX { get; }
        public int Swing { get; }
        public double HingeX => X - (AlongX ? Width / 2 : 0);
        public double HingeZ => Z - (AlongX ? 0 : Width / 2);
        public LocalPoint Position => GetAim(false);

        internal BuildingDoor(BuildingPlan plan, string id, string roomA, string roomB, double x, double y, double z,
            bool alongX, int swing)
        {
            (this.plan, Id, RoomA, RoomB) = (plan, id, roomA, roomB);
            (X, Y, Z, AlongX, Swing) = (x, y, z, alongX, swing);
        }

        public bool ContainsSwing(LocalPoint feet)
        {
            var p = plan.ToLocal(feet);
            if (!OverlapsHeight(p.Y)) return false;
            var (along, across) = HingeCoordinates(p);
            return along >= -.27 && across >= -.27 && along * along + across * across <= (Width + .27) * (Width + .27);
        }

        public LocalPoint GetAim(bool isOpen)
        {
            const double handle = .99;
            if (!isOpen) return plan.ToWorld(HingeX + (AlongX ? handle : 0), Y + 1.08, HingeZ + (AlongX ? 0 : handle));
            return plan.ToWorld(HingeX + (AlongX ? 0 : Swing * handle), Y + 1.08, HingeZ - (AlongX ? Swing * handle : 0));
        }

        internal bool Obstructs(LocalPoint feet, bool isOpen)
        {
            var p = plan.ToLocal(feet);
            if (!OverlapsHeight(p.Y)) return false;
            var (along, across) = HingeCoordinates(p);
            if (isOpen) (along, across) = (across, along);
            return BuildingPlan.TouchesBox(along - Width / 2, across, LeafWidth, LeafThickness);
        }

        private bool OverlapsHeight(double feet) => feet + BuildingPlan.CapsuleHeight > Y + .04 && feet < Y + Height - .01;

        private (double Along, double Across) HingeCoordinates(LocalPoint p)
        {
            var x = p.X - HingeX;
            var z = p.Z - HingeZ;
            return AlongX ? (x, -Swing * z) : (z, Swing * x);
        }
    }

    public sealed class BuildingAnchor
    {
        public string Id { get; }
        public string BuildingId { get; }
        public string RoomId { get; }
        public BuildingRoomRole Role { get; }
        public BuildingAnchorKind Kind { get; }
        public int Capacity { get; }
        public LocalPoint Position { get; }
        public LocalPoint ContainerPosition { get; }

        internal BuildingAnchor(BuildingPlan plan, string id, BuildingRoom room, BuildingAnchorKind kind,
            int capacity, double x, double y, double z, double containerY)
        {
            (Id, BuildingId, RoomId, Role, Kind, Capacity) = (id, plan.Id, room.Id, room.Role, kind, capacity);
            Position = plan.ToWorld(x, y, z);
            ContainerPosition = plan.ToWorld(x, containerY, z);
        }
    }

    public sealed class BuildingFurniture
    {
        public BuildingFurnitureKind Kind { get; }
        public int Floor { get; }
        public double X { get; }
        public double Z { get; }
        public double Width { get; }
        public double Depth { get; }
        public double Height { get; }
        public BuildingAnchor Anchor { get; }

        internal BuildingFurniture(BuildingFurnitureKind kind, int floor, double x, double z, double width,
            double depth, double height, BuildingAnchor anchor)
        {
            (Kind, Floor, X, Z, Width, Depth, Height, Anchor) = (kind, floor, x, z, width, depth, height, anchor);
        }
    }

    public sealed class BuildingPartition
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double Length { get; }
        public bool AlongX { get; }
        public BuildingDoor Door { get; }

        internal BuildingPartition(double x, double y, double z, double length, bool alongX, BuildingDoor door)
        {
            (X, Y, Z, Length, AlongX, Door) = (x, y, z, length, alongX, door);
        }
    }

    public sealed class BuildingStairs
    {
        public string LowerRoomId { get; }
        public string UpperRoomId { get; }
        public const double HalfWidth = .78, OpeningHalfWidth = .91, Bottom = 1.7, OpeningFront = 2.25, Top = 7.1;
        public const double RampThickness = .20, GuardThickness = .075, GuardOffset = .05;
        internal BuildingStairs(string lowerRoomId, string upperRoomId) =>
            (LowerRoomId, UpperRoomId) = (lowerRoomId, upperRoomId);
    }

    public sealed class BuildingPlan
    {
        public const double StoreyHeight = 2.82, WallThickness = .16, EntryRun = 2.05;
        internal const double CapsuleHeight = 1.82 - .035, CapsuleRadius = .27 - .035;
        private readonly double originX, originZ, rightX, rightZ;
        private readonly List<BuildingRoom> rooms = new List<BuildingRoom>();
        private readonly List<BuildingDoor> doors = new List<BuildingDoor>();
        private readonly List<BuildingPartition> partitions = new List<BuildingPartition>();
        private readonly List<BuildingAnchor> anchors = new List<BuildingAnchor>();
        private readonly List<BuildingFurniture> furniture = new List<BuildingFurniture>();
        public string Id { get; }
        public string RegionId { get; }
        public CellKey Owner { get; }
        public BuildingKind Kind { get; }
        public int Style { get; }
        public double Width { get; }
        public double Depth { get; }
        public double GroundFloor { get; }
        public double FoundationBottom { get; }
        public double RoofHeight { get; }
        public int FloorCount => Stairs == null ? 1 : 2;
        public double LandingGround { get; }
        public BuildingStairs Stairs { get; internal set; }
        public IReadOnlyList<BuildingRoom> Rooms { get; }
        public IReadOnlyList<BuildingDoor> Doors { get; }
        public IReadOnlyList<BuildingPartition> Partitions { get; }
        public IReadOnlyList<BuildingAnchor> Anchors { get; }
        public IReadOnlyList<BuildingFurniture> Furniture { get; }
        public LocalPoint Approach => ToWorld(0, LandingGround - GroundFloor, -EntryRun - .30);
        public LocalPoint Entrance => ToWorld(0, 0, 0);
        public LocalPoint StairsBottom => ToWorld(0, 0, BuildingStairs.Bottom);
        public LocalPoint StairsTop => ToWorld(0, StoreyHeight, BuildingStairs.Top + .12);

        internal BuildingPlan(string regionId, string id, CellKey owner, BuildingKind kind, int style, double width,
            double depth, GeoPoint origin, double rightX, double rightZ, double groundFloor, double foundationBottom,
            double landingGround, double roofHeight)
        {
            (RegionId, Id, Owner, Kind, Style) = (regionId, id, owner, kind, style);
            (Width, Depth, originX, originZ, this.rightX, this.rightZ) = (width, depth, origin.X, origin.Z, rightX, rightZ);
            (GroundFloor, FoundationBottom, LandingGround, RoofHeight) = (groundFloor, foundationBottom, landingGround, roofHeight);
            Rooms = rooms.AsReadOnly();
            Doors = doors.AsReadOnly();
            Partitions = partitions.AsReadOnly();
            Anchors = anchors.AsReadOnly();
            Furniture = furniture.AsReadOnly();
        }

        public LocalPoint ToWorld(double x, double y, double z) =>
            new LocalPoint(originX + rightX * x - rightZ * z, GroundFloor + y, originZ + rightZ * x + rightX * z);

        public LocalPoint ToLocal(LocalPoint p)
        {
            var x = p.X - originX;
            var z = p.Z - originZ;
            return new LocalPoint(rightX * x + rightZ * z, p.Y - GroundFloor, -rightZ * x + rightX * z);
        }

        public double DistanceToFootprint(LocalPoint p)
        {
            var local = ToLocal(p);
            var x = Math.Max(0, Math.Abs(local.X) - Width / 2);
            var z = Math.Max(0, Math.Max(-local.Z, local.Z - Depth));
            return Math.Sqrt(x * x + z * z);
        }

        public bool TryLocate(LocalPoint worldPosition, bool strict, out int floorIndex)
        {
            floorIndex = 0;
            if (!worldPosition.IsFinite) return false;
            var p = ToLocal(worldPosition);
            if (p.Z < -.12)
            {
                if (strict || p.Z < -EntryRun - .12 || Math.Abs(p.X) > .81) return false;
                var surface = (LandingGround - GroundFloor) * Math.Clamp(-p.Z / EntryRun, 0, 1);
                return NearSupport(p.Y, surface, strict);
            }
            if (p.Z > Depth + .01 || Math.Abs(p.X) > Width / 2) return false;
            if (strict && !ClearOfWalls(p)) return false;
            if (Stairs != null)
            {
                var run = (p.Z - BuildingStairs.Bottom) / (BuildingStairs.Top - BuildingStairs.Bottom);
                var inOpening = Math.Abs(p.X) < BuildingStairs.OpeningHalfWidth &&
                    p.Z >= BuildingStairs.OpeningFront && p.Z < BuildingStairs.Top;
                if (!inOpening && NearSupport(p.Y, StoreyHeight, strict) &&
                    (!strict || ClearOfFurniture(p, 1) && ClearOfLandingGuard(p)))
                {
                    floorIndex = 1;
                    return true;
                }
                var stairClearance = strict ? BuildingStairs.GuardOffset - BuildingStairs.GuardThickness / 2 - CapsuleRadius : .17;
                if (Math.Abs(p.X) <= BuildingStairs.HalfWidth + stairClearance && run >= 0 && run < 1 &&
                    NearSupport(p.Y, run * StoreyHeight, strict))
                {
                    floorIndex = run < .5 ? 0 : 1;
                    return true;
                }
            }
            return NearSupport(p.Y, 0, strict) && (!strict || ClearOfFurniture(p, 0) && HasGroundHeadroom(p));
        }

        private bool ClearOfWalls(LocalPoint p)
        {
            const double margin = WallThickness / 2 + CapsuleRadius;
            if (Math.Abs(p.X) > Width / 2 - margin || p.Z > Depth - margin ||
                HitsWall(p.X, p.Z, Width, Doors[0], p.Y))
                return false;
            foreach (var wall in Partitions)
            {
                if (p.Y + CapsuleHeight <= wall.Y || p.Y >= wall.Y + StoreyHeight - .055) continue;
                var across = wall.AlongX ? p.Z - wall.Z : p.X - wall.X;
                var along = wall.AlongX ? p.X - wall.X : p.Z - wall.Z;
                var doorOffset = wall.Door == null ? 0 : wall.AlongX ? wall.Door.X - wall.X : wall.Door.Z - wall.Z;
                if (HitsWall(along, across, wall.Length, wall.Door, p.Y, doorOffset)) return false;
            }
            return true;
        }

        private static bool HitsWall(double along, double across, double length, BuildingDoor door, double feet, double offset = 0)
        {
            if (door == null || feet + CapsuleHeight > door.Y + BuildingDoor.Height)
                return TouchesBox(along, across, length, WallThickness);
            var half = length / 2;
            return HitsSpan(-half, offset - BuildingDoor.Width / 2) || HitsSpan(offset + BuildingDoor.Width / 2, half);

            bool HitsSpan(double start, double end) => TouchesBox(along - (start + end) / 2, across, end - start, WallThickness);
        }

        private bool ClearOfFurniture(LocalPoint p, int floor)
        {
            foreach (var piece in Furniture)
                if (piece.Floor == floor && TouchesBox(p.X - piece.X, p.Z - piece.Z, piece.Width, piece.Depth))
                    return false;
            return true;
        }

        private bool HasGroundHeadroom(LocalPoint p)
        {
            if (Stairs == null || Math.Abs(p.X) > BuildingStairs.HalfWidth + CapsuleRadius ||
                p.Z < BuildingStairs.Bottom - CapsuleRadius || p.Z > BuildingStairs.Top + CapsuleRadius)
                return true;
            var grade = StoreyHeight / (BuildingStairs.Top - BuildingStairs.Bottom);
            var rampBottom = (p.Z - CapsuleRadius - BuildingStairs.Bottom) * grade -
                BuildingStairs.RampThickness * Math.Sqrt(1 + grade * grade);
            return p.Y + CapsuleHeight <= rampBottom;
        }

        private static bool ClearOfLandingGuard(LocalPoint p)
        {
            var width = BuildingStairs.OpeningHalfWidth;
            var front = BuildingStairs.OpeningFront;
            var back = BuildingStairs.Top;
            return !TouchesBox(p.X, p.Z - front, width * 2, BuildingStairs.GuardThickness) &&
                !TouchesBox(Math.Abs(p.X) - width, p.Z - (front + back) / 2, BuildingStairs.GuardThickness, back - front);
        }

        internal static bool TouchesBox(double x, double z, double width, double depth)
        {
            var dx = Math.Max(0, Math.Abs(x) - width / 2);
            var dz = Math.Max(0, Math.Abs(z) - depth / 2);
            return dx * dx + dz * dz < CapsuleRadius * CapsuleRadius;
        }

        private static bool NearSupport(double feet, double surface, bool strict) =>
            feet >= surface - (strict ? .055 : .45) && feet <= surface + (strict ? .15 : .48);

        internal BuildingRoom Room(string key, BuildingRoomRole role, int floor, double left, double right,
            double front, double back)
        {
            var room = new BuildingRoom(Key(key), role, floor, left, right, front, back);
            rooms.Add(room);
            return room;
        }

        internal BuildingDoor Door(string key, BuildingRoom a, BuildingRoom b, double x, int floor, double z,
            bool alongX, int swing)
        {
            var door = new BuildingDoor(this, Key(key), a?.Id, b.Id, x, floor * StoreyHeight, z, alongX, swing);
            doors.Add(door);
            return door;
        }

        internal void Partition(double x, int floor, double z, double length, bool alongX, BuildingDoor door = null) =>
            partitions.Add(new BuildingPartition(x, floor * StoreyHeight, z, length, alongX, door));

        internal void Furnish(BuildingRoom room, string key, BuildingFurnitureKind kind, double x, double z, double width,
            double depth, double height)
        {
            BuildingAnchor anchor = null;
            if (key != null)
            {
                var storage = kind == BuildingFurnitureKind.Shelves;
                anchor = new BuildingAnchor(this, Key(key), room, storage ? BuildingAnchorKind.Storage : BuildingAnchorKind.Surface,
                    storage ? 4 : 1, x, room.Floor * StoreyHeight + (storage ? .96 : height + .09), z - .1,
                    room.Floor * StoreyHeight + (storage ? .61 : height - .028));
                anchors.Add(anchor);
            }
            furniture.Add(new BuildingFurniture(kind, room.Floor, x, z, width, depth, height, anchor));
        }

        private string Key(string local) => $"{RegionId}/building-v{Buildings.GeneratorVersion}/{Id}/{local}";
    }
}
