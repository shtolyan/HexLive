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
}
