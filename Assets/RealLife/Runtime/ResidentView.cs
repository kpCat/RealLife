using System;
using System.Collections.Generic;
using RealLife.Simulation;
using UnityEngine;

namespace RealLife.Runtime
{
    internal sealed class ResidentView : IDisposable
    {
        private const int MaximumBodies = 24;
        private const double EnterDistance = 105, ExitDistance = 130, CollisionDistance = 20;
        private const double PriorityDistance = 75;
        private readonly RegionWorld world;
        private readonly RegionView region;
        private readonly CharacterController player;
        private readonly Material surface;
        private readonly GameObject root = new GameObject("Residents");
        private readonly Dictionary<string, ResidentModel.Body> active =
            new Dictionary<string, ResidentModel.Body>(StringComparer.Ordinal);
        private readonly Dictionary<Collider, ResidentModel.Body> focus = new Dictionary<Collider, ResidentModel.Body>();
        private readonly Dictionary<int, ResidentModel> appearances = new Dictionary<int, ResidentModel>();
        private readonly Stack<ResidentModel.Body> pool = new Stack<ResidentModel.Body>();
        private readonly List<Candidate> enter = new List<Candidate>();
        private readonly RaycastHit[] obstacles = new RaycastHit[12];
        private bool focusNearby;

        private readonly struct Candidate : IComparable<Candidate>
        {
            public readonly ResidentViewData Resident;
            public readonly double Distance;

            public Candidate(ResidentViewData resident, double distance) => (Resident, Distance) = (resident, distance);

            public int CompareTo(Candidate other)
            {
                var byDistance = Distance.CompareTo(other.Distance);
                return byDistance != 0 ? byDistance : StringComparer.Ordinal.Compare(Resident.Id, other.Resident.Id);
            }
        }

        public ResidentView(RegionWorld world, RegionView region, CharacterController player, Material surface) =>
            (this.world, this.region, this.player, this.surface) = (world, region, player, surface);

        public int Count => active.Count;
        public bool Contains(string id) => id != null && active.ContainsKey(id);

        public void Present(IReadOnlyList<ResidentViewData> residents, LocalPoint playerPosition, float seconds)
        {
            enter.Clear();
            focusNearby = false;
            foreach (var resident in residents)
            {
                var distance = HorizontalDistanceSquared(resident.Position, playerPosition);
                var represented = active.TryGetValue(resident.Id, out var body);
                var limit = represented ? ExitDistance : EnterDistance;
                if (distance > limit * limit || !IsReady(resident))
                {
                    if (represented) Release(body);
                    continue;
                }
                if (represented)
                {
                    body.Update(resident, seconds);
                    focusNearby |= distance < 16;
                }
                else enter.Add(new Candidate(resident, distance));
            }

            enter.Sort();
            foreach (var candidate in enter)
            {
                if (active.Count == MaximumBodies &&
                    (candidate.Distance > PriorityDistance * PriorityDistance || !ReleaseDistant(playerPosition))) break;
                var resident = candidate.Resident;
                var appearance = ResidentModel.VariantOf(resident.AppearanceId);
                if (!appearances.TryGetValue(appearance, out var model))
                    appearances.Add(appearance, model = new ResidentModel(appearance, surface));
                var body = pool.Count > 0 ? pool.Pop() : new ResidentModel.Body(root.transform);
                body.Show(resident, model);
                active.Add(resident.Id, body);
                focus.Add(body.FocusCollider, body);
                focusNearby |= candidate.Distance < 16;
            }
            if (focusNearby) Physics.SyncTransforms();
        }

        public bool TryFocus(Ray ray, float distance, out ResidentViewData resident)
        {
            resident = default;
            if (!focusNearby ||
                !Physics.Raycast(ray, out var hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide) ||
                !focus.TryGetValue(hit.collider, out var body))
                return false;
            resident = body.Data;
            return true;
        }

        private bool IsReady(ResidentViewData resident)
        {
            if (!HasCell(resident.Position)) return false;
            if (resident.BuildingId == null) return true;
            return resident.RoomId == null ? region.IsApproachLoaded(resident.BuildingId) :
                region.IsInteriorLoaded(resident.BuildingId);
        }

        private bool HasCell(LocalPoint position) => region.Contains(world.CellAt(new GeoPoint(position.X, position.Z)));

        public bool NeedsCollision(LocalPoint from, LocalPoint to) =>
            HorizontalDistanceSquared(to, FirstPersonMotor.ToLocal(player.transform.position)) <=
            CollisionDistance * CollisionDistance && HasCell(from) && HasCell(to);

        public bool CanStep(string id, LocalPoint from, LocalPoint to, IReadOnlyList<ResidentViewData> residents)
        {
            if (ClosesGap(from, to, FirstPersonMotor.ToLocal(player.transform.position), player.radius + .20))
                return false;
            // Give stationary neighbours room to finish their activity. Opposing walkers can share a narrow passage.
            foreach (var other in residents)
                if (other.Id != id && !other.Walking && ClosesGap(from, to, other.Position, .34))
                    return false;

            var origin = FirstPersonMotor.ToVector(from);
            var displacement = FirstPersonMotor.ToVector(to) - origin;
            var length = displacement.magnitude;
            if (length < .0001f) return true;
            // Raise the lower sphere clear of the walkable slope and floor seams.
            var count = Physics.CapsuleCastNonAlloc(origin + Vector3.up * .36f, origin + Vector3.up * 1.54f,
                .18f, displacement / length, obstacles, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
                if (obstacles[i].collider != player && obstacles[i].normal.y < .65f &&
                    Vector3.Dot(obstacles[i].normal, displacement) < -.0001f)
                    return false;
            return true;
        }

        private static bool ClosesGap(LocalPoint from, LocalPoint to, LocalPoint obstacle, double clearance)
        {
            var after = HorizontalDistanceSquared(obstacle, to);
            return Math.Abs(obstacle.Y - to.Y) < 1.2 && after < clearance * clearance &&
                after + 1e-6 < HorizontalDistanceSquared(obstacle, from);
        }

        private static double HorizontalDistanceSquared(LocalPoint a, LocalPoint b) =>
            (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

        private void Release(ResidentModel.Body body)
        {
            active.Remove(body.Data.Id);
            focus.Remove(body.FocusCollider);
            body.Hide();
            pool.Push(body);
        }

        private bool ReleaseDistant(LocalPoint playerPosition)
        {
            var farthestDistance = EnterDistance * EnterDistance;
            ResidentModel.Body farthest = null;
            foreach (var body in active.Values)
            {
                var distance = HorizontalDistanceSquared(body.Data.Position, playerPosition);
                if (distance <= farthestDistance) continue;
                farthest = body;
                farthestDistance = distance;
            }
            if (farthest == null) return false;
            Release(farthest);
            return true;
        }

        public void Reset()
        {
            foreach (var body in active.Values)
            {
                body.Hide();
                pool.Push(body);
            }
            active.Clear();
            focus.Clear();
            focusNearby = false;
            Physics.SyncTransforms();
        }

        public void Dispose()
        {
            if (root != null)
            {
                root.SetActive(false);
                UnityEngine.Object.Destroy(root);
            }
            foreach (var model in appearances.Values) model.Dispose();
            appearances.Clear();
            active.Clear();
            focus.Clear();
            pool.Clear();
        }
    }
}
