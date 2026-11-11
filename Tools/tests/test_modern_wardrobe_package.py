import hashlib
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import package_modern_wardrobe as staging


class ModernWardrobePackageTests(unittest.TestCase):
    def test_package_preserves_catalog_bytes_and_only_staged_records(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            build, people = root / "build", root / "people"
            stage = people / "Wardrobe"
            stage.mkdir(parents=True)
            build.mkdir()
            metadata = {"contentOrigin": "modern", "legacyWardrobe": True, "finalFit": True}
            keys = [("wear", "clothing.a"), ("wear", "clothing.b"),
                    ("config", "paintmaps.garment_a"), ("config", "paintmaps.garment_b")]
            records = [{"type": kind, "id": identifier, "metadata": metadata} for kind, identifier in keys]
            excluded_model = {"id": "clothing.bad", "source": "Assets/bad.prefab", "reason": "Broken skin weights"}
            policy = {"schemaVersion": 1, "excludedModels": [excluded_model]}
            selection = stage / "release-selection.json"
            selection.write_bytes(staging.json_bytes(policy))
            selection_sha = staging.content.hash_file(selection)[0]
            excluded = [{**excluded_model, "logicalIds": ["clothing.bad", "clothing.bad_alt"]}]
            catalog = stage / "release-catalog.json"
            raw = staging.json_bytes({
                "stage": "prepared-only", "contentOrigin": "modern",
                "gameReady": False, "bundleBuildAuthorized": False, "records": records,
                "releaseSelectionSHA256": selection_sha,
            }).replace(b"\n", b"\r\n")
            catalog.write_bytes(raw)
            fit = stage / "release-fit.json"
            fit.write_bytes(staging.json_bytes({
                "passed": True, "catalogPromoted": True,
                "releaseSelectionSHA256": selection_sha,
                "finalCatalog": {"sha": staging.content.hash_file(catalog)[0]},
                "counts": {"models": 1, "logicalIds": 2, "meshes": 2, "maps": 2},
                "rows": {"clothing.a": {}}, "excludedModels": excluded,
            }))
            primal = people / "catalog.json"
            primal.write_text('{"peopleCatalog":"primal-v1","records":[]}')
            hashes = {"catalogSHA256": staging.content.hash_file(catalog)[0],
                      "fitManifestSHA256": staging.content.hash_file(fit)[0]}
            (build / "build-all-summary.json").write_bytes(staging.json_bytes({
                **hashes, "primalCatalogSHA256": staging.content.hash_file(primal)[0], "releaseSelectionSHA256": selection_sha,
                "contentOrigin": "modern", "stage": "prepared-only",
                "platform": staging.PLATFORM, "runtimeProfile": staging.content.RUNTIME_PROFILE,
                "discovered": 4, "built": 4, "failed": 0, "unityVersion": "fixture",
            }))
            bundle_bytes = b"shared test payload"
            expected_payload = {"sha256": hashlib.sha256(bundle_bytes).hexdigest(), "size": len(bundle_bytes)}
            (build / "people-payload-validation.json").write_bytes(staging.json_bytes({
                "passed": True, "coexistingBundles": 2, "rendererPaintMapContracts": 2,
                "objects": [{**record, **expected_payload, "passed": True} for record in records],
            }))
            review = root / "review.json"
            review.write_bytes(staging.json_bytes({
                **hashes, "passed": True, "validationStage": "final-fit", "gallery": {"renderedPngs": 4},
                "coverage": {"definitions": 2, "uniquePrefabs": 1, "uniqueMeshes": 2, "paintMaps": 2},
            }))

            for record in records:
                folder = build / record["id"]
                folder.mkdir()
                payload = folder / "payload.bundle"
                payload.write_bytes(bundle_bytes)
                digest, size = staging.content.hash_file(payload)
                (folder / "candidate.json").write_bytes(staging.json_bytes({
                    **record, "state": "active", "metadata": {**metadata, "displayName": "Одежда"},
                    "variants": [{"platform": staging.PLATFORM, "runtimeProfile": staging.content.RUNTIME_PROFILE,
                                  "sha256": digest, "size": size, "stagedPath": str(payload)}],
                }))

            output = root / "modern.tar.gz"
            staging.package(build, people, review, output)
            manifest = staging.verify(output)
            self.assertIs(manifest["activation"], False)
            self.assertEqual(manifest["excludedModels"], excluded)
            self.assertEqual(sum(name.startswith("payloads/") for name in manifest["files"]), 1)
            with self.assertRaisesRegex(ValueError, "present in the release"):
                staging.check_exclusions(policy, excluded, {("wear", "clothing.bad_alt")})
            with tarfile.open(output) as archive:
                self.assertEqual(archive.extractfile("catalog.json").read(), raw)
                self.assertEqual(json.load(archive.extractfile("selection.json")), policy)
                descriptor = json.load(archive.extractfile("wardrobe/wear/clothing.a.json"))
                self.assertEqual(descriptor["state"], "prepared-only")
                self.assertEqual(descriptor["metadata"]["displayName"], "Одежда")
                extracted_descriptor = root / "descriptor.json"
                extracted_descriptor.write_bytes(staging.json_bytes(descriptor))
                with self.assertRaisesRegex(ValueError, "state must be active or retired"):
                    staging.content.load_candidate(extracted_descriptor)

            candidate = build / "clothing.a/candidate.json"
            invalid = json.loads(candidate.read_bytes())
            new_payload = Path(invalid["variants"][0]["stagedPath"])
            new_payload.write_bytes(b"changed test payload")
            digest, size = staging.content.hash_file(new_payload)
            invalid["variants"][0].update(sha256=digest, size=size)
            candidate.write_bytes(staging.json_bytes(invalid))
            with self.assertRaisesRegex(ValueError, "Payload changed after serialized validation"):
                staging.package(build, people, review, root / "changed.tar.gz")
            invalid["metadata"]["contentOrigin"] = "primal"
            candidate.write_bytes(staging.json_bytes(invalid))
            with self.assertRaisesRegex(ValueError, "Missing final Modern wardrobe metadata"):
                staging.package(build, people, review, root / "invalid.tar.gz")


if __name__ == "__main__":
    unittest.main()
