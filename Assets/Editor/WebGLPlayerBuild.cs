using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// §168: the web Player — a thin remote viewer. Batchmode entry point:
///
///   Unity -batchmode -quit -buildTarget WebGL -projectPath &lt;checkout&gt;
///         -executeMethod HexLiveWebGLPlayerBuild.Build -webgl-output &lt;dir&gt;
///         [-webgl-development]
///
/// Deliberately small next to the macOS/Windows publishers: no version
/// reservation, no bug snapshot, no signing — the page is served, not
/// installed. Compression is Brotli without the JS decompression fallback:
/// the server says Content-Encoding (Server/Caddyfile, Tools/webgl_serve.py),
/// which is what lets the browser stream-compile the wasm.
/// Results are LOGGED, never shown in a dialog (CLAUDE.md: a modal box owns
/// the main thread).
/// </summary>
public static class HexLiveWebGLPlayerBuild
{
    public static void Build()
    {
        var succeeded = false;
        try
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                throw new InvalidOperationException(
                    $"Active target is {EditorUserBuildSettings.activeBuildTarget}; pass -buildTarget WebGL.");
            }

            var arguments = Environment.GetCommandLineArgs();
            var output = Value(arguments, "-webgl-output") ??
                         throw new InvalidOperationException("Pass -webgl-output <directory>.");
            output = Path.GetFullPath(output);
            var development = arguments.Contains("-webgl-development");

            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.decompressionFallback = false;

            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.WebGL,
                options = development ? BuildOptions.Development : BuildOptions.None,
            });

            var summary = report.summary;
            Debug.Log($"[WebGLBuild] {summary.result}: {summary.totalSize / (1024 * 1024)} MB, " +
                      $"{summary.totalErrors} errors, {summary.totalWarnings} warnings, " +
                      $"{summary.totalTime.TotalMinutes:0.0} min -> {output}");
            succeeded = summary.result == BuildResult.Succeeded;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(succeeded ? 0 : 1);
            }
        }
    }

    private static string Value(string[] arguments, string name)
    {
        for (var i = 0; i + 1 < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], name, StringComparison.Ordinal))
            {
                return arguments[i + 1];
            }
        }
        return null;
    }
}
