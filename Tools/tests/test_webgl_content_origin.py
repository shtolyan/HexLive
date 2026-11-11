import argparse
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import build_webgl_content as webgl


class WebGLContentOriginTests(unittest.TestCase):
    def setUp(self):
        self.people = {
            "peopleCatalog": "primal-v1",
            "records": [{
                "type": "wear",
                "id": "clothing.top_primal",
                "main": "Assets/HexLiveContent/People/Prefabs/Wear/PrimalTop.prefab",
                "metadata": {},
            }],
        }

    def test_live_catalog_accepts_legacy_primal_and_explicit_primal(self):
        self.assertEqual(webgl.require_live_people_source(self.people), self.people["records"])
        self.people["contentOrigin"] = "primal"
        self.people["records"][0]["metadata"]["contentOrigin"] = "primal"
        self.assertEqual(webgl.require_live_people_source(self.people), self.people["records"])

    def test_rejects_staged_catalog_despite_primal_compatibility_id(self):
        for fields in ({"stage": "prepared-only"}, {"contentOrigin": "modern"}):
            with self.subTest(fields=fields), self.assertRaisesRegex(ValueError, "Primal People"):
                webgl.require_live_people_source({**self.people, **fields})

    def test_rejects_staged_rows_and_dependencies(self):
        for metadata in ({"contentOrigin": "modern"}, {"contentOrigin": "other"},
                         {"contentOrigin": None}, {"legacyWardrobe": True}):
            catalog = copy.deepcopy(self.people)
            catalog["records"][0]["metadata"] = metadata
            with self.subTest(metadata=metadata), self.assertRaisesRegex(ValueError, "Staged or unsupported"):
                webgl.require_live_people_source(catalog)
        record = self.people["records"][0]
        record["dependencies"] = ["Assets/HexLiveContent/People/Wardrobe/FinalFit/example.asset"]
        with self.assertRaisesRegex(ValueError, "Staged Wardrobe source"):
            webgl.require_live_people_source(self.people)

    def test_verify_rejects_modern_candidates_even_in_primal_package(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "manifest.json").write_text('{"contentOrigin":"primal"}')
            (root / "inventory.json").write_text('{"records":[]}')
            (root / "candidates").mkdir()
            (root / "candidates" / "modern.json").write_text(json.dumps({
                "type": "wear", "id": "clothing.outfit_reiko",
                "metadata": {"contentOrigin": "modern"},
            }))
            with self.assertRaisesRegex(ValueError, "Staged or unsupported"):
                webgl.verify(root)

    def test_publish_all_rejects_modern_before_uploading_eligible_candidates(self):
        content = webgl.content
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            payload = root / "payload.bundle"
            payload.write_bytes(b"origin gate test")
            digest, size = content.hash_file(payload)
            for item, origin in (("tool.a-primal", "primal"), ("tool.z-modern", "modern")):
                folder = root / item
                folder.mkdir()
                (folder / "candidate.json").write_text(json.dumps({
                    "type": "object", "id": item, "state": "active",
                    "metadata": {"contentOrigin": origin},
                    "variants": [{"platform": "WebGL", "runtimeProfile": content.RUNTIME_PROFILE,
                                  "stagedPath": str(payload), "size": size, "sha256": digest}],
                }))
            args = argparse.Namespace(input=[root], required_platform=["WebGL"], host="test-host")
            with patch.object(content, "publish_all_remote") as upload:
                with self.assertRaisesRegex(ValueError, "tool.z-modern"):
                    content.publish_all(args)
            upload.assert_not_called()


if __name__ == "__main__":
    unittest.main()
