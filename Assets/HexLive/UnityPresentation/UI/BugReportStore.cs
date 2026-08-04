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
    /// session, fixes things, flips status to "fixed" and appends a comment.
    /// The file is the source of truth — the game reloads it whenever its
    /// mtime changes, so an external edit shows up without restarting Play.
    /// In a built player (no repo around) it falls back to persistentDataPath.
    /// </summary>
    public static class BugReportStore
    {
        public const string StatusCreated = "created";
        public const string StatusFixed = "fixed";
        public const string StatusRework = "rework";

        [Serializable]
        public sealed class Comment
        {
            public string whenUtc;
            public string author; // "user" | "claude"
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

        public static string FilePath
        {
            get
            {
#if UNITY_EDITOR
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BUGS.json"));
#else
                return Path.Combine(Application.persistentDataPath, "BUGS.json");
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
                if (r.status == status)
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
            EnsureLoaded();
            var report = new Report
            {
                id = _model.nextId++,
                createdUtc = Now(),
                status = StatusCreated,
                text = text,
                context = context
            };
            _model.reports.Add(report);
            Save();
            return report;
        }

        public static void Remove(int id)
        {
            EnsureLoaded();
            _model.reports.RemoveAll(r => r.id == id);
            Save();
        }

        public static void SendToRework(int id, string comment)
        {
            EnsureLoaded();
            foreach (var r in _model.reports)
            {
                if (r.id != id)
                {
                    continue;
                }

                r.status = StatusRework;
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    r.comments.Add(new Comment { whenUtc = Now(), author = "user", text = comment.Trim() });
                }

                Save();
                return;
            }
        }

        private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");

        private static void EnsureLoaded()
        {
            if (_model != null)
            {
                return;
            }

            if (File.Exists(FilePath))
            {
                try
                {
                    _model = JsonUtility.FromJson<FileModel>(File.ReadAllText(FilePath));
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
        }

        private static void Save()
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(_model, prettyPrint: true) + "\n");
            _loadedMtimeUtc = File.GetLastWriteTimeUtc(FilePath);
        }
    }
}
