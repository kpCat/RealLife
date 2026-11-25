using System.IO;
using RealLife.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace RealLife.Editor
{
    internal static class EarthAuthoring
    {
        private const string Root = "Assets/RealLife";
        private const string Generated = Root + "/Generated";
        public const string ScenePath = Generated + "/Earth.unity";

        public static void Generate()
        {
            var stamp = Hash128.Compute(File.ReadAllText(Root + "/Editor/EarthAuthoring.cs")).ToString();
            var importer = AssetImporter.GetAtPath(ScenePath);
            if (importer == null || importer.userData != stamp)
            {
                var materialPath = Generated + "/Materials/EarthGlobe.mat";
                var globe = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (globe == null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader == null) throw new System.InvalidOperationException("URP Unlit shader is missing.");
                    globe = new Material(shader);
                    AssetDatabase.CreateAsset(globe, materialPath);
                }

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var menu = new GameObject("World selection");
                var document = menu.AddComponent<UIDocument>();
                document.panelSettings = DistrictAuthoring.RequireAsset<PanelSettings>(Generated + "/HudPanel.asset");
                var paletteNames = new[] { "Grass", "Soil", "Asphalt", "Paving", "Plaster", "Brick", "Trim", "Glass", "Metal",
                    "Stone", "Leaves", "Wood" };
                var palette = System.Array.ConvertAll(paletteNames, name =>
                    DistrictAuthoring.RequireAsset<Material>($"{Generated}/Materials/{name}.mat"));
                menu.AddComponent<WorldMenu>().Configure(document,
                    DistrictAuthoring.RequireAsset<StyleSheet>(Root + "/Content/WorldMenu.uss"),
                    DistrictAuthoring.RequireAsset<StyleSheet>(Root + "/Content/Hud.uss"), globe, palette);
                if (!EditorSceneManager.SaveScene(scene, ScenePath))
                    throw new System.InvalidOperationException("Could not save the Earth scene.");
                importer = AssetImporter.GetAtPath(ScenePath);
                importer.userData = stamp;
                importer.SaveAndReimport();
            }
            EditorBuildSettings.globalScenes = new[] { new EditorBuildSettingsScene(ScenePath, true),
                new EditorBuildSettingsScene(DistrictAuthoring.ScenePath, true) };
            AssetDatabase.SaveAssets();
        }
    }
}
