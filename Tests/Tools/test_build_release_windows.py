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
            stdout=json.dumps(reports).encode("utf-8"),
            stderr=b"",
        )

        with (
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


if __name__ == "__main__":
    unittest.main()
