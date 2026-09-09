#!/usr/bin/env python3
"""Small stdlib client for the HexLive bug API. Never logs the bearer token."""
import argparse, json, os, pathlib, subprocess, sys, urllib.error, urllib.request

# ⭐ Баг-трекер живёт на Сингапуре и не переезжал (указание игрока 2026-09-09).
# Нью-Йорк — дополнительный сервер со своим отдельным списком, не канонический.
DEFAULT="https://vmi3529459.contaboserver.net/api/bugs/v1"
REPOSITORY_TOKEN=pathlib.Path(__file__).resolve().parents[1]/"bug-token"
USER_TOKEN=pathlib.Path("~/.config/hexlive/bug-token").expanduser()

def token(args):
    value=os.environ.get("HEXLIVE_BUG_TOKEN","").strip()
    paths=[]
    if args.token_file: paths.append(pathlib.Path(args.token_file).expanduser())
    paths.extend((REPOSITORY_TOKEN,USER_TOKEN))
    for path in paths:
        if value or not path.is_file(): continue
        if path == REPOSITORY_TOKEN and os.name != "nt":
            try: path.chmod(0o600)
            except OSError: pass
        value=path.read_text(encoding="utf-8").strip()
    return value

def call(args,method,path,payload=None,auth=False):
    data=None if payload is None else json.dumps(payload,ensure_ascii=False).encode()
    headers={"Accept":"application/json"}
    if data is not None: headers["Content-Type"]="application/json"
    if auth:
        value=token(args)
        if not value: raise SystemExit("bug token is missing (environment, --token-file, repository, or user config)")
        headers["Authorization"]="Bearer "+value
    request=urllib.request.Request(args.api.rstrip("/")+path,data=data,headers=headers,method=method)
    try:
        with urllib.request.urlopen(request,timeout=15) as response:
            body=response.read().decode()
            return None if not body else json.loads(body)
    except urllib.error.HTTPError as e:
        body=e.read().decode(errors="replace")
        raise SystemExit(f"HTTP {e.code}: {body}")

# ── §114.4c: fix-commit patches ─────────────────────────────────────────────
# The server has no repository. The patch the player reads in the card is
# `git show` of the commit, uploaded from the agent's checkout once per SHA.

MAX_PATCH_CHARS=1_000_000

def git(repo,*argv):
    return subprocess.run(["git","-C",repo,*argv],check=True,capture_output=True,text=True,errors="replace").stdout

def commit_patch(repo,sha):
    """Build the upload payload for one commit; raises CalledProcessError when git does not know it."""
    full,author,when,subject=git(repo,"show","-s","--no-color","--format=%H%x00%an%x00%aI%x00%s",sha).rstrip("\n").split("\0")
    message=git(repo,"show","-s","--no-color","--format=%B",sha).strip()
    files=[]
    for line in git(repo,"show","--no-color","--format=","--numstat","-M",sha).splitlines():
        if not line.strip(): continue
        added,deleted,path=line.split("\t",2)
        binary=added=="-" or deleted=="-"
        files.append({"path":path,"added":0 if binary else int(added),"deleted":0 if binary else int(deleted),"binary":binary})
    patch=git(repo,"show","--no-color","--no-ext-diff","--format=","--patch","-M",sha)
    truncated=len(patch)>MAX_PATCH_CHARS
    if truncated: patch=patch[:MAX_PATCH_CHARS]
    return {"sha":full,"subject":subject,"message":message,"author":author,"whenUtc":when,"files":files,"patch":patch,"truncated":truncated}

def push_commit(args,sha,repo):
    payload=commit_patch(repo,sha)
    call(args,"PUT",f"/commits/{payload['sha']}",payload,True)
    return payload["sha"]

def push_commits(args,shas,repo,quiet=False):
    """Upload every SHA git knows; report the ones it does not instead of failing the whole run."""
    pushed,unknown=[],[]
    for sha in shas:
        try: pushed.append(push_commit(args,sha,repo))
        except subprocess.CalledProcessError: unknown.append(sha)
    if unknown and not quiet: print("not in this checkout (skipped): "+" ".join(unknown),file=sys.stderr)
    return pushed,unknown

def backfill(args,repo):
    reports=call(args,"GET","/reports")
    stored=set(call(args,"GET","/commits") or [])
    wanted=[]
    for r in reports:
        for sha in r.get("fixCommits") or ([r["fixCommit"]] if r.get("fixCommit") else []):
            sha=sha.strip().lower()
            if sha and sha not in wanted and not any(s==sha or s.startswith(sha) for s in stored): wanted.append(sha)
    pushed,unknown=push_commits(args,wanted,repo)
    return {"stored":len(stored),"pushed":len(pushed),"unknown":unknown}

def main():
    p=argparse.ArgumentParser(); p.add_argument("--api",default=DEFAULT); p.add_argument("--token-file")
    sub=p.add_subparsers(dest="cmd",required=True)
    sub.add_parser("list"); sub.add_parser("queue")
    g=sub.add_parser("get"); g.add_argument("id",type=int)
    c=sub.add_parser("create"); c.add_argument("--text",required=True); c.add_argument("--context",default=""); c.add_argument("--version",default="")
    u=sub.add_parser("update"); u.add_argument("id",type=int); u.add_argument("--status"); u.add_argument("--text"); u.add_argument("--assigned-agent"); u.add_argument("--handoff"); u.add_argument("--fix-commits",nargs="*"); u.add_argument("--expected-revision",type=int); u.add_argument("--archived",choices=("true","false")); u.add_argument("--repo",default=".",help="checkout to read --fix-commits patches from"); u.add_argument("--no-patch",action="store_true",help="do not upload the patches of --fix-commits")
    pc=sub.add_parser("push-commit",help="upload git show of commits so the card shows files and diff"); pc.add_argument("sha",nargs="+"); pc.add_argument("--repo",default=".")
    bf=sub.add_parser("backfill",help="upload the patch of every fix commit the tracker lacks"); bf.add_argument("--repo",default=".")
    m=sub.add_parser("comment"); m.add_argument("id",type=int); m.add_argument("--author",default="codex"); m.add_argument("--text",required=True)
    d=sub.add_parser("delete"); d.add_argument("id",type=int)
    a=p.parse_args()
    if a.cmd in ("list","queue"):
        data=call(a,"GET","/reports")
        if a.cmd=="queue": data=[r for r in data if r.get("status") in ("created","rework")]
    elif a.cmd=="get": data=call(a,"GET",f"/reports/{a.id}")
    elif a.cmd=="create": data=call(a,"POST","/reports",{"text":a.text,"context":a.context,"reportedInVersion":a.version})
    elif a.cmd=="comment": data=call(a,"POST",f"/reports/{a.id}/comments",{"author":a.author,"text":a.text},True)
    elif a.cmd=="delete": data=call(a,"POST",f"/reports/{a.id}/delete",{},True)
    elif a.cmd=="push-commit": data=push_commits(a,a.sha,a.repo)[0]
    elif a.cmd=="backfill": data=backfill(a,a.repo)
    else:
        mapping={"status":a.status,"text":a.text,"assignedAgent":a.assigned_agent,"agentHandoff":a.handoff,"fixCommits":a.fix_commits,"expectedRevision":a.expected_revision,"archived":None if a.archived is None else a.archived=="true"}
        data=call(a,"POST",f"/reports/{a.id}",{k:v for k,v in mapping.items() if v is not None},True)
        if a.fix_commits and not a.no_patch: push_commits(a,a.fix_commits,a.repo)
    if data is not None: print(json.dumps(data,ensure_ascii=False,indent=2))

if __name__=="__main__": main()
