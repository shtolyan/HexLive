#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""§174.8: стенд HTML-интерфейса веб-клиента — без Unity и без сборки.

    python3 Tools/webui_stand.py                 # http://localhost:8091/
    python3 Tools/webui_stand.py --port 9000
    python3 Tools/webui_stand.py --export-i18n <dir>   # только ru.json/en.json

Стенд грузит те же файлы, что и страница игры (Assets/WebGLTemplates/HexLive/ui),
а вместо Unity отвечает Tools/webui/stand.js: тот же протокол, тестовые данные.
Строки — из Assets/Resources/I2Languages.asset, как их отдал бы Loc.Get
(пустой перевод → английский, §58). В игре их присылает Unity (HtmlUi.cs);
этот экспорт нужен только стенду и в сборку не попадает.
"""

from __future__ import annotations

import argparse
import functools
import http.server
import json
import re
import pathlib
import sys

import yaml

REPO = pathlib.Path(__file__).resolve().parent.parent
UI = REPO / "Assets" / "WebGLTemplates" / "HexLive" / "ui"
STAND = REPO / "Tools" / "webui"
I2_ASSET = REPO / "Assets" / "Resources" / "I2Languages.asset"

SKELETON = (
    '<!doctype html><html lang="ru"><head><meta charset="utf-8">'
    '<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">'
    "<style>body{margin:0}</style></head><body>\n{page}\n</body></html>"
)


class _UnityLoader(yaml.SafeLoader):
    """Unity YAML: `%TAG !u!` и `--- !u!114 &id` — теги просто снимаем."""


_UnityLoader.add_multi_constructor(
    "tag:unity3d.com,2011:", lambda loader, suffix, node: loader.construct_mapping(node, deep=True)
)


# §58.9: разметка рода «[[слот:женская|мужская]]»; Loc.Get без героя-мужчины даёт женскую.
_GENDER = re.compile(r"\[\[(?:s|0|1|2):([^|\]]*)\|[^\]]*\]\]")


def _feminine(text: str) -> str:
    return _GENDER.sub(lambda match: match.group(1), text)


@functools.lru_cache(maxsize=1)
def i2_tables() -> dict[str, dict[str, str]]:
    document = yaml.load(I2_ASSET.read_text(encoding="utf-8"), Loader=_UnityLoader)
    source = document["MonoBehaviour"]["mSource"]
    languages = [entry["Name"] for entry in source["mLanguages"]]
    english = languages.index("English")
    russian = languages.index("Russian")
    tables: dict[str, dict[str, str]] = {"en": {}, "ru": {}}
    for term in source["mTerms"]:
        values = [value or "" for value in (term.get("Languages") or [])]
        en = _feminine(values[english] if english < len(values) else "")
        ru = _feminine(values[russian] if russian < len(values) else "")
        if en:
            tables["en"][term["Term"]] = en
        if ru or en:
            tables["ru"][term["Term"]] = ru or en  # Loc.Get: пустой перевод → английский
    return tables


def export_i18n(directory: pathlib.Path) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for lang, table in i2_tables().items():
        path = directory / f"{lang}.json"
        path.write_text(json.dumps(table, ensure_ascii=False), encoding="utf-8")
        print(f"{path}: {len(table)} terms")


class StandHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, fmt: str, *args: object) -> None:  # тише
        pass

    def end_headers(self) -> None:
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def do_GET(self) -> None:  # noqa: N802
        path = self.path.split("?", 1)[0]
        if path in ("/", "/index.html"):
            page = (STAND / "stand.html").read_text(encoding="utf-8")
            return self._send(SKELETON.replace("{page}", page).encode("utf-8"), "text/html; charset=utf-8")
        if path.startswith("/i18n/") and path.endswith(".json"):
            table = i2_tables().get(path[len("/i18n/"):-len(".json")])
            if table is None:
                return self.send_error(404)
            return self._send(json.dumps(table, ensure_ascii=False).encode("utf-8"), "application/json")
        if path.startswith("/ui/"):
            return self._file(UI, path[len("/ui/"):])
        return self._file(STAND, path.lstrip("/"))

    def _file(self, root: pathlib.Path, relative: str) -> None:
        target = (root / relative).resolve()
        if root.resolve() not in target.parents or not target.is_file():
            return self.send_error(404)
        text = {".js": "text/javascript", ".css": "text/css", ".json": "application/json"}
        binary = {".png": "image/png", ".jpg": "image/jpeg", ".svg": "image/svg+xml"}
        if target.suffix in text:
            kind = text[target.suffix] + "; charset=utf-8"
        else:
            kind = binary.get(target.suffix, "application/octet-stream")
        self._send(target.read_bytes(), kind)

    def _send(self, body: bytes, kind: str) -> None:
        self.send_response(200)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--port", type=int, default=8091)
    parser.add_argument("--export-i18n", type=pathlib.Path, help="записать ru.json/en.json и выйти")
    args = parser.parse_args()
    if args.export_i18n:
        export_i18n(args.export_i18n)
        return 0
    tables = i2_tables()
    print(f"I2: {len(tables['en'])} EN / {len(tables['ru'])} RU terms")
    # The page loads ~25 ES modules at once; Python's default listen backlog
    # of 5 makes the OS reset the overflow (ERR_CONNECTION_RESET in the stand).
    http.server.ThreadingHTTPServer.request_queue_size = 128
    server = http.server.ThreadingHTTPServer(("127.0.0.1", args.port), StandHandler)
    print(f"стенд: http://localhost:{args.port}/")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
