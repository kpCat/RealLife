using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RealLife.Simulation;
using UnityEngine;
using static RealLife.Runtime.RegionSmoke;

namespace RealLife.Runtime
{
    internal static class InteriorSmoke
    {
        public static IEnumerable<object> Scenario(WorldMenu menu, bool restart)
        {
            var world = menu.Region;
            Require(menu.AccessPlans.Select(plan => plan.Kind).Distinct().Count() == 3 &&
                menu.AccessPlans.Select(plan => plan.Owner).Distinct().Count() >= 3, "all three interior types occupy different cells");
            if (restart)
            {
                Require(menu.Launch(true), "the interior save resumes through the map in a fresh process");
                var live = menu.Game;
                var saved = SaveSlot.Read<RegionSnapshot>(live.SavePath);
                Require(saved.Version == RegionSession.SnapshotVersion && saved.InteriorFloor == 1 &&
                    live.View.IsInteriorLoaded(saved.InteriorId) &&
                    SamePose(live, saved, FirstPersonMotor.ToVector(world.Frame.ToLocal(saved.Player))),
                    "restart restores the upstairs pose with its interior collision loaded");
                Require(live.Buildings.TryGet(saved.InteriorId, out var restored), "restored building has a stable ID");
                VerifyChangedWorld(live, restored);
                yield break;
            }

            var plan = FindApproach(world, menu.AccessPlans, out var link, out var routeSegment);
            Require(plan != null, "a two-storey house has a clear approach from the street route");
            menu.Map.Pick(menu.Map.Project(link));
            Require(FlatDistance(menu.Map.Selection, link) < .05 && menu.Launch(false), "starts on the selected street from the map");
            var game = menu.Game;
            var entry = plan.Doors[0];
            var upperDoor = plan.Doors[3];
            var surface = FindAnchor(plan, "/surface.upper.left");
            var storage = FindAnchor(plan, "/storage.upper.left");
            var shelfLocal = plan.ToLocal(storage.Position);
            var roomX = plan.ToLocal(surface.Position).X;
            var upperZ = plan.Depth - 1.29;
            var upperY = BuildingPlan.StoreyHeight;
            var table = plan.Furniture.Single(piece => piece.Anchor == surface);
            // The open room door occupies the inner aisle. Pass the table on the exterior wall side.
            var aisle = (-plan.Width / 2 + BuildingPlan.WallThickness / 2 + roomX - table.Width / 2) * .5;
            var street = new LocalPoint(link.X, 0, link.Z);
            var route = world.Route;
            var walked = 0f;
            var stepped = 0;

            foreach (var frame in Walk(plan.Approach)) yield return frame;
            Require(game.View.IsInteriorLoaded(plan.Id), "the exterior approach prepares the interior");
            Aim(entry.Position, entry.Id);
            Require(game.Interact(InteractionControl.Primary) && game.Buildings.IsDoorOpen(entry.Id), "E opens the visible entrance");
            foreach (var frame in Walk(plan.StairsBottom)) yield return frame;
            foreach (var frame in Walk(plan.StairsTop)) yield return frame;
            foreach (var frame in Walk(plan.ToWorld(0, upperY, upperZ))) yield return frame;
            Require(Math.Abs(game.Position.y - (plan.GroundFloor + upperY)) < .18, "physical stairs lead to a supported upper floor");
            Aim(upperDoor.Position, upperDoor.Id);
            Require(game.Interact(InteractionControl.Primary) && game.Buildings.IsDoorOpen(upperDoor.Id), "opens the upstairs room door");
            foreach (var frame in Walk(plan.ToWorld(roomX, upperY, upperZ))) yield return frame;

            Aim(surface.Position, surface.Id);
            Require(game.Interact(InteractionControl.Primary) && game.GetInventory(surface.Id).Stacks.Count == 0,
                "takes the placed stack through the focused interaction");
            var playerId = game.PlayerInventoryId;
            var carried = game.GetInventory(playerId).Stacks.Single();
            Require(carried.Quantity > 1, "the player now carries the same stack of several items");

            foreach (var frame in Walk(plan.ToWorld(aisle, upperY, upperZ))) yield return frame;
            foreach (var frame in Walk(plan.ToWorld(aisle, upperY, shelfLocal.Z + .57))) yield return frame;
            foreach (var frame in Walk(plan.ToWorld(roomX, upperY, shelfLocal.Z + .57))) yield return frame;
            Aim(storage.ContainerPosition, storage.Id);
            Require(game.Interact(InteractionControl.Primary) && game.InventoryPanel.IsOpen, "E opens the actual container interface");
            var panel = game.InventoryPanel;
            yield return null;
            Require(panel.SelectStack(playerId, carried.InstanceId) && panel.SetQuantity(1) &&
                panel.Click(InventoryPanel.ActionKind.SplitPlayer) && game.GetInventory(playerId).Stacks.Count == 2,
                "the interface splits a selected quantity into a second stack");
            var split = game.GetInventory(playerId).Stacks.Single(stack => stack.InstanceId != carried.InstanceId);
            Require(panel.SelectStack(playerId, split.InstanceId) && panel.Click(InventoryPanel.ActionKind.ChoosePlayerMergeTarget) &&
                panel.SelectStack(playerId, carried.InstanceId) && panel.Click(InventoryPanel.ActionKind.MergePlayer) &&
                game.GetInventory(playerId).Stacks.Single().Quantity == carried.Quantity,
                "the interface merges the two compatible stacks");
            var before = game.GetInventory(storage.Id).Stacks.Sum(stack => stack.Quantity);
            Require(panel.SelectStack(playerId, carried.InstanceId) && panel.SetQuantity(1) &&
                panel.Click(InventoryPanel.ActionKind.TransferToOther) &&
                game.GetInventory(storage.Id).Stacks.Sum(stack => stack.Quantity) == before + 1 &&
                game.GetInventory(playerId).Stacks.Single().Quantity == carried.Quantity - 1,
                "the container transfer moves one item and preserves the remainder");
            Require(panel.Click(InventoryPanel.ActionKind.Close) && !panel.IsOpen, "the interface returns control to movement");

            var savePose = game.Position;
            game.Save();
            var deadline = Time.realtimeSinceStartup + 10;
            while (game.IsSaving && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                game.TickFrame(Vector2.zero, Vector2.zero, .0001f);
            }
            Require(!game.IsSaving && File.Exists(game.SavePath), "writes the upstairs snapshot to the isolated slot");
            var snapshot = SaveSlot.Read<RegionSnapshot>(game.SavePath);
            Require(snapshot.InteriorId == plan.Id && snapshot.InteriorFloor == 1, "the snapshot identifies the upstairs floor");
            foreach (var frame in LeaveRoom()) yield return frame;
            Require(Vector3.Distance(game.Position, savePose) > 1 && game.Load() && SamePose(game, snapshot, savePose),
                "F9 restores the upstairs position, view direction and time");
            VerifyChangedWorld(game, plan);

            foreach (var frame in LeaveRoom()) yield return frame;
            foreach (var frame in Walk(plan.ToWorld(0, upperY, upperZ))) yield return frame;
            foreach (var frame in Walk(plan.StairsBottom)) yield return frame;
            foreach (var frame in Walk(plan.Approach)) yield return frame;
            foreach (var frame in Walk(street)) yield return frame;

            var direction = FlatDistance(link, route[0]) > FlatDistance(link, route[route.Count - 1]) ? -1 : 1;
            var first = direction > 0 ? routeSegment : routeSegment - 1;
            var last = first;
            for (var index = first; index >= 0 && index < route.Count; index += direction)
            {
                foreach (var frame in Walk(GroundWaypoint(route[index]))) yield return frame;
                last = index;
                if (!game.View.Contains(plan.Owner) && !game.View.IsInteriorLoaded(plan.Id)) break;
            }
            Require(!game.View.Contains(plan.Owner) && !game.View.IsInteriorLoaded(plan.Id), "leaving on foot unloads the interior and its owner cell");
            for (var index = last - direction; direction * (index - first) >= 0; index -= direction)
                foreach (var frame in Walk(GroundWaypoint(route[index]))) yield return frame;
            foreach (var frame in Walk(street)) yield return frame;
            foreach (var frame in Walk(plan.Approach)) yield return frame;
            Require(game.View.Contains(plan.Owner) && game.View.IsInteriorLoaded(plan.Id), "returning on foot rebuilds the same interior");
            foreach (var frame in Walk(plan.StairsTop)) yield return frame;
            foreach (var frame in Walk(plan.ToWorld(0, upperY, upperZ))) yield return frame;
            foreach (var frame in Walk(plan.ToWorld(aisle, upperY, upperZ))) yield return frame;
            foreach (var frame in Walk(plan.ToWorld(aisle, upperY, shelfLocal.Z + .57))) yield return frame;
            foreach (var frame in Walk(plan.ToWorld(roomX, upperY, shelfLocal.Z + .57))) yield return frame;
            Aim(storage.ContainerPosition, storage.Id);
            Require(game.Interact(InteractionControl.Primary) && panel.IsOpen, "the rebuilt cupboard is still interactive");
            VerifyChangedWorld(game, plan);
            Debug.Log($"Interior route: {walked:F0} m walked, {menu.AccessPlans.Count} accessible buildings.");

            IEnumerable<object> LeaveRoom()
            {
                foreach (var frame in Walk(plan.ToWorld(aisle, upperY, shelfLocal.Z + .57))) yield return frame;
                foreach (var frame in Walk(plan.ToWorld(aisle, upperY, upperZ))) yield return frame;
            }

            IEnumerable<object> Walk(LocalPoint waypoint)
            {
                var target = new Vector2((float)waypoint.X, (float)waypoint.Z);
                var closest = float.PositiveInfinity;
                var stuck = 0;
                while (true)
                {
                    var position = game.Position;
                    var remaining = target - new Vector2(position.x, position.z);
                    var distance = remaining.magnitude;
                    if (distance <= .14f) yield break;
                    if (distance < closest - .012f) { closest = distance; stuck = 0; }
                    else if (++stuck > 80)
                        throw new InvalidOperationException($"Interior walk in {plan.Id} stalled at " +
                            $"{FirstPersonMotor.ToVector(plan.ToLocal(FirstPersonMotor.ToLocal(position)))}; " +
                            $"destination {FirstPersonMotor.ToVector(plan.ToLocal(waypoint))}, world {position}.");
                    var yaw = Mathf.Atan2(remaining.x, remaining.y) * Mathf.Rad2Deg;
                    var look = new Vector2(Mathf.DeltaAngle(game.Yaw, yaw), game.Pitch) / FirstPersonMotor.MouseSensitivity;
                    game.TickFrame(Vector2.up, look, Mathf.Min(.08f, distance / 3.3f));
                    walked += Vector3.Distance(position, game.Position);
                    if (++stepped % 16 == 0) yield return null;
                    if (stepped > 16000) throw new InvalidOperationException("Interior route exceeded its walking budget.");
                }
            }

            void Aim(LocalPoint point, string id)
            {
                var target = FirstPersonMotor.ToVector(point);
                var directionToTarget = target - (game.Position + Vector3.up * 1.63f);
                var yaw = Mathf.Atan2(directionToTarget.x, directionToTarget.z) * Mathf.Rad2Deg;
                var pitch = -Mathf.Atan2(directionToTarget.y,
                    new Vector2(directionToTarget.x, directionToTarget.z).magnitude) * Mathf.Rad2Deg;
                var look = new Vector2(Mathf.DeltaAngle(game.Yaw, yaw), game.Pitch - pitch) / FirstPersonMotor.MouseSensitivity;
                game.TickFrame(Vector2.zero, look, .0001f);
                if (!game.TryFocus(out var focus) || focus.Id != id)
                    throw new InvalidOperationException($"Focus from {game.Position} toward {target}: expected {id}, got {focus.Id} ({focus.Kind}).");
            }
        }

        private static void VerifyChangedWorld(RegionGame game, BuildingPlan plan)
        {
            var surface = FindAnchor(plan, "/surface.upper.left");
            var storage = FindAnchor(plan, "/storage.upper.left");
            var contents = game.GetInventory(storage.Id).Stacks;
            Require(game.GetInventory(surface.Id).Stacks.Count == 0 &&
                contents.Any(stack => stack.OriginId == surface.Id + "/item" && stack.Quantity == 1) &&
                game.GetInventory(game.PlayerInventoryId).Stacks.Any(stack => stack.InstanceId == surface.Id + "/item"),
                "the source stays empty and the transferred quantity keeps its origin in the cupboard");
            Require(game.Buildings.IsDoorOpen(plan.Doors[0].Id) && game.Buildings.IsDoorOpen(plan.Doors[3].Id),
                "both opened doors retain their state");
        }

        private static BuildingAnchor FindAnchor(BuildingPlan plan, string localId) =>
            plan.Anchors.Single(anchor => anchor.Id.EndsWith(localId, StringComparison.Ordinal));

        private static BuildingPlan FindApproach(RegionWorld world, IReadOnlyList<BuildingPlan> plans, out GeoPoint link, out int segment)
        {
            link = default;
            segment = 0;
            BuildingPlan closest = null;
            var bestDistance = 55.0;
            foreach (var plan in plans)
            {
                if (plan.Kind != BuildingKind.TwoStoreyHouse) continue;
                for (var index = 1; index < world.Route.Count; index++)
                {
                    var a = world.Route[index - 1];
                    var b = world.Route[index];
                    var dx = b.X - a.X;
                    var dz = b.Z - a.Z;
                    var length = dx * dx + dz * dz;
                    if (length < .01) continue;
                    var approach = new GeoPoint(plan.Approach.X, plan.Approach.Z);
                    var along = Math.Clamp(((approach.X - a.X) * dx + (approach.Z - a.Z) * dz) / length, 0, 1);
                    var street = new GeoPoint(a.X + along * dx, a.Z + along * dz);
                    var distance = FlatDistance(street, approach);
                    if (distance >= bestDistance || !WalkableLink(world, street, approach, distance)) continue;
                    closest = plan;
                    segment = index;
                    link = street;
                    bestDistance = distance;
                }
            }
            return closest;
        }

        private static bool WalkableLink(RegionWorld world, GeoPoint a, GeoPoint b, double distance)
        {
            var count = Math.Max(1, (int)Math.Ceiling(distance / .6));
            for (var index = 0; index <= count; index++)
            {
                var fraction = index / (double)count;
                if (!world.IsSafeStart(new GeoPoint(a.X + (b.X - a.X) * fraction, a.Z + (b.Z - a.Z) * fraction))) return false;
            }
            return true;
        }

        private static double FlatDistance(GeoPoint a, GeoPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
        private static LocalPoint GroundWaypoint(GeoPoint point) => new LocalPoint(point.X, 0, point.Z);
    }
}
