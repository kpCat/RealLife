using System;
using System.IO;
using RealLife.Runtime;
using RealLife.Simulation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace RealLife.Editor
{
    public static class DistrictAuthoring
    {
        private const string Root = "Assets/RealLife";
        private const string Generated = Root + "/Generated";
        public const string ScenePath = Generated + "/Sadovaya.unity";
        private const string DoorPath = Generated + "/WorkshopDoor.prefab";
        private const string LanternPath = Generated + "/WorkshopLantern.prefab";

        [MenuItem("RealLife/Generate Sadovaya District")]
        public static void Generate()
        {
            if (!EditorApplication.isPlaying && !Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var map = RequireAsset<TextAsset>(Root + "/Content/Sadovaya.json");
            var district = JsonUtility.FromJson<DistrictDefinition>(map.text);
            district.Validate();
            CreateFolder(Root, "Generated");
            var renderer = Asset<UniversalRendererData>(Generated + "/DistrictRenderer.asset");
            ResourceReloader.ReloadAllNullIn(renderer, UniversalRenderPipelineAsset.packagePath);
            var pipelinePath = Generated + "/DistrictURP.asset";
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                pipeline.msaaSampleCount = 4;
                pipeline.shadowDistance = 45;
                pipeline.shadowCascadeCount = 2;
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }
            if (GraphicsSettings.defaultRenderPipeline != pipeline) GraphicsSettings.defaultRenderPipeline = pipeline;
            if (QualitySettings.renderPipeline != null) QualitySettings.renderPipeline = null;

            var stamp = Hash128.Compute(map.text + File.ReadAllText(Root + "/Editor/DistrictAuthoring.cs") +
                File.ReadAllText(Root + "/Editor/DistrictGeometry.cs")).ToString();
            var importer = AssetImporter.GetAtPath(ScenePath);
            if (importer != null && importer.userData == stamp &&
                AssetDatabase.LoadAssetAtPath<GameObject>(DoorPath) != null &&
                AssetDatabase.LoadAssetAtPath<GameObject>(LanternPath) != null)
            {
                RegisterScene();
                AssetDatabase.SaveAssets();
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var geometry = new DistrictGeometry(Generated);
            geometry.Build(district);
            var door = (GameObject)PrefabUtility.InstantiatePrefab(geometry.SaveDoor(district.Door, DoorPath));
            door.transform.position = Vector(district.Door.Hinge);
            var lantern = (GameObject)PrefabUtility.InstantiatePrefab(geometry.SaveLantern(LanternPath));
            lantern.transform.position = Vector(district.Item.Home);

            var player = new GameObject("Player");
            player.transform.position = Vector(district.Spawn);
            var character = player.AddComponent<CharacterController>();
            character.height = 1.82f;
            character.radius = 0.27f;
            character.center = Vector3.up * (character.height / 2);
            character.skinWidth = 0.035f;
            character.stepOffset = 0.25f;
            character.minMoveDistance = 0;

            var camera = new GameObject("First person view").AddComponent<Camera>();
            camera.transform.SetParent(player.transform, false);
            camera.transform.localPosition = Vector3.up * 1.63f;
            camera.tag = "MainCamera";
            camera.fieldOfView = 67;
            camera.nearClipPlane = 0.04f;
            camera.farClipPlane = 120;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(169, 194, 206, 255);
            camera.gameObject.AddComponent<AudioListener>();

            var settings = Asset<PanelSettings>(Generated + "/HudPanel.asset");
            settings.themeStyleSheet = RequireAsset<ThemeStyleSheet>(Root + "/Content/RuntimeTheme.tss");
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1280, 800);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0.5f;
            EditorUtility.SetDirty(settings);
            var document = new GameObject("HUD").AddComponent<UIDocument>();
            document.panelSettings = settings;

            var game = new GameObject("RealLife session").AddComponent<DistrictGame>();
            game.Configure(map, character, camera, door.transform, door.GetComponentInChildren<BoxCollider>(),
                lantern.transform.GetChild(0).gameObject, lantern.GetComponent<BoxCollider>(), document,
                RequireAsset<StyleSheet>(Root + "/Content/Hud.uss"));
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Could not save the generated Sadovaya scene.");
            var sceneImporter = AssetImporter.GetAtPath(ScenePath);
            sceneImporter.userData = stamp;
            sceneImporter.SaveAndReimport();
            RegisterScene();
            AssetDatabase.SaveAssets();
        }

        private static void RegisterScene()
        {
            var guid = AssetDatabase.GUIDFromAssetPath(ScenePath);
            if (guid == default) throw new InvalidOperationException("The district scene has not been imported.");
            var scenes = EditorBuildSettings.globalScenes;
            if (scenes.Length != 1 || scenes[0].path != ScenePath || !scenes[0].enabled ||
                scenes[0].guid != guid)
                EditorBuildSettings.globalScenes = new[] { new EditorBuildSettingsScene(guid, true) };
        }

        private static T Asset<T>(string path) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        internal static T RequireAsset<T>(string path) where T : UnityEngine.Object =>
            AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new InvalidOperationException($"Missing asset: {path}");

        internal static void CreateFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
        }

        internal static Vector3 Vector(LocalPoint point) => new Vector3((float)point.X, (float)point.Y, (float)point.Z);
    }
}
