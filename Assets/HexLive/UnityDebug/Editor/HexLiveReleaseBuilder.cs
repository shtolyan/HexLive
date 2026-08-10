using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Command-line entry point used by Tools/build_release.py. Version
    /// reservation and bug stamping remain owned by HexLiveBuildVersioning.
    /// </summary>
    public static class HexLiveReleaseBuilder
    {
        private const string HutAssetPath = "Assets/Resources/HexLive/Objects/building.hut_1hex.fbx";
        private const string BedAssetPath = "Assets/Resources/HexLive/Objects/bed_basic_final_native.fbx";
        private const string OutputArgument = "-hexlive-build-output";
        private const string SummaryArgument = "-hexlive-build-summary";
        private const string ReleaseArgument = "-hexlive-release";

        [Serializable]
        private sealed class CommandLineBuildSummary
        {
            public string result;
            public string version;
            public string unityVersion;
            public string target;
            public string outputPath;
            public string builtAtUtc;
            public double durationSeconds;
            public long totalBytes;
            public int warnings;
            public int errors;
            public bool development;
        }

        public static void BuildMacOS()
        {
            var exitCode = 1;
            try
            {
                if (!Application.isBatchMode)
                {
                    throw new InvalidOperationException(
                        "HexLiveReleaseBuilder is a batch-mode entry point. Use Tools/build_release.py.");
                }

                var outputPath = Path.GetFullPath(RequireArgument(OutputArgument));
                var summaryPath = Path.GetFullPath(RequireArgument(SummaryArgument));
                var development = !HasArgument(ReleaseArgument);
                var target = BuildTarget.StandaloneOSX;

                if (EditorUserBuildSettings.activeBuildTarget != target)
                {
                    throw new InvalidOperationException(
                        $"Active build target is {EditorUserBuildSettings.activeBuildTarget}, expected {target}. " +
                        "The release script must launch Unity with -buildTarget StandaloneOSX.");
                }

                var scenes = EditorBuildSettings.scenes
                    .Where(scene => scene.enabled)
                    .Select(scene => scene.path)
                    .ToArray();
                if (scenes.Length == 0)
                {
                    throw new InvalidOperationException("No enabled scenes in EditorBuildSettings.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                Directory.CreateDirectory(Path.GetDirectoryName(summaryPath) ?? ".");

                ValidateBuildingResource(HutAssetPath, "HexLive/Objects/building.hut_1hex", 1);
                ValidateBuildingResource(BedAssetPath, "HexLive/Objects/bed_basic_final_native", 69);

                var options = BuildOptions.CompressWithLz4HC;
                if (development)
                {
                    options |= BuildOptions.Development | BuildOptions.AllowDebugging;
                }

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = target,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = options
                });

                var build = report.summary;
                if (build.result == BuildResult.Succeeded)
                {
                    ValidatePackedBuildingResource(report, HutAssetPath);
                    ValidatePackedBuildingResource(report, BedAssetPath);

                    // Unity 6 did not consistently rediscover the postprocess
                    // half of a callback that also owns preprocess in batchmode.
                    // This method is idempotent when the interface already ran.
                    HexLiveBuildVersioning.CommitSuccessfulBuild();
                }

                WriteSummary(summaryPath, new CommandLineBuildSummary
                {
                    result = build.result.ToString(),
                    version = PlayerSettings.bundleVersion,
                    unityVersion = Application.unityVersion,
                    target = build.platform.ToString(),
                    outputPath = build.outputPath,
                    builtAtUtc = DateTime.UtcNow.ToString("o"),
                    durationSeconds = build.totalTime.TotalSeconds,
                    totalBytes = checked((long)build.totalSize),
                    warnings = build.totalWarnings,
                    errors = build.totalErrors,
                    development = development
                });

                if (build.result != BuildResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"HexLive player build finished with {build.result}: " +
                        $"{build.totalErrors} errors, {build.totalWarnings} warnings.");
                }

                Debug.Log($"HexLive command-line build succeeded: {build.outputPath}");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                EditorApplication.Exit(exitCode);
            }
        }

        private static string RequireArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return args[i + 1];
                }
            }

            throw new ArgumentException($"Required command-line argument is missing: {name}");
        }

        private static void ValidateBuildingResource(
            string assetPath, string resourcePath, int minimumRenderers)
        {
            if (!File.Exists(assetPath))
            {
                throw new FileNotFoundException(
                    $"Required building Resources asset is missing: {assetPath}", assetPath);
            }

            var imported = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            var loaded = Resources.Load<GameObject>(resourcePath);
            if (imported == null || loaded == null)
            {
                throw new InvalidOperationException(
                    $"Building asset is not importable/loadable as Resources GameObject: " +
                    $"asset={assetPath}, resource={resourcePath}.");
            }

            var rendererCount = loaded.GetComponentsInChildren<Renderer>(true).Length;
            if (rendererCount < minimumRenderers)
            {
                throw new InvalidOperationException(
                    $"Building Resources asset is incomplete: {assetPath} has {rendererCount} " +
                    $"renderers, expected at least {minimumRenderers}.");
            }

            Debug.Log(
                $"[BuildGate] Building resource ready: {resourcePath} " +
                $"({rendererCount} renderers, {new FileInfo(assetPath).Length} bytes).");
        }

        private static void ValidatePackedBuildingResource(BuildReport report, string assetPath)
        {
            var packed = report.packedAssets.Any(container =>
                container.contents.Any(item =>
                    string.Equals(item.sourceAssetPath, assetPath, StringComparison.Ordinal)));
            if (!packed)
            {
                throw new InvalidOperationException(
                    $"Player build succeeded but omitted required Resources asset: {assetPath}.");
            }

            Debug.Log($"[BuildGate] Player contains building resource: {assetPath}.");
        }

        private static bool HasArgument(string name)
        {
            return Environment.GetCommandLineArgs().Any(
                arg => string.Equals(arg, name, StringComparison.Ordinal));
        }

        private static void WriteSummary(string path, CommandLineBuildSummary summary)
        {
            File.WriteAllText(path, JsonUtility.ToJson(summary, prettyPrint: true) + "\n");
        }
    }
}
