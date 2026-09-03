using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace HexLive.Server.Bugs;

/// <summary>
/// §114.3A web admin. The list is the page; a report opens in a panel over it
/// (§114.3B), so «back» returns to the same scroll position and the cards
/// behind the panel are refreshed from the server rather than remembered.
/// </summary>
public static class BugAdminPages
{
    internal const int PageSize = 24;
    internal const string GitHubCommitBase = "https://github.com/simple-diple/HexLive/commit/";
    private static readonly IReadOnlyDictionary<string,BugCommitPatch> NoPatches = new Dictionary<string,BugCommitPatch>();

    /// <summary>Full list page; with <paramref name="open"/> the panel is already showing that report (direct link).</summary>
    public static string List(IReadOnlyList<BugReport> reports,string status,string query,string notice,int page=1,
        BugReport? open=null,IReadOnlyDictionary<string,BugCommitPatch>? patches=null)
    {
        status=(status??string.Empty).Trim(); query=(query??string.Empty).Trim();
        var b=new StringBuilder();
        b.Append("<div class='sticky-head'>"); Nav(b,"Баг-трекер");
        b.Append("<section class='toolbar'><form method='get'><input name='q' value='").Append(E(query)).Append("' placeholder='Поиск'><select name='status'><option value=''>Все статусы</option>");
        foreach(var s in Statuses()) b.Append("<option value='").Append(s).Append("'").Append(s==status?" selected":"").Append(">").Append(Status(s)).Append("</option>");
        b.Append("</select><button>Фильтр</button></form><div class='toolbar-actions'><span class='count' id='count'></span><button class='primary' type='button' onclick=\"document.getElementById('new-bug').showModal()\">+ Новый отчёт</button></div></section></div>");
        Notice(b,notice);
        b.Append("<dialog id='new-bug'><div class='dialog-title'><h2>Новый отчёт</h2><form method='dialog'><button aria-label='Закрыть'>×</button></form></div><form method='post' action='/admin/bugs/create'><textarea name='text' required autofocus placeholder='Что сломалось?'></textarea><div class='grid'><input name='context' placeholder='seed=… tick=… npc=…'><input name='reportedInVersion' placeholder='Версия'></div><div class='dialog-actions'><button type='button' onclick=\"document.getElementById('new-bug').close()\">Отмена</button><button class='primary'>Отправить</button></div></form></dialog>");
        b.Append("<div id='list' data-list-url='").Append(E(ListUrl(status,query,page))).Append("'>");
        b.Append(ListFragment(reports,status,query,page));
        b.Append("</div>");
        b.Append("<dialog id='bug-panel' class='bug-panel' aria-label='Отчёт'><div id='bug-panel-body'>");
        if(open!=null) b.Append(Detail(open,patches??NoPatches,string.Empty));
        b.Append("</div></dialog>");
        b.Append("<script>").Append(Script).Append("</script>");
        return Page("Баг-трекер",b.ToString());
    }

    /// <summary>Cards + pagination only — what the panel refreshes behind itself.</summary>
    public static string ListFragment(IReadOnlyList<BugReport> reports,string status,string query,int page=1)
    {
        status=(status??string.Empty).Trim(); query=(query??string.Empty).Trim();
        var filtered=reports.Where(r=>(status.Length==0||r.Status==status)&&
            (query.Length==0||r.Text.Contains(query,StringComparison.OrdinalIgnoreCase)||r.Context.Contains(query,StringComparison.OrdinalIgnoreCase)||r.Id.ToString().Contains(query))).Reverse().ToList();
        var pageCount=Math.Max(1,(filtered.Count+PageSize-1)/PageSize);
        page=Math.Clamp(page,1,pageCount);
        var visible=filtered.Skip((page-1)*PageSize).Take(PageSize);
        var b=new StringBuilder();
        b.Append("<main class='cards' data-count='").Append(filtered.Count).Append("'>");
        foreach(var r in visible) Card(b,r);
        b.Append("</main>"); Pagination(b,status,query,page,pageCount);
        return b.ToString();
    }

    /// <summary>One report as rendered inside the panel (also the fetch response to every panel form).</summary>
    public static string Detail(BugReport r,IReadOnlyDictionary<string,BugCommitPatch> patches,string notice)
    {
        var b=new StringBuilder();
        b.Append("<div class='detail' data-bug='").Append(r.Id).Append("' data-notice='").Append(E(notice)).Append("'>");
        b.Append("<div class='detail-head'><h2>Баг #").Append(r.Id).Append("</h2><span class='chip ").Append(r.Status).Append("'>").Append(Status(r.Status)).Append("</span>").Append(r.Archived?"<span class='chip archived'>Архив</span>":"").Append("<a class='close' href='/admin/bugs' data-close aria-label='Закрыть'>×</a></div>");
        b.Append("<section class='panel'><form method='post' action='/admin/bugs/").Append(r.Id).Append("/edit'><label>Описание</label><textarea name='text' required>").Append(E(r.Text)).Append("</textarea><div class='grid'><div><label>Статус</label><select name='status'>");
        foreach(var s in Statuses()) b.Append("<option value='").Append(s).Append("'").Append(s==r.Status?" selected":"").Append(">").Append(Status(s)).Append("</option>");
        b.Append("</select></div><div><label>Агент</label><input name='assignedAgent' value='").Append(E(r.AssignedAgent)).Append("'></div></div><label>Handoff</label><textarea class='small' name='agentHandoff'>").Append(E(r.AgentHandoff)).Append("</textarea><label class='check'><input type='checkbox' name='archived'").Append(r.Archived?" checked":"").Append("> В архиве</label><button class='primary'>Сохранить</button></form><div class='meta'>").Append(E(r.Context)).Append(" · создан ").Append(E(r.CreatedUtc)).Append(" · rev ").Append(r.Revision);
        if(r.ReportedInVersion.Length>0) b.Append(" · заведён в ").Append(E(r.ReportedInVersion));
        if(r.ReadyForTestInVersion.Length>0) b.Append(" · к проверке в ").Append(E(r.ReadyForTestInVersion));
        if(r.FixedInVersion.Length>0) b.Append(" · исправлен в ").Append(E(r.FixedInVersion));
        b.Append("</div></section>");
        b.Append("<section class='panel'><h2>Комментарии</h2>");
        foreach(var c in r.Comments) b.Append("<article class='comment'><b>").Append(E(c.Author)).Append("</b><small>").Append(E(c.WhenUtc)).Append("</small><p>").Append(E(c.Text)).Append("</p></article>");
        b.Append("<form method='post' action='/admin/bugs/").Append(r.Id).Append("/comment'><textarea class='small' name='text' required placeholder='Комментарий'></textarea><button>Добавить</button></form></section>");
        b.Append("<section class='panel actions'><form method='post' action='/admin/bugs/").Append(r.Id).Append("/repeat'><input name='text' placeholder='Что повторилось?'><button class='repeat'>↻ Повтори</button></form><form method='post' action='/admin/bugs/").Append(r.Id).Append("/delete' onsubmit=\"return confirm('Удалить отчёт безвозвратно?')\"><button class='danger'>Удалить</button></form></section>");
        Commits(b,r,patches);
        b.Append("</div>");
        return b.ToString();
    }

    /// <summary>The fetch response after a panel delete: nothing to show, a notice to toast.</summary>
    public static string Deleted(string notice) =>
        "<div class='detail' data-deleted='1' data-notice='"+E(notice)+"'></div>";

    private static void Card(StringBuilder b,BugReport r)
    {
        b.Append("<a class='card' data-bug='").Append(r.Id).Append("' href='/admin/bugs/").Append(r.Id).Append("'><div class='line'><b>#").Append(r.Id).Append("</b><span class='chip ").Append(r.Status).Append("'>").Append(Status(r.Status)).Append("</span>").Append(r.Archived?"<span class='chip archived'>Архив</span>":"");
        if(r.FixCommits.Count>0) b.Append("<span class='chip commits' title='Коммиты'>⎇ ").Append(r.FixCommits.Count).Append("</span>");
        if(r.Comments.Count>0) b.Append("<span class='chip comments' title='Комментарии'>💬 ").Append(r.Comments.Count).Append("</span>");
        b.Append("</div><p>").Append(E(r.Text)).Append("</p><small>").Append(E(r.Context)).Append("</small></a>");
    }

    // ── §114.4c commits with their patches ─────────────────────────────────

    private static void Commits(StringBuilder b,BugReport r,IReadOnlyDictionary<string,BugCommitPatch> patches)
    {
        var shas=r.FixCommits.Count>0?r.FixCommits:(r.FixCommit.Length>0?new List<string>{r.FixCommit}:new List<string>());
        if(shas.Count==0) return;
        b.Append("<section class='panel commits'><h2>Коммиты</h2>");
        foreach(var sha in shas)
        {
            patches.TryGetValue(sha,out var patch);
            if(patch==null)
            {
                b.Append("<div class='commit missing'><code>").Append(E(Short(sha))).Append("</code><small>дифф ещё не загружен в трекер · <a href='").Append(E(GitHubCommitBase+sha)).Append("' target='_blank' rel='noopener'>GitHub</a></small></div>");
                continue;
            }
            var added=patch.Files.Sum(f=>f.Added); var deleted=patch.Files.Sum(f=>f.Deleted);
            b.Append("<details class='commit'><summary><code>").Append(E(Short(patch.Sha))).Append("</code><b>").Append(E(patch.Subject)).Append("</b><small>")
             .Append(E(patch.Author)).Append(patch.WhenUtc.Length>0?" · "+E(When(patch.WhenUtc)):"")
             .Append(" · ").Append(Files(patch.Files.Count)).Append(" <span class='add'>+").Append(added).Append("</span> <span class='del'>−").Append(deleted).Append("</span></small></summary>");
            b.Append("<div class='commit-body'>");
            var message=patch.Message.Trim();
            if(message.Length>patch.Subject.Trim().Length) b.Append("<pre class='message'>").Append(E(message)).Append("</pre>");
            b.Append("<ul class='files'>");
            var n=0;
            foreach(var f in patch.Files)
            {
                b.Append("<li><a href='#c").Append(E(Short(patch.Sha))).Append("-f").Append(n++).Append("'>").Append(E(f.Path)).Append("</a>");
                b.Append(f.Binary?"<small>бинарный</small>":"<small><span class='add'>+"+f.Added+"</span> <span class='del'>−"+f.Deleted+"</span></small>").Append("</li>");
            }
            b.Append("</ul>");
            b.Append("<div class='diff-actions'><a href='").Append(E(GitHubCommitBase+patch.Sha)).Append("' target='_blank' rel='noopener'>Открыть на GitHub</a></div>");
            Diff(b,patch);
            if(patch.Truncated) b.Append("<div class='truncated'>Дифф обрезан: коммит больше лимита трекера.</div>");
            b.Append("</div></details>");
        }
        b.Append("</section>");
    }

    /// <summary>Colours a unified diff line by line; header lines are only recognised outside hunks.</summary>
    internal static void Diff(StringBuilder b,BugCommitPatch patch)
    {
        if(patch.Patch.Length==0) return;
        b.Append("<pre class='diff'>");
        var file=-1; var inHunk=false;
        foreach(var raw in patch.Patch.Split('\n'))
        {
            var line=raw.TrimEnd('\r');
            if(line.StartsWith("diff --git ",StringComparison.Ordinal))
            {
                file++; inHunk=false;
                b.Append("<span class='file' id='c").Append(E(Short(patch.Sha))).Append("-f").Append(file).Append("'>").Append(E(FileHeader(line))).Append("</span>");
                continue;
            }
            string cls;
            if(line.StartsWith("@@",StringComparison.Ordinal)) { inHunk=true; cls="hunk"; }
            else if(!inHunk) cls="meta";
            else if(line.StartsWith("+",StringComparison.Ordinal)) cls="add";
            else if(line.StartsWith("-",StringComparison.Ordinal)) cls="del";
            else if(line.StartsWith("\\",StringComparison.Ordinal)) cls="meta";
            else cls="ctx";
            b.Append("<span class='").Append(cls).Append("'>").Append(E(line)).Append("</span>");
        }
        b.Append("</pre>");
    }

    private static string FileHeader(string line)
    {
        // "diff --git a/path b/path" → "path" (the b-side; renames show both).
        var marker=line.IndexOf(" b/",StringComparison.Ordinal);
        if(marker<0) return line;
        var right=line.Substring(marker+3);
        var left=line.Substring("diff --git a/".Length,marker-"diff --git a/".Length);
        return left==right?right:left+" → "+right;
    }

    private static string Files(int n) => n+(n%10==1&&n%100!=11?" файл":n%10>=2&&n%10<=4&&(n%100<10||n%100>=20)?" файла":" файлов");
    private static string Short(string sha)=>sha.Length>10?sha.Substring(0,10):sha;
    /// <summary>ISO «2026-09-03T10:58:58+07:00» → «2026-09-03 10:58»; anything else is shown as is.</summary>
    private static string When(string iso)=>iso.Length>=16&&iso[10]=='T'?iso.Substring(0,10)+" "+iso.Substring(11,5):iso;

    // ── chrome ──────────────────────────────────────────────────────────────

    private static string ListUrl(string status,string query,int page)
    {
        if(status.Length==0&&query.Length==0&&page<=1) return "/admin/bugs";
        return "/admin/bugs?status="+Uri.EscapeDataString(status)+"&q="+Uri.EscapeDataString(query)+"&page="+page;
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

    // The panel: a modal <dialog> over the list. The list document never
    // navigates while a report is open, so its scroll position survives for
    // free; the browser history still gets one entry per opened report so
    // «back» (and a shared link) behaves like a page. Every form inside the
    // panel posts with fetch and receives the re-rendered detail fragment;
    // the cards behind are then re-fetched — statuses are read, not guessed.
    private const string Script=@"
(()=>{
const F='fetch',H={'X-Requested-With':F};
const panel=document.getElementById('bug-panel'),body=document.getElementById('bug-panel-body'),list=document.getElementById('list');
const listUrl=list.dataset.listUrl;
function toast(text){if(!text)return;const n=document.createElement('div');n.className='notice';n.setAttribute('role','status');n.textContent=text;document.body.appendChild(n);setTimeout(()=>n.classList.add('closing'),2600);setTimeout(()=>n.remove(),3200)}
function count(){const m=list.querySelector('main.cards');const c=document.getElementById('count');if(m&&c)c.textContent=m.dataset.count+' отч.'}
async function refreshList(){try{const r=await fetch(listUrl,{headers:H});if(!r.ok)return;const y=window.scrollY;list.innerHTML=await r.text();count();window.scrollTo(0,y)}catch(e){}}
function show(html){body.innerHTML=html;const d=body.firstElementChild;if(!panel.open)panel.showModal();panel.scrollTop=0;return d}
async function openBug(id,push){try{const r=await fetch('/admin/bugs/'+id,{headers:H});if(!r.ok){toast('Отчёт #'+id+' не найден');return}show(await r.text());if(push)history.pushState({bug:id},'','/admin/bugs/'+id)}catch(e){location.href='/admin/bugs/'+id}}
function closePanel(){if(panel.open)panel.close();if(history.state&&history.state.bug)history.back();else{history.replaceState({},'',listUrl);refreshList()}}
list.addEventListener('click',e=>{const a=e.target.closest('a.card');if(!a||e.metaKey||e.ctrlKey||e.shiftKey||e.button)return;e.preventDefault();openBug(a.dataset.bug,true)});
panel.addEventListener('click',e=>{if(e.target.closest('[data-close]')){e.preventDefault();closePanel();return}if(e.target===panel)closePanel()});
panel.addEventListener('cancel',e=>{e.preventDefault();closePanel()});
document.addEventListener('keydown',e=>{if(e.key==='Escape'&&panel.open&&!e.defaultPrevented){e.preventDefault();closePanel()}});
panel.addEventListener('submit',async e=>{const form=e.target;if(e.defaultPrevented||!form.action)return;e.preventDefault();const btn=form.querySelector('button');if(btn)btn.disabled=true;try{const r=await fetch(form.action,{method:'POST',headers:H,body:new URLSearchParams(new FormData(form))});if(!r.ok){toast('Ошибка '+r.status);return}const d=show(await r.text());toast(d.dataset.notice);if(d.dataset.deleted){history.replaceState({},'',listUrl);if(panel.open)panel.close()}refreshList()}catch(err){toast('Нет связи с сервером')}finally{if(btn)btn.disabled=false}});
window.addEventListener('popstate',e=>{if(e.state&&e.state.bug)openBug(e.state.bug,false);else{if(panel.open)panel.close();refreshList()}});
count();
const open=body.firstElementChild;if(open){const here=location.pathname;history.replaceState({},'',listUrl);history.pushState({bug:open.dataset.bug},'',here);panel.showModal()}
})();";

    private const string Style=@"
:root{color-scheme:dark;--bg:#10161a;--panel:#141d22;--raised:#222a31;--stroke:#39454d;--text:#dce3e6;--dim:#8c999f;--accent:#4d8c61;--add:#7fd08d;--del:#ef8a80}*{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at 20% 0,#26362b 0,transparent 35%),var(--bg);color:var(--text);font:15px system-ui,sans-serif}.wrap{max-width:1120px;margin:auto;padding:28px}.sticky-head{position:sticky;z-index:10;top:0;padding-top:12px;background:linear-gradient(var(--bg) 78%,transparent)}header{display:flex;align-items:center;gap:10px;margin-bottom:12px}header a{color:#9eb2ba;text-decoration:none}h1{margin:0 0 0 auto;font-size:22px}h2{font-size:16px;margin:0 0 14px}.panel,.toolbar,.card{background:rgba(20,29,34,.97);border:1px solid var(--stroke);border-radius:13px}.panel{padding:18px;margin-bottom:14px}.toolbar{padding:12px;margin-bottom:14px;display:flex;justify-content:space-between;align-items:center;box-shadow:0 10px 24px #0005}.toolbar form,.grid,.actions,.actions form,.toolbar-actions,.dialog-actions{display:flex;gap:10px}.toolbar form{flex:1}.toolbar-actions{align-items:center}.grid>*{flex:1}.cards{display:grid;gap:10px}.card{padding:15px;color:inherit;text-decoration:none}.card:hover{border-color:#668275;background:#17231d}.line{display:flex;align-items:center;gap:8px;flex-wrap:wrap}.card p{white-space:pre-wrap;margin:10px 0}.card small,.meta,small{color:var(--dim)}input,textarea,select,button{font:inherit;color:var(--text);background:#0c1215;border:1px solid var(--stroke);border-radius:8px;padding:10px}textarea{width:100%;min-height:105px;resize:vertical;margin-bottom:10px}.small{min-height:65px}input{width:100%}label{display:block;color:#aeb9bd;font-size:12px;margin:9px 0 5px}.check{font-size:14px}.check input{width:auto}button{cursor:pointer;background:var(--raised)}button:disabled{opacity:.6;cursor:wait}button.primary{background:var(--accent);border-color:#69a67c}.danger{background:#6e302d}.repeat{background:#745d25}.chip{font-size:12px;padding:4px 8px;border-radius:999px;background:#71591f}.chip.in_progress{background:#854c1f}.chip.ready_for_test{background:#425d9a}.chip.rework{background:#8b4038}.chip.fixed{background:#3e784d}.chip.archived{background:#4d555a}.chip.commits,.chip.comments{background:#2b3a42;color:#b9c7cd}.comment{border-left:3px solid #3c4b53;padding:4px 12px;margin:12px 0}.comment small{margin-left:8px}.comment p{white-space:pre-wrap}.notice{position:fixed;z-index:30;top:20px;right:20px;max-width:min(420px,calc(100vw - 40px));background:#284733;border:1px solid #69a67c;box-shadow:0 12px 34px #0009;padding:12px 18px;border-radius:9px;transition:opacity .22s ease,transform .22s ease}.notice.closing{opacity:0;transform:translateY(-8px);pointer-events:none}dialog{width:min(680px,calc(100vw - 32px));color:var(--text);background:var(--panel);border:1px solid var(--stroke);border-radius:14px;box-shadow:0 24px 80px #000c;padding:18px}dialog::backdrop{background:#071014b8;backdrop-filter:blur(3px)}.dialog-title,.dialog-actions{display:flex;align-items:center;justify-content:space-between}.dialog-title form{margin-left:auto}.dialog-title button{font-size:22px;padding:3px 10px}.dialog-actions{justify-content:flex-end}.pagination{display:flex;align-items:center;justify-content:center;gap:7px;margin:18px 0}.pagination a,.pagination span{min-width:36px;text-align:center;padding:8px;border-radius:8px;color:var(--text);text-decoration:none}.pagination a{background:var(--panel);border:1px solid var(--stroke)}.pagination a.current{background:var(--accent);border-color:#69a67c}code{white-space:pre-wrap;color:#a9c5b2}
dialog.bug-panel{position:fixed;inset:0 0 0 auto;width:min(900px,100vw);height:100dvh;max-height:100dvh;max-width:100vw;margin:0;border-radius:0;border-width:0 0 0 1px;padding:18px 22px 40px;overflow:auto;background:var(--bg)}dialog.bug-panel::backdrop{background:#07101499}.detail-head{display:flex;align-items:center;gap:10px;margin:0 0 14px;position:sticky;top:-18px;padding:12px 0;background:var(--bg);z-index:2}.detail-head h2{margin:0;font-size:20px}.detail-head .close{margin-left:auto;font-size:26px;line-height:1;padding:2px 12px;color:var(--text);text-decoration:none;border:1px solid var(--stroke);border-radius:8px;background:var(--raised)}.detail-head .close:hover{border-color:#668275}
.commits .commit{border:1px solid var(--stroke);border-radius:10px;margin:8px 0;background:#0e1518}.commits .commit.missing{padding:10px 14px;display:flex;gap:12px;align-items:center}.commits summary{cursor:pointer;padding:10px 14px;display:flex;gap:10px;align-items:baseline;flex-wrap:wrap;list-style:none}.commits summary::-webkit-details-marker{display:none}.commits summary::before{content:'▸';color:var(--dim);margin-right:2px}.commits details[open]>summary::before{content:'▾'}.commits summary code{color:#a9c5b2}.commits summary small{margin-left:auto;white-space:nowrap}.commit-body{border-top:1px solid var(--stroke);padding:12px 14px}.commit-body .message{white-space:pre-wrap;margin:0 0 12px;color:#b9c7cd;font:13px/1.5 ui-monospace,SFMono-Regular,Menlo,monospace}.files{list-style:none;margin:0 0 12px;padding:0;display:grid;gap:4px}.files li{display:flex;gap:10px;align-items:baseline}.files a{color:#c8d7dc;text-decoration:none;font:13px ui-monospace,SFMono-Regular,Menlo,monospace;word-break:break-all}.files a:hover{text-decoration:underline}.files small{margin-left:auto;white-space:nowrap}.diff-actions{margin:0 0 10px;font-size:13px}.diff-actions a{color:#9eb2ba}.add{color:var(--add)}.del{color:var(--del)}
pre.diff{margin:0;padding:0;overflow-x:auto;font:12.5px/1.5 ui-monospace,SFMono-Regular,Menlo,monospace;background:#0a0f12;border:1px solid var(--stroke);border-radius:8px;tab-size:4}pre.diff span{display:block;padding:0 10px;white-space:pre;min-width:100%;width:max-content}pre.diff .file{position:sticky;top:0;background:#1a252b;color:#dce3e6;font-weight:600;padding:6px 10px;border-top:1px solid var(--stroke);scroll-margin-top:56px}pre.diff .file:first-child{border-top:0}pre.diff .meta{color:#6f7d84}pre.diff .hunk{color:#8fb4cf;background:#12212b}pre.diff .add{background:#12301c;color:#c8f0cf}pre.diff .del{background:#3a1b1a;color:#f5c9c4}pre.diff .ctx{color:#b6c0c4}.truncated{margin-top:8px;color:#e0b96a;font-size:13px}
@media(max-width:700px){.wrap{padding:0 12px 12px}.sticky-head{padding-top:8px}.grid,.toolbar,.toolbar form,.toolbar-actions,.actions{flex-direction:column;align-items:stretch}h1{display:none}.toolbar-actions .count{text-align:right}dialog.bug-panel{padding:12px 12px 40px}.commits summary small{margin-left:0;white-space:normal}}
";
}
