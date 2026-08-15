using System;
using System.Collections.Generic;
using System.IO;
using HexLive.UnityPresentation.UI;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Reserves one patch version before a build and only commits it after the
    /// build succeeds. A failed build leaves the reservation in Library so the
    /// retry uses exactly the same version.
    /// </summary>
    public sealed class HexLiveBuildVersioning : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string PendingRelativePath = "Library/HexLivePendingBuildVersion.txt";
        private const string PendingBugSnapshotRelativePath = "Library/HexLivePendingBugBuildSnapshot.json";

        [Serializable]
        private sealed class PendingBugSnapshot
        {
            public string version;
            public List<int> readyForTestReportIds = new();
        }

        public int callbackOrder => -1000;

        /// <summary>
        /// Scene-specific sandbox players are disposable test artefacts, not
        /// releases. Their builder sets this flag for its own Unity process so
        /// they cannot consume a release version or stamp the bug tracker.
        /// </summary>
        internal static bool SuppressForCurrentProcess { get; set; }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (SuppressForCurrentProcess)
            {
                Debug.Log("HexLive build versioning suppressed for a scene-specific sandbox build.");
                return;
            }

            var pending = ReadPendingVersion();
            if (string.IsNullOrEmpty(pending))
            {
                pending = NextPatch(PlayerSettings.bundleVersion);
                WritePendingVersion(pending);
            }

            PlayerSettings.bundleVersion = pending;
            // Platform build numbers must be synchronized before BuildPipeline
            // writes Info.plist/AndroidManifest.xml into the player artifact.
            // CommitSuccessfulBuild repeats this after success so the project
            // settings stay aligned with the published version.
            SyncPlatformBuildNumbers(pending);
            WritePendingBugSnapshot(pending, BugReportStore.CaptureReadyForTestReportIds());
            AssetDatabase.SaveAssets();
            Debug.Log($"HexLive build version reserved: {pending}");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (SuppressForCurrentProcess)
            {
                return;
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                return;
            }

            CommitSuccessfulBuild();
        }

        /// <summary>
        /// Finalize a successful build. The command-line builder calls this
        /// explicitly because Unity 6 can omit the postprocess interface when
        /// the same callback instance also handled preprocess. Idempotent so
        /// ordinary Editor builds may still invoke the interface first.
        /// </summary>
        internal static void CommitSuccessfulBuild()
        {
            var snapshot = ReadPendingBugSnapshot();

            var pending = ReadPendingVersion();
            if (string.IsNullOrEmpty(pending))
            {
                pending = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.version)
                    ? snapshot.version
                    : null;
            }

            if (string.IsNullOrEmpty(pending) && snapshot == null)
            {
                return;
            }

            pending ??= PlayerSettings.bundleVersion;
            PlayerSettings.bundleVersion = pending;
            SyncPlatformBuildNumbers(pending);
            if (snapshot != null && snapshot.version == pending)
            {
                BugReportStore.StampReadyForTestReports(snapshot.readyForTestReportIds, pending);
            }
            AssetDatabase.SaveAssets();

            var path = PendingPath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var snapshotPath = PendingBugSnapshotPath;
            if (File.Exists(snapshotPath))
            {
                File.Delete(snapshotPath);
            }

            Debug.Log($"HexLive build version committed: {pending}");
        }

        internal static string NextPatch(string current)
        {
            var normalized = string.IsNullOrWhiteSpace(current)
                ? "0.1.0"
                : current.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(normalized, out var version))
            {
                throw new BuildFailedException($"Unsupported HexLive version '{current}'. Expected 0.1.N.");
            }

            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build) + 1}";
        }

        private static string PendingPath => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", PendingRelativePath));

        private static string ReadPendingVersion()
        {
            var path = PendingPath;
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        private static void WritePendingVersion(string version)
        {
            var path = PendingPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            File.WriteAllText(path, version + "\n");
        }

        private static string PendingBugSnapshotPath => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", PendingBugSnapshotRelativePath));

        private static PendingBugSnapshot ReadPendingBugSnapshot()
        {
            var path = PendingBugSnapshotPath;
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<PendingBugSnapshot>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"HexLive bug-build snapshot could not be read: {e.Message}");
                return null;
            }
        }

        private static void WritePendingBugSnapshot(string version, List<int> reportIds)
        {
            var path = PendingBugSnapshotPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            File.WriteAllText(path, JsonUtility.ToJson(new PendingBugSnapshot
            {
                version = version,
                readyForTestReportIds = reportIds ?? new List<int>()
            }, prettyPrint: true) + "\n");
        }

        private static void SyncPlatformBuildNumbers(string version)
        {
            if (!Version.TryParse(version, out var parsed))
            {
                throw new BuildFailedException($"Cannot synchronize platform build numbers for '{version}'.");
            }

            var number = Math.Max(1, parsed.Build);
            PlayerSettings.macOS.buildNumber = number.ToString();
            PlayerSettings.iOS.buildNumber = number.ToString();
            PlayerSettings.Android.bundleVersionCode = number;
        }
    }
}
