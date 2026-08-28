using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace HexLive.Server.Bugs;

public static class BugAdminPages
{
    internal const int PageSize = 24;

    public static string List(IReadOnlyList<BugReport> reports,string status,string query,string notice,int page=1)
    {
        status=(status??string.Empty).Trim(); query=(query??string.Empty).Trim();
        var filtered=reports.Where(r=>(status.Length==0||r.Status==status)&&
            (query.Length==0||r.Text.Contains(query,StringComparison.OrdinalIgnoreCase)||r.Context.Contains(query,StringComparison.OrdinalIgnoreCase)||r.Id.ToString().Contains(query))).Reverse().ToList();
        var pageCount=Math.Max(1,(filtered.Count+PageSize-1)/PageSize);
        page=Math.Clamp(page,1,pageCount);
        var visible=filtered.Skip((page-1)*PageSize).Take(PageSize);
        var b=new StringBuilder();
        b.Append("<div class='sticky-head'>"); Nav(b,"Баг-трекер");
        b.Append("<section class='toolbar'><form method='get'><input name='q' value='").Append(E(query)).Append("' placeholder='Поиск'><select name='status'><option value=''>Все статусы</option>");
        foreach(var s in Statuses()) b.Append("<option value='").Append(s).Append("'").Append(s==status?" selected":"").Append(">").Append(Status(s)).Append("</option>");
        b.Append("</select><button>Фильтр</button></form><div class='toolbar-actions'><span class='count'>").Append(filtered.Count).Append(" отч.</span><button class='primary' type='button' onclick=\"document.getElementById('new-bug').showModal()\">+ Новый отчёт</button></div></section></div>");
        Notice(b,notice);
        b.Append("<dialog id='new-bug'><div class='dialog-title'><h2>Новый отчёт</h2><form method='dialog'><button aria-label='Закрыть'>×</button></form></div><form method='post' action='/admin/bugs/create'><textarea name='text' required autofocus placeholder='Что сломалось?'></textarea><div class='grid'><input name='context' placeholder='seed=… tick=… npc=…'><input name='reportedInVersion' placeholder='Версия'></div><div class='dialog-actions'><button type='button' onclick=\"document.getElementById('new-bug').close()\">Отмена</button><button class='primary'>Отправить</button></div></form></dialog>");
        b.Append("<main class='cards'>");
        foreach(var r in visible)
        {
            b.Append("<a class='card' href='/admin/bugs/").Append(r.Id).Append("'><div class='line'><b>#").Append(r.Id).Append("</b><span class='chip ").Append(r.Status).Append("'>").Append(Status(r.Status)).Append("</span>").Append(r.Archived?"<span class='chip archived'>Архив</span>":"").Append("</div><p>").Append(E(r.Text)).Append("</p><small>").Append(E(r.Context)).Append("</small></a>");
        }
        b.Append("</main>"); Pagination(b,status,query,page,pageCount);
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
    private static void Notice(StringBuilder b,string value) { if(!string.IsNullOrWhiteSpace(value)) b.Append("<div class='notice' role='status' aria-live='polite'>").Append(E(value)).Append("</div>"); }
    private static void Pagination(StringBuilder b,string status,string query,int page,int pageCount)
    {
        if(pageCount<=1) return;
        b.Append("<nav class='pagination' aria-label='Страницы'>");
        if(page>1) PageLink(b,status,query,page-1,"‹",false);
        var first=Math.Max(1,page-2); var last=Math.Min(pageCount,page+2);
        if(first>1) { PageLink(b,status,query,1,"1",false); if(first>2) b.Append("<span>…</span>"); }
        for(var number=first;number<=last;number++) PageLink(b,status,query,number,number.ToString(),number==page);
        if(last<pageCount) { if(last<pageCount-1) b.Append("<span>…</span>"); PageLink(b,status,query,pageCount,pageCount.ToString(),false); }
        if(page<pageCount) PageLink(b,status,query,page+1,"›",false);
        b.Append("</nav>");
    }
    private static void PageLink(StringBuilder b,string status,string query,int page,string label,bool current)
    {
        var href="/admin/bugs?status="+Uri.EscapeDataString(status)+"&q="+Uri.EscapeDataString(query)+"&page="+page;
        b.Append("<a href='").Append(E(href)).Append("'").Append(current?" class='current' aria-current='page'":"").Append(">").Append(label).Append("</a>");
    }
    private static string[] Statuses()=>new[]{BugStatuses.Created,BugStatuses.InProgress,BugStatuses.ReadyForTest,BugStatuses.Rework,BugStatuses.Fixed};
    private static string Status(string s)=>s switch { BugStatuses.Created=>"🆕 Создан",BugStatuses.InProgress=>"🛠 В работе",BugStatuses.ReadyForTest=>"🧪 К проверке",BugStatuses.Rework=>"🔁 Повтори",BugStatuses.Fixed=>"✅ Исправлен",_=>s };
    private static string E(string? s)=>WebUtility.HtmlEncode(s??string.Empty);
    private static string Page(string title,string body)=>"<!doctype html><html lang='ru'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>"+E(title)+" · HexLive</title><style>"+Style+"</style></head><body><div class='wrap'>"+body+"</div><script>const n=document.querySelector('.notice');if(n)setTimeout(()=>n.classList.add('closing'),2600)</script></body></html>";
    private const string Style=@"
:root{color-scheme:dark;--bg:#10161a;--panel:#141d22;--raised:#222a31;--stroke:#39454d;--text:#dce3e6;--dim:#8c999f;--accent:#4d8c61}*{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at 20% 0,#26362b 0,transparent 35%),var(--bg);color:var(--text);font:15px system-ui,sans-serif}.wrap{max-width:1120px;margin:auto;padding:28px}.sticky-head{position:sticky;z-index:10;top:0;padding-top:12px;background:linear-gradient(var(--bg) 78%,transparent)}header{display:flex;align-items:center;gap:10px;margin-bottom:12px}header a{color:#9eb2ba;text-decoration:none}h1{margin:0 0 0 auto;font-size:22px}h2{font-size:16px;margin:0 0 14px}.panel,.toolbar,.card{background:rgba(20,29,34,.97);border:1px solid var(--stroke);border-radius:13px}.panel{padding:18px;margin-bottom:14px}.toolbar{padding:12px;margin-bottom:14px;display:flex;justify-content:space-between;align-items:center;box-shadow:0 10px 24px #0005}.toolbar form,.grid,.actions,.actions form,.toolbar-actions,.dialog-actions{display:flex;gap:10px}.toolbar form{flex:1}.toolbar-actions{align-items:center}.grid>*{flex:1}.cards{display:grid;gap:10px}.card{padding:15px;color:inherit;text-decoration:none}.card:hover{border-color:#668275;background:#17231d}.line{display:flex;align-items:center;gap:8px}.card p{white-space:pre-wrap;margin:10px 0}.card small,.meta,small{color:var(--dim)}input,textarea,select,button{font:inherit;color:var(--text);background:#0c1215;border:1px solid var(--stroke);border-radius:8px;padding:10px}textarea{width:100%;min-height:105px;resize:vertical;margin-bottom:10px}.small{min-height:65px}input{width:100%}label{display:block;color:#aeb9bd;font-size:12px;margin:9px 0 5px}.check{font-size:14px}.check input{width:auto}button{cursor:pointer;background:var(--raised)}button.primary{background:var(--accent);border-color:#69a67c}.danger{background:#6e302d}.repeat{background:#745d25}.chip{font-size:12px;padding:4px 8px;border-radius:999px;background:#71591f}.chip.in_progress{background:#854c1f}.chip.ready_for_test{background:#425d9a}.chip.rework{background:#8b4038}.chip.fixed{background:#3e784d}.chip.archived{background:#4d555a}.comment{border-left:3px solid #3c4b53;padding:4px 12px;margin:12px 0}.comment small{margin-left:8px}.comment p{white-space:pre-wrap}.notice{position:fixed;z-index:20;top:20px;right:20px;max-width:min(420px,calc(100vw - 40px));background:#284733;border:1px solid #69a67c;box-shadow:0 12px 34px #0009;padding:12px 18px;border-radius:9px;transition:opacity .22s ease,transform .22s ease}.notice.closing{opacity:0;transform:translateY(-8px);pointer-events:none}dialog{width:min(680px,calc(100vw - 32px));color:var(--text);background:var(--panel);border:1px solid var(--stroke);border-radius:14px;box-shadow:0 24px 80px #000c;padding:18px}dialog::backdrop{background:#071014b8;backdrop-filter:blur(3px)}.dialog-title,.dialog-actions{display:flex;align-items:center;justify-content:space-between}.dialog-title form{margin-left:auto}.dialog-title button{font-size:22px;padding:3px 10px}.dialog-actions{justify-content:flex-end}.pagination{display:flex;align-items:center;justify-content:center;gap:7px;margin:18px 0}.pagination a,.pagination span{min-width:36px;text-align:center;padding:8px;border-radius:8px;color:var(--text);text-decoration:none}.pagination a{background:var(--panel);border:1px solid var(--stroke)}.pagination a.current{background:var(--accent);border-color:#69a67c}code{white-space:pre-wrap;color:#a9c5b2}@media(max-width:700px){.wrap{padding:0 12px 12px}.sticky-head{padding-top:8px}.grid,.toolbar,.toolbar form,.toolbar-actions,.actions{flex-direction:column;align-items:stretch}h1{display:none}.toolbar-actions .count{text-align:right}}
";
}
