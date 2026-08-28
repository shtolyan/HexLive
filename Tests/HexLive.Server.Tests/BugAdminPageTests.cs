using System;
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
}
