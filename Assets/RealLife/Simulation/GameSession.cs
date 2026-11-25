using System;

namespace RealLife.Simulation
{
    public enum GameAction { ToggleDoor, TakeItem, ReturnItem }
    public enum ActionResult { Applied, UnknownTarget, OutOfReach, Unavailable, DoorObstructed }

    [Serializable]
    public sealed class SessionSnapshot
    {
        public int Version;
        public string DistrictId;
        public int DistrictRevision;
        public long Tick;
        public GlobalPosition Player;
        public float Yaw;
        public float Pitch;
        public string DoorId;
        public int DoorState;
        public string ItemId;
        public int ItemPlacement;
    }

    public sealed class GameSession
    {
        public const int SnapshotVersion = 1;
        public const int TicksPerSecond = SimulationTime.TicksPerSecond;
        public const float Reach = 2.6f;
        private const int Closed = 1;
        private const int Open = 2;
        private const int OnWorkbench = 1;
        private const int InInventory = 2;

        private readonly string districtId;
        private readonly int districtRevision;
        private readonly WorldFrame frame;
        private readonly Area bounds;
        private readonly LocalPoint doorHinge;
        private readonly LocalPoint doorCenter;
        private readonly float doorWidth;
        private readonly LocalPoint itemHome;
        private GlobalPosition player;
        private int doorState = Closed;
        private int itemPlacement = OnWorkbench;

        public GameSession(DistrictDefinition district)
        {
            district.Validate();
            districtId = district.Id;
            districtRevision = district.Revision;
            frame = new WorldFrame(district.Latitude, district.Longitude, district.Height);
            bounds = district.Bounds;
            doorHinge = district.Door.Hinge;
            doorWidth = district.Door.Width;
            doorCenter = new LocalPoint(doorHinge.X + doorWidth / 2, 1.1, doorHinge.Z);
            itemHome = district.Item.Home;
            DoorId = district.Door.Id;
            ItemId = district.Item.Id;
            if (!SetPlayerPose(district.Spawn, 0, 0))
                throw new ArgumentException("District spawn is outside the playable area.", nameof(district));
        }

        public string DoorId { get; }
        public string ItemId { get; }
        public LocalPoint PlayerLocal => frame.ToLocal(player);
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public long Tick { get; private set; }
        public bool IsDoorOpen => doorState == Open;
        public bool IsCarryingItem => itemPlacement == InInventory;
        public int MinuteOfDay => SimulationTime.MinuteOfDay(Tick);
        public void AdvanceTick()
        {
            Tick = SimulationTime.Advance(Tick);
        }

        public bool SetPlayerPose(LocalPoint position, float yaw, float pitch)
        {
            if (!ValidPose(position, yaw, pitch))
                return false;

            player = frame.ToGlobal(position);
            Yaw = yaw;
            Pitch = pitch;
            return true;
        }

        public ActionResult Apply(GameAction action, string targetId)
        {
            var position = PlayerLocal;
            switch (action)
            {
                case GameAction.ToggleDoor when targetId == DoorId:
                    if (!WithinReach(position, doorCenter)) return ActionResult.OutOfReach;
                    if (InsideDoorSwing(position)) return ActionResult.DoorObstructed;
                    doorState = IsDoorOpen ? Closed : Open;
                    return ActionResult.Applied;

                case GameAction.TakeItem when targetId == ItemId:
                case GameAction.ReturnItem when targetId == ItemId:
                    if (!WithinReach(position, itemHome)) return ActionResult.OutOfReach;
                    var taking = action == GameAction.TakeItem;
                    if (taking == IsCarryingItem) return ActionResult.Unavailable;
                    itemPlacement = taking ? InInventory : OnWorkbench;
                    return ActionResult.Applied;

                default:
                    return ActionResult.UnknownTarget;
            }
        }

        public SessionSnapshot Capture() => new SessionSnapshot
        {
            Version = SnapshotVersion,
            DistrictId = districtId,
            DistrictRevision = districtRevision,
            Tick = Tick,
            Player = player,
            Yaw = Yaw,
            Pitch = Pitch,
            DoorId = DoorId,
            DoorState = doorState,
            ItemId = ItemId,
            ItemPlacement = itemPlacement
        };

        public bool CanRestore(SessionSnapshot snapshot) => snapshot != null && snapshot.Version == SnapshotVersion &&
            snapshot.DistrictId == districtId && snapshot.DistrictRevision == districtRevision &&
            snapshot.Tick >= 0 && snapshot.Tick < long.MaxValue && snapshot.DoorId == DoorId &&
            (snapshot.DoorState == Closed || snapshot.DoorState == Open) && snapshot.ItemId == ItemId &&
            (snapshot.ItemPlacement == OnWorkbench || snapshot.ItemPlacement == InInventory) &&
            ValidPose(frame.ToLocal(snapshot.Player), snapshot.Yaw, snapshot.Pitch);

        public bool Restore(SessionSnapshot snapshot)
        {
            if (!CanRestore(snapshot)) return false;

            Tick = snapshot.Tick;
            player = snapshot.Player;
            Yaw = snapshot.Yaw;
            Pitch = snapshot.Pitch;
            doorState = snapshot.DoorState;
            itemPlacement = snapshot.ItemPlacement;
            return true;
        }

        private bool ValidPose(LocalPoint position, float yaw, float pitch) => position.IsFinite &&
            bounds.Contains(position) && position.Y >= -0.75 && position.Y <= 3 &&
            PlayerPose.ValidLook(yaw, pitch);

        private static bool WithinReach(LocalPoint feet, LocalPoint target)
        {
            var dx = feet.X - target.X;
            var dy = feet.Y + 1.45 - target.Y;
            var dz = feet.Z - target.Z;
            return dx * dx + dy * dy + dz * dz <= Reach * Reach;
        }

        private bool InsideDoorSwing(LocalPoint feet) =>
            feet.X > doorHinge.X - 0.35 && feet.X < doorHinge.X + doorWidth + 0.35 &&
            feet.Z > doorHinge.Z - 0.45 && feet.Z < doorHinge.Z + doorWidth + 0.35;
    }
}
