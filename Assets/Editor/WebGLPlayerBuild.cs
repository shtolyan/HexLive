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
            // §168.11 скорость wasm (замер 10.10.2026: ~120 мс CPU на кадр в мире).
            // Исключения: «только явные» превращают NullReferenceException в
            // «table index is out of bounds» и роняют страницу (9.10.2026), а
            // «полные со стеком» оборачивали почти каждый вызов JS-трамплином
            // invoke_* (509 против 176) — основная цена кадра. По умолчанию —
            // полные БЕЗ стека поверх нативных исключений WebAssembly 2023:
            // NRE ловится, а трамплинов нет. -webgl-exceptions full — со стеком
            // для отладки, explicit — минимальные.
            var exceptions = Value(arguments, "-webgl-exceptions");
            PlayerSettings.WebGL.exceptionSupport = exceptions switch
            {
                "full" => WebGLExceptionSupport.FullWithStacktrace,
                "explicit" => WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly,
                _ => WebGLExceptionSupport.FullWithoutStacktrace,
            };
            // WebAssembly 2023: нативные исключения, SIMD, BigInt — Chrome 95+,
            // Firefox 100+, Safari 15.2+.
            PlayerSettings.WebGL.wasm2023 = true;
            ConfigureCodeOptimization("RuntimeSpeedLTO");

            ConfigureWebAudioBuffer();
            // В браузере каждая строка лога со стеком дорогая (стек собирается
            // в wasm, текст уходит в консоль JS): журнал событий сима — сотни
            // строк — съедал кадр, а с ним и микшер FMOD. Ошибки и исключения
            // стек сохраняют. PlayerSettings общие для проекта — восстанавливаем.
            var logTrace = PlayerSettings.GetStackTraceLogType(LogType.Log);
            var warningTrace = PlayerSettings.GetStackTraceLogType(LogType.Warning);
            PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            PlayerSettings.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);

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
                PlayerSettings.SetStackTraceLogType(LogType.Log, logTrace);
                PlayerSettings.SetStackTraceLogType(LogType.Warning, warningTrace);
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
            if (succeeded)
            {
                CapDevicePixelRatio(Path.Combine(output, "index.html"));
            }
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
    /// §168.6: буфер микшера FMOD для браузера. По умолчанию 4 блока по 1024
    /// сэмпла; в вебе микшер живёт рядом с кадром, и на просадках FPS звук
    /// рвётся — документация FMOD (HTML5, «Audio Stability») прямо советует
    /// 2 блока по 2048. Настройки FMOD лежат вне git (Assets/Plugins/FMOD),
    /// поэтому ставим их здесь, на каждой веб-сборке. Поля защищённые —
    /// отражение; если FMOD их переименует, сборка падает, а не молчит.
    /// </summary>
    private static void ConfigureWebAudioBuffer()
    {
        var settings = FMODUnity.Settings.Instance;
        var platform = settings.Platforms.FirstOrDefault(p => p.GetType().Name == "PlatformWebGL") ??
                       throw new InvalidOperationException("FMOD settings have no PlatformWebGL entry.");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public;
        var properties = typeof(FMODUnity.Platform).GetField("Properties", flags)?.GetValue(platform) ??
                         throw new InvalidOperationException("FMOD Platform.Properties not found.");
        void Set(string name, int value)
        {
            var property = properties.GetType().GetField(name, flags)?.GetValue(properties) as
                               FMODUnity.Platform.Property<int> ??
                           throw new InvalidOperationException($"FMOD property {name} not found.");
            property.Value = value;
            property.HasValue = true;
        }
        // 4 × 2048 ≈ 186 мс при 44.1 кГц: в вебе FMOD подкачивает микшер с
        // главного потока, и кадр дольше буфера (2 × 2048 = 93 мс) давал
        // «дребезг» (жалоба 10.10.2026). Задержка эффектов терпима.
        Set("DSPBufferLength", 2048);
        Set("DSPBufferCount", 4);
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        Debug.Log("[WebGLBuild] FMOD WebGL DSP buffer: 4 x 2048");
    }

    /// <summary>
    /// §168.11: на Retina стандартный шаблон рендерит в devicePixelRatio (2×2 =
    /// 4 раза больше пикселей). Его строка «config.devicePixelRatio = 1» стоит
    /// только в ветке для телефонов — на Mac она не действовала (замер 10.10.2026:
    /// холст 1920×1200 под окном 960×600). Ставим для всех платформ.
    /// </summary>
    private static void CapDevicePixelRatio(string indexPath)
    {
        const string anchor = "document.querySelector(\"#unity-loading-bar\").style.display = \"block\";";
        var html = File.ReadAllText(indexPath);
        if (html.Contains("§168.11"))
        {
            return;
        }
        var at = html.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException($"WebGL template changed: no loading-bar line in {indexPath}.");
        }
        html = html.Insert(at, "// §168.11: Retina — рендер в CSS-пикселях.\n      config.devicePixelRatio = 1;\n      ");
        File.WriteAllText(indexPath, html, new UTF8Encoding(false));
        Debug.Log("[WebGLBuild] devicePixelRatio capped at 1");
    }

    /// <summary>
    /// §168.11: оптимизация wasm под скорость, а не под размер. Сборочный профиль
    /// WebGL стоял на DiskSizeLTO (-Oz). Свойство живёт в
    /// UnityEditor.WebGL.Extensions (модуль платформы), прямой ссылки на сборку
    /// у Assembly-CSharp-Editor нет — поэтому отражение; не нашли — падаем.
    /// </summary>
    private static void ConfigureCodeOptimization(string value)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeTypes(assembly))
            {
                if (type.Namespace != "UnityEditor.WebGL")
                {
                    continue;
                }
                var property = type.GetProperty("codeOptimization",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (property == null || !property.CanWrite || !property.PropertyType.IsEnum)
                {
                    continue;
                }
                property.SetValue(null, Enum.Parse(property.PropertyType, value));
                Debug.Log($"[WebGLBuild] code optimization: {property.GetValue(null)} ({type.FullName})");
                return;
            }
        }
        throw new InvalidOperationException("UnityEditor.WebGL codeOptimization property not found.");
    }

    private static Type[] SafeTypes(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null).ToArray();
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
