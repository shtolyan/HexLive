#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Local stand for measuring a WebGL build on a big world (§168.11).

    python3 Tools/webgl_perf_stand.py <build-dir> [--mode hugeisland] [--seed N]
                                      [--port 5201] [--asset-root DIR]

Runs the game server of THIS checkout with a fresh world of the chosen mode
and serves the given WebGL build from the same origin (`--web-root`), so the
page connects to it at /watch exactly as production does. Then open

    http://localhost:<port>/play/?perf=1

and read the `[WebPerf]` summaries the page logs every 5 s (browser console).
Switches for A/B: `&rscale=0.7`, `&bloom=0`, `&portraitHz=0`.

Nothing outside a fresh work directory is written:
- the build is cloned (APFS `cp -c`) and its `server-directory.json` is
  replaced by a single local entry, so the menu never offers production
  (plain http/ws is accepted there only for a loopback host, §168.11);
- the asset root (WebGL content; default: the Primal integration-test root)
  is cloned, because the server keeps runtime state inside it;
- the world save, admin account and logs live in the work directory.
No accounts (Firebase) and no player control: the browser is a viewer, which
is what a frame measurement needs. The build and the server must come from
the same source SHA, or the handshake refuses the protocol.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_ASSETS = pathlib.Path.home() / "hex-girls/webgl-build/it-server-primal-scale-b993f5726/assets"


def clone_tree(source: pathlib.Path, target: pathlib.Path) -> None:
    # APFS clone: instant and free until something writes; fall back to a copy.
    if subprocess.run(["cp", "-c", "-R", str(source), str(target)]).returncode != 0:
        shutil.copytree(source, target)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("build", type=pathlib.Path, help="WebGL build directory (index.html + Build/)")
    parser.add_argument("--mode", default="hugeisland",
                        help="fresh world mode: bigisland (Singapore) or hugeisland (the stress case)")
    parser.add_argument("--seed", type=int, default=1288076543)
    parser.add_argument("--port", type=int, default=5201)
    parser.add_argument("--asset-root", type=pathlib.Path, default=DEFAULT_ASSETS,
                        help="asset root with WebGL variants (cloned, never modified)")
    parser.add_argument("--work", type=pathlib.Path, help="work directory (default: new temp directory)")
    args = parser.parse_args()

    build = args.build.expanduser().resolve()
    if not (build / "index.html").is_file() or not (build / "Build").is_dir():
        print(f"not a WebGL build: {build}", file=sys.stderr)
        return 2
    assets = args.asset_root.expanduser().resolve()
    if not (assets / "registry-state.json").is_file():
        print(f"not an asset root (no registry-state.json): {assets}", file=sys.stderr)
        return 2

    work = args.work.expanduser().resolve() if args.work else pathlib.Path(
        tempfile.mkdtemp(prefix="hexlive-perf-stand."))
    work.mkdir(parents=True, exist_ok=True)
    web = work / "web"
    if not web.exists():
        clone_tree(build, web)
    socket = f"ws://localhost:{args.port}/watch"
    (web / "server-directory.json").write_text(json.dumps({
        "version": 1,
        "servers": [{
            "id": "local-perf",
            "name": f"Perf stand ({args.mode})",
            "socketUrl": socket,
            "playUrl": f"http://localhost:{args.port}/play/",
            "enabled": True,
        }],
    }, indent=2) + "\n", encoding="utf-8")
    if not (work / "assets").exists():
        clone_tree(assets, work / "assets")
    shutil.copy2(REPO / "SimData/simdata.json", work / "simdata.json")

    command = [
        "dotnet", "run", "--project", str(REPO / "Server/HexLive.Server"), "--configuration", "Release", "--",
        "--port", str(args.port),
        "--mode", args.mode,
        "--seed", str(args.seed),
        "--save", str(work / "world.sav"),
        "--simdata", str(work / "simdata.json"),
        "--asset-root", str(work / "assets"),
        "--web-root", str(web),
        "--autosave", "0",
    ]
    print(f"work dir: {work}")
    print(f"open:     http://localhost:{args.port}/play/?perf=1")
    print("stop:     Ctrl-C (the work dir is kept; pass --work to reuse it)")
    log = (work / "server.log").open("ab")
    try:
        with subprocess.Popen(command, cwd=work, stdout=subprocess.PIPE, stderr=subprocess.STDOUT) as server:
            assert server.stdout is not None
            for line in server.stdout:
                log.write(line)
                log.flush()
                sys.stdout.buffer.write(line)
                sys.stdout.flush()
            return server.wait()
    except KeyboardInterrupt:
        return 0
    finally:
        log.close()


if __name__ == "__main__":
    sys.exit(main())
