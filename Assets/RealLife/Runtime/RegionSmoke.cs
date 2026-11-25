using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using RealLife.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealLife.Runtime
{
    internal static class RegionSmoke
    {
        public static IEnumerator Run(WorldMenu menu, bool restart)
        {
            using var scenario = Scenario(menu, restart).GetEnumerator();
            while (true)
            {
                Exception failure = null;
                var next = false;
                try { next = scenario.MoveNext(); }
                catch (Exception error) { failure = error; }
                if (failure != null)
                {
                    Debug.LogException(failure);
                    Application.Quit(1);
                    yield break;
                }
                if (!next) break;
                yield return scenario.Current;
            }
            Debug.Log(restart ? "RealLife region restart smoke passed." : "RealLife region walking smoke passed.");
            Application.Quit(0);
        }

        private static IEnumerable<object> Scenario(WorldMenu menu, bool restart)
        {
            Require(Application.platform == RuntimePlatform.WindowsPlayer && Application.version == "0.1.0" &&
                GraphicsSettings.currentRenderPipeline != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null,
                "Windows 0.1.0 with active URP and graphics");
            var world = menu.Region;
            Require(world != null && menu.Globe != null && menu.Map == null, "offline data and globe loaded at startup");
            yield return null;
            yield return new WaitForEndOfFrame();
            menu.ChooseGlobe(-31, -160);
            Require(menu.Map == null && menu.Status.Contains("нет установленного пакета"), "uncovered globe location is labelled");
            var marker = menu.Globe.Marker();
            Require(marker.x >= 0, "installed region marker is visible");
            menu.Globe.Pick(marker);
            Require(menu.Map != null, "globe marker opens the published map");
            yield return null;
            yield return new WaitForEndOfFrame();

            var route = world.Route;
            var firstIndex = 0;
            while (firstIndex < route.Count - 1 && !world.IsSafeStart(route[firstIndex])) firstIndex++;
            var first = route[firstIndex];
            menu.Map.Pick(menu.Map.Project(first));
            Require(Math.Abs(menu.Map.Selection.X - first.X) < 0.05 && Math.Abs(menu.Map.Selection.Z - first.Z) < 0.05,
                "selecting the real public street on the map");
            Require(menu.Launch(restart), restart ? "continues through the world menu after process restart" : "starts the chosen region");
            var game = menu.Game;
            if (restart)
            {
                var snapshot = SaveSlot.Read<RegionSnapshot>(game.SavePath);
                var target = FirstPersonMotor.ToVector(world.Frame.ToLocal(snapshot.Player));
                Require(snapshot.Version == RegionSession.SnapshotVersion && SamePose(game, snapshot, target),
                    "restart restores the global pose, orientation and tick");
                yield break;
            }

            var origin = game.CurrentCell;
            var buildingKey = origin;
            string buildingId = null;
            foreach (var key in world.Nearby(origin, 1))
                if (world.TryChunk(key, out var cell) && cell.Buildings.Length != 0)
                {
                    buildingKey = key;
                    buildingId = cell.Buildings[0].Id;
                    break;
                }
            Require(buildingId != null && game.View.HasBuilding(buildingKey, buildingId), "initial footprint has a streamed world instance");
            var traversed = new HashSet<CellKey> { origin };
            var minLoaded = game.View.Count;
            var maxLoaded = minLoaded;
            var elapsedSteps = 0;
            var walked = 0f;
            var furthest = firstIndex;
            var start = game.Position;
            for (var i = firstIndex + 1; i < route.Count; i++)
            {
                foreach (var frame in Walk(route[i])) yield return frame;
                furthest = i;
                if (traversed.Count >= 5 && !game.View.Contains(origin) && !game.View.Contains(buildingKey) &&
                    Vector3.Distance(start, game.Position) > 630)
                    break;
            }
            Require(traversed.Count >= 5 && !game.View.Contains(origin) && !game.View.Contains(buildingKey),
                "walking across several cells unloads the original terrain and building");
            var away = game.Position;
            for (var i = 0; i < 12; i++) game.TickFrame(Vector2.zero, Vector2.zero, .05f);
            game.Save();
            var deadline = Time.realtimeSinceStartup + 10;
            while (game.IsSaving && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                game.TickFrame(Vector2.zero, Vector2.zero, .0001f);
            }
            Require(!game.IsSaving && File.Exists(game.SavePath), "region snapshot written to the isolated slot");
            var snapshotAway = SaveSlot.Read<RegionSnapshot>(game.SavePath);
            Require(snapshotAway.WorldId == "earth:" + world.PackageId && snapshotAway.PackageRevision == world.Revision,
                "snapshot is pinned to the real offline package");

            for (var i = furthest - 1; i >= firstIndex; i--)
                foreach (var frame in Walk(route[i])) yield return frame;
            Require(game.CurrentCell.Equals(origin) && game.View.Contains(origin) &&
                game.View.HasBuilding(buildingKey, buildingId), "returning on foot reloads the original cell and building ID");
            Require(game.Load() && SamePose(game, snapshotAway, away), "loading restores the distant pose and world time");
            Require(maxLoaded <= 49 && maxLoaded < 182, "loaded views stay within the local streaming budget");
            Debug.Log($"Region route: {walked:F0} m walked, {traversed.Count} visited cells, {minLoaded}-{maxLoaded} loaded cells.");

            IEnumerable<object> Walk(GeoPoint waypoint)
            {
                var target = new Vector2((float)waypoint.X, (float)waypoint.Z);
                var waits = 0;
                var lastDistance = float.PositiveInfinity;
                while (true)
                {
                    var position = game.Position;
                    var difference = target - new Vector2(position.x, position.z);
                    var distance = difference.magnitude;
                    if (distance <= .31f) break;
                    if (distance < lastDistance - .015f) waits = 0;
                    else if (++waits > 100)
                        throw new InvalidOperationException($"Walking stalled at {position} towards {target}, cell {game.CurrentCell}.");
                    lastDistance = distance;
                    var yaw = Mathf.Atan2(difference.x, difference.y) * Mathf.Rad2Deg;
                    var look = new Vector2(Mathf.DeltaAngle(game.Yaw, yaw) / FirstPersonMotor.MouseSensitivity, 0);
                    game.TickFrame(Vector2.up, look, .08f);
                    walked += Vector3.Distance(position, game.Position);
                    var feet = game.Position;
                    RequireGround(world, feet);
                    traversed.Add(game.CurrentCell);
                    minLoaded = Math.Min(minLoaded, game.View.Count);
                    maxLoaded = Math.Max(maxLoaded, game.View.Count);
                    if (++elapsedSteps % 16 == 0) yield return null;
                    if (elapsedSteps > 20000) throw new InvalidOperationException("Regional route exceeded its walking budget.");
                }
            }
        }

        private static bool SamePose(RegionGame game, RegionSnapshot snapshot, Vector3 target) =>
            Vector3.Distance(game.Position, target) < .09f && Mathf.Abs(Mathf.DeltaAngle(game.Yaw, snapshot.Yaw)) < .02f &&
            Mathf.Abs(game.Pitch - snapshot.Pitch) < .02f && game.Tick == snapshot.Tick;

        private static void RequireGround(RegionWorld world, Vector3 p)
        {
            if (!world.TryGround(new GeoPoint(p.x, p.z), out var ground) || p.y < ground - .15 || p.y > ground + .7)
                throw new InvalidOperationException($"Player left the physical relief at {p}, expected ground {ground:F2}.");
        }

        private static void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException($"Region smoke failed: {description}.");
            Debug.Log($"Region smoke: {description}.");
        }
    }
}
