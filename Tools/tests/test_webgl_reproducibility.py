import contextlib
import hashlib
import io
import json
from pathlib import Path
import re
import sys
import tempfile
import unittest
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import webgl_compile_check as compile_check
import webgl_verify_audio_reuse as audio


class AudioReuseProofTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="hexlive-audio-proof-test-", dir=TOOLS.parent / "Build")
        self.repo = Path(self.temp.name)
        self.source = self.repo / audio.AUDIO_ROOT / "Sfx/test.wav"
        self.source.parent.mkdir(parents=True)
        self.payload = b"small hydrated fixture"
        self.blob = b"version https://git-lfs.github.com/spec/v1\n" + b"oid sha256:" + hashlib.sha256(self.payload).hexdigest().encode() + b"\nsize 22\n"
        self.commit = "a" * 40
        self.source.write_bytes(self.payload)
        self.manifest = json.dumps({"files": ["Sfx/test.fsb"]}).encode()
        self.addCleanup(self.temp.cleanup)
        for replacement in (patch.object(audio.subprocess, "check_output", side_effect=self.git),
                            patch.object(audio.subprocess, "Popen", side_effect=self.blobs)):
            replacement.start(); self.addCleanup(replacement.stop)

    def tearDown(self):
        self.temp.cleanup()

    def git(self, args, **kwargs):
        if "rev-parse" in args: return self.commit.encode() + b"\n"
        if "ls-tree" in args: return b"100644 blob fixture\t" + (audio.AUDIO_ROOT + "/Sfx/test.wav").encode() + b"\0"
        raise AssertionError("Unexpected git command")

    def blobs(self, *args, **kwargs):
        class BlobProcess:
            stdin = io.BytesIO()
            stdout = io.BytesIO(b"fixture blob " + str(len(self.blob)).encode() + b"\n" + self.blob + b"\n")
            def wait(self): return 0
            def poll(self): return 0
        return BlobProcess()

    def test_hydrated_lfs_passes_and_source_change_fails(self):
        proof = audio.verify(self.repo, self.commit, self.manifest)
        self.assertTrue(proof["passed"])
        self.assertEqual(proof["files"][0]["sha256"], hashlib.sha256(self.payload).hexdigest())
        self.source.write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "SourceChanged"):
            audio.verify(self.repo, self.commit, self.manifest)

    def test_new_and_deleted_audio_close_the_reuse_path(self):
        extra = self.source.with_name("new.WAV")
        extra.write_bytes(b"new")
        with self.assertRaisesRegex(ValueError, "SourceTreeChanged"):
            audio.verify(self.repo, self.commit, self.manifest)
        extra.unlink(); self.source.unlink()
        with self.assertRaisesRegex(ValueError, "SourceTreeChanged"):
            audio.verify(self.repo, self.commit, self.manifest)

    def test_failed_cli_overwrites_a_stale_pass_receipt(self):
        manifest = self.repo / "manifest.json"; manifest.write_bytes(self.manifest)
        output = self.repo / "proof.json"; output.write_text('{"passed":true}')
        self.source.write_bytes(b"changed")
        with patch.object(sys, "argv", ["proof", "--checkout", str(self.repo), "--published-source-commit", self.commit,
                "--manifest", str(manifest), "--output", str(output)]), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(audio.main(), 1)
        proof = json.loads(output.read_text())
        self.assertFalse(proof["passed"])
        self.assertTrue(proof["error"].startswith("SourceChanged"))

    def test_manifest_rejects_escape_duplicates_and_non_audio_paths(self):
        for entry in ("../private.fsb", "Sfx/../private.fsb", "C:/secret.fsb", "Sfx\\a.fsb", "Other/a.fsb", "Sfx/a.json", "Sfx//a.fsb"):
            with self.subTest(entry=entry), self.assertRaises(ValueError):
                audio.manifest_entries(json.dumps({"files": [entry]}).encode())
        with self.assertRaises(ValueError):
            audio.manifest_entries(b'{"files":["Sfx/a.fsb","Sfx/a.fsb"]}')
        with self.assertRaisesRegex(ValueError, "MissingOrAmbiguousSource"):
            audio.source_for("Sfx/a.fsb", {audio.AUDIO_ROOT + "/Sfx/a.wav", audio.AUDIO_ROOT + "/Sfx/a.ogg"})


class WebGlCompileCheckTests(unittest.TestCase):
    def test_previous_editor_install_does_not_hide_bcl_or_leak_editor_engine(self):
        old = "C:/Program Files/Unity/Hub/Editor/6000.4.5f1/Editor/Data/"
        with patch.object(Path, "exists", return_value=True):
            bcl = compile_check.player_reference("webgl", old + "NetStandard/ref/2.1.0/netstandard.dll")
            self.assertIn(compile_check.UNITY_VERSION, bcl)
            self.assertNotIn("6000.4.5f1", bcl)
            engine = compile_check.player_reference("webgl", old + "Managed/UnityEngine/UnityEngine.CoreModule.dll")
            self.assertEqual(engine, str(compile_check.PLAYER_MANAGED["webgl"] / "UnityEngine.CoreModule.dll"))
            self.assertIsNone(compile_check.player_reference("webgl", old + "Managed/UnityEngine/UnityEditor.CoreModule.dll"))

    def test_windows_and_posix_diagnostics_keep_distinct_columns_without_summary_duplicates(self):
        lines = [r"C:\repo\a.cs(26,17): error RS0030: banned HttpClient [C:\out\p.csproj]",
                 r"C:\repo\a.cs(26,36): error RS0030: banned HttpClient [C:\out\p.csproj]",
                 "/repo/b.cs(14,3): error CS1061: missing awaiter [/out/p.csproj]"]
        findings = list(compile_check.unique_diagnostics("\n".join(lines + lines)))
        self.assertEqual(len(findings), 3)
        self.assertEqual([f["col"] for f in findings[:2]], ["17", "36"])
        self.assertEqual(sum(f["code"] == "RS0030" for f in findings), 2)

    def test_all_browser_assemblies_keep_strict_enforcement(self):
        self.assertEqual(compile_check.BANNED_ENFORCED, compile_check.OWN)

    def test_spec168_subsection_numbers_are_unique_and_server_selection_is168_14(self):
        spec = (TOOLS.parent / "Spec/168.md").read_text(encoding="utf-8")
        numbers = re.findall(r"^### §(168\.\d+)\b", spec, re.MULTILINE)
        self.assertEqual(len(numbers), len(set(numbers)))
        self.assertIn("### §168.14 Выбор сервера после входа (#439)", spec)


if __name__ == "__main__":
    unittest.main()
