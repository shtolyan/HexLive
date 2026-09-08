using System;
using System.IO;
using System.Threading.Tasks;
using HexLive.Server.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.Bugs;

public static class BugAdminEndpoints
{
    private const string Cookie = "hexlive_admin";

    public static void Map(WebApplication app, BugDatabase bugs, AdminSessions sessions)
    {
        // §114.3B: the same URLs answer two ways. A browser navigation gets a
        // whole page; the panel script (X-Requested-With: fetch) gets only the
        // fragment it swaps in, so the list underneath keeps its scroll.
        app.MapGet("/admin/bugs", (HttpContext c) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var status=c.Request.Query["status"].ToString(); var query=c.Request.Query["q"].ToString();
            return Html(Partial(c)
                ? BugAdminPages.ListFragment(bugs.List(),status,query,Page(c))
                : BugAdminPages.List(bugs.List(),status,query,c.Request.Query["notice"].ToString(),Page(c)));
        });

        app.MapGet("/admin/bugs/{id:int}", (HttpContext c, int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var report=bugs.Get(id);
            if (report==null) return Partial(c) ? Results.NotFound() : RedirectWithNotice("/admin/bugs",$"Отчёт #{id} не найден");
            var notice=c.Request.Query["notice"].ToString();
            return Html(Partial(c)
                ? BugAdminPages.Detail(report,Patches(bugs,report),notice)
                : BugAdminPages.List(bugs.List(),string.Empty,string.Empty,notice,1,report,Patches(bugs,report)));
        });

        app.MapPost("/admin/bugs/create", async (HttpContext c) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var f=await c.Request.ReadFormAsync();
            try
            {
                bugs.Create(new CreateBugRequest { Text=f["text"].ToString(), Context=f["context"].ToString(), ReportedInVersion=f["reportedInVersion"].ToString() });
                // PRG back to the list: the freshly created report is already
                // the first card, and refreshing cannot submit the form twice.
                return RedirectWithNotice("/admin/bugs", "Отправлено");
            }
            catch(InvalidDataException e) { return RedirectWithNotice("/admin/bugs", e.Message); }
        });

        app.MapPost("/admin/bugs/{id:int}/edit", async (HttpContext c,int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var f=await c.Request.ReadFormAsync();
            var status=f["status"].ToString();
            try
            {
                bugs.Update(id,new UpdateBugRequest { Text=f["text"].ToString(), Status=status, AssignedAgent=f["assignedAgent"].ToString(), AgentHandoff=f["agentHandoff"].ToString(), Archived=f.ContainsKey("archived") });
                return Answer(c,bugs,id,"Сохранено");
            }
            catch(InvalidDataException e) { return Answer(c,bugs,id,e.Message); }
        });

        app.MapPost("/admin/bugs/{id:int}/comment", async (HttpContext c,int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var f=await c.Request.ReadFormAsync();
            try { bugs.AddComment(id,new AddBugCommentRequest { Author="user",Text=f["text"].ToString() }); }
            catch(InvalidDataException) { }
            return Answer(c,bugs,id,string.Empty);
        });

        app.MapPost("/admin/bugs/{id:int}/repeat", async (HttpContext c,int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var f=await c.Request.ReadFormAsync();
            bugs.Update(id,new UpdateBugRequest { Status=BugStatuses.Rework, Archived=false, ReadyForTestInVersion=string.Empty, FixedInVersion=string.Empty });
            var text=f["text"].ToString().Trim();
            if(text.Length>0) bugs.AddComment(id,new AddBugCommentRequest { Author="user",Text=text });
            return Answer(c,bugs,id,"Возвращено на доработку");
        });

        app.MapPost("/admin/bugs/{id:int}/delete", (HttpContext c,int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            bugs.Delete(id);
            return Partial(c) ? Html(BugAdminPages.Deleted("Удалено")) : RedirectWithNotice("/admin/bugs", "Удалено");
        });
    }

    /// <summary>After a panel form: the re-rendered fragment; after a plain form: PRG as before.</summary>
    private static IResult Answer(HttpContext c,BugDatabase bugs,int id,string notice)
    {
        if (!Partial(c)) return notice.Length==0 ? Results.Redirect($"/admin/bugs/{id}") : RedirectWithNotice($"/admin/bugs/{id}",notice);
        var report=bugs.Get(id);
        return report==null ? Html(BugAdminPages.Deleted($"Отчёт #{id} уже удалён")) : Html(BugAdminPages.Detail(report,Patches(bugs,report),notice));
    }

    private static System.Collections.Generic.IReadOnlyDictionary<string,BugCommitPatch> Patches(BugDatabase bugs,BugReport report) =>
        bugs.GetCommitPatches(report.FixCommits.Count>0 ? report.FixCommits : new[]{report.FixCommit});

    internal static bool Partial(HttpContext c) =>
        string.Equals(c.Request.Headers["X-Requested-With"].ToString(),"fetch",StringComparison.OrdinalIgnoreCase) ||
        c.Request.Query["partial"].ToString()=="1";

    internal static IResult RedirectWithNotice(string path,string notice) =>
        Results.Redirect(path+"?notice="+Uri.EscapeDataString(notice));

    private static int Page(HttpContext context) =>
        int.TryParse(context.Request.Query["page"].ToString(),out var page) && page>0 ? page : 1;

    private static bool SignedIn(HttpContext c,AdminSessions sessions) => sessions.IsSignedIn(c.Request.Cookies[Cookie]??string.Empty);
    private static IResult Html(string value) => Results.Content(value,"text/html; charset=utf-8");
}
