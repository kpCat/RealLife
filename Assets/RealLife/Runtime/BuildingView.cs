using System;
using System.Collections.Generic;
using RealLife.Simulation;
using UnityEngine;
using Tone = RealLife.Runtime.RegionView.Tone;
using Object = UnityEngine.Object;

namespace RealLife.Runtime
{
    internal enum BuildingFocusKind { Door, Container, PlacedItem, Unsupported }

    internal readonly struct BuildingFocus
    {
        public string Id { get; }
        public BuildingFocusKind Kind { get; }
        public LocalPoint Position { get; }

        public BuildingFocus(string id, BuildingFocusKind kind, LocalPoint position) =>
            (Id, Kind, Position) = (id, kind, position);
    }

    internal sealed class BuildingView : IDisposable
    {
        internal const double EntryRampWidth = 1.62;
        private const double Wall = BuildingPlan.WallThickness;
        private const double Storey = BuildingPlan.StoreyHeight;
        private readonly BuildingPlan plan;
        private readonly Material[] palette;
        private readonly GameObject root;
        private readonly List<(BuildingDoor Door, Transform Hinge, BoxCollider Leaf, float ClosedAngle)> doors =
            new List<(BuildingDoor, Transform, BoxCollider, float)>();
        private readonly List<(BuildingAnchor Anchor, GameObject Item, MeshRenderer Display)> items =
            new List<(BuildingAnchor, GameObject, MeshRenderer)>();
        private readonly Dictionary<Collider, BuildingFocus> focus = new Dictionary<Collider, BuildingFocus>();
        private readonly List<Mesh> meshes = new List<Mesh>();

        public BuildingView(BuildingPlan plan, Material[] palette, Transform parent)
        {
            this.plan = plan;
            this.palette = palette;
            root = new GameObject(plan.Id + " interior");
            root.transform.SetParent(parent, false);
            var origin = FirstPersonMotor.ToVector(plan.ToWorld(0, 0, 0));
            var ahead = FirstPersonMotor.ToVector(plan.ToWorld(0, 0, 1)) - origin;
            root.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(ahead, Vector3.up));
            Shell();
            foreach (var partition in plan.Partitions)
                WallRun(partition.X, partition.Y, partition.Z, partition.Length, partition.AlongX, partition.Door, Tone.Plaster);
            foreach (var door in plan.Doors) Door(door);
            if (plan.Stairs != null) Stairwell();
            foreach (var piece in plan.Furniture) Furnish(piece);
            foreach (var room in plan.Rooms) Lighting(room);
        }

        public bool TryFocus(Collider hit, out BuildingFocus target) => focus.TryGetValue(hit, out target);

        public void Refresh(Buildings buildings, Func<string, string> displayedItem)
        {
            foreach (var (door, hinge, leaf, closedAngle) in doors)
            {
                var isOpen = buildings.IsDoorOpen(door.Id);
                var angle = isOpen ? door.Swing * 90 : 0;
                hinge.localRotation = Quaternion.Euler(0, closedAngle + angle, 0);
                focus[leaf] = new BuildingFocus(door.Id, BuildingFocusKind.Door, door.GetAim(isOpen));
            }
            foreach (var (anchor, item, display) in items)
            {
                var appearance = displayedItem?.Invoke(anchor.Id);
                item.SetActive(appearance != null);
                if (appearance != null) StyleItem(display, appearance);
            }
        }

        private void Shell()
        {
            var width = plan.Width;
            var depth = plan.Depth;
            var side = width / 2;
            var bottom = plan.FoundationBottom - plan.GroundFloor;
            var exterior = plan.Style == 0 ? Tone.Brick : plan.Style == 2 ? Tone.Stone : Tone.Plaster;
            var shop = plan.Kind == BuildingKind.PublicPremises;
            var floor = shop ? Tone.Paving : Tone.Wood;
            Part("Foundation", 0, bottom / 2 - .04, depth / 2, width + Wall, -bottom, depth + Wall, Tone.Stone);
            Slab("Ground floor", -side, side, 0, depth, 0, floor);
            for (var storey = 0; storey < plan.FloorCount; storey++)
            {
                var y = storey * Storey;
                if (storey == 0)
                {
                    var wing = side - BuildingDoor.Width / 2;
                    var x = (side + BuildingDoor.Width / 2) / 2;
                    WindowWall(-x, y, 0, wing, true, exterior, shop);
                    WindowWall(x, y, 0, wing, true, exterior, shop);
                    Segment(0, y + BuildingDoor.Height, 0, BuildingDoor.Width, Storey - BuildingDoor.Height, true, exterior);
                }
                else
                {
                    WindowWall(-side / 2, y, 0, side, true, exterior, false);
                    WindowWall(side / 2, y, 0, side, true, exterior, false);
                }
                WindowWall(-side / 2, y, depth, side, true, exterior, false);
                WindowWall(side / 2, y, depth, side, true, exterior, false);
                foreach (var x in new[] { -side, side })
                {
                    WindowWall(x, y, depth / 4, depth / 2, false, exterior, false);
                    WindowWall(x, y, 3 * depth / 4, depth / 2, false, exterior, false);
                }
            }
            var ceiling = plan.FloorCount * Storey;
            Slab("Ceiling", -side, side, 0, depth, ceiling, Tone.Trim);
            var roof = plan.Style == 1 || plan.Style == 3 ? Tone.Metal : Tone.Brick;
            var height = plan.RoofHeight - ceiling;
            Part("Eaves", 0, ceiling + .06, depth / 2, width + .48, .20, depth + .4, roof, false);
            if (!shop)
            {
                var pitch = Math.Atan2(height, side + .20) * Mathf.Rad2Deg;
                var slope = Math.Sqrt((side + .20) * (side + .20) + height * height);
                Part("Left roof", -(side + .20) / 2, ceiling + height / 2 + .15, depth / 2,
                    slope, .13, depth + .38, roof, false).transform.localRotation = Quaternion.Euler(0, 0, (float)pitch);
                Part("Right roof", (side + .20) / 2, ceiling + height / 2 + .15, depth / 2,
                    slope, .13, depth + .38, roof, false).transform.localRotation = Quaternion.Euler(0, 0, (float)-pitch);
                Gable(0, ceiling + .13, side, height, exterior, false);
                Gable(depth, ceiling + .13, side, height, exterior, true);
            }
            Part("Entry lintel", 0, BuildingDoor.Height + .13, -.12, 1.58, .16, .32, shop ? Tone.Metal : Tone.Trim, false);
            if (shop) Part("Shop awning", 0, 2.32, -.48, 2.40, .13, .96, Tone.Metal, false);
            Slope("Entry ramp", 0, plan.LandingGround - plan.GroundFloor, -BuildingPlan.EntryRun, 0, 0, EntryRampWidth, Tone.Paving);
        }

        private void Gable(double z, double y, double halfWidth, double rise, Tone material, bool back)
        {
            var entity = new GameObject("Gable");
            entity.transform.SetParent(root.transform, false);
            var mesh = new Mesh
            {
                name = "Gable",
                vertices = new[] { V(-halfWidth, y, z), V(0, y + rise, z), V(halfWidth, y, z) },
                triangles = back ? new[] { 0, 2, 1 } : new[] { 0, 1, 2 }
            };
            mesh.RecalculateNormals();
            meshes.Add(mesh);
            entity.AddComponent<MeshFilter>().sharedMesh = mesh;
            entity.AddComponent<MeshRenderer>().sharedMaterial = palette[(int)material];
        }

        private void WindowWall(double x, double y, double z, double length, bool alongX, Tone tone, bool shop)
        {
            const double apertureHeight = 1.13;
            var sill = shop ? .78 : .93;
            var opening = Math.Min(length - .52, shop ? 1.8 : 1.38);
            var end = (length - opening) / 2;
            Segment(x, y, z, length, sill, alongX, tone);
            Segment(x, y + sill + apertureHeight, z, length, Storey - sill - apertureHeight, alongX, tone);
            WallSegment(-1);
            WallSegment(1);
            var (width, depth) = alongX ? (opening, .035) : (.035, opening);
            Part("Window pane", x, y + sill + apertureHeight / 2, z, width, apertureHeight, depth, Tone.Glass, true, false);
            Strip("Window sill", 0, y + sill, opening + .15, .09, .29, Tone.Trim);
            Strip("Window head", 0, y + sill + apertureHeight, opening + .12, .08, .19, Tone.Trim);
            Strip("Window mullion", 0, y + sill + apertureHeight / 2, .07, apertureHeight, .12, Tone.Trim);
            Strip("Window side", -opening / 2, y + sill + apertureHeight / 2, .08, apertureHeight, .18, Tone.Trim);
            Strip("Window side", opening / 2, y + sill + apertureHeight / 2, .08, apertureHeight, .18, Tone.Trim);
            Strip("Skirting", 0, y + .10, length, .11, Wall + .045, Tone.Trim);

            void WallSegment(int sign)
            {
                var offset = sign * (opening + end) / 2;
                Segment(x + (alongX ? offset : 0), y + sill, z + (alongX ? 0 : offset), end, apertureHeight, alongX, tone);
            }

            void Strip(string label, double offset, double centerY, double span, double height, double thickness, Tone material) =>
                Part(label, x + (alongX ? offset : 0), centerY, z + (alongX ? 0 : offset),
                    alongX ? span : thickness, height, alongX ? thickness : span, material, false);
        }

        private void WallRun(double x, double y, double z, double length, bool alongX, BuildingDoor door, Tone tone)
        {
            if (door == null)
            {
                Segment(x, y, z, length, Storey, alongX, tone);
                return;
            }
            var middle = alongX ? x : z;
            var center = alongX ? door.X : door.Z;
            var low = middle - length / 2;
            var high = middle + length / 2;
            Add(low, center - BuildingDoor.Width / 2);
            Add(center + BuildingDoor.Width / 2, high);
            Segment(door.X, y + BuildingDoor.Height, door.Z, BuildingDoor.Width, Storey - BuildingDoor.Height, alongX, tone);

            void Add(double a, double b)
            {
                if (b <= a) return;
                var pos = (a + b) / 2;
                Segment(alongX ? pos : x, y, alongX ? z : pos, b - a, Storey, alongX, tone);
                Part("Skirting", alongX ? pos : x, y + .09, alongX ? z : pos,
                    alongX ? b - a : Wall + .045, .10, alongX ? Wall + .045 : b - a, Tone.Trim, false);
            }
        }

        private void Segment(double x, double floor, double z, double length, double height, bool alongX, Tone tone) =>
            Part("Wall", x, floor + height / 2, z, alongX ? length : Wall, height, alongX ? Wall : length, tone);

        private void Slab(string label, double left, double right, double front, double back, double top, Tone tone) =>
            Part(label, (left + right) / 2, top - .08, (front + back) / 2, right - left, .16, back - front, tone);

        private void Door(BuildingDoor door)
        {
            var hinge = new GameObject("Door hinge").transform;
            hinge.SetParent(root.transform, false);
            hinge.localPosition = V(door.HingeX, door.Y, door.HingeZ);
            var closedAngle = door.AlongX ? 0 : -90;
            var doorTone = door.Exterior && plan.Kind == BuildingKind.PublicPremises ? Tone.Metal : Tone.Wood;
            var leaf = Part("Door", BuildingDoor.Width / 2, BuildingDoor.Height / 2 + .015, 0,
                BuildingDoor.LeafWidth, BuildingDoor.Height - .05, BuildingDoor.LeafThickness, doorTone,
                parent: hinge).GetComponent<BoxCollider>();
            Part("Door panel", .60, .71, 0, .91, .98, .105, door.Exterior ? Tone.Metal : Tone.Brick, false, parent: hinge);
            Part("Door panel", .60, 1.66, 0, .91, .69, .105, door.Exterior ? Tone.Metal : Tone.Brick, false, parent: hinge);
            Part("Handle", .99, 1.08, 0, .13, .052, .17, Tone.Trim, false, parent: hinge);
            doors.Add((door, hinge, leaf, closedAngle));
            var frameThickness = door.Exterior ? .29 : Wall + .07;
            for (var side = -1; side <= 1; side += 2)
            {
                var offset = side * (BuildingDoor.Width / 2 + .035);
                Part("Door jamb", door.X + (door.AlongX ? offset : 0), door.Y + BuildingDoor.Height / 2,
                    door.Z + (door.AlongX ? 0 : offset), door.AlongX ? .10 : frameThickness,
                    BuildingDoor.Height, door.AlongX ? frameThickness : .10, Tone.Trim, false);
            }
        }

        private void Stairwell()
        {
            var half = plan.Width / 2;
            var edge = BuildingStairs.OpeningHalfWidth;
            var front = BuildingStairs.OpeningFront;
            var back = BuildingStairs.Top;
            Slab("Upper floor", -half, -edge, 0, plan.Depth, Storey, Tone.Wood);
            Slab("Upper floor", edge, half, 0, plan.Depth, Storey, Tone.Wood);
            Slab("Upper floor", -edge, edge, 0, front, Storey, Tone.Wood);
            Slab("Upper landing", -edge, edge, back, plan.Depth, Storey, Tone.Wood);
            Slope("Stair support", 0, 0, BuildingStairs.Bottom, Storey, back, BuildingStairs.HalfWidth * 2, Tone.Wood, false);
            const int steps = 18;
            var tread = (back - BuildingStairs.Bottom) / steps;
            var rise = Storey / steps;
            for (var i = 0; i < steps; i++)
            {
                var z = BuildingStairs.Bottom + (i + .5) * tread;
                Part("Stair tread", 0, (i + .5) * rise - .09, z, BuildingStairs.HalfWidth * 2, .18, tread + .01, Tone.Wood, false);
                Part("Stair nosing", 0, (i + .5) * rise - .025, z - tread / 2, BuildingStairs.HalfWidth * 2,
                    .05, .035, Tone.Trim, false);
            }
            foreach (var side in new[] { -1, 1 })
            {
                Rail(side * edge, Storey, (front + back) / 2, BuildingStairs.GuardThickness, back - front);
                var x = side * (BuildingStairs.HalfWidth + BuildingStairs.GuardOffset);
                Slope("Stair guard", x, .84, BuildingStairs.Bottom, Storey + .84, back, BuildingStairs.GuardThickness, Tone.Wood);
                for (var i = 1; i <= 5; i++)
                {
                    var t = i / 6.0;
                    Part("Stair baluster", x, t * Storey + .40, BuildingStairs.Bottom + t * (back - BuildingStairs.Bottom),
                        .065, .82, .065, Tone.Trim, false);
                }
            }
            Rail(0, Storey, front, 2 * edge, BuildingStairs.GuardThickness);
        }

        private void Rail(double x, double y, double z, double width, double depth)
        {
            Part("Landing guard", x, y + .53, z, width, 1.06, depth, Tone.Trim, true, false);
            Part("Landing banister", x, y + 1.05, z, width + .045, .10, depth + .045, Tone.Wood, false);
            var length = Math.Max(width, depth);
            var posts = (int)Math.Ceiling(length / .38);
            for (var i = 0; i <= posts; i++)
            {
                var offset = length * (i / (double)posts - .5);
                Part("Landing baluster", x + (width > depth ? offset : 0), y + .51, z + (depth > width ? offset : 0),
                    .055, 1.02, .055, Tone.Trim, false);
            }
        }

        private void Slope(string label, double x, double frontY, double frontZ, double backY, double backZ,
            double width, Tone tone, bool visible = true)
        {
            const double thickness = BuildingStairs.RampThickness;
            var rise = backY - frontY;
            var run = backZ - frontZ;
            var length = Math.Sqrt(rise * rise + run * run);
            var rotation = Quaternion.Euler((float)(-Math.Atan2(rise, run) * Mathf.Rad2Deg), 0, 0);
            var middle = V(x, (frontY + backY) / 2, (frontZ + backZ) / 2) - rotation * new Vector3(0, (float)thickness / 2, 0);
            var ramp = Part(label, middle.x, middle.y, middle.z, width, thickness, length, tone, true, visible);
            ramp.transform.localRotation = rotation;
        }

        private void Furnish(BuildingFurniture piece)
        {
            var level = Storey * piece.Floor;
            var w = piece.Width;
            var h = piece.Height;
            var d = piece.Depth;
            var x = piece.X;
            var z = piece.Z;
            var anchor = piece.Anchor;
            GameObject target = null;
            switch (piece.Kind)
            {
                case BuildingFurnitureKind.Shelves:
                    for (var side = -1; side <= 1; side += 2)
                        TrackContainer(Part("Shelf side", x + side * (w - .05) / 2, level + h / 2, z, .05, h, d, Tone.Wood));
                    foreach (var shelf in new[] { .27, .865, 1.20, h - .022 })
                        TrackContainer(Part("Shelf", x, level + shelf, z, w, .048, d, Tone.Wood));
                    target = Part("Storage drawer", x, level + .54, z, w - .10, .37, d - .018, Tone.Metal);
                    Part("Drawer pull", x, level + .64, z - d / 2, .23, .036, .055, Tone.Trim, false);
                    Part("Drawer pull", x, level + .64, z + d / 2, .23, .036, .055, Tone.Trim, false);
                    break;
                case BuildingFurnitureKind.Bed:
                    Part("Bed base", x, level + .20, z, w, .40, d, Tone.Wood);
                    Part("Mattress", x, level + h - .067, z, w - .09, .14, d - .08, Tone.Trim);
                    Part("Bedspread", x, level + h + .007, z - .25, w - .12, .035, d - .58, Tone.Glass, false);
                    Part("Headboard", x, level + .37, z + d / 2, w, .74, .085, Tone.Wood, false);
                    Part("Pillow", x, level + h + .041, z + d / 2 - .3, w * .66, .083, .31, Tone.Plaster, false);
                    break;
                case BuildingFurnitureKind.Counter:
                    Part("Cabinet", x, level + h / 2, z, w - .07, h - .075, d - .095, Tone.Metal);
                    target = Part("Countertop", x, level + h - .033, z, w, .066, d, Tone.Stone);
                    foreach (var side in new[] { -1, 1 })
                    {
                        Part("Cabinet door", x + side * w / 4, level + h * .50, z - d / 2 + .036,
                            w / 2 - .065, h - .16, .055, Tone.Wood, false);
                        Part("Cabinet pull", x + side * .09, level + h - .27, z - d / 2,
                            .033, .12, .052, Tone.Trim, false);
                    }
                    break;
                case BuildingFurnitureKind.Table:
                case BuildingFurnitureKind.Chair:
                    target = Part("Wood top", x, level + h - .034, z, w, .068, d, Tone.Wood);
                    foreach (var dx in new[] { -1, 1 })
                        foreach (var dz in new[] { -1, 1 })
                            Part("Furniture leg", x + dx * (w / 2 - .075), level + h / 2 - .04, z + dz * (d / 2 - .073),
                                .059, h - .08, .059, Tone.Metal);
                    if (piece.Kind == BuildingFurnitureKind.Chair)
                        Part("Chair back", x, level + h + .21, z + d / 2 - .03, w, .41, .055, Tone.Wood);
                    break;
            }

            if (anchor == null) return;
            var p = plan.ToLocal(anchor.Position);
            var item = Part("Placed item", p.X, p.Y, p.Z, .21, .165, .15, Tone.Glass);
            var display = item.GetComponent<MeshRenderer>();
            items.Add((anchor, item, display));
            item.SetActive(false);
            focus[item.GetComponent<Collider>()] = new BuildingFocus(anchor.Id, BuildingFocusKind.PlacedItem, anchor.Position);
            TrackContainer(target);

            void TrackContainer(GameObject part)
            {
                if (anchor != null)
                    focus[part.GetComponent<Collider>()] = new BuildingFocus(anchor.Id, BuildingFocusKind.Container, anchor.ContainerPosition);
            }
        }

        private void StyleItem(MeshRenderer display, string appearance)
        {
            var (size, tone) = appearance switch
            {
                "hand-tool" => (V(.22, .085, .084), Tone.Metal),
                "book" => (V(.21, .066, .15), Tone.Brick),
                "plate" => (V(.21, .038, .21), Tone.Plaster),
                "cup" => (V(.105, .12, .106), Tone.Plaster),
                "carton" => (V(.14, .18, .085), Tone.Wood),
                "tin" => (V(.103, .13, .103), Tone.Metal),
                _ => (V(.12, .19, .13), Tone.Glass)
            };
            display.transform.localScale = size;
            display.sharedMaterial = palette[(int)tone];
        }

        private void Lighting(BuildingRoom room)
        {
            // Keep the stairs clear and illuminate the central entrance from the side rooms.
            if (room.Role == BuildingRoomRole.Hall) return;
            var level = room.Floor * Storey;
            var x = (room.Left + room.Right) / 2;
            var z = (room.Front + room.Back) / 2;
            Part("Ceiling shade", x, level + Storey - .21, z, .33, .095, .29, Tone.Trim, false);
            var lamp = new GameObject("Interior light").AddComponent<Light>();
            lamp.transform.SetParent(root.transform, false);
            lamp.transform.localPosition = V(x, level + Storey - .40, z);
            lamp.type = LightType.Point;
            lamp.range = 5.8f;
            lamp.intensity = 1.45f;
            lamp.color = new Color32(255, 229, 189, 255);
        }

        private GameObject Part(string label, double x, double y, double z, double width, double height, double depth,
            Tone tone, bool collidable = true, bool visible = true, Transform parent = null)
        {
            var part = visible ? GameObject.CreatePrimitive(PrimitiveType.Cube) : new GameObject();
            part.name = label;
            part.transform.SetParent(parent == null ? root.transform : parent, false);
            part.transform.localPosition = V(x, y, z);
            part.transform.localScale = V(width, height, depth);
            if (visible)
            {
                part.GetComponent<MeshRenderer>().sharedMaterial = palette[(int)tone];
                if (!collidable)
                {
                    var collider = part.GetComponent<Collider>();
                    collider.enabled = false;
                    Object.Destroy(collider);
                }
            }
            else if (collidable)
            {
                part.AddComponent<BoxCollider>();
            }
            return part;
        }

        private static Vector3 V(double x, double y, double z) => new Vector3((float)x, (float)y, (float)z);

        public void Dispose()
        {
            if (root != null)
            {
                root.SetActive(false);
                Object.Destroy(root);
            }
            foreach (var mesh in meshes)
                if (mesh != null) Object.Destroy(mesh);
            meshes.Clear();
        }
    }
}
