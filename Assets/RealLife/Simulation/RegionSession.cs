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
    }

    public sealed class RegionSession
    {
        public const int SnapshotVersion = 2;
        private readonly RegionWorld world;
        private GlobalPosition player;

        public RegionSession(RegionWorld world, GeoPoint spawn, float yaw)
        {
            this.world = world;
            if (!world.IsSafeStart(spawn) || !world.TryGround(spawn, out var ground) ||
                !SetPlayerPose(new LocalPoint(spawn.X, ground + 0.08, spawn.Z), yaw, 0))
                throw new ArgumentException("Invalid region spawn.");
        }

        public string WorldId => $"earth:{world.PackageId}";
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

        public RegionSnapshot Capture() => new RegionSnapshot
        {
            Version = SnapshotVersion,
            WorldId = WorldId,
            PackageId = world.PackageId,
            PackageFormat = RegionWorld.PackageFormat,
            PackageRevision = world.Revision,
            Player = player,
            Yaw = Yaw,
            Pitch = Pitch,
            Tick = Tick
        };

        public bool Matches(RegionSnapshot snapshot) => snapshot != null && snapshot.Version == SnapshotVersion &&
            snapshot.WorldId == WorldId && snapshot.PackageId == world.PackageId &&
            snapshot.PackageFormat == RegionWorld.PackageFormat && snapshot.PackageRevision == world.Revision;

        public bool CanRestore(RegionSnapshot snapshot)
        {
            if (!Matches(snapshot) || snapshot.Tick < 0 || snapshot.Tick == long.MaxValue || !snapshot.Player.IsFinite)
                return false;
            var position = world.Frame.ToLocal(snapshot.Player);
            return ValidPose(position, snapshot.Yaw, snapshot.Pitch) && world.IsOpen(new GeoPoint(position.X, position.Z), 0.05);
        }

        public bool Restore(RegionSnapshot snapshot)
        {
            if (!CanRestore(snapshot)) return false;
            player = snapshot.Player;
            Yaw = snapshot.Yaw;
            Pitch = snapshot.Pitch;
            Tick = snapshot.Tick;
            return true;
        }

        private bool ValidPose(LocalPoint p, float yaw, float pitch)
        {
            if (!p.IsFinite || !PlayerPose.ValidLook(yaw, pitch)) return false;
            var flat = new GeoPoint(p.X, p.Z);
            return world.DistanceToOutline(flat) >= .4 &&
                world.TryGround(flat, out var ground) && world.TryChunk(world.CellAt(flat), out _) &&
                p.Y >= ground - .4 && p.Y <= ground + 1.5;
        }
    }
}
