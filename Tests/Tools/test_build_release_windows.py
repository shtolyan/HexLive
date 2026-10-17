from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import sys
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "Tools" / "build_release_windows.py"


def load_build_module():
    spec = importlib.util.spec_from_file_location("build_release_windows", SCRIPT)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load {SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    with mock.patch.object(sys, "path", [str(ROOT / "Tools"), *sys.path]):
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

    def test_missing_device_token_never_uses_repository_or_inherited_token(self) -> None:
        module = load_build_module()
        with tempfile.TemporaryDirectory() as directory:
            repository_token = Path(directory) / "bug-token"
            repository_token.write_text("repository-secret\n", encoding="utf-8")
            missing_user_token = Path(directory) / "missing-user-token"
            with (
                mock.patch.dict(os.environ, {"HEXLIVE_BUG_TOKEN": "inherited-secret"}, clear=True),
                mock.patch("bug_credentials.device_token_path", return_value=missing_user_token),
            ):
                with self.assertRaisesRegex(RuntimeError, "no fallback"):
                    module.configure_bug_token()


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


class WindowsLocalBuildTests(unittest.TestCase):
    def test_missing_or_invalid_snapshot_never_falls_back_to_server(self):
        module = load_build_module()
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "snapshot.json"
            for payload in ({}, {"reports": [{}]}, {"bugs": {}},
                            {"reports": [{"id": 1, "status": "ready_for_test"}] * 2}):
                path.write_text(json.dumps(payload), encoding="utf-8")
                with self.subTest(payload=payload), self.assertRaises(RuntimeError):
                    module.read_local_snapshot(path)
        with (mock.patch.object(sys, "argv", [str(SCRIPT), "--local-only", "--dry-run"]),
              mock.patch.object(module, "configure_bug_token", side_effect=AssertionError("credential access")),
              mock.patch.object(module, "read_bug_tracker", side_effect=AssertionError("network"))):
            with self.assertRaisesRegex(RuntimeError, "requires --bug-snapshot"):
                module.main()

    def local_build(self, code=0, mutate_snapshot=False, mutate_reservation=False, mutate_source=False, dry_run=False):
        from contextlib import ExitStack, nullcontext
        module = load_build_module()
        with tempfile.TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            snapshot = root / "snapshot.json"
            snapshot.write_text(json.dumps({"capturedAtUtc": "2026-10-01T00:00:00Z", "reports": [
                {"id": 1, "status": "ready_for_test", "text": "test"}]}), encoding="utf-8")
            pending = root / "Library/HexLivePendingBuildVersion.txt"
            pending.parent.mkdir()
            pending.write_text("0.1.999", encoding="utf-8")
            latest = root / "HexLive"
            latest.write_text("previous latest", encoding="utf-8")
            info = dict(commit="a" * 40, branch="test", commits=[], comparison="test", workingTree=[])
            stack.enter_context(mock.patch.object(module, "ROOT", root))
            stack.enter_context(mock.patch.object(module, "PENDING_VERSION", pending))
            stack.enter_context(mock.patch.object(module, "DEFAULT_RELEASES", root / "Releases"))
            args = [str(SCRIPT), "--local-only", "--bug-snapshot", str(snapshot), "--output-root", str(root / "out")]
            if dry_run:
                args.append("--dry-run")
            stack.enter_context(mock.patch.object(sys, "argv", args))
            for name in ("configure_bug_token", "read_bug_tracker", "require_successful_version_finalize",
                         "publish_staging", "write_last_success", "make_junction"):
                stack.enter_context(mock.patch.object(module, name, side_effect=AssertionError(name + " forbidden")))
            stack.enter_context(mock.patch.object(module, "collect_git", return_value=info))
            stack.enter_context(mock.patch.object(module, "local_source_state", side_effect=[
                {"commit": "a" * 40}, {"commit": ("b" if mutate_source else "a") * 40}]))
            stack.enter_context(mock.patch.object(module, "read_current_version", return_value="0.1.7"))
            stack.enter_context(mock.patch.object(module, "find_unity", return_value=Path("Unity.exe")))
            stack.enter_context(mock.patch.object(module, "validate_windows_player_references"))
            stack.enter_context(mock.patch.object(module, "require_free_unity_lock"))
            stack.enter_context(mock.patch.object(module, "temporary_unity_auto_refresh", return_value=nullcontext()))
            package = stack.enter_context(mock.patch.object(module.subprocess, "run"))
            def unity(_exe, arguments, _log, _label):
                self.assertIn("-hexlive-local-only", arguments)
                self.assertNotIn("-hexlive-bugs-api", arguments)
                artifact = Path(arguments[arguments.index("-hexlive-build-output") + 1])
                artifact.parent.mkdir(parents=True)
                artifact.write_bytes(b"local executable")
                summary = Path(arguments[arguments.index("-hexlive-build-summary") + 1])
                summary.write_text(json.dumps({"result": "Succeeded", "version": "0.1.7"}), encoding="utf-8")
                if mutate_snapshot:
                    snapshot.write_text("changed", encoding="utf-8")
                if mutate_reservation:
                    pending.write_text("changed", encoding="utf-8")
                return code
            run = stack.enter_context(mock.patch.object(module, "run_unity", side_effect=unity))
            if mutate_snapshot or mutate_reservation or mutate_source:
                with self.assertRaises(RuntimeError):
                    module.main()
                package.assert_not_called()
                return
            self.assertEqual(code, module.main())
            self.assertEqual("0.1.999", pending.read_text(encoding="utf-8"))
            self.assertEqual("previous latest", latest.read_text(encoding="utf-8"))
            manifests = list((root / "out").glob("*/build-manifest.json"))
            if code or dry_run:
                self.assertEqual([], manifests)
                package.assert_not_called()
                if dry_run:
                    run.assert_not_called()
                    self.assertFalse((root / "out").exists())
            else:
                self.assertEqual(1, len(manifests))
                built = json.loads(manifests[0].read_text(encoding="utf-8"))
                self.assertFalse(built["published"])
                self.assertFalse(built["bugs"]["stamped"])
                self.assertEqual("0.1.7", built["version"])
                self.assertTrue(built["bugSnapshot"]["historicalOfflineSnapshot"])

    def test_success_is_local_and_keeps_reservation_latest_and_server_untouched(self):
        self.local_build()

    def test_failure_never_packages_or_claims_success(self):
        self.local_build(code=1)

    def test_dry_run_does_not_launch_unity_or_create_outputs(self):
        self.local_build(dry_run=True)

    def test_changed_snapshot_is_rejected(self):
        self.local_build(mutate_snapshot=True)

    def test_changed_release_reservation_is_rejected(self):
        self.local_build(mutate_reservation=True)

    def test_changed_source_is_rejected(self):
        self.local_build(mutate_source=True)

    def test_publisher_rejects_offline_snapshot_before_credentials_or_network(self):
        module = load_build_module()
        with (mock.patch.object(sys, "argv", [str(SCRIPT), "--bug-snapshot", "offline.json"]),
              mock.patch.object(module, "configure_bug_token", side_effect=AssertionError("credential access")),
              mock.patch.object(module, "read_bug_tracker", side_effect=AssertionError("network"))):
            with self.assertRaisesRegex(RuntimeError, "publisher requires a fresh live snapshot"):
                module.main()

    def test_missing_or_invalid_runtime_module_rejected_without_editor_fallback(self):
        module = load_build_module()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            settings = root / "ProjectSettings.asset"
            settings.write_text("  scriptingBackend:\n    Standalone: 1\n  nextField: value\n", encoding="utf-8")
            unity = root / "Editor/Unity.exe"
            reference = unity.parent / "Data/PlaybackEngines/WindowsStandaloneSupport/Variations/il2cpp/Managed/UnityEngine.CoreModule.dll"
            with (mock.patch.object(module, "PROJECT_SETTINGS", settings),
                  mock.patch.object(module, "unity_version", return_value="6000.4.6f1")):
                with self.assertRaisesRegex(RuntimeError, "Missing Windows il2cpp player references"):
                    module.validate_windows_player_references(unity)
                reference.parent.mkdir(parents=True)
                reference.write_bytes(b"not an assembly")
                with self.assertRaisesRegex(RuntimeError, "Invalid Windows player reference"):
                    module.validate_windows_player_references(unity)
                reference.write_bytes(b"MZ" + b"fixture")
                module.validate_windows_player_references(unity)


if __name__ == "__main__":
    unittest.main()
