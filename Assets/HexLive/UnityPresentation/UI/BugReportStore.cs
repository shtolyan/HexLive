using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using UnityEngine;
using HexLive.UnityPresentation.Bootstrap;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// HTTP client for the central §114 SQLite bug tracker. The game keeps only
    /// an in-memory view and never writes BUGS.json.
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
            public long revision;
        }

        [Serializable]
        private sealed class FileModel
        {
            public List<Report> reports = new();
        }

        private static FileModel _model;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

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

        // Последний сырой ответ GET /reports. Сравнение СТРОК заменяет прежний
        // JsonUtility.ToJson обеих моделей целиком «только чтобы сравнить» —
        // вместе с синхронным HTTP это давало циклический провал кадра
        // 600–800 мс каждые 2 секунды (профайл 2026-08-28: 660 мс из 705 в
        // DebugControlsPanel.Update).
        private static string _lastServerJson;
        private static System.Threading.Tasks.Task<string> _refresh;

        /// <summary>
        /// Poll the server WITHOUT blocking the frame: kicks an async GET and
        /// reports the previous poll's outcome. Returns true when the in-memory
        /// list was replaced, so the panel knows to rebuild. Parsing happens on
        /// the main thread, but only when the raw payload actually changed.
        /// </summary>
        public static bool CheckExternalChange()
        {
            if (_model == null)
            {
                return false;
            }

            if (_refresh == null)
            {
                // Токен и endpoint читаются ЗДЕСЬ, на главном потоке —
                // SessionConfig/ServerBook не для тредпула.
                _refresh = FetchReportsTextAsync(ApiEndpoint(), CurrentToken());
                return false;
            }

            if (!_refresh.IsCompleted)
            {
                return false;
            }

            string json = null;
            if (_refresh.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
            {
                json = _refresh.Result;
            }
            _refresh = null;
            if (string.IsNullOrEmpty(json) || json == _lastServerJson)
            {
                return false;
            }

            var fresh = JsonUtility.FromJson<FileModel>("{\"reports\":" + json + "}");
            if (fresh?.reports == null)
            {
                return false;
            }

            _lastServerJson = json;
            _model = fresh;
            NormalizeModel();
            return true;
        }

        private static async System.Threading.Tasks.Task<string> FetchReportsTextAsync(
            string endpoint, string token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/reports");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                // Недоступный трекер — не событие для каждого опроса; синхронный
                // путь (EnsureLoaded/мутации) свой warning уже пишет.
                return null;
            }
        }

        public static Report Add(string text, string context)
        {
            var request = new CreateRequest
            {
                text = text,
                context = context,
                reportedInVersion = Application.version
            };
            var report = Send<Report>(HttpMethod.Post, "/reports", JsonUtility.ToJson(request), false);
            if (report == null) return null;
            EnsureLoaded();
            _model.reports.Add(report);
            return report;
        }

        public static void SetArchived(int id, bool archived)
        {
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

            var updated = Send<Report>(HttpMethod.Post, $"/reports/{id}",
                "{\"archived\":" + (archived ? "true" : "false") + "}", true);
            Replace(updated);
        }

        public static void AddComment(int id, string text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return;
            }

            Replace(Send<Report>(HttpMethod.Post, $"/reports/{id}/comments",
                JsonUtility.ToJson(new CommentRequest { author="user", text=trimmed }), true));
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

            var report = Find(id);
            if (report == null || report.text == trimmed)
            {
                return;
            }

            Replace(Send<Report>(HttpMethod.Post, $"/reports/{id}",
                JsonUtility.ToJson(new TextRequest { text=trimmed }), true));
        }

        public static void EditUserComment(int id, int commentIndex, string text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return;
            }

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

            Replace(Send<Report>(HttpMethod.Post, $"/reports/{id}/comments/{commentIndex}",
                JsonUtility.ToJson(new CommentRequest { author="user", text=trimmed }), true));
        }

        public static void SendToRework(int id, string comment)
        {
            var r = Find(id);
            if (r != null && r.status == StatusReadyForTest)
            {
                Replace(Send<Report>(HttpMethod.Post, $"/reports/{id}",
                    "{\"status\":\"rework\",\"archived\":false,\"readyForTestInVersion\":\"\",\"fixedInVersion\":\"\"}", true));
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    AddComment(id, comment);
                }
            }
        }

        /// <summary>Player confirmation after testing a ready report.</summary>
        public static void MarkFixed(int id)
        {
            var report = Find(id);
            if (report == null || report.status != StatusReadyForTest)
            {
                return;
            }

            Replace(Send<Report>(HttpMethod.Post, $"/reports/{id}",
                "{\"status\":\"fixed\"}", true));
            AddComment(id, "Подтверждено пользователем: исправлено.");
        }

        /// <summary>
        /// Snapshot the reports whose completed code is eligible for the next
        /// build. The build pipeline owns the snapshot so a report completed
        /// while a build is already running cannot be stamped by that build.
        /// </summary>
        public static List<int> CaptureReadyForTestReportIds()
        {
            EnsureLoaded();
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

            EnsureLoaded();
            // Не итерировать живой _model.reports: Replace после Send может
            // ДОПОЛНИТЬ этот же список (Add, когда id не нашёлся после
            // серверной перезагрузки модели) — сборка v0.1.78 упала ровно
            // здесь с «Collection was modified». Идём по снапшоту id и ищем
            // каждый отчёт заново.
            var changed = false;
            foreach (var id in new HashSet<int>(reportIds))
            {
                var report = Find(id);
                if (report == null ||
                    (report.status != StatusReadyForTest && report.status != StatusFixed))
                {
                    continue;
                }

                if (report.readyForTestInVersion == version)
                {
                    continue;
                }

                var updated = Send<Report>(HttpMethod.Post, $"/reports/{id}",
                    JsonUtility.ToJson(new ReadyVersionRequest { readyForTestInVersion=version }), true);
                Replace(updated);
                changed = updated != null;
            }

            _ = changed;
        }

        private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");

        private static void EnsureLoaded()
        {
            if (_model != null)
            {
                return;
            }

            _model ??= new FileModel();
            TryReloadFromServer();
        }

        private static bool TryReloadFromServer()
        {
            try
            {
                var json = SendText(HttpMethod.Get, "/reports", null, true);
                if (string.IsNullOrEmpty(json)) return false;
                // Сырая строка ответа и есть признак изменения — сериализовать
                // обе модели целиком ради сравнения было половиной провала
                // кадра (см. CheckExternalChange).
                if (json == _lastServerJson) return false;
                var fresh = JsonUtility.FromJson<FileModel>("{\"reports\":" + json + "}");
                if (fresh?.reports == null) return false;
                _lastServerJson = json;
                _model = fresh;
                NormalizeModel();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Bug tracker API unavailable: {e.Message}");
                return false;
            }
        }

        private static string CurrentToken() =>
            System.Environment.GetEnvironmentVariable("HEXLIVE_BUG_TOKEN") ??
            SessionConfig.ControlToken ?? ServerBook.LastToken;

        private static T Send<T>(HttpMethod method, string path, string json, bool authenticated) where T : class
        {
            var text = SendText(method, path, json, authenticated);
            return string.IsNullOrEmpty(text) ? null : JsonUtility.FromJson<T>(text);
        }

        private static string SendText(HttpMethod method, string path, string json, bool authenticated)
        {
            using var request = new HttpRequestMessage(method, ApiEndpoint() + path);
            if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (authenticated)
            {
                var token = CurrentToken();
                if (!string.IsNullOrWhiteSpace(token))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            using var response = Http.SendAsync(request).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new IOException($"HTTP {(int)response.StatusCode}: {body}");
            return body;
        }

        private static string ApiEndpoint()
        {
            const string argument = "-hexlive-bugs-api";
            var args = System.Environment.GetCommandLineArgs();
            for (var i=0;i+1<args.Length;i++)
                if (string.Equals(args[i], argument, StringComparison.OrdinalIgnoreCase))
                    return args[i+1].TrimEnd('/');
            var websocket = new Uri(SessionConfig.ServerUrl ?? ServerBook.ProductionUrl, UriKind.Absolute);
            var builder = new UriBuilder(websocket)
            {
                Scheme = websocket.Scheme == "wss" ? "https" : "http",
                Path = "/api/bugs/v1", Query = string.Empty, Fragment = string.Empty
            };
            return builder.Uri.ToString().TrimEnd('/');
        }

        private static void Replace(Report report)
        {
            if (report == null) return;
            EnsureLoaded();
            for (var i=0;i<_model.reports.Count;i++)
                if (_model.reports[i].id==report.id) { _model.reports[i]=report; return; }
            _model.reports.Add(report);
        }

        [Serializable] private sealed class CreateRequest { public string text; public string context; public string reportedInVersion; }
        [Serializable] private sealed class TextRequest { public string text; }
        [Serializable] private sealed class ReadyVersionRequest { public string readyForTestInVersion; }
        [Serializable] private sealed class CommentRequest { public string author; public string text; }

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
