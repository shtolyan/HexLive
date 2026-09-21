import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from windows_update_package import archive_player


class PackageTests(unittest.TestCase):
    def test_archive_matches_manifest_and_contains_updater(self):
        with tempfile.TemporaryDirectory(prefix="hexlive-update-package-") as directory:
            root = Path(directory)
            player = root / "HexLive"
            player.mkdir()
            (player / "HexLive.exe").write_bytes(b"test player fixture")
            (player / "HexLiveUpdater.exe").write_bytes(b"test updater fixture")
            archive_player(root, {"version": "test"}, 19)
            manifest = json.loads((root / "build-manifest.json").read_text())["playerRelease"]
            archive = root / manifest["archiveFileName"]
            self.assertEqual(manifest["protocolVersion"], 19)
            self.assertEqual(manifest["archiveSha256"], hashlib.sha256(archive.read_bytes()).hexdigest())
            self.assertEqual(manifest["archiveSize"], archive.stat().st_size)
            with zipfile.ZipFile(archive) as zipped:
                self.assertEqual(set(zipped.namelist()), {"HexLive/HexLive.exe", "HexLive/HexLiveUpdater.exe"})


if __name__ == "__main__":
    unittest.main()
