using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    private const string AudioRoot = "Assets/StreamingAssets/HexLive";
    private const string AudioManifest = AudioRoot + "/web-audio-manifest.json";

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
            var webMusic = WriteWebMusic();
            WriteAudioManifest();
            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = output,
                    target = BuildTarget.WebGL,
                    options = development ? BuildOptions.Development : BuildOptions.None,
                });
            }
            finally
            {
                // Генерируется на каждую сборку из того, что реально лежит в
                // StreamingAssets, — в репозитории ему не место (устареет).
                AssetDatabase.DeleteAsset(AudioManifest);
                foreach (var generated in webMusic)
                {
                    AssetDatabase.DeleteAsset(generated);
                }
            }

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

    /// <summary>
    /// §168.6: в браузере StreamingAssets — URL, обойти его нельзя. Список
    /// аудио-дерева (звуки, голоса с .vis, музыка) едет рядом с ним и
    /// читается WebAudioFiles.
    /// </summary>
    private static void WriteAudioManifest()
    {
        var files = new List<string>();
        foreach (var directory in new[] { "Sfx", "Music" })
        {
            var root = Path.Combine(AudioRoot, directory);
            if (!Directory.Exists(root))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                files.Add(Path.GetRelativePath(AudioRoot, file).Replace('\\', '/'));
            }
        }
        files.Sort(StringComparer.Ordinal);

        var json = new StringBuilder("{\"files\":[");
        for (var i = 0; i < files.Count; i++)
        {
            json.Append(i == 0 ? "\n" : ",\n").Append('"')
                .Append(files[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
        json.Append("\n]}\n");
        File.WriteAllText(AudioManifest, json.ToString(), new UTF8Encoding(false));
        AssetDatabase.ImportAsset(AudioManifest);
        Debug.Log($"[WebGLBuild] audio manifest: {files.Count} files");
    }

    /// <summary>
    /// §168.6: в FMOD для браузера нет MP3-декодера (ERR_FORMAT). Каждый
    /// mp3-трек на время веб-сборки получает Ogg-соседа (ffmpeg → oggenc -q4);
    /// веб играет его, десктоп по-прежнему mp3. В репозитории копий нет:
    /// «новый трек = новый mp3» остаётся правилом.
    /// </summary>
    private static List<string> WriteWebMusic()
    {
        var generated = new List<string>();
        var root = Path.Combine(AudioRoot, "Music");
        if (!Directory.Exists(root))
        {
            return generated;
        }

        foreach (var mp3 in Directory.GetFiles(root, "*.mp3"))
        {
            var ogg = Path.ChangeExtension(mp3, ".ogg");
            if (File.Exists(ogg))
            {
                continue; // авторский Ogg главнее
            }

            var command = $"/opt/homebrew/bin/ffmpeg -v error -i '{mp3}' -f wav - | " +
                          $"/opt/homebrew/bin/oggenc -Q -q 4 -o '{ogg}' -";
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("/bin/sh", $"-c \"{command}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                });
            var error = process!.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(ogg))
            {
                Debug.LogError($"[WebGLBuild] web music conversion failed for {mp3}: {error}");
                continue;
            }

            AssetDatabase.ImportAsset(ogg.Replace('\\', '/'));
            generated.Add(ogg.Replace('\\', '/'));
        }

        Debug.Log($"[WebGLBuild] web music: {generated.Count} Ogg track(s) for the browser");
        return generated;
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
