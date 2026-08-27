using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HexLive.Server.Bugs;

/// <summary>§114 central, transactional bug store. One process owns the file.</summary>
public sealed class BugDatabase
{
    private static readonly object ProviderGate = new();
    private static bool _providerReady;
    private readonly string _connectionString;
    private readonly object _gate = new();

    public BugDatabase(string path)
    {
        lock (ProviderGate)
        {
            if (!_providerReady)
            {
                SQLitePCL.Batteries_V2.Init();
                _providerReady = true;
            }
        }
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Initialize();
    }

    public int CountActionable()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM bug_reports WHERE archived=0 AND status IN ('created','rework','in_progress','ready_for_test')";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<BugReport> List(string? status = null, bool includeArchived = true)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(status))
            {
                where.Add("status=$status");
                command.Parameters.AddWithValue("$status", status);
            }
            if (!includeArchived) where.Add("archived=0");
            command.CommandText = SelectReports +
                (where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where)) +
                " ORDER BY id";
            using var reader = command.ExecuteReader();
            var reports = new List<BugReport>();
            while (reader.Read()) reports.Add(ReadReport(reader));
            LoadChildren(connection, reports);
            return reports;
        }
    }

    public BugReport? Get(int id)
    {
        lock (_gate)
        {
            using var connection = Open();
            var report = Get(connection, id);
            if (report != null) LoadChildren(connection, new[] { report });
            return report;
        }
    }

    public BugReport Create(CreateBugRequest request)
    {
        var text = RequireText(request.Text, "text");
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO bug_reports(created_utc,status,text,context,reported_version)
VALUES($created,'created',$text,$context,$version);
SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$created", Now());
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$context", request.Context?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$version", request.ReportedInVersion?.Trim() ?? string.Empty);
            var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return Get(connection, id)!;
        }
    }

    public BugReport? Update(int id, UpdateBugRequest request)
    {
        if (request.Status != null && !BugStatuses.IsValid(request.Status))
            throw new InvalidDataException("Unknown bug status.");
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var current = Get(connection, id, transaction);
            if (current == null) return null;
            if (request.ExpectedRevision.HasValue && request.ExpectedRevision.Value != current.Revision)
                throw new BugRevisionConflictException(current.Revision);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE bug_reports SET
 text=$text,status=$status,assigned_agent=$agent,agent_handoff=$handoff,
 ready_version=$ready,fixed_version=$fixed,archived=$archived,revision=revision+1
WHERE id=$id";
            command.Parameters.AddWithValue("$text", request.Text == null ? current.Text : RequireText(request.Text, "text"));
            command.Parameters.AddWithValue("$status", request.Status ?? current.Status);
            command.Parameters.AddWithValue("$agent", request.AssignedAgent ?? current.AssignedAgent);
            command.Parameters.AddWithValue("$handoff", request.AgentHandoff ?? current.AgentHandoff);
            command.Parameters.AddWithValue("$ready", request.ReadyForTestInVersion ?? current.ReadyForTestInVersion);
            command.Parameters.AddWithValue("$fixed", request.FixedInVersion ?? current.FixedInVersion);
            command.Parameters.AddWithValue("$archived", (request.Archived ?? current.Archived) ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();

            if (request.FixCommits != null)
            {
                using var clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM bug_fix_commits WHERE report_id=$id";
                clear.Parameters.AddWithValue("$id", id);
                clear.ExecuteNonQuery();
                InsertCommits(connection, transaction, id, request.FixCommits);
            }
            transaction.Commit();
            var result = Get(connection, id)!;
            LoadChildren(connection, new[] { result });
            return result;
        }
    }

    public BugReport? AddComment(int id, AddBugCommentRequest request)
    {
        var text = RequireText(request.Text, "comment text");
        var author = string.IsNullOrWhiteSpace(request.Author) ? "user" : request.Author.Trim();
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (Get(connection, id, transaction) == null) return null;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO bug_comments(report_id,ordinal,when_utc,author,text)
VALUES($id,COALESCE((SELECT MAX(ordinal)+1 FROM bug_comments WHERE report_id=$id),0),$when,$author,$text);
UPDATE bug_reports SET revision=revision+1 WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$when", Now());
            command.Parameters.AddWithValue("$author", author);
            command.Parameters.AddWithValue("$text", text);
            command.ExecuteNonQuery();
            transaction.Commit();
            var result = Get(connection, id)!;
            LoadChildren(connection, new[] { result });
            return result;
        }
    }

    public BugReport? EditComment(int id, int ordinal, AddBugCommentRequest request, bool userOnly)
    {
        var text = RequireText(request.Text, "comment text");
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = userOnly
                ? "UPDATE bug_comments SET text=$text,when_utc=$when WHERE report_id=$id AND ordinal=$n AND author='user'; UPDATE bug_reports SET revision=revision+changes() WHERE id=$id"
                : "UPDATE bug_comments SET text=$text,when_utc=$when WHERE report_id=$id AND ordinal=$n; UPDATE bug_reports SET revision=revision+changes() WHERE id=$id";
            command.Parameters.AddWithValue("$text", text); command.Parameters.AddWithValue("$when", Now());
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$n", ordinal);
            command.ExecuteNonQuery();
            var result=Get(connection,id);
            if(result!=null) LoadChildren(connection,new[]{result});
            return result;
        }
    }

    public bool Delete(int id)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM bug_reports WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() != 0;
        }
    }

    public void ImportJsonOnce(string path)
    {
        if (!File.Exists(path)) return;
        lock (_gate)
        {
            using var connection = Open();
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT value FROM bug_meta WHERE key='legacy_json_imported'";
            if (check.ExecuteScalar() != null) return;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("reports", out var reports) || reports.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("BUGS.json has no reports array.");
            using var transaction = connection.BeginTransaction();
            foreach (var element in reports.EnumerateArray()) ImportReport(connection, transaction, element);
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO bug_meta(key,value) VALUES('legacy_json_imported',$value)";
            mark.Parameters.AddWithValue("$value", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            mark.ExecuteNonQuery();
            transaction.Commit();
            Console.WriteLine($"[bugs] imported {reports.GetArrayLength()} reports from {Path.GetFullPath(path)}");
        }
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS bug_reports(
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 created_utc TEXT NOT NULL,
 status TEXT NOT NULL,
 text TEXT NOT NULL,
 context TEXT NOT NULL DEFAULT '',
 assigned_agent TEXT NOT NULL DEFAULT '',
 agent_handoff TEXT NOT NULL DEFAULT '',
 fix_commit TEXT NOT NULL DEFAULT '',
 reported_version TEXT NOT NULL DEFAULT '',
 ready_version TEXT NOT NULL DEFAULT '',
 fixed_version TEXT NOT NULL DEFAULT '',
 archived INTEGER NOT NULL DEFAULT 0,
 revision INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS bug_comments(
 report_id INTEGER NOT NULL REFERENCES bug_reports(id) ON DELETE CASCADE,
 ordinal INTEGER NOT NULL,
 when_utc TEXT NOT NULL,
 author TEXT NOT NULL,
 text TEXT NOT NULL,
 PRIMARY KEY(report_id,ordinal)
);
CREATE TABLE IF NOT EXISTS bug_fix_commits(
 report_id INTEGER NOT NULL REFERENCES bug_reports(id) ON DELETE CASCADE,
 ordinal INTEGER NOT NULL,
 sha TEXT NOT NULL,
 PRIMARY KEY(report_id,ordinal), UNIQUE(report_id,sha)
);
CREATE TABLE IF NOT EXISTS bug_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_bug_reports_status ON bug_reports(status,archived,id);";
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private const string SelectReports = @"SELECT id,created_utc,status,text,context,assigned_agent,
agent_handoff,fix_commit,reported_version,ready_version,fixed_version,archived,revision FROM bug_reports";

    private static BugReport ReadReport(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0), CreatedUtc = reader.GetString(1), Status = reader.GetString(2),
        Text = reader.GetString(3), Context = reader.GetString(4), AssignedAgent = reader.GetString(5),
        AgentHandoff = reader.GetString(6), FixCommit = reader.GetString(7),
        ReportedInVersion = reader.GetString(8), ReadyForTestInVersion = reader.GetString(9),
        FixedInVersion = reader.GetString(10), Archived = reader.GetInt32(11) != 0,
        Revision = reader.GetInt64(12),
    };

    private static BugReport? Get(SqliteConnection connection, int id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectReports + " WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadReport(reader) : null;
    }

    private static void LoadChildren(SqliteConnection connection, IEnumerable<BugReport> source)
    {
        foreach (var report in source)
        {
            using var comments = connection.CreateCommand();
            comments.CommandText = "SELECT when_utc,author,text FROM bug_comments WHERE report_id=$id ORDER BY ordinal";
            comments.Parameters.AddWithValue("$id", report.Id);
            using (var reader = comments.ExecuteReader())
                while (reader.Read()) report.Comments.Add(new BugComment { WhenUtc=reader.GetString(0), Author=reader.GetString(1), Text=reader.GetString(2) });

            using var commits = connection.CreateCommand();
            commits.CommandText = "SELECT sha FROM bug_fix_commits WHERE report_id=$id ORDER BY ordinal";
            commits.Parameters.AddWithValue("$id", report.Id);
            using var commitReader = commits.ExecuteReader();
            while (commitReader.Read()) report.FixCommits.Add(commitReader.GetString(0));
        }
    }

    private static void ImportReport(SqliteConnection connection, SqliteTransaction transaction, JsonElement e)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT OR IGNORE INTO bug_reports
(id,created_utc,status,text,context,assigned_agent,agent_handoff,fix_commit,reported_version,ready_version,fixed_version,archived)
VALUES($id,$created,$status,$text,$context,$agent,$handoff,$legacy,$reported,$ready,$fixed,$archived)";
        command.Parameters.AddWithValue("$id", Int(e,"id"));
        command.Parameters.AddWithValue("$created", Str(e,"createdUtc"));
        command.Parameters.AddWithValue("$status", Str(e,"status", BugStatuses.Created));
        command.Parameters.AddWithValue("$text", Str(e,"text"));
        command.Parameters.AddWithValue("$context", Str(e,"context"));
        command.Parameters.AddWithValue("$agent", Str(e,"assignedAgent"));
        command.Parameters.AddWithValue("$handoff", Str(e,"agentHandoff"));
        command.Parameters.AddWithValue("$legacy", Str(e,"fixCommit"));
        command.Parameters.AddWithValue("$reported", Str(e,"reportedInVersion"));
        command.Parameters.AddWithValue("$ready", Str(e,"readyForTestInVersion"));
        command.Parameters.AddWithValue("$fixed", Str(e,"fixedInVersion"));
        command.Parameters.AddWithValue("$archived", Bool(e,"archived") ? 1 : 0);
        command.ExecuteNonQuery();
        var id = Int(e,"id");
        if (e.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Array)
        {
            var n=0;
            foreach (var c in comments.EnumerateArray())
            {
                using var insert=connection.CreateCommand(); insert.Transaction=transaction;
                insert.CommandText="INSERT OR IGNORE INTO bug_comments(report_id,ordinal,when_utc,author,text) VALUES($id,$n,$when,$author,$text)";
                insert.Parameters.AddWithValue("$id",id); insert.Parameters.AddWithValue("$n",n++);
                insert.Parameters.AddWithValue("$when",Str(c,"whenUtc")); insert.Parameters.AddWithValue("$author",Str(c,"author")); insert.Parameters.AddWithValue("$text",Str(c,"text")); insert.ExecuteNonQuery();
            }
        }
        if (e.TryGetProperty("fixCommits", out var commits) && commits.ValueKind == JsonValueKind.Array)
            InsertCommits(connection, transaction, id, commits.EnumerateArray().Select(x=>x.GetString() ?? string.Empty));
    }

    private static void InsertCommits(SqliteConnection connection, SqliteTransaction transaction, int id, IEnumerable<string> commits)
    {
        var n=0;
        foreach (var sha in commits.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            using var insert=connection.CreateCommand(); insert.Transaction=transaction;
            insert.CommandText="INSERT INTO bug_fix_commits(report_id,ordinal,sha) VALUES($id,$n,$sha)";
            insert.Parameters.AddWithValue("$id",id); insert.Parameters.AddWithValue("$n",n++); insert.Parameters.AddWithValue("$sha",sha.Trim()); insert.ExecuteNonQuery();
        }
    }

    private static string RequireText(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"Empty {field}.") : value.Trim();
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    private static string Str(JsonElement e,string n,string fallback="") => e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.String ? v.GetString()??fallback : fallback;
    private static int Int(JsonElement e,string n) => e.TryGetProperty(n,out var v)&&v.TryGetInt32(out var x) ? x : 0;
    private static bool Bool(JsonElement e,string n) => e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.True;
}

public sealed class BugRevisionConflictException : Exception
{
    public BugRevisionConflictException(long actual) : base("The report changed; reload it before saving.") => ActualRevision = actual;
    public long ActualRevision { get; }
}
