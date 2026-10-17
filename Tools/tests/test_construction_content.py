"""The handoff verifier must reject stale bundles without launching Unity."""
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "construction_content", Path(__file__).resolve().parents[1] / "construction_content.py")
CONTENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CONTENT)


class ConstructionContentTests(unittest.TestCase):
    def test_committed_source_contracts(self):
        contracts = CONTENT.source_contracts()
        self.assertEqual(len(contracts), 14)
        self.assertEqual(sum(len(r["actions"]) for r in contracts.values()), 299)

    def test_candidates_require_current_receipt_platform_and_payload(self):
        contracts = CONTENT.source_contracts()
        platform = "StandaloneWindows64"
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for (kind, identity), receipt in contracts.items():
                folder = root / kind / identity / platform
                folder.mkdir(parents=True)
                payload = b"unit-test fixture, not a Unity bundle"
                (folder / "payload.bundle").write_bytes(payload)
                candidate = {"type": kind, "id": identity,
                             "metadata": {"construction": receipt},
                             "variants": [{"platform": platform, "size": len(payload),
                                           "sha256": hashlib.sha256(payload).hexdigest()}]}
                (folder / "candidate.json").write_text(json.dumps(candidate), encoding="utf-8")
            with patch("sys.argv", ["construction_content", "verify-candidates", "--root", str(root)]):
                with contextlib.redirect_stdout(io.StringIO()):
                    CONTENT.main()
                kind, identity = next(iter(contracts))
                path = root / kind / identity / platform / "candidate.json"
                original = json.loads(path.read_text(encoding="utf-8"))
                for mutation, expected in (
                    (lambda c: c["metadata"].clear(), "receipt mismatch"),
                    (lambda c: c["variants"][0].update(platform="WebGL"), "platform"),
                    (lambda c: c["variants"][0].update(sha256="0" * 64), "payload differs"),
                ):
                    candidate = json.loads(json.dumps(original))
                    mutation(candidate)
                    path.write_text(json.dumps(candidate), encoding="utf-8")
                    with self.assertRaisesRegex(ValueError, expected):
                        CONTENT.main()


if __name__ == "__main__":
    unittest.main()
