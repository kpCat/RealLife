using System;
using System.Collections.Generic;
using RealLife.Simulation;
using UnityEngine;

namespace RealLife.Runtime
{
    // The mesh is authored in standing pose. Each variant reuses the same joints and one texture/material.
    internal sealed class ResidentModel : IDisposable
    {
        private enum Joint { Hips, Chest, Head, LeftLeg, LeftShin, LeftFoot, RightLeg, RightShin, RightFoot, LeftArm, LeftForearm, RightArm, RightForearm }
        private enum Dye { Skin, Shadow, Hair, Knit, Cuff, Trousers, Shoes, Sole, Eyes, Iris, Lips }
        private static readonly int[] Parents = { -1, 0, 1, 0, 3, 4, 0, 6, 7, 1, 9, 1, 11 };
        private static readonly Vector3[] Joints =
        {
            new Vector3(0, .95f, 0), new Vector3(0, 1.18f, 0), new Vector3(0, 1.46f, 0),
            new Vector3(-.104f, .93f, 0), new Vector3(-.104f, .52f, .008f), new Vector3(-.104f, .105f, 0),
            new Vector3(.104f, .93f, 0), new Vector3(.104f, .52f, .008f), new Vector3(.104f, .105f, 0),
            new Vector3(-.201f, 1.386f, 0), new Vector3(-.237f, 1.145f, 0),
            new Vector3(.201f, 1.386f, 0), new Vector3(.237f, 1.145f, 0)
        };
        private readonly Mesh mesh;
        private readonly Material material;
        private readonly Texture2D atlas;
        private readonly Vector3 proportions;

        internal static int VariantOf(int id) => (int)(unchecked((uint)id) % 12);

        public ResidentModel(int variant, Material surface)
        {
            proportions = new Vector3(.91f + (variant % 4) * .066f, .95f + (variant * 5 % 7) * .019f, 1);
            var builder = new Form();
            MakeClothing(builder, variant);
            MakeFace(builder, variant);
            mesh = builder.MakeMesh();
            var dyes = Colorway(variant);
            atlas = new Texture2D(dyes.Length, 1, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Resident fabric and skin"
            };
            atlas.SetPixels32(dyes);
            atlas.Apply(false, true);
            material = new Material(surface)
            {
                name = "Resident fabric", color = Color.white, mainTexture = atlas
            };
            material.SetFloat("_Smoothness", .23f);
        }

        public void Dispose()
        {
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (material != null) UnityEngine.Object.Destroy(material);
            if (atlas != null) UnityEngine.Object.Destroy(atlas);
        }

        private static Color32[] Colorway(int variant)
        {
            Color32 skin = (variant % 4) switch
            {
                0 => new Color32(221, 171, 139, 255),
                1 => new Color32(186, 126, 93, 255),
                2 => new Color32(157, 104, 77, 255),
                _ => new Color32(234, 186, 156, 255)
            };
            Color32 hair = (variant % 6) switch
            {
                0 => new Color32(48, 36, 32, 255),
                1 => new Color32(95, 61, 45, 255),
                2 => new Color32(40, 38, 36, 255),
                3 => new Color32(158, 121, 75, 255),
                4 => new Color32(92, 87, 82, 255),
                _ => new Color32(118, 71, 52, 255)
            };
            Color32 knit = (variant * 5 % 7) switch
            {
                0 => new Color32(70, 99, 111, 255),
                1 => new Color32(138, 82, 70, 255),
                2 => new Color32(141, 127, 101, 255),
                3 => new Color32(76, 103, 82, 255),
                4 => new Color32(109, 90, 112, 255),
                5 => new Color32(114, 108, 93, 255),
                _ => new Color32(170, 141, 72, 255)
            };
            Color32 trousers = (variant % 3) switch
            {
                0 => new Color32(57, 62, 69, 255),
                1 => new Color32(66, 79, 90, 255),
                _ => new Color32(90, 79, 64, 255)
            };
            return new Color32[]
            {
                skin, Color.Lerp(skin, new Color32(144, 76, 73, 255), .28f), hair,
                knit, Color.Lerp(knit, Color.black, .28f), trousers, new Color32(54, 49, 45, 255),
                new Color32(32, 33, 31, 255), new Color32(226, 218, 204, 255),
                new Color32(62, 59, 48, 255), Color.Lerp(skin, new Color32(127, 57, 64, 255), .45f)
            };
        }

        private static void MakeClothing(Form model, int variant)
        {
            var knit = variant % 3 == 1 ? .013f : 0;
            model.Loft(Joint.Hips, Dye.Trousers, new Ring(.81f, .142f, .092f),
                new Ring(.9f, .172f, .112f), new Ring(.99f, .158f, .102f));
            model.Loft(Joint.Chest, Dye.Knit, new Ring(.906f, .167f, .108f, blend: Joint.Hips, weight: 1),
                new Ring(.977f, .174f, .114f, blend: Joint.Hips, weight: .85f),
                new Ring(1.112f, .158f, .109f, blend: Joint.Hips, weight: .32f),
                new Ring(1.277f, .181f, .111f + knit),
                new Ring(1.373f, .202f, .105f),
                new Ring(1.429f, .152f, .073f),
                new Ring(1.448f, .060f, .056f));
            model.Loft(Joint.Hips, Dye.Cuff, new Ring(.905f, .165f, .108f),
                new Ring(.941f, .170f, .111f));
            model.Loft(Joint.Chest, Dye.Cuff, new Ring(1.423f, .073f, .062f),
                new Ring(1.451f, .065f, .053f));
            if (variant % 3 == 1) // A raised jacket zip and collar.
            {
                model.Loft(Joint.Chest, Dye.Cuff, new Ring(1.045f, .006f, .006f, z: .111f),
                    new Ring(1.272f, .006f, .006f, z: .126f), new Ring(1.424f, .006f, .006f, z: .075f));
                model.Loft(Joint.Chest, Dye.Knit, new Ring(1.443f, .065f, .054f),
                    new Ring(1.485f, .061f, .054f));
            }
            for (var side = -1; side <= 1; side += 2)
            {
                var leg = side < 0 ? Joint.LeftLeg : Joint.RightLeg;
                var shin = side < 0 ? Joint.LeftShin : Joint.RightShin;
                var foot = side < 0 ? Joint.LeftFoot : Joint.RightFoot;
                var arm = side < 0 ? Joint.LeftArm : Joint.RightArm;
                var forearm = side < 0 ? Joint.LeftForearm : Joint.RightForearm;
                float x = side * .104f;
                model.Loft(leg, Dye.Trousers, new Ring(.105f, .054f, .054f, x, blend: shin, weight: 1),
                    new Ring(.18f, .055f, .057f, x, blend: shin, weight: 1),
                    new Ring(.34f, .063f, .070f, x, -.012f, shin, 1),
                    new Ring(.503f, .067f, .067f, x, .012f, shin, .66f),
                    new Ring(.548f, .069f, .067f, x, .009f, shin, .34f),
                    new Ring(.737f, .083f, .090f, x, -.005f),
                    new Ring(.897f, .089f, .099f, x * .9f, blend: Joint.Hips, weight: .42f),
                    new Ring(.939f, .077f, .089f, x * .9f, blend: Joint.Hips, weight: .7f));
                model.Loft(foot, Dye.Shoes, new Ring(.019f, .061f, .123f, x, .050f),
                    new Ring(.048f, .058f, .121f, x, .050f),
                    new Ring(.073f, .056f, .092f, x, .029f),
                    new Ring(.116f, .051f, .056f, x, .002f));
                model.Loft(foot, Dye.Sole, new Ring(.006f, .060f, .122f, x, .05f),
                    new Ring(.026f, .062f, .124f, x, .05f));

                model.Loft(arm, Dye.Knit, new Ring(.95f, .038f, .036f, side * .258f, blend: forearm, weight: 1),
                    new Ring(1.065f, .045f, .047f, side * .253f, blend: forearm, weight: 1),
                    new Ring(1.138f, .048f, .050f, side * .238f, blend: forearm, weight: .6f),
                    new Ring(1.177f, .051f, .052f, side * .231f, blend: forearm, weight: .22f),
                    new Ring(1.326f, .061f, .064f, side * .204f),
                    new Ring(1.399f, .058f, .059f, side * .191f),
                    new Ring(1.421f, .034f, .043f, side * .191f));
                model.Loft(forearm, Dye.Cuff, new Ring(.934f, .037f, .034f, side * .259f),
                    new Ring(.966f, .039f, .036f, side * .257f));
                model.Loft(forearm, Dye.Skin, new Ring(.826f, .021f, .014f, side * .258f, .011f),
                    new Ring(.852f, .030f, .022f, side * .262f, .008f),
                    new Ring(.902f, .031f, .023f, side * .263f, .004f),
                    new Ring(.947f, .028f, .027f, side * .259f));
                model.Oval(forearm, Dye.Skin, new Vector3(side * .237f, .884f, .018f),
                    new Vector3(.016f, .029f, .015f));
            }
        }

        private static void MakeFace(Form model, int variant)
        {
            model.Loft(Joint.Head, Dye.Skin, new Ring(1.425f, .050f, .046f),
                new Ring(1.506f, .053f, .046f, z: .006f));
            model.Loft(Joint.Head, Dye.Skin, new Ring(1.487f, .034f, .041f, z: .020f),
                new Ring(1.520f, .067f, .067f, z: .011f),
                new Ring(1.558f, .084f, .076f, z: .008f),
                new Ring(1.604f, .092f, .082f),
                new Ring(1.650f, .088f, .080f, z: -.004f),
                new Ring(1.694f, .067f, .067f, z: -.010f),
                new Ring(1.718f, .022f, .027f, z: -.011f));
            model.Loft(Joint.Head, Dye.Hair,
                new Ring(1.635f + .009f * (variant % 3), .089f, .064f, z: -.023f),
                new Ring(1.671f, .092f, .085f, z: -.007f),
                new Ring(1.707f + .004f * (variant % 3), .069f, .069f, z: -.012f),
                new Ring(1.727f + .009f * (variant % 3), .026f, .032f, x: .013f, z: -.011f));
            model.Oval(Joint.Head, Dye.Hair, new Vector3(variant % 2 == 0 ? -.027f : .026f, 1.681f, .043f),
                new Vector3(.062f, .029f, .045f));
            if (variant % 4 == 3)
                model.Oval(Joint.Head, Dye.Hair, new Vector3(0, 1.673f, -.083f), new Vector3(.043f, .041f, .034f));
            model.Loft(Joint.Head, Dye.Skin, new Ring(1.567f, .014f, .012f, z: .083f),
                new Ring(1.582f, .019f, .024f, z: .084f),
                new Ring(1.609f, .010f, .018f, z: .080f), new Ring(1.626f, .008f, .010f, z: .080f));
            model.Oval(Joint.Head, Dye.Lips, new Vector3(0, 1.545f, .082f), new Vector3(.025f, .0038f, .006f));
            for (var side = -1; side <= 1; side += 2)
            {
                model.Oval(Joint.Head, Dye.Skin, new Vector3(side * .086f, 1.593f, -.003f),
                    new Vector3(.018f, .031f, .017f));
                model.Oval(Joint.Head, Dye.Shadow, new Vector3(side * .092f, 1.596f, .007f),
                    new Vector3(.010f, .019f, .009f));
                model.Oval(Joint.Head, Dye.Shadow, new Vector3(side * .034f, 1.609f, .075f),
                    new Vector3(.022f, .010f, .009f));
                model.Oval(Joint.Head, Dye.Eyes, new Vector3(side * .035f, 1.612f, .080f),
                    new Vector3(.016f, .0058f, .0062f));
                model.Oval(Joint.Head, Dye.Iris, new Vector3(side * .034f, 1.612f, .0855f),
                    new Vector3(.0052f, .0053f, .0026f));
                model.Oval(Joint.Head, Dye.Hair, new Vector3(side * .036f, 1.628f, .076f),
                    new Vector3(.020f, .003f, .006f));
            }
        }

        internal sealed class Body
        {
            private readonly GameObject root;
            private readonly SkinnedMeshRenderer renderer;
            private readonly Transform[] joints = new Transform[Joints.Length];
            private float phase, breathing, effort, rest;
            public readonly CapsuleCollider FocusCollider;
            public ResidentViewData Data { get; private set; }

            public Body(Transform parent)
            {
                root = new GameObject("Resident");
                root.transform.SetParent(parent, false);
                for (var i = 0; i < joints.Length; i++)
                {
                    var joint = new GameObject(((Joint)i).ToString()).transform;
                    var predecessor = Parents[i];
                    joint.SetParent(predecessor < 0 ? root.transform : joints[predecessor], false);
                    joint.localPosition = predecessor < 0 ? Joints[i] : Joints[i] - Joints[predecessor];
                    joints[i] = joint;
                }
                renderer = root.AddComponent<SkinnedMeshRenderer>();
                renderer.bones = joints;
                renderer.rootBone = root.transform;
                renderer.localBounds = new Bounds(new Vector3(0, .88f, 0), new Vector3(1.25f, 2.25f, 1.25f));
                FocusCollider = root.AddComponent<CapsuleCollider>();
                FocusCollider.isTrigger = true;
                FocusCollider.radius = .24f;
                FocusCollider.height = 1.74f;
                FocusCollider.center = Vector3.up * .87f;
                Hide();
            }

            public void Show(ResidentViewData data, ResidentModel model)
            {
                Data = data;
                root.name = data.Name;
                root.transform.localScale = model.proportions;
                root.transform.SetPositionAndRotation(FirstPersonMotor.ToVector(data.Position), Quaternion.Euler(0, data.Heading, 0));
                renderer.sharedMesh = model.mesh;
                renderer.sharedMaterial = model.material;
                phase = ResidentModel.VariantOf(data.AppearanceId) * .61f;
                breathing = phase;
                effort = data.Walking ? 1 : 0;
                rest = data.Resting ? 1 : 0;
                Pose();
                root.SetActive(true);
            }

            public void Update(ResidentViewData data, float seconds)
            {
                Data = data;
                var current = root.transform.position;
                var target = FirstPersonMotor.ToVector(data.Position);
                var smoothing = 1 - Mathf.Exp(-20 * seconds);
                var next = seconds <= 0 || (target - current).sqrMagnitude > 1
                    ? target : Vector3.Lerp(current, target, smoothing);
                var yaw = Quaternion.Euler(0, data.Heading, 0);
                root.transform.SetPositionAndRotation(next, Quaternion.RotateTowards(root.transform.rotation, yaw, 450 * seconds));
                phase = Mathf.Repeat(phase + Vector3.Distance(current, next) * 4.6f, 2 * Mathf.PI);
                breathing = Mathf.Repeat(breathing + 1.78f * seconds, 2 * Mathf.PI);
                effort = Mathf.MoveTowards(effort, data.Walking ? 1 : 0, 7 * seconds);
                rest = Mathf.MoveTowards(rest, data.Resting ? 1 : 0, 2 * seconds);
                Pose();
            }

            private void Pose()
            {
                var wave = Mathf.Sin(phase);
                var breath = Mathf.Sin(breathing);
                var stride = wave * effort;
                var step = (1 - Mathf.Cos(2 * phase)) * effort;
                joints[0].localPosition = Joints[0] + new Vector3(stride * .013f, step * .011f - rest * .025f, 0);
                Set(Joint.Hips, 4 * rest, stride * 3, stride * 2);
                Set(Joint.Chest, breath * (1.05f + .8f * rest) + rest * 5, -stride * 7, -stride * 2);
                Set(Joint.Head, -effort + rest * 11, breath * (1 - effort) * 3.5f, 3 * rest);
                Set(Joint.LeftLeg, stride * 27 - rest * 8);
                Set(Joint.RightLeg, -stride * 27 - rest * 8);
                Set(Joint.LeftShin, (Mathf.Max(0, -wave) * 36 + step * 2) * effort + 12 * rest);
                Set(Joint.RightShin, (Mathf.Max(0, wave) * 36 + step * 2) * effort + 12 * rest);
                Set(Joint.LeftFoot, Mathf.Max(0, -stride) * -13);
                Set(Joint.RightFoot, Mathf.Max(0, stride) * -13);
                Set(Joint.LeftArm, -stride * 22 - rest * 12 + breath, 0, -4 - 4 * rest);
                Set(Joint.RightArm, stride * 22 - rest * 10 + breath, 0, 4 + 4 * rest);
                Set(Joint.LeftForearm, -6 - Mathf.Max(0, stride) * 18 - rest * 28);
                Set(Joint.RightForearm, -6 - Mathf.Max(0, -stride) * 18 - rest * 34);
            }

            private void Set(Joint joint, float pitch, float yaw = 0, float roll = 0) =>
                joints[(int)joint].localRotation = Quaternion.Euler(pitch, yaw, roll);

            public void Hide()
            {
                root.SetActive(false);
                Data = default;
            }
        }

        private readonly struct Ring
        {
            public readonly float Y, Rx, Rz, X, Z, Weight;
            public readonly Joint Blend;

            public Ring(float y, float rx, float rz, float x = 0, float z = 0, Joint blend = Joint.Hips, float weight = 0) =>
                (Y, Rx, Rz, X, Z, Blend, Weight) = (y, rx, rz, x, z, blend, weight);
        }

        private sealed class Form
        {
            private const int Segments = 12, Dyes = 11;
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<int> triangles = new List<int>();
            private readonly List<Vector2> uv = new List<Vector2>();
            private readonly List<BoneWeight> weights = new List<BoneWeight>();

            public void Oval(Joint joint, Dye dye, Vector3 center, Vector3 radius) =>
                Loft(joint, dye, new Ring(center.y - .99f * radius.y, .14f * radius.x, .14f * radius.z, center.x, center.z),
                    new Ring(center.y - .66f * radius.y, .75f * radius.x, .75f * radius.z, center.x, center.z),
                    new Ring(center.y, radius.x, radius.z, center.x, center.z),
                    new Ring(center.y + .66f * radius.y, .75f * radius.x, .75f * radius.z, center.x, center.z),
                    new Ring(center.y + .99f * radius.y, .14f * radius.x, .14f * radius.z, center.x, center.z));

            public void Loft(Joint joint, Dye dye, params Ring[] rings)
            {
                var first = vertices.Count;
                for (var level = 0; level < rings.Length; level++)
                {
                    var ring = rings[level];
                    for (var s = 0; s < Segments; s++)
                    {
                        var angle = 2 * Mathf.PI * s / Segments;
                        Add(ring, joint, dye, ring.Rx * Mathf.Sin(angle), ring.Rz * Mathf.Cos(angle));
                        if (level == 0) continue;
                        var bottom = first + (level - 1) * Segments;
                        var next = (s + 1) % Segments;
                        Triangle(bottom + s, bottom + next, bottom + s + Segments);
                        Triangle(bottom + next, bottom + next + Segments, bottom + s + Segments);
                    }
                }
                var last = first + (rings.Length - 1) * Segments;
                var lower = Add(rings[0], joint, dye);
                var upper = Add(rings[rings.Length - 1], joint, dye);
                for (var i = 0; i < Segments; i++)
                {
                    var next = (i + 1) % Segments;
                    Triangle(lower, first + next, first + i);
                    Triangle(upper, last + i, last + next);
                }
            }

            private int Add(Ring ring, Joint joint, Dye dye, float x = 0, float z = 0)
            {
                var index = vertices.Count;
                vertices.Add(new Vector3(ring.X + x, ring.Y, ring.Z + z));
                uv.Add(new Vector2(((int)dye + .5f) / Dyes, .5f));
                var blend = ring.Weight;
                var first = joint;
                var second = ring.Blend;
                // Unity expects the dominant influence to be first.
                if (blend > .5f) (first, second, blend) = (second, first, 1 - blend);
                weights.Add(new BoneWeight
                {
                    boneIndex0 = (int)first, boneIndex1 = (int)second, weight0 = 1 - blend, weight1 = blend
                });
                return index;
            }

            private void Triangle(int a, int b, int c)
            {
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
            }

            public Mesh MakeMesh()
            {
                var bindposes = new Matrix4x4[Joints.Length];
                for (var i = 0; i < Joints.Length; i++) bindposes[i] = Matrix4x4.Translate(-Joints[i]);
                var mesh = new Mesh { name = "Resident clothing and figure", bindposes = bindposes, subMeshCount = 1 };
                mesh.SetVertices(vertices);
                mesh.SetUVs(0, uv);
                mesh.SetTriangles(triangles, 0);
                mesh.boneWeights = weights.ToArray();
                mesh.RecalculateNormals();
                return mesh;
            }
        }
    }
}
