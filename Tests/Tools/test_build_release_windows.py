from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "Tools" / "build_release_windows.py"


def load_build_module():
    spec = importlib.util.spec_from_file_location("build_release_windows", SCRIPT)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load {SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class WindowsBuildBugGateTests(unittest.TestCase):
    def test_windows_snapshot_uses_schannel_without_tls_bypass(self) -> None:
        module = load_build_module()
        reports = [{"id": 264, "status": "ready_for_test"}]
        completed = subprocess.CompletedProcess(
            args=[],
            returncode=0,
            stdout=json.dumps(reports).encode("utf-8") + b"\n200\n",
            stderr=b"",
        )

        with (
            mock.patch.dict(os.environ, {"HEXLIVE_BUG_TOKEN": "test-token"}),
            mock.patch.object(module, "windows_curl_executable", return_value=Path("curl.exe")),
            mock.patch.object(module.subprocess, "run", return_value=completed) as run,
        ):
            actual = module.read_bug_tracker_with_windows_tls(
                "https://vmi3529459.contaboserver.net/api/bugs/v1/reports"
            )

        self.assertEqual(reports, actual)
        command = run.call_args.args[0]
        self.assertIn("--noproxy", command)
        self.assertIn("=https", command)
        self.assertNotIn("-k", command)
        self.assertNotIn("--insecure", command)

    def test_repository_token_is_used_on_a_fresh_machine(self) -> None:
        module = load_build_module()
        with tempfile.TemporaryDirectory() as directory:
            repository_token = Path(directory) / "bug-token"
            repository_token.write_text("repository-secret\n", encoding="utf-8")
            missing_user_token = Path(directory) / "missing-user-token"
            with (
                mock.patch.dict(os.environ, {}, clear=True),
                mock.patch.object(module, "REPOSITORY_BUG_TOKEN", repository_token),
                mock.patch.object(module, "USER_BUG_TOKEN", missing_user_token),
            ):
                module.configure_bug_token()
                self.assertEqual("repository-secret", os.environ.get("HEXLIVE_BUG_TOKEN"))


class WindowsBugSnapshotAuthTests(unittest.TestCase):
    @staticmethod
    def reply(body=b"[]", status=200, redirect="", code=0, stderr=b""):
        metadata = b"\n" + str(status).encode() + b"\n" + redirect.encode()
        return subprocess.CompletedProcess([], code, body + metadata, stderr)

    def invoke(self, responses, url="https://sg.example.test/api/bugs/v1/reports",
               token='test-"quoted\\token'):
        module = load_build_module()
        with (
            mock.patch.object(module.os, "environ", {"HEXLIVE_BUG_TOKEN": token}),
            mock.patch.object(module, "windows_curl_executable", return_value=Path("curl.exe")),
            mock.patch.object(module.subprocess, "run", side_effect=responses) as run,
        ):
            return module.read_bug_tracker_with_windows_tls(url), run.call_args_list

    def test_authorization_only_stdin_and_tls_not_disabled(self):
        result, calls = self.invoke([self.reply(b'[{"id": 1}]')])
        self.assertEqual([{"id": 1}], result)
        argv = calls[0].args[0]
        self.assertEqual("--disable", argv[1])
        self.assertEqual(b'header = "Authorization: Bearer test-\\"quoted\\\\token"\n',
                         calls[0].kwargs["input"])
        self.assertNotIn("Authorization", " ".join(argv))
        self.assertNotIn("test-", " ".join(argv))
        self.assertNotIn("--location", argv)
        self.assertNotIn("--insecure", argv)
        self.assertIn("=https", argv)

    def test_same_origin_redirect_preserves_auth(self):
        _, calls = self.invoke([
            self.reply(status=302, redirect="https://sg.example.test:443/new"), self.reply()
        ])
        self.assertEqual(2, len(calls))
        self.assertEqual(calls[0].kwargs["input"], calls[1].kwargs["input"])
        self.assertEqual("https://sg.example.test:443/new", calls[1].args[0][-1])

    def test_cross_origin_and_downgrade_never_followed(self):
        for url in ("https://other.example.test/new", "http://sg.example.test/new",
                    "https://sg.example.test:444/new"):
            with self.subTest(url=url), self.assertRaises(RuntimeError):
                # A second call would exhaust this one-element side effect.
                self.invoke([self.reply(status=302, redirect=url)])

    def test_injected_token_or_url_rejected_before_request(self):
        for token in ("", " ", "good\nheader = bad", "good\x00bad", "good\n",
                      "good\r\n", "\tgood", "good\x7f", "goodé"):
            with self.subTest(token=repr(token)), self.assertRaises(RuntimeError):
                self.invoke([], token=token)
        for url in ("http://sg.example.test/api", "https://user:pass@sg.example.test/api",
                    "https://sg.example.test/api#fragment", "https://sg.example.test/api\n"):
            with self.subTest(url=url), self.assertRaises(RuntimeError):
                self.invoke([], url=url)

    def test_failure_does_not_echo_response_credentials(self):
        with self.assertRaises(RuntimeError) as error:
            self.invoke([self.reply(status=401, code=22,
                                    stderr=b"Authorization: Bearer test-secret")],
                        token="test-secret")
        self.assertNotIn("test-secret", str(error.exception))
        self.assertIn("22", str(error.exception))

    def test_redirect_loop_is_bounded(self):
        with self.assertRaisesRegex(RuntimeError, "limit"):
            self.invoke([self.reply(status=307, redirect="https://sg.example.test/loop")] * 6)


if __name__ == "__main__":
    unittest.main()
