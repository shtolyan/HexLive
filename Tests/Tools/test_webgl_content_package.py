import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

TOOLS = Path(__file__).resolve().parents[2] / "Tools"
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location("webgl_package", TOOLS / "build_webgl_content.py")
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class WebGLPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.build = self.root / "build"
        self.package = self.root / "package"
        self.people = self.root / "people.json"
        rows = [{"type": "actor", "id": "Marta"}, {"type": "wear", "id": "primal"}]
        builder.write_json(self.people, {"records": rows})
        builder.write_json(self.build / "inventory.json", {"passed": True, "records": rows})
        builder.write_json(self.build / "build-all-summary.json", {"built": 2, "discovered": 2, "failed": 0})
        builder.write_json(self.build / "build-receipt.json", {"editorExitCode": 0, "platform": "WebGL"})
        builder.write_json(self.build / "people-payload-validation.json", {"passed": True,
            "objects": [{**r, "passed": True} for r in rows], "coexistingBundles": 2})
        for row in rows + [{"type": "config", "id": "simdata"}]:
            data = (row["type"] + row["id"]).encode()
            folder = self.build / row["type"] / row["id"]
            folder.mkdir(parents=True)
            payload = folder / "payload.bundle"
            payload.write_bytes(data)
            builder.write_json(folder / "candidate.json", {**row, "state": "active", "metadata": {}, "variants": [{
                "platform": "WebGL", "runtimeProfile": "unity6000-content1", "payloadType": "assetBundle",
                "entryAsset": "main", "sha256": hashlib.sha256(data).hexdigest(), "size": len(data),
                "stagedPath": str(payload)}]})

    def make_package(self):
        return builder.package(self.build, self.package, self.people)

    def test_package_survives_move_and_source_removal(self):
        self.make_package()
        moved = self.root / "different-machine" / "upload"
        moved.parent.mkdir()
        shutil.move(self.package, moved)
        shutil.rmtree(self.build)
        self.assertEqual(builder.verify(moved)["objects"], 3)
        self.assertNotIn(str(self.root), (moved / "candidates/actor--Marta.json").read_text())

    def test_corrupt_blob_is_rejected(self):
        self.make_package()
        next((self.package / "payloads").iterdir()).write_bytes(b"corrupted")
        with self.assertRaises(ValueError):
            builder.verify(self.package)

    def test_missing_candidate_cannot_be_packaged(self):
        (self.build / "wear/primal/candidate.json").unlink()
        with self.assertRaises(ValueError):
            self.make_package()

    def test_incomplete_build_cannot_be_packaged(self):
        builder.write_json(self.build / "build-all-summary.json", {"built": 1, "discovered": 2, "failed": 1})
        with self.assertRaises(ValueError):
            self.make_package()

    def add_legacy_normal(self, referenced=False):
        row = {"type": "config", "id": "paintmaps.skinnrm_old_g0",
            "main": "Assets/HexLiveContent/RuntimeSource/PaintMaps/skinnrm_Old_g0.png"}
        value = builder.read_json(self.build / "config/simdata/candidate.json")
        value["id"] = row["id"]
        builder.write_json(self.build / "config" / row["id"] / "candidate.json", value)
        inventory = builder.read_json(self.build / "inventory.json")
        inventory["records"].append(row)
        if referenced:
            inventory["records"][0]["dependencies"] = [row["main"]]
        builder.write_json(self.build / "inventory.json", inventory)
        builder.write_json(self.build / "build-all-summary.json", {"built": 3, "discovered": 3, "failed": 0})

    def test_unreferenced_old_skin_texture_is_omitted_with_a_receipt(self):
        self.add_legacy_normal()
        result = self.make_package()
        self.assertEqual(result["objects"], 3)
        self.assertEqual(result["excludedUnusedLegacyObjects"], 1)
        self.assertEqual(len(builder.read_json(self.package / "inventory.json")["excludedUnusedLegacyRecords"]), 1)

    def test_referenced_old_skin_texture_cannot_be_silently_omitted(self):
        self.add_legacy_normal(referenced=True)
        with self.assertRaisesRegex(ValueError, "still depends"):
            self.make_package()

    def test_legacy_actor_is_rejected_even_if_present_in_inventory(self):
        path = self.build / "actor/Marta/candidate.json"
        value = json.loads(path.read_text())
        value["id"] = "Legacy"
        builder.write_json(self.build / "actor/Legacy/candidate.json", value)
        inventory = builder.read_json(self.build / "inventory.json")
        inventory["records"].append({"type": "actor", "id": "Legacy"})
        builder.write_json(self.build / "inventory.json", inventory)
        builder.write_json(self.build / "build-all-summary.json", {"built": 3, "discovered": 3, "failed": 0})
        with self.assertRaisesRegex(ValueError, "Legacy actor"):
            self.make_package()


if __name__ == "__main__":
    unittest.main()
