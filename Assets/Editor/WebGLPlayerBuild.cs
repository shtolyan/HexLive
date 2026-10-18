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
    private const string PageTemplate = "PROJECT:HexLive"; // Assets/WebGLTemplates/HexLive

    /// <summary>Идёт ли сборка из <see cref="Build"/> — читает <see cref="HexLiveWebGLBuildGuard"/>.</summary>
    internal static bool Running { get; private set; }

    public static void Build()
    {
        var succeeded = false;
        try
        {
            RequireWebInputFix(Application.unityVersion);
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
            // RuntimeSpeedLTO линкуется больше часа (wasm-ld, замер v5 10.10.2026)
            // ради нескольких процентов; по умолчанию RuntimeSpeed, LTO — флагом
            // -webgl-lto для финальных выпусков.
            ConfigureCodeOptimization(arguments.Contains("-webgl-lto") ? "RuntimeSpeedLTO" : "RuntimeSpeed");

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
            // MSI may reuse an already published FSB tree only when the
            // publisher verifies unchanged audio sources and complete stems.
            var reusePublishedAudio = arguments.Contains("-webgl-reuse-published-audio");
            var webMusic = reusePublishedAudio ? new List<string>() : WriteWebMusic();
            WriteAudioManifest();
            // §174.6: the page with the HTML interface. The template setting is
            // shared with the desktop project file, so it is set per build and
            // put back, like the stack-trace settings above.
            var template = PlayerSettings.WebGL.template;
            PlayerSettings.WebGL.template = PageTemplate;
            // §168.11: the page has its own preloader (§174.7), so the Unity
            // logo screen is only ~2.7 MB of download and seconds of wait.
            // Shared with the desktop project file — put back below.
            var splash = PlayerSettings.SplashScreen.show;
            PlayerSettings.SplashScreen.show = false;
            Debug.Log($"[WebGLBuild] Unity splash screen: {(PlayerSettings.SplashScreen.show ? "ON (license kept it)" : "off")}");
            BuildReport report;
            try
            {
                Running = true;
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
                Running = false;
                PlayerSettings.WebGL.template = template;
                PlayerSettings.SplashScreen.show = splash;
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
                RequireHtmlInterface(output);
                CapDevicePixelRatio(Path.Combine(output, "index.html"));
                File.Copy("Assets/Resources/HexLive/UI/ServerDirectory.json",
                    Path.Combine(output, "server-directory.json"), overwrite: true);
                if (reusePublishedAudio)
                    File.WriteAllText(Path.Combine(output, "audio-reuse-required.json"),
                        "{\"requiresVerifiedPublishedFsb\":true}\n");
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
        // Выключенная в настройках FMOD платформа (active: 0) берёт ВСЕ свойства
        // у родителя (десктопа): v3–v8 уходили в браузер с буфером 1024×4 и без
        // обработчика старта, хотя скрипт их «ставил» (замер ?perf=1, 11.10.2026).
        // AffirmProperties включает платформу; свойства читаем ПОСЛЕ него.
        var active = (bool)(typeof(FMODUnity.Platform).GetProperty("Active", flags)?.GetValue(platform) ??
                            throw new InvalidOperationException("FMOD Platform.Active not found."));
        if (!active)
        {
            (typeof(FMODUnity.Platform).GetMethod("AffirmProperties", flags) ??
             throw new InvalidOperationException("FMOD Platform.AffirmProperties not found.")).Invoke(platform, null);
            Debug.Log("[WebGLBuild] FMOD WebGL platform was inactive — activated");
        }
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
        // #483: player verified 4096×4 reduces/removes crackle on PC Chrome.
        // Nominal capacity is ~341 ms at 48 kHz; this trades effect latency for
        // headroom, but cannot cover arbitrary main-thread stalls.
        Set("DSPBufferLength", HexLive.UnityPresentation.Audio.WebAudioTuning.DefaultBufferLength);
        Set("DSPBufferCount", HexLive.UnityPresentation.Audio.WebAudioTuning.DefaultBufferCount);

        // §168.6: обработчик старта FMOD для браузера (частота микшера под
        // устройство, полевые переключатели из URL) — HexLive WebAudioTuning.
        const string tuningPath = "Assets/HexLive/UnityPresentation/Audio/WebAudioTuning.asset";
        var tuning = AssetDatabase.LoadAssetAtPath<HexLive.UnityPresentation.Audio.WebAudioTuning>(tuningPath);
        if (tuning == null)
        {
            tuning = ScriptableObject.CreateInstance<HexLive.UnityPresentation.Audio.WebAudioTuning>();
            AssetDatabase.CreateAsset(tuning, tuningPath);
        }
        var handler = properties.GetType().GetField("CallbackHandler", flags)?.GetValue(properties) ??
                      throw new InvalidOperationException("FMOD property CallbackHandler not found.");
        handler.GetType().GetField("Value", flags)!.SetValue(handler, tuning);
        handler.GetType().GetField("HasValue", flags)!.SetValue(handler, true);
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        Debug.Log($"[WebGLBuild] FMOD WebGL DSP buffer: {platform.DSPBufferCount} x " +
                  $"{platform.DSPBufferLength}, callback WebAudioTuning");
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
            // §174.6: the HexLive template carries the line itself.
            if (!html.Contains("config.devicePixelRatio = 1;"))
            {
                throw new InvalidOperationException($"{indexPath} names §168.11 but does not cap devicePixelRatio.");
            }

            Debug.Log("[WebGLBuild] devicePixelRatio capped at 1");
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
    /// §174.6: a player built from another template would quietly keep the
    /// UI Toolkit windows (HtmlUi stays inactive) — refuse it instead.
    /// </summary>
    private static void RequireHtmlInterface(string output)
    {
        foreach (var file in new[] { "ui/main.js", "ui/bridge.js", "ui/theme.css" })
        {
            if (!File.Exists(Path.Combine(output, file)))
            {
                throw new InvalidOperationException($"WebGL page has no {file}: template {PageTemplate} was not used.");
            }
        }

        if (!File.ReadAllText(Path.Combine(output, "index.html")).Contains("ui/main.js"))
        {
            throw new InvalidOperationException("WebGL index.html does not load ui/main.js.");
        }

        // Unity runs template files through its preprocessor ({{{ }}}, #if).
        // ui/ must come out byte for byte, or the page runs code nobody wrote.
        const string source = "Assets/WebGLTemplates/HexLive/ui";
        var count = 0;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".meta", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath("Assets/WebGLTemplates/HexLive", file);
            var built = Path.Combine(output, relative);
            if (!File.Exists(built) || !File.ReadAllBytes(built).SequenceEqual(File.ReadAllBytes(file)))
            {
                throw new InvalidOperationException($"WebGL page file {relative} differs from {file}.");
            }

            count++;
        }

        Debug.Log($"[WebGLBuild] HTML interface: ui/ from template HexLive, {count} files verbatim");
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

    // #473 / UUM-137597: native Editor input tests do not exercise the
    // WebGL keyboard focus regression. Keep the supported build line explicit.
    internal static void RequireWebInputFix(string version)
    {
        var match = System.Text.RegularExpressions.Regex.Match(version ?? "", @"^6000\.4\.(\d+)f\d+$");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var patch) && patch >= 6) return;
        throw new UnityEditor.Build.BuildFailedException(
            "WebGL Player requires a stable Unity 6000.4 patch >= 6000.4.6f1: " +
            "UUM-137597 makes UI Toolkit text inputs lose focus in desktop browsers. " +
            "Current editor: " + version + ". Other Unity release lines need compatibility review. See Docs/WebGLBuild.md.");
    }
}

/// <summary>
/// §168.10: WebGL-плеер собирается только через <see cref="HexLiveWebGLPlayerBuild.Build"/>.
/// Настройки FMOD лежат вне git (Assets/Plugins/FMOD в .gitignore), а
/// буфер 4×4096, обработчик WebAudioTuning, wasm2023, исключения без стека,
/// RuntimeSpeed, манифест звука и devicePixelRatio 1 ставит только этот
/// метод — на каждой сборке. Сборка из окна Build Settings или из чужого
/// скрипта собралась бы «успешно» и уехала бы в браузер без всего этого,
/// поэтому она отказывает сразу. Бандлы (BuildAssetBundles) сюда не доходят.
/// Порядок сборки — Docs/WebGLBuild.md.
/// </summary>
internal sealed class HexLiveWebGLBuildGuard : UnityEditor.Build.IPreprocessBuildWithReport
{
    public int callbackOrder => int.MinValue;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.WebGL)
        {
            return;
        }
        HexLiveWebGLPlayerBuild.RequireWebInputFix(Application.unityVersion);
        if (HexLiveWebGLPlayerBuild.Running) return;
        throw new UnityEditor.Build.BuildFailedException(
            "WebGL Player is built only by HexLiveWebGLPlayerBuild.Build " +
            "(-executeMethod HexLiveWebGLPlayerBuild.Build -webgl-output <dir>): it applies the gitignored " +
            "FMOD web settings and the wasm/page tuning on every build. See Docs/WebGLBuild.md.");
    }
}
