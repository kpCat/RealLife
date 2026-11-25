using System;
using RealLife.Simulation;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealLife.Editor
{
    internal sealed class DistrictGeometry
    {
        private enum Tone { Grass, Soil, Asphalt, Stone, Paving, Plaster, Brick, Trim, Glass, Door, Wood, Metal, Leaves, LeavesLight, Paint, Light }
        private readonly Material[] palette = new Material[Enum.GetValues(typeof(Tone)).Length];

        public DistrictGeometry(string directory)
        {
            DistrictAuthoring.CreateFolder(directory, "Materials");
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader is missing.");

            var colors = new[]
            {
                new Color32(106, 124, 82, 255), new Color32(105, 89, 66, 255), new Color32(93, 99, 100, 255),
                new Color32(172, 174, 160, 255), new Color32(192, 174, 143, 255), new Color32(227, 211, 171, 255),
                new Color32(147, 101, 81, 255), new Color32(237, 224, 193, 255), new Color32(126, 161, 167, 255),
                new Color32(74, 101, 85, 255), new Color32(152, 107, 69, 255), new Color32(60, 70, 69, 255),
                new Color32(84, 116, 67, 255), new Color32(119, 150, 79, 255), new Color32(215, 150, 62, 255),
                new Color32(246, 225, 173, 255)
            };
            for (var index = 0; index < palette.Length; index++)
            {
                var tone = (Tone)index;
                var path = $"{directory}/Materials/{tone}.mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    material = new Material(shader) { name = tone.ToString() };
                    AssetDatabase.CreateAsset(material, path);
                }
                material.SetColor("_BaseColor", colors[index]);
                material.SetFloat("_Smoothness", tone == Tone.Glass ? 0.78f : tone == Tone.Metal ? 0.34f : 0.16f);
                EditorUtility.SetDirty(material);
                palette[index] = material;
            }
        }

        public void Build(DistrictDefinition district)
        {
            var yard = new GameObject("Courtyard and street").transform;
            Surface(yard, "Ground", district.Bounds, 0, Tone.Grass);
            foreach (var surface in district.Surfaces ?? Array.Empty<SurfaceDefinition>())
            {
                switch (surface.Kind)
                {
                    case SurfaceKind.Asphalt: Street(yard, surface.Area); break;
                    case SurfaceKind.Sidewalk: Sidewalk(yard, surface.Area); break;
                    case SurfaceKind.Path: Path(yard, surface.Area); break;
                    case SurfaceKind.Soil: Surface(yard, "Planting bed", surface.Area, 0.025f, Tone.Soil); break;
                }
            }
            Enclosure(yard, district.Bounds);
            Workshop(district.Building, district.Door);
            foreach (var prop in district.Props ?? Array.Empty<PropDefinition>()) BuildProp(yard, prop);
            Lighting(district.Building);
        }

        public GameObject SaveDoor(DoorDefinition definition, string path)
        {
            var root = new GameObject("Workshop door");
            var w = definition.Width;
            var h = definition.Height;
            Box(root.transform, "Door leaf", w / 2, (h + 0.045f) / 2, 0, w - 0.035f, h - 0.075f, 0.10f, Tone.Door);
            for (var side = -1; side <= 1; side += 2)
            {
                var z = side * 0.052f;
                Box(root.transform, $"Lower inset {side}", w / 2, 0.53f, z, w - 0.2f, 0.63f, 0.013f, Tone.Trim, false);
                Box(root.transform, $"Blue glass {side}", w / 2, 1.58f, z, w - 0.28f, 0.61f, 0.015f, Tone.Glass, false);
                Box(root.transform, $"Glass divider {side}", w / 2, 1.58f, z * 1.2f, 0.045f, 0.61f, 0.024f, Tone.Door, false);
                Box(root.transform, $"Handle plate {side}", w - 0.19f, 1.08f, z * 1.15f, 0.088f, 0.24f, 0.022f, Tone.Metal, false);
                Box(root.transform, $"Handle {side}", w - 0.24f, 1.13f, z * 1.77f, 0.15f, 0.045f, 0.045f, Tone.Trim, false);
            }
            return SavePrefab(root, path);
        }

        public GameObject SaveLantern(string path)
        {
            var root = new GameObject("Workshop lantern");
            var model = Group(root.transform, "Lantern model", Vector3.zero);
            Shape(model, "Rubber foot", PrimitiveType.Cylinder, 0, 0.034f, 0, 0.22f, 0.034f, 0.22f, Tone.Metal, false);
            Shape(model, "Diffuser", PrimitiveType.Cylinder, 0, 0.128f, 0, 0.159f, 0.064f, 0.159f, Tone.Light, false);
            Shape(model, "Cap", PrimitiveType.Cylinder, 0, 0.204f, 0, 0.214f, 0.026f, 0.214f, Tone.Paint, false);
            for (var side = -1; side <= 1; side += 2)
            {
                Box(model, $"Diffuser guard {side}", side * 0.086f, 0.12f, 0, 0.019f, 0.15f, 0.028f, Tone.Paint, false);
                Box(model, $"Handle post {side}", side * 0.077f, 0.259f, 0, 0.019f, 0.089f, 0.031f, Tone.Metal, false);
            }
            Box(model, "Handle grip", 0, 0.302f, 0, 0.17f, 0.022f, 0.04f, Tone.Metal, false);
            var target = root.AddComponent<BoxCollider>();
            target.center = new Vector3(0, 0.15f, 0);
            target.size = new Vector3(0.27f, 0.30f, 0.28f);
            target.isTrigger = true;
            return SavePrefab(root, path);
        }

        private static GameObject SavePrefab(GameObject root, string path)
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab != null ? prefab : throw new InvalidOperationException($"Could not save prefab {path}.");
        }

        private void Street(Transform parent, Area area)
        {
            Surface(parent, "Asphalt street", area, 0.026f, Tone.Asphalt);
            var center = ((float)area.MinZ + (float)area.MaxZ) / 2;
            for (var x = (float)area.MinX + 1.6f; x < area.MaxX - 0.8; x += 3.8f)
                Box(parent, "Road marking", x, 0.028f, center, 1.9f, 0.003f, 0.10f, Tone.Trim, false);
        }

        private void Sidewalk(Transform parent, Area area)
        {
            Surface(parent, "Concrete sidewalk", area, 0.10f, Tone.Stone);
            var center = ((float)area.MinZ + (float)area.MaxZ) / 2;
            var depth = (float)(area.MaxZ - area.MinZ);
            for (var x = (float)area.MinX + 1.2f; x < area.MaxX; x += 1.2f)
                Box(parent, "Expansion joint", x, 0.101f, center, 0.015f, 0.003f, depth, Tone.Asphalt, false);
            Box(parent, "Curb edge", ((float)area.MinX + (float)area.MaxX) / 2, 0.057f, (float)area.MinZ + 0.055f,
                (float)(area.MaxX - area.MinX), 0.100f, 0.11f, Tone.Trim, false);
        }

        private void Path(Transform parent, Area area)
        {
            Surface(parent, "Courtyard paving", area, 0.077f, Tone.Paving);
            var width = (float)(area.MaxX - area.MinX);
            var center = ((float)area.MinX + (float)area.MaxX) / 2;
            for (var z = (float)area.MinZ + 0.58f; z < area.MaxZ; z += 0.58f)
                Box(parent, "Paver seam", center, 0.078f, z, width - 0.05f, 0.002f, 0.014f, Tone.Stone, false);
        }

        private void Enclosure(Transform parent, Area area)
        {
            var left = (float)area.MinX + 0.13f;
            var right = (float)area.MaxX - 0.13f;
            var center = (left + right) / 2;
            var width = right - left;
            var north = (float)area.MaxZ - 0.13f;
            var south = (float)area.MinZ + 0.13f;
            var mid = (south + north) / 2;
            foreach (var x in new[] { left, right })
            {
                Box(parent, "Side garden wall", x, 0.79f, mid, 0.22f, 1.58f, north - south, Tone.Brick);
                Box(parent, "Side coping", x, 1.61f, mid, 0.32f, 0.09f, north - south, Tone.Trim, false);
                for (var z = south + 1; z < north; z += 3.8f)
                    Box(parent, "Wall column", x, 0.93f, z, 0.40f, 1.86f, 0.42f, Tone.Stone);
            }
            Box(parent, "North garden wall", center, 0.92f, north, width, 1.84f, 0.25f, Tone.Brick);
            Box(parent, "North coping", center, 1.87f, north, width, 0.10f, 0.38f, Tone.Trim, false);
            Box(parent, "Street boundary", center, 0.30f, south, width, 0.60f, 0.23f, Tone.Stone);
            Box(parent, "Street railing", center, 0.92f, south, width, 0.075f, 0.085f, Tone.Metal);
            for (var post = left + 1; post < right; post += 1.6f)
                Box(parent, "Railing post", post, 0.68f, south, 0.075f, 0.62f, 0.075f, Tone.Metal);
        }

        private void Workshop(Area plan, DoorDefinition door)
        {
            var root = new GameObject("Workshop shell").transform;
            var left = (float)plan.MinX;
            var right = (float)plan.MaxX;
            var front = (float)plan.MinZ;
            var back = (float)plan.MaxZ;
            var width = right - left;
            var depth = back - front;
            var x = (left + right) / 2;
            var z = (front + back) / 2;
            const float wall = 0.26f;
            const float top = 2.91f;
            Surface(root, "Workshop floor", plan, 0.034f, Tone.Paving);
            Box(root, "Left wall", left + wall / 2, top / 2, z, wall, top, depth, Tone.Plaster);
            Box(root, "Right wall", right - wall / 2, top / 2, z, wall, top, depth, Tone.Plaster);
            Box(root, "Back wall", x, top / 2, back - wall / 2, width, top, wall, Tone.Plaster);
            var hinge = (float)door.Hinge.X;
            var latch = hinge + door.Width;
            FrontWing(root, left + wall, hinge, front, top, wall);
            FrontWing(root, latch, right - wall, front, top, wall);
            Box(root, "Door lintel", (hinge + latch) / 2, (top + door.Height) / 2, front + wall / 2,
                door.Width, top - door.Height, wall, Tone.Plaster);
            Box(root, "Lintel trim", (hinge + latch) / 2, door.Height + 0.05f, front - 0.015f,
                door.Width + 0.21f, 0.105f, 0.14f, Tone.Trim, false);
            for (var side = -1; side <= 1; side += 2)
                Box(root, "Door casing", (hinge + latch) / 2 + side * (door.Width / 2 + 0.037f), door.Height / 2,
                    front - 0.025f, 0.075f, door.Height, 0.14f, Tone.Trim, false);
            Box(root, "Door threshold", (hinge + latch) / 2, 0.035f, front, door.Width, 0.072f, 0.39f, Tone.Stone);
            Box(root, "Roof overhang", x, top + 0.08f, z, width + 0.69f, 0.16f, depth + 0.65f, Tone.Metal);
            Box(root, "Roof cap", x, top + 0.21f, z, width + 0.44f, 0.14f, depth + 0.40f, Tone.Door, false);
            Box(root, "Interior work rail", x, 1.25f, back - wall - 0.025f, width - 0.8f, 0.14f, 0.07f, Tone.Wood, false);
            Box(root, "Interior floor trim", x, 0.14f, back - wall - 0.027f, width - 0.50f, 0.22f, 0.054f, Tone.Brick, false);
            Box(root, "Entry canopy", (hinge + latch) / 2, 2.52f, front - 0.46f, door.Width + 1.08f, 0.10f, 1.04f, Tone.Door, false);
            Box(root, "Entry light", (hinge + latch) / 2, 2.44f, front - 0.15f, 0.37f, 0.068f, 0.24f, Tone.Light, false);
        }

        private void FrontWing(Transform root, float left, float right, float front, float top, float thickness)
        {
            var width = right - left;
            var center = (left + right) / 2;
            if (width < 1.4f)
            {
                Box(root, "Facade wall", center, top / 2, front + thickness / 2, width, top, thickness, Tone.Plaster);
                return;
            }
            Box(root, "Window lower wall", center, 0.46f, front + thickness / 2, width, 0.92f, thickness, Tone.Plaster);
            Box(root, "Facade plinth", center, 0.24f, front - 0.027f, width, 0.44f, 0.065f, Tone.Brick, false);
            Box(root, "Window upper wall", center, (top + 2.14f) / 2, front + thickness / 2, width, top - 2.14f, thickness, Tone.Plaster);
            var pane = Mathf.Min(width - 0.75f, 1.85f);
            var flank = (width - pane) / 2;
            for (var side = -1; side <= 1; side += 2)
                Box(root, "Window pier", center + side * (pane + flank) / 2, 1.53f, front + thickness / 2,
                    flank, 1.22f, thickness, Tone.Plaster);
            Box(root, "Window frame", center, 1.53f, front + thickness / 2, pane, 1.22f, thickness * 0.75f, Tone.Door);
            for (var side = -1; side <= 1; side += 2)
                for (var leaf = -1; leaf <= 1; leaf += 2)
                    Box(root, "Window pane", center + leaf * pane / 4, 1.53f, front + thickness / 2 + side * thickness * 0.39f,
                        pane / 2 - 0.09f, 1.06f, 0.015f, Tone.Glass, false);
            Box(root, "Window stone sill", center, 0.91f, front + 0.04f, pane + 0.15f, 0.105f, thickness + 0.28f, Tone.Trim, false);
            Box(root, "Facade fascia", center, top - 0.17f, front - 0.031f, width, 0.13f, 0.08f, Tone.Trim, false);
        }

        private void BuildProp(Transform yard, PropDefinition definition)
        {
            var prop = Group(yard, definition.Kind.ToString(), DistrictAuthoring.Vector(definition.Position));
            prop.localRotation = Quaternion.Euler(0, definition.Yaw, 0);
            switch (definition.Kind)
            {
                case PropKind.Tree:
                    Shape(prop, "Trunk", PrimitiveType.Cylinder, 0, 1.32f, 0, 0.28f, 1.31f, 0.31f, Tone.Wood);
                    Shape(prop, "Fork", PrimitiveType.Cylinder, -0.18f, 2.13f, 0, 0.14f, 0.75f, 0.14f, Tone.Wood, false)
                        .transform.localRotation = Quaternion.Euler(0, 0, 27);
                    Shape(prop, "Main crown", PrimitiveType.Sphere, 0.27f, 3.35f, 0, 2.78f, 2.85f, 2.45f, Tone.LeavesLight, false);
                    Shape(prop, "Low crown", PrimitiveType.Sphere, -0.73f, 2.73f, 0.14f, 2.15f, 2.15f, 1.85f, Tone.Leaves, false);
                    Shape(prop, "Outer crown", PrimitiveType.Sphere, 0.63f, 2.89f, -0.66f, 2.02f, 1.87f, 1.85f, Tone.Leaves, false);
                    break;
                case PropKind.Planter:
                    Box(prop, "Masonry planter", 0, 0.22f, 0, 1.60f, 0.44f, 0.71f, Tone.Brick);
                    Box(prop, "Planter stone rim", 0, 0.44f, 0, 1.66f, 0.08f, 0.77f, Tone.Trim, false);
                    Box(prop, "Potting soil", 0, 0.483f, 0, 1.45f, 0.025f, 0.56f, Tone.Soil, false);
                    for (var plant = -2; plant <= 2; plant++)
                        Shape(prop, "Low shrub", PrimitiveType.Sphere, plant * 0.26f, 0.58f, 0.08f * (plant % 2),
                            0.39f, 0.41f, 0.44f, plant % 2 == 0 ? Tone.LeavesLight : Tone.Leaves, false);
                    break;
                case PropKind.Bench:
                    Box(prop, "Seat", 0, 0.44f, 0, 1.65f, 0.105f, 0.45f, Tone.Wood);
                    for (var side = -1; side <= 1; side += 2)
                    {
                        Box(prop, "Cast support", side * 0.64f, 0.22f, 0, 0.11f, 0.44f, 0.37f, Tone.Metal);
                        Box(prop, "Back support", side * 0.64f, 0.68f, 0.20f, 0.055f, 0.55f, 0.07f, Tone.Metal, false);
                    }
                    for (var slat = 0; slat < 3; slat++)
                        Box(prop, "Back slat", 0, 0.62f + slat * 0.113f, 0.22f, 1.65f, 0.083f, 0.044f, Tone.Wood, false);
                    break;
                case PropKind.Workbench:
                    Box(prop, "Workbench top", 0, 0.945f, 0, 2.30f, 0.11f, 0.78f, Tone.Wood);
                    Box(prop, "Workbench shelf", 0, 0.27f, 0, 2.15f, 0.075f, 0.69f, Tone.Wood);
                    for (var side = -1; side <= 1; side += 2)
                        for (var foot = -1; foot <= 1; foot += 2)
                            Box(prop, "Workbench leg", side * 0.99f, 0.46f, foot * 0.29f, 0.115f, 0.92f, 0.115f, Tone.Metal);
                    Box(prop, "Parts tray", -0.73f, 1.035f, 0.04f, 0.47f, 0.07f, 0.28f, Tone.Door, false);
                    Box(prop, "Closed toolbox", 0.74f, 1.11f, 0.11f, 0.46f, 0.22f, 0.32f, Tone.Brick, false);
                    Box(prop, "Toolbox handle", 0.74f, 1.24f, 0.11f, 0.18f, 0.052f, 0.05f, Tone.Metal, false);
                    break;
                case PropKind.Shelf:
                    for (var shelf = 0; shelf < 4; shelf++)
                    {
                        var height = 0.25f + shelf * 0.49f;
                        Box(prop, "Shelf board", 0, height, 0, 1.65f, 0.07f, 0.46f, Tone.Wood);
                        Box(prop, "Storage bin", (shelf % 2 == 0 ? -1 : 1) * 0.41f, height + 0.16f, 0.025f,
                            0.48f, 0.25f, 0.34f, shelf % 2 == 0 ? Tone.Brick : Tone.Door, false);
                    }
                    for (var side = -1; side <= 1; side += 2)
                        for (var foot = -1; foot <= 1; foot += 2)
                            Box(prop, "Shelf upright", side * 0.80f, 0.96f, foot * 0.20f, 0.065f, 1.92f, 0.065f, Tone.Metal);
                    break;
                case PropKind.Crate:
                    Box(prop, "Slatted wood crate", 0, 0.23f, 0, 0.67f, 0.46f, 0.58f, Tone.Wood);
                    for (var rim = 0.09f; rim <= 0.42f; rim += 0.32f)
                        Box(prop, "Crate band", 0, rim, 0, 0.70f, 0.049f, 0.61f, Tone.Trim, false);
                    break;
                case PropKind.Streetlight:
                    Shape(prop, "Streetlight base", PrimitiveType.Cylinder, 0, 0.085f, 0, 0.28f, 0.085f, 0.28f, Tone.Stone);
                    Shape(prop, "Streetlight pole", PrimitiveType.Cylinder, 0, 1.91f, 0, 0.106f, 1.90f, 0.106f, Tone.Metal);
                    Box(prop, "Streetlight arm", 0, 3.78f, 0.28f, 0.085f, 0.081f, 0.61f, Tone.Metal, false);
                    Box(prop, "Streetlight shade", 0, 3.74f, 0.63f, 0.28f, 0.105f, 0.38f, Tone.Metal, false);
                    Box(prop, "Streetlight diffuser", 0, 3.68f, 0.63f, 0.22f, 0.022f, 0.29f, Tone.Light, false);
                    break;
            }
        }

        private void Lighting(Area workshop)
        {
            var sun = new GameObject("Morning sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(42, -33, 0);
            sun.color = new Color32(255, 236, 207, 255);
            sun.intensity = 1.7f;
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color32(158, 171, 178, 255);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color32(169, 194, 206, 255);
            RenderSettings.fogStartDistance = 33;
            RenderSettings.fogEndDistance = 74;

            var x = (float)(workshop.MinX + workshop.MaxX) / 2;
            var z = (float)(workshop.MinZ + workshop.MaxZ) / 2;
            var ceiling = new GameObject("Workshop lighting").transform;
            for (var side = -1; side <= 1; side += 2)
            {
                Box(ceiling, "Ceiling fixture", x + side * 2.1f, 2.78f, z, 0.24f, 0.11f, 1.20f, Tone.Light, false);
                var lamp = new GameObject("Warm workshop light").AddComponent<Light>();
                lamp.transform.SetParent(ceiling, false);
                lamp.transform.position = new Vector3(x + side * 2.1f, 2.52f, z);
                lamp.type = LightType.Point;
                lamp.color = new Color32(255, 223, 177, 255);
                lamp.intensity = 3.0f;
                lamp.range = 7;
            }
        }

        private void Surface(Transform root, string name, Area area, float height, Tone tone)
        {
            var x = (float)(area.MinX + area.MaxX) / 2;
            var z = (float)(area.MinZ + area.MaxZ) / 2;
            Box(root, name, x, height - 0.09f, z, (float)(area.MaxX - area.MinX), 0.18f, (float)(area.MaxZ - area.MinZ), tone);
        }

        private GameObject Box(Transform parent, string name, float x, float y, float z, float w, float h, float d, Tone tone, bool solid = true) =>
            Shape(parent, name, PrimitiveType.Cube, x, y, z, w, h, d, tone, solid);

        private GameObject Shape(Transform parent, string name, PrimitiveType type, float x, float y, float z,
            float width, float height, float depth, Tone tone, bool solid = true)
        {
            var item = GameObject.CreatePrimitive(type);
            item.name = name;
            item.transform.SetParent(parent, false);
            item.transform.localPosition = new Vector3(x, y, z);
            item.transform.localScale = new Vector3(width, height, depth);
            item.GetComponent<MeshRenderer>().sharedMaterial = palette[(int)tone];
            if (!solid) UnityEngine.Object.DestroyImmediate(item.GetComponent<Collider>());
            return item;
        }

        private static Transform Group(Transform parent, string name, Vector3 localPosition)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = localPosition;
            return child;
        }
    }
}
