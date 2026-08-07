using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// The in-game bug tracker's storage: a single BUGS.json in the repo root
    /// (next to Assets/), shared between the game and the agent. The player
    /// files bugs from the BugReportPanel; the agent reads the same file in a
    /// session, moves them through the workflow and appends a comment.
    /// The file is the source of truth — the game reloads it whenever its
    /// mtime changes, so an external edit shows up without restarting Play.
    /// In a built player (no repo around) it falls back to persistentDataPath.
    /// </summary>
    public static class BugReportStore
    {
        public const string StatusCreated = "created";
        public const string StatusInProgress = "in_progress";
        public const string StatusReadyForTest = "ready_for_test";
        public const string StatusFixed = "fixed";
        public const string StatusRework = "rework";

        [Serializable]
        public sealed class Comment
        {
            public string whenUtc;
            public string author; // "user" | "codex" | "claude"
            public string text;
        }

        [Serializable]
        public sealed class Report
        {
            public int id;
            public string createdUtc;
            public string status;
            public string text;
            public string context; // seed/tick/selected NPC at submit time
            public string assignedAgent;
            public string agentHandoff;
            public List<string> fixCommits = new();
            // Compatibility with reports written before fixCommits existed.
            public string fixCommit;
            public string reportedInVersion;
            public string readyForTestInVersion;
            public string fixedInVersion;
            public bool archived;
            public List<Comment> comments = new();
        }

        [Serializable]
        private sealed class FileModel
        {
            public int nextId = 1;
            public List<Report> reports = new();
        }

        private static FileModel _model;
        private static DateTime _loadedMtimeUtc;
        private static string _filePath;

        // Every surface — editor Play, a local build, the agent — must share
        // ONE file, or bugs filed from a build land in a sandbox nobody reads
        // (that happened on day one: persistentDataPath swallowed report #1).
        // Resolution order: explicit -hexlive-bugs <path> → the repo root
        // (editor derives it, a dev build on this machine finds it by its
        // well-known path) → persistentDataPath as the last resort for a
        // build on a machine without the repo.
        public static string FilePath
        {
            get
            {
                if (_filePath != null)
                {
                    return _filePath;
                }

                // Fully qualified on purpose: this file sits in
                // HexLive.UnityPresentation.UI, and the project owns a
                // HexLive.UnityPresentation.Environment namespace. A namespace
                // member shadows a using-directive, so a bare `Environment`
                // binds to THAT and the assembly stops compiling.
                var args = System.Environment.GetCommandLineArgs();
                for (var i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "-hexlive-bugs")
                    {
                        return _filePath = Path.GetFullPath(args[i + 1]);
                    }
                }

#if UNITY_EDITOR
                return _filePath = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", "BUGS.json"));
#else
                const string devRepo = "/Volumes/ORICO/HexLive";
                return _filePath = Directory.Exists(devRepo)
                    ? Path.Combine(devRepo, "BUGS.json")
                    : Path.Combine(Application.persistentDataPath, "BUGS.json");
#endif
            }
        }

        public static IReadOnlyList<Report> Reports
        {
            get
            {
                EnsureLoaded();
                return _model.reports;
            }
        }

        public static int CountWithStatus(string status)
        {
            EnsureLoaded();
            var n = 0;
            foreach (var r in _model.reports)
            {
                if (r.status == status && !r.archived)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// Reloads from disk if the file changed since we last read it (the
        /// agent edits it from outside Play mode). Returns true when the
        /// in-memory list was replaced, so the panel knows to rebuild.
        /// </summary>
        public static bool CheckExternalChange()
        {
            if (_model == null)
            {
                return false;
            }

            var mtime = File.Exists(FilePath)
                ? File.GetLastWriteTimeUtc(FilePath)
                : DateTime.MinValue;
            if (mtime == _loadedMtimeUtc)
            {
                return false;
            }

            _model = null;
            EnsureLoaded();
            return true;
        }

        public static Report Add(string text, string context)
        {
            EnsureFreshForMutation();
            var report = new Report
            {
                id = _model.nextId++,
                createdUtc = Now(),
                status = StatusCreated,
                text = text,
                context = context,
                reportedInVersion = Application.version,
                archived = false
            };
            _model.reports.Add(report);
            Save();
            return report;
        }

        public static void SetArchived(int id, bool archived)
        {
            EnsureFreshForMutation();
            var report = Find(id);
            if (report == null)
            {
                return;
            }

            // Archive is history for player-confirmed fixes. It must never
            // hide an actionable report from the agent's queue.
            if (archived && report.status != StatusFixed)
            {
                return;
            }

            report.archived = archived;
            Save();
        }

        public static void AddComment(int id, string text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return;
            }

            EnsureFreshForMutation();
            var report = Find(id);
            if (report == null)
            {
                return;
            }

            report.comments ??= new List<Comment>();
            report.comments.Add(new Comment { whenUtc = Now(), author = "user", text = trimmed });
            Save();
        }

        /// <summary>
        /// Replaces only the player-authored report description. Identity,
        /// captured repro context and all workflow/history fields stay intact.
        /// </summary>
        public static void EditReportText(int id, string text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return;
            }

            EnsureFreshForMutation();
            var report = Find(id);
            if (report == null || report.text == trimmed)
            {
                return;
            }

            report.text = trimmed;
            Save();
        }

        public static void EditUserComment(int id, int commentIndex, string text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return;
            }

            EnsureFreshForMutation();
            var report = Find(id);
            if (report?.comments == null || commentIndex < 0 || commentIndex >= report.comments.Count)
            {
                return;
            }

            var comment = report.comments[commentIndex];
            if (!string.Equals(comment.author, "user", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            comment.text = trimmed;
            comment.whenUtc = Now();
            Save();
        }

        public static void SendToRework(int id, string comment)
        {
            EnsureFreshForMutation();
            var r = Find(id);
            if (r != null && r.status == StatusReadyForTest)
            {
                r.status = StatusRework;
                r.archived = false;
                r.readyForTestInVersion = null;
                r.fixedInVersion = null;
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    r.comments ??= new List<Comment>();
                    r.comments.Add(new Comment { whenUtc = Now(), author = "user", text = comment.Trim() });
                }

                Save();
            }
        }

        /// <summary>Player confirmation after testing a ready report.</summary>
        public static void MarkFixed(int id)
        {
            EnsureFreshForMutation();
            var report = Find(id);
            if (report == null || report.status != StatusReadyForTest)
            {
                return;
            }

            report.status = StatusFixed;
            report.comments ??= new List<Comment>();
            report.comments.Add(new Comment
            {
                whenUtc = Now(),
                author = "user",
                text = "Подтверждено пользователем: исправлено."
            });
            Save();
        }

        /// <summary>
        /// Snapshot the reports whose completed code is eligible for the next
        /// build. The build pipeline owns the snapshot so a report completed
        /// while a build is already running cannot be stamped by that build.
        /// </summary>
        public static List<int> CaptureReadyForTestReportIds()
        {
            EnsureFreshForMutation();
            var ids = new List<int>();
            foreach (var report in _model.reports)
            {
                if (!report.archived && report.status == StatusReadyForTest)
                {
                    ids.Add(report.id);
                }
            }

            return ids;
        }

        /// <summary>Called only after a successful build for its pre-build snapshot.</summary>
        public static void StampReadyForTestReports(IReadOnlyList<int> reportIds, string version)
        {
            if (reportIds == null || reportIds.Count == 0 || string.IsNullOrWhiteSpace(version))
            {
                return;
            }

            EnsureFreshForMutation();
            var ids = new HashSet<int>(reportIds);
            var changed = false;
            foreach (var report in _model.reports)
            {
                if (!ids.Contains(report.id) ||
                    (report.status != StatusReadyForTest && report.status != StatusFixed))
                {
                    continue;
                }

                if (report.readyForTestInVersion == version)
                {
                    continue;
                }

                report.readyForTestInVersion = version;
                changed = true;
            }

            if (changed)
            {
                Save();
            }
        }

        private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");

        private static void EnsureLoaded()
        {
            if (_model != null)
            {
                return;
            }

            RecoverInterruptedReplace(FilePath);
            if (File.Exists(FilePath))
            {
                try
                {
                    _model = JsonUtility.FromJson<FileModel>(File.ReadAllText(FilePath));
                    NormalizeModel();
                }
                catch (Exception e)
                {
                    // A malformed file must not eat the player's bug list —
                    // keep it on disk untouched and start an empty session copy.
                    Debug.LogError($"BUGS.json parse failed, leaving file as-is: {e.Message}");
                }

                _loadedMtimeUtc = File.GetLastWriteTimeUtc(FilePath);
            }

            _model ??= new FileModel();
            NormalizeModel();
        }

        private static void Save()
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, JsonUtility.ToJson(_model, prettyPrint: true) + "\n");
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch (Exception e) when (e is PlatformNotSupportedException or IOException)
            {
                // Unity's API profile has no File.Move(source, destination,
                // overwrite). Keep a recoverable two-rename fallback for
                // platforms where File.Replace is unavailable.
                var backupPath = path + ".replace-backup";
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                if (File.Exists(path))
                {
                    File.Move(path, backupPath);
                }

                try
                {
                    File.Move(tempPath, path);
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                catch
                {
                    if (!File.Exists(path) && File.Exists(backupPath))
                    {
                        File.Move(backupPath, path);
                    }

                    throw;
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            _loadedMtimeUtc = File.GetLastWriteTimeUtc(path);
        }

        private static void RecoverInterruptedReplace(string path)
        {
            var backupPath = path + ".replace-backup";
            if (!File.Exists(path) && File.Exists(backupPath))
            {
                File.Move(backupPath, path);
            }
        }

        private static void EnsureFreshForMutation()
        {
            EnsureLoaded();
            var mtime = File.Exists(FilePath)
                ? File.GetLastWriteTimeUtc(FilePath)
                : DateTime.MinValue;
            if (mtime == _loadedMtimeUtc)
            {
                return;
            }

            _model = null;
            EnsureLoaded();
        }

        private static Report Find(int id)
        {
            foreach (var report in _model.reports)
            {
                if (report.id == id)
                {
                    return report;
                }
            }

            return null;
        }

        private static void NormalizeModel()
        {
            _model ??= new FileModel();
            _model.reports ??= new List<Report>();
            foreach (var report in _model.reports)
            {
                report.comments ??= new List<Comment>();
                report.fixCommits ??= new List<string>();
                if (!string.IsNullOrEmpty(report.fixCommit) && !report.fixCommits.Contains(report.fixCommit))
                {
                    report.fixCommits.Add(report.fixCommit);
                }
                if (!IsKnownStatus(report.status))
                {
                    report.status = StatusCreated;
                }
                if (report.status == StatusRework)
                {
                    // Rework is always actionable, even if an older client
                    // archived the report before returning it.
                    report.archived = false;
                }
            }
        }

        private static bool IsKnownStatus(string status) =>
            status == StatusCreated || status == StatusInProgress ||
            status == StatusReadyForTest || status == StatusRework ||
            status == StatusFixed;
    }
}
