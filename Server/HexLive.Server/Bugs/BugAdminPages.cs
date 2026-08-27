using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace HexLive.Server.Bugs;

public static class BugAdminPages
{
    public static string List(IReadOnlyList<BugReport> reports,string status,string query,string notice)
    {
        status=(status??string.Empty).Trim(); query=(query??string.Empty).Trim();
        var filtered=reports.Where(r=>(status.Length==0||r.Status==status)&&
            (query.Length==0||r.Text.Contains(query,StringComparison.OrdinalIgnoreCase)||r.Context.Contains(query,StringComparison.OrdinalIgnoreCase)||r.Id.ToString().Contains(query))).Reverse().ToList();
        var b=new StringBuilder();
        Nav(b,"Баг-трекер"); Notice(b,notice);
        b.Append("<section class='panel'><h2>Новый отчёт</h2><form method='post' action='/admin/bugs/create'><textarea name='text' required placeholder='Что сломалось?'></textarea><div class='grid'><input name='context' placeholder='seed=… tick=… npc=…'><input name='reportedInVersion' placeholder='Версия'></div><button class='primary'>Отправить</button></form></section>");
        b.Append("<section class='toolbar'><form method='get'><input name='q' value='").Append(E(query)).Append("' placeholder='Поиск'><select name='status'><option value=''>Все статусы</option>");
        foreach(var s in Statuses()) b.Append("<option value='").Append(s).Append("'").Append(s==status?" selected":"").Append(">").Append(Status(s)).Append("</option>");
        b.Append("</select><button>Фильтр</button></form><span class='count'>").Append(filtered.Count).Append(" отч.</span></section><main class='cards'>");
        foreach(var r in filtered)
        {
            b.Append("<a class='card' href='/admin/bugs/").Append(r.Id).Append("'><div class='line'><b>#").Append(r.Id).Append("</b><span class='chip ").Append(r.Status).Append("'>").Append(Status(r.Status)).Append("</span>").Append(r.Archived?"<span class='chip archived'>Архив</span>":"").Append("</div><p>").Append(E(r.Text)).Append("</p><small>").Append(E(r.Context)).Append("</small></a>");
        }
        b.Append("</main>");
        return Page("Баг-трекер",b.ToString());
    }

    public static string Detail(BugReport r,string notice)
    {
        var b=new StringBuilder(); Nav(b,$"Баг #{r.Id}"); Notice(b,notice);
        b.Append("<section class='panel'><form method='post' action='/admin/bugs/").Append(r.Id).Append("/edit'><label>Описание</label><textarea name='text' required>").Append(E(r.Text)).Append("</textarea><div class='grid'><div><label>Статус</label><select name='status'>");
        foreach(var s in Statuses()) b.Append("<option value='").Append(s).Append("'").Append(s==r.Status?" selected":"").Append(">").Append(Status(s)).Append("</option>");
        b.Append("</select></div><div><label>Агент</label><input name='assignedAgent' value='").Append(E(r.AssignedAgent)).Append("'></div></div><label>Handoff</label><textarea class='small' name='agentHandoff'>").Append(E(r.AgentHandoff)).Append("</textarea><label class='check'><input type='checkbox' name='archived'").Append(r.Archived?" checked":"").Append("> В архиве</label><button class='primary'>Сохранить</button></form><div class='meta'>").Append(E(r.Context)).Append(" · создан ").Append(E(r.CreatedUtc)).Append(" · rev ").Append(r.Revision).Append("</div></section>");
        b.Append("<section class='panel'><h2>Комментарии</h2>");
        foreach(var c in r.Comments) b.Append("<article class='comment'><b>").Append(E(c.Author)).Append("</b><small>").Append(E(c.WhenUtc)).Append("</small><p>").Append(E(c.Text)).Append("</p></article>");
        b.Append("<form method='post' action='/admin/bugs/").Append(r.Id).Append("/comment'><textarea class='small' name='text' required placeholder='Комментарий'></textarea><button>Добавить</button></form></section>");
        b.Append("<section class='panel actions'><form method='post' action='/admin/bugs/").Append(r.Id).Append("/repeat'><input name='text' placeholder='Что повторилось?'><button class='repeat'>↻ Повтори</button></form><form method='post' action='/admin/bugs/").Append(r.Id).Append("/delete' onsubmit=\"return confirm('Удалить отчёт безвозвратно?')\"><button class='danger'>Удалить</button></form></section>");
        if(r.FixCommits.Count>0) b.Append("<section class='panel'><h2>Коммиты</h2><code>").Append(E(string.Join("\n",r.FixCommits))).Append("</code></section>");
        return Page($"Баг #{r.Id}",b.ToString());
    }

    private static void Nav(StringBuilder b,string title) => b.Append("<header><a href='/admin'>HexLive</a><span>›</span><a href='/admin/bugs'>Баги</a><h1>").Append(E(title)).Append("</h1></header>");
    private static void Notice(StringBuilder b,string value) { if(!string.IsNullOrWhiteSpace(value)) b.Append("<div class='notice'>").Append(E(value)).Append("</div>"); }
    private static string[] Statuses()=>new[]{BugStatuses.Created,BugStatuses.InProgress,BugStatuses.ReadyForTest,BugStatuses.Rework,BugStatuses.Fixed};
    private static string Status(string s)=>s switch { BugStatuses.Created=>"🆕 Создан",BugStatuses.InProgress=>"🛠 В работе",BugStatuses.ReadyForTest=>"🧪 К проверке",BugStatuses.Rework=>"🔁 Повтори",BugStatuses.Fixed=>"✅ Исправлен",_=>s };
    private static string E(string? s)=>WebUtility.HtmlEncode(s??string.Empty);
    private static string Page(string title,string body)=>"<!doctype html><html lang='ru'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>"+E(title)+" · HexLive</title><style>"+Style+"</style></head><body><div class='wrap'>"+body+"</div></body></html>";
    private const string Style=@"
:root{color-scheme:dark;--bg:#10161a;--panel:#141d22;--raised:#222a31;--stroke:#39454d;--text:#dce3e6;--dim:#8c999f;--accent:#4d8c61}*{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at 20% 0,#26362b 0,transparent 35%),var(--bg);color:var(--text);font:15px system-ui,sans-serif}.wrap{max-width:1120px;margin:auto;padding:28px}header{display:flex;align-items:center;gap:10px;margin-bottom:22px}header a{color:#9eb2ba;text-decoration:none}h1{margin:0 0 0 auto;font-size:22px}h2{font-size:16px;margin:0 0 14px}.panel,.toolbar,.card{background:rgba(20,29,34,.97);border:1px solid var(--stroke);border-radius:13px}.panel{padding:18px;margin-bottom:14px}.toolbar{padding:12px;margin-bottom:14px;display:flex;justify-content:space-between;align-items:center}.toolbar form,.grid,.actions,.actions form{display:flex;gap:10px}.grid>*{flex:1}.cards{display:grid;gap:10px}.card{padding:15px;color:inherit;text-decoration:none}.card:hover{border-color:#668275;background:#17231d}.line{display:flex;align-items:center;gap:8px}.card p{white-space:pre-wrap;margin:10px 0}.card small,.meta,small{color:var(--dim)}input,textarea,select,button{font:inherit;color:var(--text);background:#0c1215;border:1px solid var(--stroke);border-radius:8px;padding:10px}textarea{width:100%;min-height:105px;resize:vertical;margin-bottom:10px}.small{min-height:65px}input{width:100%}label{display:block;color:#aeb9bd;font-size:12px;margin:9px 0 5px}.check{font-size:14px}.check input{width:auto}button{cursor:pointer;background:var(--raised)}button.primary{background:var(--accent);border-color:#69a67c}.danger{background:#6e302d}.repeat{background:#745d25}.chip{font-size:12px;padding:4px 8px;border-radius:999px;background:#71591f}.chip.in_progress{background:#854c1f}.chip.ready_for_test{background:#425d9a}.chip.rework{background:#8b4038}.chip.fixed{background:#3e784d}.chip.archived{background:#4d555a}.comment{border-left:3px solid #3c4b53;padding:4px 12px;margin:12px 0}.comment small{margin-left:8px}.comment p{white-space:pre-wrap}.notice{background:#284733;border:1px solid #4d8c61;padding:11px;border-radius:9px;margin-bottom:14px}code{white-space:pre-wrap;color:#a9c5b2}@media(max-width:700px){.wrap{padding:12px}.grid,.toolbar form,.actions{flex-direction:column}h1{display:none}}
";
}
