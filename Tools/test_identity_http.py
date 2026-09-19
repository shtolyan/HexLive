"""Exercise the real identity process behind a simulated loopback TLS proxy.

Only disposable local credentials are used. No production secrets are read.
Build Server/HexLive.Identity first; requires a compatible dotnet runtime.
"""
from __future__ import annotations

import base64
import hashlib
import html
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "Server/HexLive.Identity/bin/Debug/net9.0/HexLive.Identity.dll"
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    with tempfile.TemporaryDirectory(prefix="hexlive-identity-http-") as directory:
        root = Path(directory)
        env = dict(os.environ, HEXLIVE_IDENTITY_DATA=str(root),
                   ASPNETCORE_URLS=f"http://127.0.0.1:{port}", DOTNET_ROLL_FORWARD="Major",
                   HEXLIVE_BOOTSTRAP_OUTPUT=str(root / "owner-key"))
        subprocess.run(["dotnet", str(dll), "--bootstrap-owner"], env=env, check=True, stdout=subprocess.DEVNULL)
        owner_key = (root / "owner-key").read_text(encoding="utf-8-sig")
        process = subprocess.Popen(["dotnet", str(dll)], env=env, stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL)
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
            cookies = {}

            def request(path, form=None, bearer=None):
                headers = {"X-Forwarded-Proto": "https", "Cookie": "; ".join(f"{k}={v}" for k, v in cookies.items())}
                if bearer is not None:
                    headers["Authorization"] = "Bearer " + bearer
                data = None if form is None else urllib.parse.urlencode(form, doseq=True).encode()
                req = urllib.request.Request(f"http://127.0.0.1:{port}" + path, data=data, headers=headers)
                try:
                    response = opener.open(req, timeout=5)
                except urllib.error.HTTPError as error:
                    response = error
                with response:
                    for cookie in response.headers.get_all("Set-Cookie", []):
                        key, value = cookie.split(";", 1)[0].split("=", 1)
                        cookies[key] = value
                        assert "secure" in cookie.lower()
                    return response.status, response.read().decode()

            for _ in range(100):
                try:
                    if request("/health")[0] == 200:
                        break
                except OSError:
                    time.sleep(.1)
            else:
                raise RuntimeError("Identity service did not start")

            def csrf():
                status, body = request("/")
                assert status == 200
                return html.unescape(re.search(r"name='__RequestVerificationToken' value='([^']+)'", body)[1])

            token = csrf()
            assert request("/issue", {"name": "unauthorized", "__RequestVerificationToken": token})[0] == 401
            assert request("/login", {"key": owner_key})[0] == 400
            status, body = request("/login", {"key": "invalid", "__RequestVerificationToken": token})
            assert status == 401 and "role='alert'" in body and "Ключ не найден" in body
            assert "action='/login'" in body
            assert request("/login")[0] == 302
            assert request("/login", {"key": " \r\n" + owner_key + "\t ", "__RequestVerificationToken": token})[0] == 302
            token = csrf()
            assert request("/issue", {"name": "no-csrf"})[0] == 401
            status, issued = request("/issue", {"name": "Local tester <script>", "permissions": ["game.play"], "__RequestVerificationToken": token})
            assert status == 200 and "<script>" not in issued
            key = re.search(r"<code>(hexlive_[0-9a-f]{64})</code>", issued)[1]
            assert key not in (root / "accounts.json").read_text()
            status, body = request("/api/identity/v1/validate", {}, key)
            assert status == 200
            account = json.loads(body)["accountId"]
            assert request("/api/identity/v1/validate", {}, "hexplay_old")[0] == 401
            assert json.loads(body)["permissions"] == ["game.play"]
            assert request("/permissions", {"id": account, "expectedRevision": 1, "permissions": ["game.play", "bugs.create"], "__RequestVerificationToken": token})[0] == 302
            assert request("/permissions", {"id": account, "expectedRevision": 1, "__RequestVerificationToken": token})[0] == 409
            assert json.loads(request("/api/identity/v1/validate", {}, key)[1])["permissions"] == ["bugs.create", "game.play"]
            assert request("/revoke", {"id": account, "expectedRevision": 2, "__RequestVerificationToken": token})[0] == 302
            assert request("/api/identity/v1/validate", {}, key)[0] == 401
            status, body = request("/issue", {"name": "Tester", "id": account, "expectedRevision": 3, "__RequestVerificationToken": token})
            replacement = re.search(r"<code>(hexlive_[0-9a-f]{64})</code>", body)[1]
            status, body = request("/api/identity/v1/validate", {}, replacement)
            assert status == 200 and json.loads(body)["accountId"] == account
            assert request("/logout", {"__RequestVerificationToken": token})[0] == 302
            status, body = request("/login", {"key": replacement, "__RequestVerificationToken": csrf()})
            assert status == 401 and "нет права keys.manage" in body and "action='/login'" in body
            assert request("/revoke", {"id": account, "__RequestVerificationToken": token})[0] == 401
            print("PASS: login, secure cookies, CSRF, HTML escaping, issue, validate, hash-only storage, revoke, rotate, logout")
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


if __name__ == "__main__":
    main()
