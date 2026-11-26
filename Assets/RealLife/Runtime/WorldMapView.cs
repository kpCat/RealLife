using System;
using System.Collections.Generic;
using RealLife.Simulation;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace RealLife.Runtime
{
    internal sealed class GlobeView : IDisposable
    {
        private readonly GameObject root = new GameObject("Globe preview");
        private readonly RenderTexture render = new RenderTexture(896, 896, 24);
        private readonly Mesh mesh;
        private readonly Material material;
        private readonly Camera camera;
        private readonly Transform sphere;
        private readonly Image image;
        private readonly VisualElement pin = new VisualElement { pickingMode = PickingMode.Ignore };
        private readonly Label place = new Label { pickingMode = PickingMode.Ignore };
        private readonly RegionWorld world;
        private readonly Action<double, double> selected;
        private const int PreviewLayer = 31;
        private float longitude = 18;
        private float latitude = -29;
        private float zoom = 1;

        public GlobeView(Texture2D texture, Material template, RegionWorld world, Action<double, double> selected)
        {
            this.world = world;
            this.selected = selected;
            root.transform.position = Vector3.right * 5000;
            camera = new GameObject("Globe camera").AddComponent<Camera>();
            camera.transform.SetParent(root.transform, false);
            camera.transform.localPosition = Vector3.back * 3;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(17, 34, 43, 255);
            camera.orthographic = true;
            camera.cullingMask = 1 << PreviewLayer;
            camera.nearClipPlane = .15f;
            camera.farClipPlane = 5;
            camera.targetTexture = render;
            sphere = new GameObject("Natural Earth sphere").transform;
            sphere.SetParent(root.transform, false);
            sphere.gameObject.layer = PreviewLayer;
            mesh = Sphere();
            sphere.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            material = new Material(template);
            material.SetTexture("_BaseMap", texture);
            sphere.gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;

            Element = new VisualElement();
            Element.AddToClassList("world-geo");
            image = new Image { image = render, scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            image.style.position = Position.Absolute;
            Element.Add(image);
            pin.AddToClassList("globe-pin");
            place.AddToClassList("globe-label");
            place.text = world?.Title;
            Element.Add(pin);
            Element.Add(place);
            Element.RegisterCallback<GeometryChangedEvent>(_ => Layout());
            Element.RegisterCallback<WheelEvent>(evt =>
            {
                zoom = Mathf.Clamp(zoom * Mathf.Exp(-evt.delta.y * .075f), .82f, 2.7f);
                UpdateGlobe();
                evt.StopPropagation();
            });
            MapGesture.Bind(Element, delta =>
            {
                longitude += delta.x * .37f / zoom;
                latitude = Mathf.Clamp(latitude - delta.y * .37f / zoom, -90, 90);
                UpdateGlobe();
            }, Pick);
            UpdateGlobe();
        }

        public VisualElement Element { get; }
        public void SetVisible(bool visible) => root.SetActive(visible);

        public Vector2 Marker()
        {
            if (world == null) return new Vector2(-1000, -1000);
            var direction = GlobeDirection(world.Anchor.Latitude, world.Anchor.Longitude);
            var point = camera.WorldToViewportPoint(sphere.TransformPoint(direction));
            if (sphere.TransformDirection(direction).z >= -.04f || point.x < 0 || point.x > 1 || point.y < 0 || point.y > 1)
                return new Vector2(-1000, -1000);
            return new Vector2(square.x + point.x * square.width, square.y + (1 - point.y) * square.height);
        }

        public void Pick(Vector2 pointer)
        {
            if (!square.Contains(pointer) || square.width < 1) return;
            if (world != null && Vector2.Distance(pointer, Marker()) < 18)
            {
                selected(world.Anchor.Latitude, world.Anchor.Longitude);
                return;
            }
            var x = (2 * (pointer.x - square.x) / square.width - 1) * camera.orthographicSize;
            var y = (1 - 2 * (pointer.y - square.y) / square.height) * camera.orthographicSize;
            if (x * x + y * y > 1) return;
            var front = new Vector3(x, y, -Mathf.Sqrt(1 - x * x - y * y));
            var local = sphere.InverseTransformDirection(front);
            var lat = Mathf.Asin(Mathf.Clamp(local.y, -1, 1)) * Mathf.Rad2Deg;
            var lon = Mathf.Atan2(local.x, -local.z) * Mathf.Rad2Deg;
            selected(lat, lon);
        }

        private Rect square;
        private void Layout()
        {
            var size = Mathf.Min(Element.contentRect.width, Element.contentRect.height);
            if (size < 1) return;
            square = new Rect((Element.contentRect.width - size) / 2, (Element.contentRect.height - size) / 2, size, size);
            image.style.left = square.x;
            image.style.top = square.y;
            image.style.width = size;
            image.style.height = size;
            PositionMarker();
        }

        private void UpdateGlobe()
        {
            camera.orthographicSize = 1.15f / zoom;
            sphere.localRotation = Quaternion.Euler(latitude, longitude, 0);
            PositionMarker();
        }

        private void PositionMarker()
        {
            var location = Marker();
            pin.style.display = place.style.display = location.x < 0 ? DisplayStyle.None : DisplayStyle.Flex;
            pin.style.left = location.x - 7;
            pin.style.top = location.y - 7;
            place.style.left = location.x + 11;
            place.style.top = location.y - 12;
        }

        private static Vector3 GlobeDirection(double latitude, double longitude)
        {
            var lat = (float)latitude * Mathf.Deg2Rad;
            var lon = (float)longitude * Mathf.Deg2Rad;
            var horizontal = Mathf.Cos(lat);
            return new Vector3(horizontal * Mathf.Sin(lon), Mathf.Sin(lat), -horizontal * Mathf.Cos(lon));
        }

        private static Mesh Sphere()
        {
            const int columns = 128, rows = 64;
            var positions = new Vector3[(columns + 1) * (rows + 1)];
            var uv = new Vector2[positions.Length];
            var indices = new List<int>(columns * rows * 6);
            for (var y = 0; y <= rows; y++)
                for (var x = 0; x <= columns; x++)
                {
                    var i = y * (columns + 1) + x;
                    var u = (float)x / columns;
                    var v = (float)y / rows;
                    positions[i] = GlobeDirection(v * 180 - 90, u * 360 - 180);
                    uv[i] = new Vector2(u, v);
                    if (x == columns || y == rows) continue;
                    var next = i + columns + 1;
                    indices.Add(i); indices.Add(next); indices.Add(i + 1);
                    indices.Add(i + 1); indices.Add(next); indices.Add(next + 1);
                }
            return new Mesh { name = "WGS84 preview sphere", vertices = positions, normals = positions, uv = uv, triangles = indices.ToArray() };
        }

        public void Dispose()
        {
            if (camera != null) camera.targetTexture = null;
            if (root != null)
            {
                root.SetActive(false);
                Object.Destroy(root);
            }
            if (render != null)
            {
                render.Release();
                Object.Destroy(render);
            }
            if (material != null) Object.Destroy(material);
            if (mesh != null) Object.Destroy(mesh);
        }
    }

    internal sealed class RegionMap : VisualElement
    {
        private readonly RegionWorld world;
        private readonly IReadOnlyList<BuildingPlan> accessPlans;
        private readonly Action<GeoPoint> choose;
        private readonly Vector2 min, max;
        private Vector2 pan;
        private float zoom = 1;
        public GeoPoint Selection { get; set; }

        public RegionMap(RegionWorld world, IReadOnlyList<BuildingPlan> accessPlans, GeoPoint selection, Action<GeoPoint> choose)
        {
            this.world = world;
            this.accessPlans = accessPlans;
            this.choose = choose;
            Selection = selection;
            min = max = Flat(world.Outline[0]);
            foreach (var p in world.Outline)
            {
                min = Vector2.Min(min, Flat(p));
                max = Vector2.Max(max, Flat(p));
            }
            AddToClassList("world-geo");
            generateVisualContent += Draw;
            MapGesture.Bind(this, delta => { pan += delta; MarkDirtyRepaint(); }, Pick);
            RegisterCallback<WheelEvent>(evt =>
            {
                var position = this.WorldToLocal((Vector2)evt.mousePosition);
                var before = Unproject(position);
                zoom = Mathf.Clamp(zoom * Mathf.Exp(-evt.delta.y * .08f), .8f, 12);
                pan += position - Project(before);
                MarkDirtyRepaint();
                evt.StopPropagation();
            });
        }

        public Vector2 Project(GeoPoint point) => contentRect.center + pan + (Flat(point) - (min + max) / 2) * Scale;
        private float Scale => Mathf.Max(.0001f, Mathf.Min((contentRect.width - 44) / (max.x - min.x),
            (contentRect.height - 44) / (max.y - min.y))) * zoom;

        private GeoPoint Unproject(Vector2 point)
        {
            var flat = (point - contentRect.center - pan) / Scale + (min + max) / 2;
            return new GeoPoint(flat.x, -flat.y);
        }

        public void Pick(Vector2 point) => choose(Unproject(point));

        private void Draw(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            Path(painter, world.Outline);
            painter.fillColor = new Color32(49, 76, 65, 255);
            painter.Fill();

            var viewport = contentRect;
            foreach (var chunk in world.Chunks)
            {
                foreach (var road in chunk.Roads)
                {
                    var a = Project(road.A);
                    var b = Project(road.B);
                    if (!Intersects(viewport, a, b)) continue;
                    painter.strokeColor = road.Trail ? new Color32(155, 166, 126, 255) : new Color32(215, 206, 163, 255);
                    painter.lineWidth = Mathf.Clamp(road.Width * Scale, road.Trail ? .65f : 1.2f, 16);
                    painter.BeginPath();
                    painter.MoveTo(a);
                    painter.LineTo(b);
                    painter.Stroke();
                }
            }
            painter.fillColor = new Color32(156, 168, 144, 255);
            foreach (var chunk in world.Chunks)
                foreach (var building in chunk.Buildings)
                {
                    var lower = Project(building.Footprint[0]);
                    var upper = lower;
                    foreach (var corner in building.Footprint)
                    {
                        var p = Project(corner);
                        lower = Vector2.Min(lower, p);
                        upper = Vector2.Max(upper, p);
                    }
                    if (!Intersects(viewport, lower, upper)) continue;
                    Path(painter, building.Footprint);
                    painter.Fill();
                }

            Path(painter, world.Outline);
            painter.lineWidth = 2;
            painter.strokeColor = new Color32(221, 177, 107, 255);
            painter.Stroke();

            foreach (var plan in accessPlans)
            {
                var marker = Project(new GeoPoint(plan.Entrance.X, plan.Entrance.Z));
                if (!viewport.Contains(marker)) continue;
                painter.BeginPath();
                painter.Arc(marker, 4.5f, 0, 360);
                painter.ClosePath();
                painter.fillColor = plan.Kind == BuildingKind.PublicPremises
                    ? new Color32(116, 192, 237, 255) : new Color32(142, 214, 151, 255);
                painter.Fill();
            }

            var dot = Project(Selection);
            painter.BeginPath();
            painter.Arc(dot, 7, 0, 360);
            painter.ClosePath();
            painter.fillColor = new Color32(227, 156, 76, 255);
            painter.Fill();
            painter.strokeColor = new Color32(255, 235, 167, 255);
            painter.Stroke();
        }

        private void Path(Painter2D painter, IReadOnlyList<GeoPoint> polygon)
        {
            painter.BeginPath();
            painter.MoveTo(Project(polygon[0]));
            for (var i = 1; i < polygon.Count; i++) painter.LineTo(Project(polygon[i]));
            painter.ClosePath();
        }

        private static Vector2 Flat(GeoPoint point) => new Vector2((float)point.X, (float)-point.Z);
        private static bool Intersects(Rect rect, Vector2 a, Vector2 b) => Mathf.Max(a.x, b.x) >= rect.xMin &&
            Mathf.Min(a.x, b.x) <= rect.xMax && Mathf.Max(a.y, b.y) >= rect.yMin && Mathf.Min(a.y, b.y) <= rect.yMax;
    }

    internal static class MapGesture
    {
        public static void Bind(VisualElement element, Action<Vector2> drag, Action<Vector2> pick)
        {
            var active = -1;
            var start = Vector2.zero;
            var last = Vector2.zero;
            var dragging = false;
            element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || active >= 0) return;
                active = evt.pointerId;
                start = last = (Vector2)evt.localPosition;
                dragging = false;
                element.CapturePointer(active);
            });
            element.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (evt.pointerId != active) return;
                var position = (Vector2)evt.localPosition;
                if (!dragging && Vector2.Distance(position, start) < 4) return;
                dragging = true;
                drag(position - last);
                last = position;
            });
            element.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (evt.pointerId != active) return;
                var click = !dragging;
                element.ReleasePointer(active);
                active = -1;
                if (click) pick((Vector2)evt.localPosition);
            });
            element.RegisterCallback<PointerCaptureOutEvent>(evt =>
            {
                if (evt.pointerId == active) active = -1;
            });
        }
    }
}
