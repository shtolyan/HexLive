using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Server.Bugs;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace HexLive.Server.Tests;

[TestFixture]
public sealed class BugAdminPageTests
{
    [Test]
    public void RussianNoticeIsEncodedIntoRedirectLocation()
    {
        var result=BugAdminEndpoints.RedirectWithNotice("/admin/bugs/7", "Сохранено");

        Assert.That(result,Is.TypeOf<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>());
        var redirect=(Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult)result;
        Assert.That(redirect.Url,Is.EqualTo("/admin/bugs/7?notice="+Uri.EscapeDataString("Сохранено")));
        Assert.That(redirect.Url!.All(c=>c<=127),Is.True,
            "Location must contain ASCII only; raw Cyrillic produced the 404 regression.");
    }

    [Test]
    public void ListShowsAccessibleAutoClosingNoticeAndNewestReport()
    {
        var report=new BugReport { Id=7,Text="новый баг",Status=BugStatuses.Created };

        var html=BugAdminPages.List(new[]{report},string.Empty,string.Empty,"Отправлено");

        Assert.That(html,Does.Contain("role='status'").And.Contain("Отправлено"));
        Assert.That(html,Does.Contain("setTimeout").And.Contain("/admin/bugs/7"));
    }

    [Test]
    public void ListUsesStickyFiltersModalCreationAndPagination()
    {
        var reports=Enumerable.Range(1,30)
            .Select(id=>new BugReport { Id=id,Text="баг "+id,Status=BugStatuses.Created })
            .ToArray();

        var first=BugAdminPages.List(reports,BugStatuses.Created,"баг",string.Empty,1);
        var second=BugAdminPages.List(reports,BugStatuses.Created,"баг",string.Empty,2);

        Assert.That(first,Does.Contain("class='sticky-head'")
            .And.Contain("<dialog id='new-bug'>")
            .And.Contain("+ Новый отчёт")
            .And.Contain("aria-label='Страницы'"));
        Assert.That(first,Does.Contain("/admin/bugs/30").And.Not.Contain("/admin/bugs/6'"));
        Assert.That(second,Does.Contain("/admin/bugs/6")
            .And.Contain("status=created&amp;q=%D0%B1%D0%B0%D0%B3&amp;page=1"));
    }

    [Test]
    public void ListOpensReportsInPanelAndDirectLinkPreloadsIt()
    {
        var report=new BugReport { Id=7,Text="баг",Status=BugStatuses.ReadyForTest,FixCommits={"abc1234567890"} };

        var list=BugAdminPages.List(new[]{report},string.Empty,string.Empty,string.Empty);
        var direct=BugAdminPages.List(new[]{report},string.Empty,string.Empty,string.Empty,1,report,new Dictionary<string,BugCommitPatch>());

        Assert.That(list,Does.Contain("<dialog id='bug-panel'").And.Contain("data-list-url='/admin/bugs'")
            .And.Contain("data-bug='7' href='/admin/bugs/7'").And.Contain("X-Requested-With").And.Contain("history.pushState"));
        Assert.That(list,Does.Not.Contain("class='detail'"),"nothing is open until a card is clicked");
        Assert.That(direct,Does.Contain("class='detail' data-bug='7'").And.Contain("data-close"),"a shared link shows the report over the list");
        Assert.That(BugAdminPages.ListFragment(new[]{report},string.Empty,string.Empty),Does.StartWith("<main class='cards' data-count='1'>").And.Not.Contain("<dialog"));
        Assert.That(BugAdminPages.Deleted("Удалено"),Does.Contain("data-deleted='1'").And.Contain("data-notice='Удалено'"));
    }

    [Test]
    public void DetailRendersCommitPatchAsExpandableColouredDiffAndLinksMissingOnes()
    {
        var sha=new string('a',40);
        var report=new BugReport { Id=7,Text="баг",Status=BugStatuses.Fixed,FixCommits={sha.Substring(0,9),"bbbbbbbbb"} };
        var patch=new BugCommitPatch
        {
            Sha=sha,Subject="fix(bug-7): починил",Message="fix(bug-7): починил\n\nПодробности.\n\nBug: #7",Author="Анатолий",WhenUtc="2026-09-03T10:00:00+07:00",
            Files={new BugCommitFile{Path="Assets/A.cs",Added=2,Deleted=1},new BugCommitFile{Path="img.png",Binary=true}},
            Patch="diff --git a/Assets/A.cs b/Assets/A.cs\nindex 1..2 100644\n--- a/Assets/A.cs\n+++ b/Assets/A.cs\n@@ -1,2 +1,3 @@\n context\n-old <b>\n+new\n+--- looks like a header but is a removed SQL comment\ndiff --git a/img.png b/img.png\nBinary files differ\n",
        };

        var html=BugAdminPages.Detail(report,new Dictionary<string,BugCommitPatch>{[sha.Substring(0,9)]=patch},"Сохранено");

        Assert.That(html,Does.Contain("data-notice='Сохранено'"));
        Assert.That(html,Does.Contain("<details class='commit'>").And.Contain("fix(bug-7): починил").And.Contain("2 файла").And.Contain("<span class='add'>+2</span>"));
        Assert.That(html,Does.Contain("<pre class='message'>").And.Contain("Подробности."),"the body of the message beyond the subject is shown");
        Assert.That(html,Does.Contain("<a href='#caaaaaaaaaa-f0'>Assets/A.cs</a>").And.Contain("id='caaaaaaaaaa-f1'>img.png</span>"));
        Assert.That(html,Does.Contain("<span class='del'>-old &lt;b&gt;</span>").And.Contain("<span class='add'>+new</span>").And.Contain("<span class='hunk'>@@ -1,2 +1,3 @@</span>").And.Contain("<span class='ctx'> context</span>"));
        Assert.That(html,Does.Contain("<span class='add'>+--- looks like"),"inside a hunk a +/- line is content, not a file header");
        Assert.That(html,Does.Contain("<span class='meta'>--- a/Assets/A.cs</span>").And.Contain("<span class='meta'>Binary files differ</span>"));
        Assert.That(html,Does.Contain("<div class='commit missing'><code>bbbbbbbbb</code>").And.Contain(BugAdminPages.GitHubCommitBase+"bbbbbbbbb"));
    }
}
