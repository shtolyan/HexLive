using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexLive.Server.Bugs;

public sealed class BugComment
{
    [JsonPropertyName("whenUtc")] public string WhenUtc { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

public sealed class BugReport
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = BugStatuses.Created;
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("context")] public string Context { get; set; } = string.Empty;
    [JsonPropertyName("assignedAgent")] public string AssignedAgent { get; set; } = string.Empty;
    [JsonPropertyName("agentHandoff")] public string AgentHandoff { get; set; } = string.Empty;
    [JsonPropertyName("fixCommits")] public List<string> FixCommits { get; set; } = new();
    [JsonPropertyName("fixCommit")] public string FixCommit { get; set; } = string.Empty;
    [JsonPropertyName("reportedInVersion")] public string ReportedInVersion { get; set; } = string.Empty;
    [JsonPropertyName("readyForTestInVersion")] public string ReadyForTestInVersion { get; set; } = string.Empty;
    [JsonPropertyName("fixedInVersion")] public string FixedInVersion { get; set; } = string.Empty;
    [JsonPropertyName("archived")] public bool Archived { get; set; }
    [JsonPropertyName("comments")] public List<BugComment> Comments { get; set; } = new();
    [JsonPropertyName("revision")] public long Revision { get; set; }
}

public sealed class CreateBugRequest
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("context")] public string Context { get; set; } = string.Empty;
    [JsonPropertyName("reportedInVersion")] public string ReportedInVersion { get; set; } = string.Empty;
}

public sealed class UpdateBugRequest
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("assignedAgent")] public string? AssignedAgent { get; set; }
    [JsonPropertyName("agentHandoff")] public string? AgentHandoff { get; set; }
    [JsonPropertyName("fixCommits")] public List<string>? FixCommits { get; set; }
    [JsonPropertyName("readyForTestInVersion")] public string? ReadyForTestInVersion { get; set; }
    [JsonPropertyName("fixedInVersion")] public string? FixedInVersion { get; set; }
    [JsonPropertyName("archived")] public bool? Archived { get; set; }
    [JsonPropertyName("expectedRevision")] public long? ExpectedRevision { get; set; }
}

public sealed class AddBugCommentRequest
{
    [JsonPropertyName("author")] public string Author { get; set; } = "user";
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

public static class BugStatuses
{
    public const string Created = "created";
    public const string InProgress = "in_progress";
    public const string ReadyForTest = "ready_for_test";
    public const string Fixed = "fixed";
    public const string Rework = "rework";

    public static bool IsValid(string? value) => value is
        Created or InProgress or ReadyForTest or Fixed or Rework;
}
