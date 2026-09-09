#!/usr/bin/env python3
"""Build the standalone BuildHutTest sandbox without publishing a release."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import build_release


ROOT = build_release.ROOT
OUTPUT_DIR = Path.home() / "hex-girls" / "BuildHutTest"
CONTENT_ROOT = Path.home() / "hex-girls" / "HexLiveContent"
UNITY_METHOD = "HexLive.UnityDebug.Editor.HexLiveBuildHutTestBuilder.BuildMacOS"


def main() -> int:
    subprocess.run([sys.executable, str(ROOT / "Tools/macos_signing.py"), "--check-identity"], check=True)
    if build_release.UNITY_LOCK.exists():
        raise RuntimeError(
            f"Unity Editor holds {build_release.UNITY_LOCK}; close it before building."
        )
    if OUTPUT_DIR.exists() or OUTPUT_DIR.is_symlink():
        raise RuntimeError(f"Refusing to overwrite existing BuildHutTest output: {OUTPUT_DIR}")

    unity = build_release.find_unity(None)
    catalog = build_release.select_external_catalog(CONTENT_ROOT)
    staging = OUTPUT_DIR.parent / f"BuildHutTest-staging-{os.getpid()}"
    app = staging / "BuildHutTest.app"
    summary_path = staging / "unity-summary.json"
    log_path = staging / "unity-build.log"

    if staging.exists():
        raise RuntimeError(f"Staging path already exists: {staging}")
    staging.mkdir(parents=True)

    command = [
        str(unity),
        "-batchmode",
        "-nographics",
        "-quit",
        "-accept-apiupdate",
        "-projectPath",
        str(ROOT),
        "-buildTarget",
        "StandaloneOSX",
        "-executeMethod",
        UNITY_METHOD,
        "-hexlive-build-output",
        str(app),
        "-hexlive-build-summary",
        str(summary_path),
        "-logFile",
        str(log_path),
    ]

    print(f"BuildHutTest output: {OUTPUT_DIR}")
    print(f"Unity log: {log_path}")
    try:
        with build_release.temporary_unity_auto_refresh():
            completed = subprocess.run(command, cwd=ROOT, check=False)
        if completed.returncode != 0:
            raise RuntimeError(
                f"Unity BuildHutTest build failed with exit code {completed.returncode}; "
                f"see {log_path}"
            )
        if not app.is_dir() or not summary_path.is_file():
            raise RuntimeError("Unity exited successfully without a complete BuildHutTest app/summary")

        summary = json.loads(summary_path.read_text(encoding="utf-8"))
        if summary.get("result") != "Succeeded" or int(summary.get("errors", 0)) != 0:
            raise RuntimeError(f"BuildHutTest summary is not successful: {summary}")

        build_release.install_external_content_bootstrap(app, CONTENT_ROOT, catalog)
        (staging / "HexLiveContent").symlink_to(CONTENT_ROOT, target_is_directory=True)
        signature = build_release.ensure_development_signature(app, release=True)

        report = {
            "kind": "BuildHutTest",
            "scene": "Assets/Scenes/BuildHutTest.unity",
            "artifact": str(OUTPUT_DIR / "BuildHutTest.app"),
            "doesNotPublishRelease": True,
            "unity": summary,
            "codeSignature": signature,
            "externalContent": {
                "source": str(CONTENT_ROOT),
                "catalog": str(catalog["catalog"]),
                "bundleCount": catalog["bundleCount"],
                "metadataCount": catalog["metadataCount"],
            },
        }
        (staging / "build-manifest.json").write_text(
            json.dumps(report, ensure_ascii=False, indent=4) + "\n", encoding="utf-8"
        )
        shutil.move(str(staging), str(OUTPUT_DIR))

        # A test client follows the same certificate policy as the main client.
        # Verify the final location without replacing its identity with ad-hoc.
        published_app = OUTPUT_DIR / "BuildHutTest.app"
        valid, details = build_release.verify_code_signature(published_app)
        if not valid:
            raise RuntimeError(f"BuildHutTest certificate signature is invalid:\n{details}")
    except Exception:
        print(f"Staging preserved for diagnostics: {staging}", file=sys.stderr)
        raise

    print(f"Done: {OUTPUT_DIR / 'BuildHutTest.app'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
