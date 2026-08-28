#!/usr/bin/env python3
"""Small stdlib client for the HexLive bug API. Never logs the bearer token."""
import argparse, json, os, pathlib, sys, urllib.error, urllib.request

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

def main():
    p=argparse.ArgumentParser(); p.add_argument("--api",default=DEFAULT); p.add_argument("--token-file")
    sub=p.add_subparsers(dest="cmd",required=True)
    sub.add_parser("list"); sub.add_parser("queue")
    g=sub.add_parser("get"); g.add_argument("id",type=int)
    c=sub.add_parser("create"); c.add_argument("--text",required=True); c.add_argument("--context",default=""); c.add_argument("--version",default="")
    u=sub.add_parser("update"); u.add_argument("id",type=int); u.add_argument("--status"); u.add_argument("--text"); u.add_argument("--assigned-agent"); u.add_argument("--handoff"); u.add_argument("--fix-commits",nargs="*"); u.add_argument("--expected-revision",type=int); u.add_argument("--archived",choices=("true","false"))
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
    else:
        mapping={"status":a.status,"text":a.text,"assignedAgent":a.assigned_agent,"agentHandoff":a.handoff,"fixCommits":a.fix_commits,"expectedRevision":a.expected_revision,"archived":None if a.archived is None else a.archived=="true"}
        data=call(a,"POST",f"/reports/{a.id}",{k:v for k,v in mapping.items() if v is not None},True)
    if data is not None: print(json.dumps(data,ensure_ascii=False,indent=2))

if __name__=="__main__": main()
