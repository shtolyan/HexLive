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
        app.MapGet("/admin/bugs", (HttpContext c) =>
            SignedIn(c,sessions)
                ? Html(BugAdminPages.List(bugs.List(), c.Request.Query["status"].ToString(), c.Request.Query["q"].ToString(), c.Request.Query["notice"].ToString()))
                : Results.Redirect("/admin"));

        app.MapGet("/admin/bugs/{id:int}", (HttpContext c, int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var report=bugs.Get(id);
            return report==null ? Results.NotFound() : Html(BugAdminPages.Detail(report,c.Request.Query["notice"].ToString()));
        });

        app.MapPost("/admin/bugs/create", async (HttpContext c) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var f=await c.Request.ReadFormAsync();
            try
            {
                var report=bugs.Create(new CreateBugRequest { Text=f["text"].ToString(), Context=f["context"].ToString(), ReportedInVersion=f["reportedInVersion"].ToString() });
                return Results.Redirect($"/admin/bugs/{report.Id}?notice=Создано");
            }
            catch(InvalidDataException e) { return Results.Redirect("/admin/bugs?notice="+Uri.EscapeDataString(e.Message)); }
        });

        app.MapPost("/admin/bugs/{id:int}/edit", async (HttpContext c,int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var f=await c.Request.ReadFormAsync();
            var status=f["status"].ToString();
            try
            {
                bugs.Update(id,new UpdateBugRequest { Text=f["text"].ToString(), Status=status, AssignedAgent=f["assignedAgent"].ToString(), AgentHandoff=f["agentHandoff"].ToString(), Archived=f.ContainsKey("archived") });
                return Results.Redirect($"/admin/bugs/{id}?notice=Сохранено");
            }
            catch(InvalidDataException e) { return Results.Redirect($"/admin/bugs/{id}?notice="+Uri.EscapeDataString(e.Message)); }
        });

        app.MapPost("/admin/bugs/{id:int}/comment", async (HttpContext c,int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var f=await c.Request.ReadFormAsync();
            try { bugs.AddComment(id,new AddBugCommentRequest { Author="user",Text=f["text"].ToString() }); }
            catch(InvalidDataException) { }
            return Results.Redirect($"/admin/bugs/{id}");
        });

        app.MapPost("/admin/bugs/{id:int}/repeat", async (HttpContext c,int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            var f=await c.Request.ReadFormAsync();
            bugs.Update(id,new UpdateBugRequest { Status=BugStatuses.Rework, Archived=false, ReadyForTestInVersion=string.Empty, FixedInVersion=string.Empty });
            var text=f["text"].ToString().Trim();
            if(text.Length>0) bugs.AddComment(id,new AddBugCommentRequest { Author="user",Text=text });
            return Results.Redirect($"/admin/bugs/{id}?notice=Возвращено+на+доработку");
        });

        app.MapPost("/admin/bugs/{id:int}/delete", (HttpContext c,int id) =>
        {
            if (!SignedIn(c,sessions)) return Results.Redirect("/admin");
            bugs.Delete(id);
            return Results.Redirect("/admin/bugs?notice=Удалено");
        });
    }

    private static bool SignedIn(HttpContext c,AdminSessions sessions) => sessions.IsSignedIn(c.Request.Cookies[Cookie]??string.Empty);
    private static IResult Html(string value) => Results.Content(value,"text/html; charset=utf-8");
}
