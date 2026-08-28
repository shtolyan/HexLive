using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Command-line entry point used by Tools/build_release.py. Version
    /// reservation and bug stamping remain owned by HexLiveBuildVersioning.
    /// </summary>
    public static class HexLiveReleaseBuilder
    {
        private const string PcPipelineAssetPath = "Assets/Settings/PC_RPAsset.asset";
        private const string RuntimeLitShaderName = "Universal Render Pipeline/Lit";
        private const string RuntimeLitShaderGuid = "933532a4fcc9baf4fa0491de14d08ed7";
        private const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";
        private const long MaximumPlayerBytes = 850L * 1024L * 1024L;
        private const long MaximumDataAndStreamingBytes = 400L * 1024L * 1024L;
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
            public long playerBytes;
            public long dataUnity3dBytes;
            public long streamingAssetsBytes;
            public int packedAssetEntries;
            public int warnings;
            public int errors;
            public bool development;
        }

        public static void BuildMacOS()
        {
            Run(BuildTarget.StandaloneOSX);
        }

        public static void BuildWindows()
        {
            Run(BuildTarget.StandaloneWindows64);
        }

        public static void ValidateAtomicPlayerInputs()
        {
            var exitCode = 1;
            try
            {
                ValidateNoForcedPlayerContent();
                ValidateRuntimeGeneratedWorldRendering();
                Debug.Log("[BuildGate] Atomic Player inputs are valid.");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(exitCode);
                }
            }
        }

        private static void Run(BuildTarget target)
        {
            var exitCode = 1;
            try
            {
                if (!Application.isBatchMode)
                {
                    throw new InvalidOperationException(
                        "HexLiveReleaseBuilder is a batch-mode entry point. Use Tools/build_release.py " +
                        "(macOS) or Tools/build_release_windows.py (Windows).");
                }

                var outputPath = Path.GetFullPath(RequireArgument(OutputArgument));
                var summaryPath = Path.GetFullPath(RequireArgument(SummaryArgument));
                var development = !HasArgument(ReleaseArgument);

                if (EditorUserBuildSettings.activeBuildTarget != target)
                {
                    throw new InvalidOperationException(
                        $"Active build target is {EditorUserBuildSettings.activeBuildTarget}, expected {target}. " +
                        $"The release script must launch Unity with -buildTarget {target}.");
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

                // ⭐ Детерминизм float против сервера. IL2CPP компилирует C#
                // клангом, а кланг на arm64 по умолчанию СКЛЕИВАЕТ a*b + c в
                // FMA (одно округление вместо двух). Mono-редактор и .NET 9
                // сервер так не делают — и на большом острове worldgen-шум
                // качает MathF.Round(height*6) на границе: один тайл сменил
                // высоту, и клиент отбивается с «Topology mismatch» (замер
                // 2026-08-27: server 0xECB0DA57, IL2CPP-плеер 0x67AEA5B3 на
                // одном и том же коммите; Mono-редактор с сервером сходился).
                // «Порядок float-операций — это поведение» — контракцию
                // выключаем. Флаг не сериализуется в ProjectSettings, поэтому
                // проставляется на каждый запуск сборки.
                PlayerSettings.SetAdditionalIl2CppArgs(
                    "--compiler-flags=\"-ffp-contract=off\"");
                Debug.Log("[HexLiveReleaseBuilder] IL2CPP: -ffp-contract=off " +
                          "(float-паритет worldgen с Mono/.NET 9).");

                // The FMOD integration is machine-local and ignored by Git.
                // Reassert the atomic-content mode on every release build:
                // editor event indexing is allowed, bank copying is not.
                ConfigureFmodForAtomicPlayer();
                ValidateNoForcedPlayerContent();
                ValidateRuntimeGeneratedWorldRendering();

                var options = BuildOptions.CompressWithLz4HC;
                if (development)
                {
                    options |= BuildOptions.Development | BuildOptions.AllowDebugging;
                }

                var buildOptions = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = target,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = options
                };
                var report = BuildPlayerWithSafeGraphicsApi(buildOptions);

                var build = report.summary;
                if (build.result == BuildResult.Succeeded)
                {
                    ValidatePackedPlayerContent(report);
                    ValidatePlayerSize(outputPath, target, out var playerBytes,
                        out var dataUnity3dBytes, out var streamingAssetsBytes);

                    // Unity 6 did not consistently rediscover the postprocess
                    // half of a callback that also owns preprocess in batchmode.
                    // This method is idempotent when the interface already ran.
                    HexLiveBuildVersioning.CommitSuccessfulBuild();

                    WriteSummary(summaryPath, CreateSummary(
                        build, development, playerBytes, dataUnity3dBytes,
                        streamingAssetsBytes, PackedEntryCount(report)));
                }
                else
                {
                    WriteSummary(summaryPath, CreateSummary(
                        build, development, 0, 0, 0, PackedEntryCount(report)));
                }

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

        private static void ConfigureFmodForAtomicPlayer()
        {
            // HexLiveFmodSetup deliberately lives in the predefined Editor
            // assembly beside the machine-local FMOD integration. This build
            // assembly cannot reference a predefined assembly directly, so use
            // its stable public entry point and fail loudly if installation or
            // setup is absent.
            var setupType = Type.GetType("HexLiveFmodSetup, Assembly-CSharp-Editor");
            var configure = setupType?.GetMethod(
                "ConfigureAtomicPlayer",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (configure == null)
            {
                throw new InvalidOperationException(
                    "HexLiveFmodSetup.ConfigureAtomicPlayer is unavailable. " +
                    "Install/configure the FMOD Unity integration before building Player.");
            }

            var result = configure.Invoke(null, null);
            Debug.Log(result as string ?? "[FMOD] atomic Player mode configured.");
        }

        private static BuildReport BuildPlayerWithSafeGraphicsApi(BuildPlayerOptions options)
        {
            if (options.target != BuildTarget.StandaloneWindows64)
            {
                return BuildPipeline.BuildPlayer(options);
            }

            // Unity 6000.4 may choose D3D12 first when the project uses the
            // default Windows API list. On affected hybrid Intel/NVIDIA
            // machines the device is created successfully but the very first
            // swap-chain Present fails with DXGI_ERROR_INVALID_CALL, causing a
            // native crash before any HexLive bootstrap code can run. D3D11 is
            // the stable Windows baseline for this Player. Keep the override
            // scoped to the build so an interactive Editor retains its prefs.
            var target = BuildTarget.StandaloneWindows64;
            var usedDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
            var configuredApis = PlayerSettings.GetGraphicsAPIs(target);
            try
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                PlayerSettings.SetGraphicsAPIs(
                    target, new[] { GraphicsDeviceType.Direct3D11 });
                Debug.Log("[HexLiveReleaseBuilder] Windows graphics API: D3D11 " +
                          "(D3D12 startup-device-loss fallback).");
                return BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                PlayerSettings.SetGraphicsAPIs(target, configuredApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(target, usedDefaultApis);
            }
        }

        private static CommandLineBuildSummary CreateSummary(
            BuildSummary build,
            bool development,
            long playerBytes,
            long dataUnity3dBytes,
            long streamingAssetsBytes,
            int packedAssetEntries)
        {
            return new CommandLineBuildSummary
            {
                result = build.result.ToString(),
                version = PlayerSettings.bundleVersion,
                unityVersion = Application.unityVersion,
                target = build.platform.ToString(),
                outputPath = build.outputPath,
                builtAtUtc = DateTime.UtcNow.ToString("o"),
                durationSeconds = build.totalTime.TotalSeconds,
                totalBytes = checked((long)build.totalSize),
                playerBytes = playerBytes,
                dataUnity3dBytes = dataUnity3dBytes,
                streamingAssetsBytes = streamingAssetsBytes,
                packedAssetEntries = packedAssetEntries,
                warnings = build.totalWarnings,
                errors = build.totalErrors,
                development = development
            };
        }

        private static void ValidateNoForcedPlayerContent()
        {
            var violations = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { "Assets/Resources" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!AssetDatabase.IsValidFolder(path) && IsForbiddenPlayerContent(path))
                {
                    violations.Add(path);
                }
            }

            if (Directory.Exists("Assets/StreamingAssets"))
            {
                violations.AddRange(Directory.EnumerateFiles(
                        "Assets/StreamingAssets", "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    // Finder-мусор (.DS_Store) — не игровой контент: он
                    // появляется от одного открытия папки и не должен
                    // останавливать публикацию.
                    .Where(path => !Path.GetFileName(path).StartsWith(
                        ".", StringComparison.Ordinal))
                    .Where(IsForbiddenPlayerContent)
                    .Select(path => path.Replace('\\', '/')));
            }

            foreach (var scene in EditorBuildSettings.scenes.Where(value => value.enabled))
            {
                violations.AddRange(AssetDatabase.GetDependencies(scene.path, true)
                    .Where(IsForbiddenPlayerContent));
            }

            if (violations.Count > 0)
            {
                var preview = string.Join("\n", violations.OrderBy(path => path).Take(20));
                throw new InvalidOperationException(
                    $"Player contains {violations.Count} forced game-content asset(s). " +
                    "Publish them as atomic ContentObjects and remove them from Resources/" +
                    "StreamingAssets before building:\n" + preview);
            }

            Debug.Log("[BuildGate] Resources and StreamingAssets contain bootstrap files only.");
        }

        private static void ValidatePackedPlayerContent(BuildReport report)
        {
            var forbidden = report.packedAssets
                .SelectMany(container => container.contents)
                .Select(item => item.sourceAssetPath)
                .Where(path => IsForbiddenPlayerContent(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path)
                .ToArray();
            if (forbidden.Length > 0)
            {
                throw new InvalidOperationException(
                    $"BuildReport detected {forbidden.Length} game-content asset(s) in Player:\n" +
                    string.Join("\n", forbidden.Take(20)));
            }

            Debug.Log($"[BuildGate] BuildReport contains no game content " +
                      $"({PackedEntryCount(report)} packed entries inspected).");
        }

        private static bool IsForbiddenPlayerContent(string path)
        {
            if (string.IsNullOrEmpty(path) ||
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Assets/StreamingAssets/HexLive/Sfx/",
                    StringComparison.Ordinal) ||
                normalized.StartsWith("Assets/StreamingAssets/HexLive/Music/",
                    StringComparison.Ordinal) ||
                normalized.StartsWith("Assets/StreamingAssets/FMODBanks/",
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (normalized.StartsWith("Assets/StreamingAssets/", StringComparison.Ordinal) ||
                normalized.StartsWith("Assets/ImportedActors/", StringComparison.Ordinal) ||
                normalized.StartsWith("Assets/HexLiveContent/", StringComparison.Ordinal) ||
                normalized.StartsWith("Assets/AtomicContent/", StringComparison.Ordinal) ||
                normalized.StartsWith("Assets/FMODBanks/", StringComparison.Ordinal))
            {
                return true;
            }

            if (!normalized.StartsWith("Assets/Resources/", StringComparison.Ordinal))
            {
                return false;
            }

            // Runtime UI layouts and their small shared artwork are part of the
            // stable client interface, just like the C# controllers that bind
            // them. They must render before the asset API is available and are
            // intentionally shipped in Player, never as ui/* content objects.
            if (normalized.StartsWith(
                    "Assets/Resources/HexLive/UI/", StringComparison.Ordinal))
            {
                return false;
            }

            // §152: «обязательные эффекты» — Player-контент по решению трекера.
            // bug-253 вернул VFX крови, bug-256 — общие decals состояния:
            // как и UI, они обязаны работать до готовности Asset API и
            // грузятся синхронным Resources.Load.
            if (normalized.StartsWith(
                    "Assets/Resources/HexLive/VFX/", StringComparison.Ordinal) ||
                normalized.StartsWith(
                    "Assets/Resources/HexLive/Decals/", StringComparison.Ordinal))
            {
                return false;
            }

            return !BootstrapResourceAllowList.Contains(normalized);
        }

        private static readonly HashSet<string> BootstrapResourceAllowList =
            new(StringComparer.Ordinal)
            {
                "Assets/Resources/I2Languages.asset",
                // com.unity.test-framework.performance creates these two
                // transient resources in its build callback and removes them
                // again after the build. They are tooling metadata, not game
                // content; every other unexpected Resources path still fails.
                "Assets/Resources/PerformanceTestRunInfo.json",
                "Assets/Resources/PerformanceTestRunSettings.json",
                "Assets/Resources/HexLive/DebugPanelSettings.asset",
                // Spec 20.16 (r3): вода — часть каждого ПЕРВОГО кадра острова
                // и обязана рендериться до готовности Asset API, ровно как UI.
                // Бандловая доставка воды уже отзывалась двумя багами: ленивый
                // AtomicResources.Load отдавал null на первом кадре (море
                // мигало fallback-материалом и месяцами жило на нём), а шейдер
                // из бандла собирался с чужим набором URP-вариантов. Материал
                // зашит в Player; его шейдер и текстуры едут вместе с ним.
                "Assets/Resources/HexLive/Water/StylizedWaterDefinitive.mat",
                "Assets/Resources/HexLive/UI/Fonts/Caveat-Regular.ttf",
                "Assets/Resources/HexLive/UI/GameModePanel.uss",
                "Assets/Resources/HexLive/UI/GameModePanel.uxml",
                "Assets/Resources/HexLive/UI/WorldLibraryPanel.uss",
                "Assets/Resources/HexLive/UI/WorldLibraryPanel.uxml",
                "Assets/Resources/HexLive/UI/loading_island.png",
                "Assets/Resources/HexLive/UI/logo.png",
                "Assets/Resources/HexLive/UI/mode_huge_island.png",
                "Assets/Resources/HexLive/UI/mode_maniac.png"
            };

        private static int PackedEntryCount(BuildReport report)
        {
            return report.packedAssets.Sum(container => container.contents.Length);
        }

        private static void ValidatePlayerSize(
            string outputPath,
            BuildTarget target,
            out long playerBytes,
            out long dataUnity3dBytes,
            out long streamingAssetsBytes)
        {
            playerBytes = File.Exists(outputPath)
                ? new FileInfo(outputPath).Length
                : DirectorySize(outputPath);

            var dataRoot = target == BuildTarget.StandaloneOSX
                ? Path.Combine(outputPath, "Contents", "Resources", "Data")
                : Path.Combine(Path.GetDirectoryName(outputPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(outputPath) + "_Data");
            var dataUnity3d = Path.Combine(dataRoot, "data.unity3d");
            var streamingAssets = Path.Combine(dataRoot, "StreamingAssets");
            dataUnity3dBytes = File.Exists(dataUnity3d) ? new FileInfo(dataUnity3d).Length : 0;
            streamingAssetsBytes = DirectorySize(streamingAssets);

            if (playerBytes > MaximumPlayerBytes)
            {
                throw new InvalidOperationException(
                    $"Player is {playerBytes} bytes; maximum is {MaximumPlayerBytes} bytes.");
            }

            if (dataUnity3dBytes + streamingAssetsBytes > MaximumDataAndStreamingBytes)
            {
                throw new InvalidOperationException(
                    $"data.unity3d + StreamingAssets is " +
                    $"{dataUnity3dBytes + streamingAssetsBytes} bytes; maximum is " +
                    $"{MaximumDataAndStreamingBytes} bytes.");
            }

            Debug.Log($"[BuildGate] Minimal Player: {playerBytes} bytes total; " +
                      $"data.unity3d={dataUnity3dBytes}; StreamingAssets={streamingAssetsBytes}.");
        }

        private static long DirectorySize(string path)
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length)
                : 0;
        }

        private static void ValidateRuntimeGeneratedWorldRendering()
        {
            var pipeline = AssetDatabase.LoadMainAssetAtPath(PcPipelineAssetPath);
            if (pipeline == null)
            {
                throw new FileNotFoundException(
                    $"PC render-pipeline asset is missing: {PcPipelineAssetPath}",
                    PcPipelineAssetPath);
            }

            var serialized = new SerializedObject(pipeline);
            var residentDrawer = serialized.FindProperty("m_GPUResidentDrawerMode");
            var occlusion = serialized.FindProperty(
                "m_GPUResidentDrawerEnableOcclusionCullingInCameras");
            if (residentDrawer == null || occlusion == null)
            {
                throw new InvalidOperationException(
                    $"GPU Resident Drawer settings are missing from {PcPipelineAssetPath}.");
            }

            // §41.1 / bug #111: the island is generated at runtime. Unity 6's
            // BRG occlusion path treated those MeshRenderers as fully occluded
            // in the macOS Player while skinned actors remained visible.
            if (residentDrawer.intValue != 0 || occlusion.boolValue)
            {
                throw new InvalidOperationException(
                    "GPU Resident Drawer and its camera occlusion must stay disabled for " +
                    "the runtime-generated HexLive world. Otherwise a release Player can " +
                    "render actors and palms while culling every hex and world prop.");
            }

            var runtimeLit = Shader.Find(RuntimeLitShaderName);
            if (runtimeLit == null)
            {
                throw new InvalidOperationException(
                    $"Required runtime shader is unavailable: {RuntimeLitShaderName}.");
            }

            var graphicsSettings = File.ReadAllText(GraphicsSettingsPath);
            if (!graphicsSettings.Contains(
                    $"guid: {RuntimeLitShaderGuid}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{RuntimeLitShaderName} must remain in GraphicsSettings always-included shaders. " +
                    "The minimal Player creates terrain and fallback materials at runtime, so Unity's " +
                    "normal scene-reference stripping cannot discover this dependency.");
            }

            Debug.Log("[BuildGate] Runtime-generated world uses classic MeshRenderer path " +
                      $"with {RuntimeLitShaderName} retained.");
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
