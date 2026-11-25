using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealLife.Editor
{
    public static class WindowsBuild
    {
        public static void Build()
        {
            var args = Environment.GetCommandLineArgs();
            var outputIndex = Array.IndexOf(args, "-realLifeOutput");
            if (outputIndex < 0 || outputIndex + 1 >= args.Length)
                throw new BuildFailedException("Use Tools/RealLife.ps1 to provide a Windows player output path.");

            var target = BuildTarget.StandaloneWindows64;
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
                throw new BuildFailedException("Unity Windows x64 build support is required.");

            DistrictAuthoring.Generate();
            EarthAuthoring.Generate();
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11 });
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 800;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.forceSingleInstance = true;
            PlayerSettings.bundleVersion = "0.1.0";
            AssetDatabase.SaveAssets();

            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { EarthAuthoring.ScenePath, DistrictAuthoring.ScenePath },
                target = target,
                locationPathName = Path.GetFullPath(args[outputIndex + 1]),
                options = BuildOptions.StrictMode
            });
            if (result.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException($"Windows build {result.summary.result} ({result.summary.totalErrors} errors).");
            Debug.Log($"RealLife Windows build succeeded ({result.summary.totalSize} bytes).");
        }
    }
}
