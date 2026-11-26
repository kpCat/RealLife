using System;
using System.Collections.Generic;
using RealLife.Simulation;
using UnityEngine;

namespace RealLife.Runtime
{
    internal sealed class RegionView : IDisposable
    {
        private const int PrefetchRadius = 2;
        private const int RetainRadius = 3;
        private const double InteriorLoadDistance = 21;
        private const double InteriorRetainDistance = 30;
        private readonly RegionWorld world;
        private readonly Material[] palette;
        private readonly Buildings buildings;
        private readonly Func<string, string> displayedItem;
        private readonly GameObject root = new GameObject("Regional cells");
        private readonly Dictionary<CellKey, CellView> loaded = new Dictionary<CellKey, CellView>();
        private readonly Dictionary<string, BuildingView> interiors = new Dictionary<string, BuildingView>(StringComparer.Ordinal);
        private readonly Dictionary<Collider, RegionChunk> shellCollision = new Dictionary<Collider, RegionChunk>();
        private readonly List<CellKey> discard = new List<CellKey>();
        private int preparedFrame = -1;

        internal enum Tone { Grass, Soil, Asphalt, Paving, Plaster, Brick, Trim, Glass, Metal, Stone, Leaves, Wood }

        public RegionView(RegionWorld world, Material[] palette, Buildings buildings, Func<string, string> displayedItem = null)
        {
            this.world = world;
            this.palette = palette;
            this.buildings = buildings;
            this.displayedItem = displayedItem;
        }

        public int Count => loaded.Count;
        public bool Contains(CellKey key) => loaded.ContainsKey(key);
        public bool HasBuilding(CellKey key, string id) =>
            loaded.TryGetValue(key, out var chunk) && chunk.BuildingIds.Contains(id);

        public bool IsInteriorLoaded(string buildingId) => interiors.ContainsKey(buildingId);

        public void Prepare(LocalPoint position)
        {
            var center = world.CellAt(new GeoPoint(position.X, position.Z));
            Prime(center);
            UpdateInteriors(position);
            Trim(center);
            Physics.SyncTransforms();
        }

        public void Step(CellKey center, LocalPoint position)
        {
            if (UpdateInteriors(position)) Physics.SyncTransforms();
            Step(center);
        }

        public void RefreshInteractions()
        {
            foreach (var interior in interiors.Values) interior.Refresh(buildings, displayedItem);
            Physics.SyncTransforms();
        }

        public bool TryFocus(Ray ray, float maxDistance, out BuildingFocus focus)
        {
            focus = default;
            if (!Physics.Raycast(ray, out var hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return false;
            foreach (var interior in interiors.Values)
                if (interior.TryFocus(hit.collider, out focus))
                    return true;
            if (!shellCollision.TryGetValue(hit.collider, out var chunk)) return false;
            const float tolerance = .10f;
            var nearest = tolerance * tolerance;
            var point = Flat(hit.point);
            BuildingFootprint match = null;
            foreach (var footprint in chunk.Buildings)
            {
                if (buildings.TryGet(footprint.Id, out _)) continue;
                var vertices = footprint.Footprint;
                for (var i = 0; i < vertices.Length; i++)
                {
                    var a = Flat(vertices[i]);
                    var edge = Flat(vertices[(i + 1) % vertices.Length]) - a;
                    var along = edge.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(point - a, edge) / edge.sqrMagnitude) : 0;
                    var squared = (point - a - edge * along).sqrMagnitude;
                    if (squared >= nearest) continue;
                    nearest = squared;
                    match = footprint;
                }
            }
            if (match == null) return false;
            focus = new BuildingFocus(match.Id, BuildingFocusKind.Unsupported, FirstPersonMotor.ToLocal(hit.point));
            return true;
        }

        private bool UpdateInteriors(LocalPoint position)
        {
            var changed = false;
            foreach (var plan in buildings.Plans)
            {
                var distance = plan.DistanceToFootprint(position);
                var nearby = interiors.TryGetValue(plan.Id, out var interior);
                if (nearby && distance > InteriorRetainDistance)
                {
                    interior.Dispose();
                    interiors.Remove(plan.Id);
                    if (loaded.TryGetValue(plan.Owner, out var cell)) cell.SetProxy(plan.Id, true);
                    changed = true;
                }
                else if (!nearby && distance <= InteriorLoadDistance)
                {
                    Add(plan.Owner);
                    interior = new BuildingView(plan, palette, root.transform);
                    interior.Refresh(buildings, displayedItem);
                    interiors.Add(plan.Id, interior);
                    loaded[plan.Owner].SetProxy(plan.Id, false);
                    changed = true;
                }
            }
            return changed;
        }

        public void Prime(CellKey center)
        {
            if (!world.TryChunk(center, out _)) throw new ArgumentException("The pose has no terrain cell.");
            foreach (var key in world.Nearby(center, 1)) Add(key);
            Physics.SyncTransforms();
            Trim(center);
        }

        public void Step(CellKey center)
        {
            if (preparedFrame == Time.frameCount) return;
            preparedFrame = Time.frameCount;
            foreach (var key in world.Nearby(center, PrefetchRadius))
                if (!Contains(key))
                {
                    Add(key);
                    Physics.SyncTransforms();
                    break;
                }
            Trim(center);
        }

        private void Trim(CellKey center)
        {
            discard.Clear();
            foreach (var key in loaded.Keys)
                if (Math.Max(Math.Abs(key.X - center.X), Math.Abs(key.Y - center.Y)) > RetainRadius)
                {
                    var pinned = false;
                    foreach (var plan in buildings.Plans)
                        if (plan.Owner.Equals(key) && interiors.ContainsKey(plan.Id))
                        {
                            pinned = true;
                            break;
                        }
                    if (!pinned) discard.Add(key);
                }
            foreach (var key in discard)
            {
                if (loaded[key].ShellCollision != null) shellCollision.Remove(loaded[key].ShellCollision);
                loaded[key].Dispose();
                loaded.Remove(key);
            }
        }

        private void Add(CellKey key)
        {
            if (Contains(key) || !world.TryChunk(key, out var data)) return;
            var cell = new CellView(key);
            cell.Root.transform.SetParent(root.transform, false);
            var ground = new MeshBuffer();
            var detail = new MeshBuffer();
            var collision = new MeshBuffer();
            var size = world.Steps + 1;
            for (var j = 0; j < size; j++)
                for (var i = 0; i < size; i++)
                    ground.Vertices.Add(FirstPersonMotor.ToVector(world.TerrainVertex(key, data, i, j)));
            for (var j = 0; j < world.Steps; j++)
                for (var i = 0; i < world.Steps; i++)
                {
                    var a = j * size + i;
                    var d = a + size + 1;
                    var upward = Vector3.Cross(ground.Vertices[d] - ground.Vertices[a],
                        ground.Vertices[a + 1] - ground.Vertices[a]).y > 0;
                    var indices = ground.Indices[(int)Tone.Grass];
                    if (upward) indices.AddRange(new[] { a, d, a + 1, a, a + size, d });
                    else indices.AddRange(new[] { a, a + 1, d, a, d, a + size });
                }

            foreach (var road in data.Roads) AddRoad(detail, road);
            foreach (var building in data.Buildings)
            {
                if (buildings.TryGet(building.Id, out _))
                {
                    var exterior = new MeshBuffer();
                    var solid = new MeshBuffer();
                    AddBuilding(exterior, solid, building);
                    cell.AddProxy(building.Id, exterior, solid, palette);
                    cell.SetProxy(building.Id, !interiors.ContainsKey(building.Id));
                }
                else AddBuilding(detail, collision, building);
                cell.BuildingIds.Add(building.Id);
            }
            AddTrees(detail, key, data);
            cell.Add("Relief", ground, palette, true, true);
            cell.Add("Roads and shells", detail, palette, true, false);
            var shell = cell.Add("Building collision", collision, palette, false, true);
            if (shell != null)
            {
                cell.ShellCollision = shell.GetComponent<Collider>();
                shellCollision.Add(cell.ShellCollision, data);
            }
            loaded.Add(key, cell);
        }

        private void AddRoad(MeshBuffer mesh, RoadSpan road)
        {
            var a = new Vector2((float)road.A.X, (float)road.A.Z);
            var b = new Vector2((float)road.B.X, (float)road.B.Z);
            var direction = (b - a).normalized;
            if (direction == Vector2.zero) return;
            var normal = new Vector2(-direction.y, direction.x);
            var half = road.Width / 2;
            var main = road.Trail ? Tone.Soil : Tone.Asphalt;
            Strip(mesh, a, b, normal, -half, half, road.Trail ? 0.035f : 0.06f, main);
            if ((road.Sidewalk & 1) != 0) Strip(mesh, a, b, normal, half, half + 1.2f, 0.085f, Tone.Paving);
            if ((road.Sidewalk & 2) != 0) Strip(mesh, a, b, normal, -half - 1.2f, -half, 0.085f, Tone.Paving);

            // A rounded cap covers joins between unequal-width ways and the ends of clipped spans.
            var center = Ground(a, 0.064f);
            for (var i = 0; i < 8; i++)
            {
                var from = 2 * Mathf.PI * i / 8;
                var to = 2 * Mathf.PI * (i + 1) / 8;
                mesh.Triangle(main, center, Ground(a + half * new Vector2(Mathf.Cos(to), Mathf.Sin(to)), 0.064f),
                    Ground(a + half * new Vector2(Mathf.Cos(from), Mathf.Sin(from)), 0.064f));
            }
        }

        private void Strip(MeshBuffer mesh, Vector2 a, Vector2 b, Vector2 normal, float left, float right, float raise, Tone tone)
        {
            var steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 3));
            var across = Mathf.Max(1, Mathf.CeilToInt((right - left) / 2));
            for (var j = 0; j < across; j++)
            {
                var l = normal * Mathf.Lerp(left, right, (float)j / across);
                var r = normal * Mathf.Lerp(left, right, (float)(j + 1) / across);
                var p = Ground(a + l, raise);
                var q = Ground(a + r, raise);
                for (var i = 1; i <= steps; i++)
                {
                    var next = Vector2.Lerp(a, b, (float)i / steps);
                    var x = Ground(next + l, raise);
                    var y = Ground(next + r, raise);
                    mesh.Quad(tone, p, q, y, x);
                    (p, q) = (x, y);
                }
            }
        }

        private Vector3 Ground(Vector2 p, float offset)
        {
            if (!world.TryGround(new GeoPoint(p.x, p.y), out var y))
            {
                // The cropped road width may extend beyond the outer coverage.
                var closest = p;
                var best = float.PositiveInfinity;
                for (var i = 0; i < world.Outline.Count; i++)
                {
                    var a = Flat(world.Outline[i]);
                    var b = Flat(world.Outline[(i + 1) % world.Outline.Count]);
                    var d = b - a;
                    var candidate = a + d * Mathf.Clamp01(Vector2.Dot(p - a, d) / d.sqrMagnitude);
                    if ((candidate - p).sqrMagnitude >= best) continue;
                    closest = candidate;
                    best = (candidate - p).sqrMagnitude;
                }
                p = closest * 0.999999f;
                world.TryGround(new GeoPoint(p.x, p.y), out y);
            }
            return new Vector3(p.x, (float)y + offset, p.y);
        }

        private void AddBuilding(MeshBuffer detail, MeshBuffer collision, BuildingFootprint building)
        {
            var footprint = building.Footprint;
            var ground = new Vector3[footprint.Length];
            var level = float.NegativeInfinity;
            var area = 0.0;
            for (var i = 0; i < footprint.Length; i++)
            {
                ground[i] = Ground(Flat(footprint[i]), -0.6f);
                level = Mathf.Max(level, ground[i].y + 0.6f);
                var a = footprint[i];
                var b = footprint[(i + 1) % footprint.Length];
                area += a.X * b.Z - a.Z * b.X;
            }
            var rise = GableRise(ground, building.Height, area, out var first);
            var wallHeight = building.Height - rise;
            var top = level + wallHeight;
            var wall = building.Style == 0 ? Tone.Brick : building.Style == 2 ? Tone.Stone : Tone.Plaster;
            var roof = building.Style == 1 || building.Style == 3 ? Tone.Metal : Tone.Brick;
            Vector3 At(Vector3 p, float y) => new Vector3(p.x, y, p.z);
            for (var i = 0; i < footprint.Length; i++)
            {
                var a = ground[i];
                var b = ground[(i + 1) % footprint.Length];
                if (area < 0) (a, b) = (b, a);
                var at = At(a, top);
                var bt = At(b, top);
                detail.Quad(wall, a, at, bt, b);
                detail.Quad(Tone.Trim, At(a, top - .18f), at, bt, At(b, top - .18f), .018f);
                collision.Quad(wall, a, at, bt, b);
                var length = Vector2.Distance(Flat(a), Flat(b));
                var bays = Mathf.Min(24, Mathf.FloorToInt(length / 2.7f));
                var floors = Mathf.Min(14, Mathf.Max(1, Mathf.FloorToInt(wallHeight / 2.8f)));
                for (var bay = 0; bay < bays; bay++)
                {
                    var left = Vector3.Lerp(a, b, (bay + .28f) / bays);
                    var right = Vector3.Lerp(a, b, (bay + .72f) / bays);
                    for (var floor = 0; floor < floors; floor++)
                    {
                        var bottom = level + floor * wallHeight / floors + .8f;
                        var upper = Mathf.Min(top - .35f, bottom + 1.12f);
                        detail.Quad(Tone.Trim, At(left, bottom - .09f), At(left, upper + .09f),
                            At(right, upper + .09f), At(right, bottom - .09f), .027f);
                        detail.Quad(Tone.Glass, At(left, bottom), At(left, upper), At(right, upper), At(right, bottom), .044f);
                    }
                }
            }
            if (rise > 0)
            {
                var step = area > 0 ? 1 : 3;
                var a = At(ground[first], top);
                var b = At(ground[(first + step) % 4], top);
                var c = At(ground[(first + 2 * step) % 4], top);
                var d = At(ground[(first + 3 * step) % 4], top);
                var left = At((a + d) / 2, top + rise);
                var right = At((b + c) / 2, top + rise);
                detail.Quad(roof, a, left, right, b);
                detail.Quad(roof, c, right, left, d);
                detail.Triangle(wall, b, right, c);
                detail.Triangle(wall, d, left, a);
                return;
            }
            var tri = building.Triangles;
            for (var i = 0; i < tri.Length; i += 3)
                detail.Triangle(roof, At(ground[tri[i]], top), At(ground[tri[i + 1]], top), At(ground[tri[i + 2]], top));
        }

        private static float GableRise(Vector3[] footprint, float height, double signedArea, out int first)
        {
            first = 0;
            if (footprint.Length != 4 || height < 3.1f || height > 9) return 0;
            var step = signedArea > 0 ? 1 : 3;
            for (var i = 0; i < footprint.Length; i++)
            {
                var edge = footprint[(i + step) % 4] - footprint[i];
                var next = footprint[(i + 2 * step) % 4] - footprint[i];
                if (Vector3.Cross(edge, next).y >= -.05f) return 0;
            }
            var x = Vector2.Distance(Flat(footprint[0]), Flat(footprint[step]));
            var z = Vector2.Distance(Flat(footprint[step]), Flat(footprint[2]));
            if (z > x) first = step;
            var span = Mathf.Min(x, z);
            if (span < 3.2f || span > 11f || Mathf.Max(x, z) > 24f) return 0;
            return Mathf.Min(span * .27f, Mathf.Min(height * .29f, height - 2.5f));
        }

        private void AddTrees(MeshBuffer mesh, CellKey key, RegionChunk data)
        {
            var rng = new System.Random(unchecked(key.X * 73856093 ^ key.Y * 19349663));
            for (var i = 0; i < 9; i++)
            {
                var flat = world.Frame.ProjectDirection(key.DirectionAt(.06 + rng.NextDouble() * .88, .06 + rng.NextDouble() * .88));
                var p = new GeoPoint(flat.X, flat.Z);
                if (!world.IsSafeStart(p)) continue;
                var xz = Flat(p);
                var besideRoad = false;
                foreach (var road in data.Roads)
                {
                    var a = Flat(road.A);
                    var delta = Flat(road.B) - a;
                    var t = delta.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(xz - a, delta) / delta.sqrMagnitude) : 0;
                    if (Vector2.Distance(xz, a + delta * t) < road.Width / 2 + 3) { besideRoad = true; break; }
                }
                if (besideRoad) continue;
                var basePoint = Ground(xz, 0);
                var height = 3.4f + (float)rng.NextDouble() * 3.4f;
                Crown(mesh, basePoint, height * .72f, .16f, Tone.Wood);
                Crown(mesh, basePoint + Vector3.up * (height * .28f), height * .7f, height * .32f, Tone.Leaves);
            }
        }

        private static void Crown(MeshBuffer mesh, Vector3 basePoint, float height, float radius, Tone tone)
        {
            var middle = basePoint + Vector3.up * (height * .33f);
            var peak = basePoint + Vector3.up * height;
            for (var i = 0; i < 7; i++)
            {
                var a = i * (2 * Mathf.PI / 7);
                var b = (i + 1) * (2 * Mathf.PI / 7);
                var u = middle + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius;
                var v = middle + new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * radius;
                mesh.Triangle(tone, v, u, peak);
                mesh.Triangle(tone, u, v, basePoint);
            }
        }

        private static Vector2 Flat(GeoPoint p) => new Vector2((float)p.X, (float)p.Z);
        private static Vector2 Flat(Vector3 p) => new Vector2(p.x, p.z);

        public void Dispose()
        {
            foreach (var interior in interiors.Values) interior.Dispose();
            interiors.Clear();
            shellCollision.Clear();
            foreach (var cell in loaded.Values) cell.Dispose();
            loaded.Clear();
            if (root != null) UnityEngine.Object.Destroy(root);
        }

        private sealed class CellView : IDisposable
        {
            private readonly List<Mesh> meshes = new List<Mesh>();
            private readonly Dictionary<string, GameObject> proxies = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            public GameObject Root { get; }
            public HashSet<string> BuildingIds { get; } = new HashSet<string>(StringComparer.Ordinal);
            public Collider ShellCollision { get; set; }

            public CellView(CellKey key) => Root = new GameObject($"Cell {key}");

            public void AddProxy(string id, MeshBuffer exterior, MeshBuffer solid, Material[] materials)
            {
                var proxy = new GameObject(id + " shell");
                proxy.transform.SetParent(Root.transform, false);
                Add("Facade", exterior, materials, true, false, proxy.transform);
                Add("Facade collision", solid, materials, false, true, proxy.transform);
                proxies.Add(id, proxy);
            }

            public void SetProxy(string id, bool active) => proxies[id].SetActive(active);

            public GameObject Add(string name, MeshBuffer buffer, Material[] materials, bool visible, bool collidable,
                Transform parent = null)
            {
                if (buffer.Vertices.Count == 0) return null;
                var entity = new GameObject(name);
                entity.transform.SetParent(parent == null ? Root.transform : parent, false);
                var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                meshes.Add(mesh);
                mesh.SetVertices(buffer.Vertices);
                var used = new List<Material>();
                for (var i = 0; i < buffer.Indices.Length; i++)
                {
                    var indices = buffer.Indices[i];
                    if (indices.Count == 0) continue;
                    mesh.subMeshCount = used.Count + 1;
                    mesh.SetTriangles(indices, used.Count);
                    used.Add(materials[i]);
                }
                if (visible)
                {
                    mesh.RecalculateNormals();
                    entity.AddComponent<MeshFilter>().sharedMesh = mesh;
                    entity.AddComponent<MeshRenderer>().sharedMaterials = used.ToArray();
                }
                if (collidable) entity.AddComponent<MeshCollider>().sharedMesh = mesh;
                return entity;
            }

            public void Dispose()
            {
                if (Root != null)
                {
                    Root.SetActive(false);
                    UnityEngine.Object.Destroy(Root);
                }
                foreach (var mesh in meshes)
                    if (mesh != null) UnityEngine.Object.Destroy(mesh);
            }
        }

        private sealed class MeshBuffer
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<int>[] Indices = new List<int>[Enum.GetValues(typeof(Tone)).Length];

            public MeshBuffer()
            {
                for (var i = 0; i < Indices.Length; i++) Indices[i] = new List<int>();
            }

            public void Triangle(Tone tone, Vector3 a, Vector3 b, Vector3 c)
            {
                var first = Vertices.Count;
                Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
                var indices = Indices[(int)tone];
                indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
            }

            public void Quad(Tone tone, Vector3 a, Vector3 b, Vector3 c, Vector3 d, float offset = 0)
            {
                var n = offset * Vector3.Cross(b - a, c - a).normalized;
                Triangle(tone, a + n, b + n, c + n);
                Triangle(tone, a + n, c + n, d + n);
            }
        }
    }
}
