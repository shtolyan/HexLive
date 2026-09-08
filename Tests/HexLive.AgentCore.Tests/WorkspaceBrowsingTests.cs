using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class WorkspaceBrowsingTests
{
    [Test] public async Task ImportedDocumentsAndActivityAreReadableWithoutStartingOrRewritingMemory()
    {
        var root = Directory.CreateTempSubdirectory("studio-browse-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, ".state"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "memory", "imports", "iphone"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "SOUL.md"), "Original soul");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "MEMORY.md"), "Original memories");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "memory", "imports", "iphone", "diary.md"), "Room and island");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, ".state", "private.md"), "Private");
            var state = Path.Combine(root.FullName, ".state", "state.json");
            const string json = """
                {"worlds":[{"label":"Island","lastSeenUtc":"2026-09-08T12:00:00Z","lastIntentSummary":"Help Elsa",
                "journal":[{"createdAtUtc":"2026-09-07T12:00:00Z","text":"I remember the room"}]}]}
                """;
            await File.WriteAllTextAsync(state, json);
            var documents = new WorkspaceDocuments(root.FullName);
            Assert.That(documents.ListDocuments(), Is.EqualTo(new[] { "MEMORY.md", "SOUL.md", "memory/imports/iphone/diary.md" }));
            Assert.That((await documents.ReadAsync("SOUL.md")).Text, Is.EqualTo("Original soul"));
            var activity = await WorkspaceActivity.ReadAsync(root.FullName);
            Assert.That(activity.Select(x => x.Text), Is.EqualTo(new[] { "Help Elsa", "I remember the room" }));
            Assert.That(activity[0].IsIntent, Is.True);
            Assert.That(await File.ReadAllTextAsync(state), Is.EqualTo(json));
            Assert.That(File.Exists(Path.Combine(root.FullName, ".agent-studio.lock")), Is.False);
        }
        finally { root.Delete(true); }
    }
    [Test] public async Task EmptyWorkspaceDoesNotCreateMemoryOrMixProfiles()
    {
        var root = Directory.CreateTempSubdirectory("studio-empty-");
        try
        {
            Assert.That(new WorkspaceDocuments(root.FullName).ListDocuments(), Is.Empty);
            Assert.That(await WorkspaceActivity.ReadAsync(root.FullName), Is.Empty);
            Assert.That(Directory.EnumerateFileSystemEntries(root.FullName), Is.Empty);
        }
        finally { root.Delete(true); }
    }
}
