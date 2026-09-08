using System;
using System.IO;
using System.Linq;
using HexLive.Server.Bugs;
using NUnit.Framework;

namespace HexLive.Server.Tests;

[TestFixture]
public sealed class BugDatabaseTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory=Path.Combine(Path.GetTempPath(),"hexlive-bugs-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory,true);

    [Test]
    public void CreateUpdateCommentAndDeleteAreTransactional()
    {
        var db=new BugDatabase(Path.Combine(_directory,"bugs.sqlite3"));
        var created=db.Create(new CreateBugRequest { Text="сломалось",Context="seed=7 tick=12",ReportedInVersion="0.9" });
        Assert.That(created.Id,Is.EqualTo(1));
        var updated=db.Update(created.Id,new UpdateBugRequest { Status=BugStatuses.InProgress,AssignedAgent="/root",ExpectedRevision=created.Revision });
        Assert.That(updated!.Revision,Is.EqualTo(created.Revision+1));
        db.AddComment(created.Id,new AddBugCommentRequest { Author="codex",Text="Взял в работу." });
        var read=db.Get(created.Id)!;
        Assert.That(read.Comments.Single().Author,Is.EqualTo("codex"));
        Assert.That(read.Status,Is.EqualTo(BugStatuses.InProgress));
        Assert.That(db.Delete(created.Id),Is.True);
        Assert.That(db.Get(created.Id),Is.Null);
    }

    [Test]
    public void LegacyImportRunsOnceAndPreservesIdsAndHistory()
    {
        var json=Path.Combine(_directory,"BUGS.json");
        File.WriteAllText(json,"""
        {"nextId":10,"reports":[{"id":7,"createdUtc":"now","status":"ready_for_test","text":"x","context":"seed=1","fixCommits":["abc"],"comments":[{"whenUtc":"then","author":"codex","text":"done"}]}]}
        """);
        var db=new BugDatabase(Path.Combine(_directory,"bugs.sqlite3"));
        db.ImportJsonOnce(json);
        db.ImportJsonOnce(json);
        var report=db.Get(7)!;
        Assert.That(report.FixCommits,Is.EqualTo(new[]{"abc"}));
        Assert.That(report.Comments,Has.Count.EqualTo(1));
        Assert.That(db.Create(new CreateBugRequest { Text="next" }).Id,Is.EqualTo(8));
    }

    [Test]
    public void CommitPatchIsStoredOncePerShaAndFoundByPrefix()
    {
        var db=new BugDatabase(Path.Combine(_directory,"bugs.sqlite3"));
        var sha=new string('a',40);
        var stored=db.PutCommitPatch(new BugCommitPatch
        {
            Sha=sha.ToUpperInvariant(),Subject="fix(bug-1): x",Message="fix(bug-1): x\n\nBug: #1",Author="me",WhenUtc="2026-09-03",
            Files={new BugCommitFile{Path="A.cs",Added=3,Deleted=1}},Patch="diff --git a/A.cs b/A.cs\n@@ -1 +1 @@\n-a\n+b\n",
        });
        Assert.That(stored.Sha,Is.EqualTo(sha));
        Assert.That(stored.StoredUtc,Is.Not.Empty);

        db.PutCommitPatch(new BugCommitPatch { Sha=sha,Subject="replaced",Patch="p" });
        Assert.That(db.ListCommitPatchShas(),Is.EqualTo(new[]{sha}));
        Assert.That(db.GetCommitPatch(sha.Substring(0,9))!.Subject,Is.EqualTo("replaced"),"old reports carry short SHAs");
        Assert.That(db.GetCommitPatch("abc"),Is.Null,"too short to be a commit reference");

        var patches=db.GetCommitPatches(new[]{sha.Substring(0,9),new string('b',40)});
        Assert.That(patches.Keys,Is.EqualTo(new[]{sha.Substring(0,9)}),"keyed as written on the report; missing ones are absent, not null");
    }

    [Test]
    public void CommitPatchRejectsBadShaAndAmbiguousPrefixAndCapsSize()
    {
        var db=new BugDatabase(Path.Combine(_directory,"bugs.sqlite3"));
        Assert.Throws<InvalidDataException>(()=>db.PutCommitPatch(new BugCommitPatch { Sha="9e257f5d2" }),"a short SHA cannot be a storage key");
        Assert.Throws<InvalidDataException>(()=>db.PutCommitPatch(new BugCommitPatch { Sha=new string('z',40) }));

        var big=db.PutCommitPatch(new BugCommitPatch { Sha=new string('c',40),Patch=new string('x',BugCommitPatch.MaxPatchChars+10) });
        Assert.That(big.Patch.Length,Is.EqualTo(BugCommitPatch.MaxPatchChars));
        Assert.That(big.Truncated,Is.True);

        db.PutCommitPatch(new BugCommitPatch { Sha="c"+new string('d',39) });
        Assert.That(db.GetCommitPatch("c"),Is.Null);
        Assert.That(db.GetCommitPatch("cccccccc"),Is.Not.Null);
        Assert.That(db.GetCommitPatch("cdcdcdcd"),Is.Null,"no such commit");
        Assert.That(db.GetCommitPatches(new[]{"cccccccc"}).Count,Is.EqualTo(1));
    }
}
