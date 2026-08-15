using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Builds only BuildHutTest into a standalone sandbox player. It never
    /// changes the release version, bug stamps, or the latest-player link.
    /// </summary>
    public static class HexLiveBuildHutTestBuilder
    {
        private const string ScenePath = "Assets/Scenes/BuildHutTest.unity";
        private const string OutputArgument = "-hexlive-build-output";
        private const string SummaryArgument = "-hexlive-build-summary";

        [Serializable]
        private sealed class Summary
        {
            public string result;
            public string version;
            public string unityVersion;
            public string scene;
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
                    throw new InvalidOperationException("BuildHutTest builder requires Unity batch mode.");
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
                    throw new InvalidOperationException(
                        $"Active target is {EditorUserBuildSettings.activeBuildTarget}, expected StandaloneOSX.");
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                    throw new FileNotFoundException($"BuildHutTest scene is missing: {ScenePath}", ScenePath);

                var outputPath = Path.GetFullPath(RequireArgument(OutputArgument));
                var summaryPath = Path.GetFullPath(RequireArgument(SummaryArgument));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                Directory.CreateDirectory(Path.GetDirectoryName(summaryPath) ?? ".");

                HexLiveBuildVersioning.SuppressForCurrentProcess = true;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = outputPath,
                    target = BuildTarget.StandaloneOSX,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.CompressWithLz4HC
                });

                var build = report.summary;
                File.WriteAllText(summaryPath, JsonUtility.ToJson(new Summary
                {
                    result = build.result.ToString(),
                    version = PlayerSettings.bundleVersion,
                    unityVersion = Application.unityVersion,
                    scene = ScenePath,
                    outputPath = build.outputPath,
                    builtAtUtc = DateTime.UtcNow.ToString("o"),
                    durationSeconds = build.totalTime.TotalSeconds,
                    totalBytes = checked((long)build.totalSize),
                    warnings = build.totalWarnings,
                    errors = build.totalErrors,
                    development = false
                }, prettyPrint: true) + "\n");

                if (build.result != BuildResult.Succeeded)
                    throw new InvalidOperationException(
                        $"BuildHutTest player finished with {build.result}: " +
                        $"{build.totalErrors} errors, {build.totalWarnings} warnings.");

                Debug.Log($"BuildHutTest standalone player succeeded: {build.outputPath}");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                HexLiveBuildVersioning.SuppressForCurrentProcess = false;
                EditorApplication.Exit(exitCode);
            }
        }

        private static string RequireArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], name, StringComparison.Ordinal))
                    return args[index + 1];
            }

            throw new ArgumentException($"Required command-line argument is missing: {name}");
        }
    }
}
