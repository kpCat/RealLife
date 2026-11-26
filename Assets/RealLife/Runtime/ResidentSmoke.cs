using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RealLife.Simulation;
using UnityEngine;
using static RealLife.Runtime.RegionSmoke;

namespace RealLife.Runtime
{
    internal static class ResidentSmoke
    {
        public static IEnumerable<object> Scenario(WorldMenu menu, bool restart)
        {
            if (restart)
            {
                Require(menu.Launch(true), "continues the populated region through the menu in a new process");
                var resumed = menu.Game;
                var expected = SaveSlot.Read<RegionSnapshot>(resumed.SavePath);
                var restored = resumed.CaptureState();
                Require(SamePose(resumed, expected, FirstPersonMotor.ToVector(menu.Region.Frame.ToLocal(expected.Player))),
                    "player pose and common clock survive process restart");
                Require(expected.Version == RegionSession.SnapshotVersion && expected.Residents.Length >= 20 &&
                    expected.Residents.Length == restored.Residents.Length,
                    "the populated slot restores its original roster size");
                Require(expected.Inventory.Uses.Length > 0 &&
                    JsonUtility.ToJson(expected.Inventory) == JsonUtility.ToJson(restored.Inventory),
                    "stocks and confirmed item use restore without duplication");
                Require(expected.Acquaintances.Length == 1 && restored.Acquaintances.Length == 1 &&
                    JsonUtility.ToJson(expected.Acquaintances[0]) == JsonUtility.ToJson(restored.Acquaintances[0]),
                    "the greeting and relationship restore with the original person");
                for (var i = 0; i < expected.Residents.Length; i++)
                {
                    var original = expected.Residents[i];
                    var actual = restored.Residents[i];
                    var precise = menu.Region.Frame.ToLocal(original.Position);
                    var rebuilt = menu.Region.Frame.ToLocal(actual.Position);
                    if (Vector3.Distance(FirstPersonMotor.ToVector(precise), FirstPersonMotor.ToVector(rebuilt)) > .001f)
                        throw new InvalidOperationException($"Restart shifted {original.Id} away from the saved route position.");
                    // The saved ECEF pose is redundant with the checked route. Compare it spatially before
                    // comparing the discrete state and the exact saved progression.
                    original.Position = actual.Position;
                    if (JsonUtility.ToJson(original) != JsonUtility.ToJson(actual))
                        throw new InvalidOperationException($"Restart changed the activity or needs of {original.Id}.");
                }
                Debug.Log("Region smoke: individual routes, positions and needs restore without duplication.");
                Debug.Log($"Residents route: restored {restored.Residents.Length} identities and " +
                    $"{restored.Inventory.Uses.Length} inventory-use records at tick {restored.Tick}.");
                yield break;
            }

            var world = menu.Region;
            var navigator = new ResidentNavigation(world, new Buildings(world));
            var streets = NearbyStreets(menu.AccessPlans, navigator);
            var street = streets.Where(node => world.IsSafeStart(Flat(navigator.GetNode(node).Position)))
                .OrderByDescending(node => streets.Count(other =>
                    FlatDistance(Flat(navigator.GetNode(node).Position), Flat(navigator.GetNode(other).Position)) < 48))
                .ThenBy(node => FlatDistance(Flat(navigator.GetNode(node).Position), world.Route[world.Route.Count / 2]))
                .DefaultIfEmpty(-1).First();
            Require(street >= 0, "prepared houses have reachable streets");
            var spawn = Flat(navigator.GetNode(street).Position);
            menu.Map.Pick(menu.Map.Project(spawn));
            Require(FlatDistance(menu.Map.Selection, spawn) < .08 && menu.Launch(false),
                "selects a populated street on the normal region map");
            var game = menu.Game;
            var initial = game.Residents.ToDictionary(person => person.Id);
            Require(initial.Count is >= 20 and <= 40 && game.MaterializedResidentCount > 0,
                "normal start has an individual population and streamed bodies");

            foreach (var frame in MeasureFrames(game, initial.Count)) yield return frame;

            string greeted = null;
            string approachId = null;
            var sawWalking = false;
            var walkStart = game.Position;
            for (var i = 0; i < 90; i++)
            {
                game.TickFrame(Vector2.zero, Vector2.zero, .08f);
                sawWalking |= HasWalkers(game, initial);
                if (i % 16 == 0) yield return null;
            }
            for (var i = 0; i < 650 && greeted == null; i++)
            {
                var feet = game.Position;
                var candidate = game.Residents.Where(person => person.BuildingId == null &&
                        game.IsResidentMaterialized(person.Id) &&
                        FlatDistance(Flat(feet), Flat(person.Position)) <= 52 &&
                        (approachId == null ? FlatDistance(Flat(feet), Flat(person.Position)) >= 4 : person.Id == approachId) &&
                        ClearGround(world, Flat(feet), Flat(person.Position)))
                    .OrderBy(person => FlatDistance(Flat(feet), Flat(person.Position))).FirstOrDefault();
                sawWalking |= HasWalkers(game, initial);
                if (candidate.Id == null)
                {
                    approachId = null;
                    game.TickFrame(Vector2.zero, Vector2.zero, .08f);
                }
                else
                {
                    approachId = candidate.Id;
                    var target = FirstPersonMotor.ToVector(candidate.Position);
                    var distance = FlatDistance(Flat(feet), Flat(candidate.Position));
                    game.TickFrame(distance > 2 ? Vector2.up : Vector2.zero, LookAt(game, target + Vector3.up * 1.40f),
                        Mathf.Min(.08f, Mathf.Max(.001f, (float)(distance - 1.8) / 3.3f)));
                    if (distance <= 2.55 && game.TryFocusResident(out var found) &&
                        game.Interact(InteractionControl.Primary) && game.ConversationOpen)
                    {
                        if (game.LastConversation.Applied)
                        {
                            Require(game.LastConversation.Encounters == 1 && game.AskResident(ConversationTopic.Name) &&
                                game.LastConversation.Text.Contains(found.Name) &&
                                game.AskResident(ConversationTopic.Activity) && game.AskResident(ConversationTopic.Wellbeing),
                                "a walking approach, crosshair and E open a dialogue with contextual replies");
                            greeted = found.Id;
                            Debug.Log($"Residents route: spoke to {found.Name} ({found.Id}) during {found.Activity}.");
                        }
                        game.CloseInteraction();
                    }
                }
                if (i % 16 == 0) yield return null;
            }
            Require(greeted != null && sawWalking && Vector3.Distance(walkStart, game.Position) > 1.5f,
                "moving bodies and a successful meeting are observable on a walking approach");

            Require(navigator.TryFindOutdoor(Flat(game.Position), 16, street, out var origin),
                "player is linked to the same traversable street component");
            var home = game.Residents.Single(person => person.Id == greeted).HomeId;
            var homePlan = menu.AccessPlans.Single(plan => plan.Id == home);
            var watching = PorchRoute(navigator, origin, homePlan);
            if (watching != null)
            {
                foreach (var frame in WalkPath(game, navigator, watching)) yield return frame;
                var side = new[] { -1.7, 1.7 }.Select(x => homePlan.ToWorld(x, 0, -BuildingPlan.EntryRun - 1.1))
                    .FirstOrDefault(point => ClearGround(world, Flat(game.Position), Flat(point)));
                if (side.IsFinite && (side.X != 0 || side.Z != 0))
                    foreach (var frame in WalkTo(game, FirstPersonMotor.ToVector(side), null)) yield return frame;
            }

            ResidentViewData arrived = default;
            for (var i = 0; i < 2600; i++)
            {
                game.TickFrame(Vector2.zero, Vector2.zero, .08f);
                foreach (var person in game.Residents)
                {
                    if (!game.IsResidentMaterialized(person.Id) || person.RoomId == null ||
                        person.RoomId == initial[person.Id].RoomId || person.Traveling ||
                        FlatDistance(Flat(game.Position), Flat(person.Position)) > 19.5 ||
                        FlatDistance(Flat(person.Position), Flat(initial[person.Id].Position)) < 4) continue;
                    arrived = person;
                    break;
                }
                var used = game.Residents.Sum(person => person.InventoryUses);
                if (arrived.Id != null && used > 0) break;
                if (i % 16 == 0) yield return null;
            }
            Require(arrived.Id != null && game.Residents.Sum(person => person.InventoryUses) > 0 &&
                game.CaptureState().OpenDoors.Length > 0,
                "near residents walk through real doors into a room and the residents use shared inventory provisions");
            Debug.Log($"Residents route: {arrived.Id} reached {arrived.RoomId}, " +
                $"{game.Residents.Sum(person => person.InventoryUses)} successful uses.");

            Require(navigator.TryFindOutdoor(Flat(game.Position), 18, origin, out origin),
                "the player can leave the house lookout through a road link");
            var tracked = game.Residents.Single(person => person.Id == greeted);
            if (!game.IsResidentMaterialized(greeted))
                foreach (var frame in ApproachCurrent(game, navigator, menu, origin, tracked)) yield return frame;
            Require(game.IsResidentMaterialized(greeted), "the known identity is represented before departing");
            Require(navigator.TryFindOutdoor(Flat(game.Position), 25, origin, out origin), "links departure to the outdoor graph");
            var vacation = FindDeparture(world, game, navigator, origin, greeted);
            Require(vacation != null, "finds a walkable trip outside the represented neighbourhood");
            var lastChecked = game.Residents.Single(person => person.Id == greeted).Position;
            var lastTick = game.Tick;
            foreach (var frame in WalkPath(game, navigator, vacation, afterStep: CheckContinuity)) yield return frame;

            var away = game.Residents.Single(person => person.Id == greeted);
            Require(!game.IsResidentMaterialized(greeted) &&
                FlatDistance(Flat(game.Position), Flat(away.Position)) > 130,
                "walking away removes the known resident's 3D body");
            var awayTick = game.Tick;
            ResidentViewData progressed = away;
            var activityAdvanced = false;
            for (var i = 0; i < 2100; i++)
            {
                game.TickFrame(Vector2.zero, Vector2.zero, .08f);
                CheckContinuity();
                progressed = game.Residents.Single(person => person.Id == greeted);
                activityAdvanced |= progressed.CompletedActivities > away.CompletedActivities ||
                    progressed.Intent != away.Intent || FlatDistance(Flat(progressed.Position), Flat(away.Position)) > .8;
                if (game.Tick - awayTick >= 400 && activityAdvanced) break;
                if (i % 20 == 0) yield return null;
            }
            var progressedTick = game.Tick;
            Require(game.Tick - awayTick >= 400 && activityAdvanced && !game.IsResidentMaterialized(greeted) &&
                (Math.Abs(progressed.Hunger - away.Hunger) > .006f || Math.Abs(progressed.Thirst - away.Thirst) > .006f) &&
                progressed.Name == tracked.Name && progressed.InventoryUses >= away.InventoryUses,
                "the same far identity updates needs and progresses its activity without a body");

            Array.Reverse(vacation);
            foreach (var frame in WalkPath(game, navigator, vacation, afterStep: CheckContinuity)) yield return frame;
            var returnee = game.Residents.Single(person => person.Id == greeted);
            if (!game.IsResidentMaterialized(greeted))
                foreach (var frame in ApproachCurrent(game, navigator, menu, origin, returnee)) yield return frame;
            var rematerialized = game.Residents.Single(person => person.Id == greeted);
            Require(game.IsResidentMaterialized(greeted) && rematerialized.Name == tracked.Name &&
                rematerialized.InventoryUses >= progressed.InventoryUses &&
                game.CaptureState().Acquaintances.Single().ResidentId == greeted,
                "returning on foot reuses the known identity and its advanced state");
            Debug.Log($"Residents route: {greeted} streamed out and back, far hunger " +
                $"{away.Hunger:F3}->{progressed.Hunger:F3}, tick {awayTick}->{progressedTick}.");

            game.Save();
            var deadline = Time.realtimeSinceStartup + 10;
            while (game.IsSaving && Time.realtimeSinceStartup < deadline)
            {
                game.TickFrame(Vector2.zero, Vector2.zero, .0001f);
                yield return null;
            }
            Require(!game.IsSaving && File.Exists(game.SavePath), "populated regional snapshot written to the isolated slot");
            var snapshot = SaveSlot.Read<RegionSnapshot>(game.SavePath);
            Require(snapshot.Residents.Length == initial.Count && snapshot.Acquaintances.Single().ResidentId == greeted,
                "saved snapshot contains the same identities and meeting");

            void CheckContinuity()
            {
                var point = game.Residents.Single(person => person.Id == greeted).Position;
                if (FlatDistance(Flat(point), Flat(lastChecked)) > (game.Tick - lastTick) * .08 + .01)
                    throw new InvalidOperationException("Known resident jumped ahead of its route during a view transition.");
                lastChecked = point;
                lastTick = game.Tick;
            }
        }

        private static bool HasWalkers(RegionGame game, IReadOnlyDictionary<string, ResidentViewData> initial) =>
            game.Residents.Any(person => person.Walking && game.IsResidentMaterialized(person.Id) &&
                FlatDistance(Flat(person.Position), Flat(initial[person.Id].Position)) > .8);

        private static IEnumerable<object> MeasureFrames(RegionGame game, int population)
        {
            // A hidden automation window still renders the active URP camera into this target.
            var target = new RenderTexture(Screen.width, Screen.height, 24);
            var camera = game.RegionalCamera;
            var cpu = new List<double>();
            var gpu = new List<double>();
            var timing = new FrameTiming[1];
            var maxBodies = game.MaterializedResidentCount;
            var endOfFrame = new WaitForEndOfFrame();
            camera.targetTexture = target;
            try
            {
                for (var i = 0; i < 105; i++)
                {
                    game.TickFrame(Vector2.zero, Vector2.zero, 1f / 60);
                    maxBodies = Math.Max(maxBodies, game.MaterializedResidentCount);
                    FrameTimingManager.CaptureFrameTimings();
                    yield return endOfFrame;
                    if (i < 25 || FrameTimingManager.GetLatestTimings(1, timing) == 0) continue;
                    if (timing[0].cpuMainThreadFrameTime > 0) cpu.Add(timing[0].cpuMainThreadFrameTime);
                    if (timing[0].gpuFrameTime > 0) gpu.Add(timing[0].gpuFrameTime);
                }
                Debug.Log($"Residents route: {population} individual residents, up to {maxBodies} bodies at start, " +
                    $"URP camera {target.width}x{target.height} on {SystemInfo.graphicsDeviceName}, " +
                    $"CPU main {Percentiles(cpu)}, GPU {Percentiles(gpu)}.");
            }
            finally
            {
                camera.targetTexture = null;
                target.Release();
                UnityEngine.Object.Destroy(target);
            }

            static string Percentiles(List<double> times)
            {
                if (times.Count == 0) return "unavailable";
                times.Sort();
                return $"{times[times.Count / 2]:F2}/{times[(int)(times.Count * .95)]:F2} ms p50/p95 ({times.Count} frames)";
            }
        }

        private static IEnumerable<object> ApproachCurrent(RegionGame game, ResidentNavigation navigator,
            WorldMenu menu, int origin, ResidentViewData resident)
        {
            if (!navigator.TryFindOutdoor(Flat(game.Position), 25, origin, out var from)) yield break;
            int[] route;
            if (resident.BuildingId == null)
            {
                if (!navigator.TryFindOutdoor(Flat(resident.Position), 25, from, out var target) ||
                    !navigator.TryRoute(from, target, out route)) yield break;
            }
            else
            {
                var plan = menu.AccessPlans.SingleOrDefault(value => value.Id == resident.BuildingId);
                if (plan == null || (route = PorchRoute(navigator, from, plan)) == null) yield break;
            }
            foreach (var frame in WalkPath(game, navigator, route, stopShort: true)) yield return frame;
        }

        private static int[] PorchRoute(ResidentNavigation navigation, int from, BuildingPlan house)
        {
            if (!navigation.TryRest(house.Id, 0, out var rest) && !navigation.TryWork(house.Id, 0, out rest))
                return null;
            if (!navigation.TryRoute(from, rest, out var route)) return null;
            return route.TakeWhile(node => navigation.GetNode(node).BuildingId == null).ToArray();
        }

        private static int[] NearbyStreets(IReadOnlyList<BuildingPlan> plans, ResidentNavigation navigation)
        {
            var nodes = new HashSet<int>();
            foreach (var plan in plans)
            {
                if (plan.Kind == BuildingKind.PublicPremises || !navigation.TryRest(plan.Id, 0, out _)) continue;
                for (var slot = 0; slot < 3; slot++)
                    if (navigation.TryOutside(plan.Id, slot, out var node)) nodes.Add(node);
            }
            return nodes.OrderBy(node => node).ToArray();
        }

        private static int[] FindDeparture(RegionWorld world, RegionGame game, ResidentNavigation navigation, int origin,
            string greeted)
        {
            var actor = game.Residents.Single(person => person.Id == greeted);
            var start = Flat(navigation.GetNode(origin).Position);
            foreach (var desired in new[] { 250, 380, 580 })
                foreach (var waypoint in world.Route.OrderBy(point => Math.Abs(FlatDistance(point, start) - desired)))
                    if (FlatDistance(waypoint, Flat(actor.Position)) > 220 &&
                        navigation.TryFindOutdoor(waypoint, 25, origin, out var end) &&
                        navigation.TryRoute(origin, end, out var path) &&
                        path.All(node => navigation.GetNode(node).BuildingId == null))
                        return path;
            return null;
        }

        private static IEnumerable<object> WalkPath(RegionGame game, ResidentNavigation navigation, int[] path,
            bool stopShort = false, Action afterStep = null)
        {
            var count = stopShort && path.Length > 2 ? path.Length - 1 : path.Length;
            for (var i = 0; i < count; i++)
            {
                var target = FirstPersonMotor.ToVector(navigation.GetNode(path[i]).Position);
                foreach (var frame in WalkTo(game, target, afterStep)) yield return frame;
            }
        }

        private static IEnumerable<object> WalkTo(RegionGame game, Vector3 target, Action afterStep)
        {
            var stalled = 0;
            var previous = double.PositiveInfinity;
            for (var step = 0; ; step++)
            {
                var distance = FlatDistance(Flat(game.Position), Flat(target));
                if (distance <= .22) break;
                stalled = distance < previous - .008 ? 0 : stalled + 1;
                if (stalled > 55 || step > 2000)
                    throw new InvalidOperationException($"Resident smoke walking stalled at {game.Position} toward {target}.");
                previous = distance;
                game.TickFrame(Vector2.up, LookAt(game, target + Vector3.up * 1.63f),
                    Mathf.Min(.08f, (float)distance / 3.3f));
                afterStep?.Invoke();
                if (step % 16 == 15) yield return null;
            }
        }

        private static Vector2 LookAt(RegionGame game, Vector3 target)
        {
            var difference = target - (game.Position + Vector3.up * 1.63f);
            var yaw = Mathf.Atan2(difference.x, difference.z) * Mathf.Rad2Deg;
            var pitch = -Mathf.Atan2(difference.y, new Vector2(difference.x, difference.z).magnitude) * Mathf.Rad2Deg;
            return new Vector2(Mathf.DeltaAngle(game.Yaw, yaw), game.Pitch - pitch) / FirstPersonMotor.MouseSensitivity;
        }

        private static bool ClearGround(RegionWorld world, GeoPoint from, GeoPoint to)
        {
            var count = Math.Max(1, (int)Math.Ceiling(FlatDistance(from, to)));
            for (var i = 1; i <= count; i++)
                if (!world.IsSafeStart(new GeoPoint(from.X + (to.X - from.X) * i / count, from.Z + (to.Z - from.Z) * i / count)))
                    return false;
            return true;
        }

        private static GeoPoint Flat(LocalPoint point) => new GeoPoint(point.X, point.Z);
        private static GeoPoint Flat(Vector3 point) => new GeoPoint(point.x, point.z);
        private static double FlatDistance(GeoPoint a, GeoPoint b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
    }
}
