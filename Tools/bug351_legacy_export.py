#!/usr/bin/env python3
"""Export bug #351's original v64 save through its unmodified historical reader."""

import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
from xml.sax.saxutils import escape

COMMIT = "2236e4a97a72f125d4cb889a398558666fc533d3"
SOURCE_SHA256 = "d403257bd7965bc172199fdf45f034e7e843fdc2397de58bfd095bd6ee74890f"
REPO = Path(__file__).resolve().parent.parent


def run(*args, **kwargs):
    subprocess.run(args, check=True, **kwargs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="Original world.dat (read only)")
    parser.add_argument("--output", type=Path, required=True, help="New or empty snapshot directory")
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    if hashlib.sha256(source.read_bytes()).hexdigest() != SOURCE_SHA256:
        parser.error("Source is not the original #351 save: SHA-256 mismatch.")
    if output.exists() and (not output.is_dir() or any(output.iterdir())):
        parser.error("Output must be a new or empty directory; existing results are never overwritten.")
    # The project forbids command-line builds while Unity is running. Fail closed
    # if process inspection is unavailable; this helper does not close the editor.
    try:
        unity = subprocess.run(["pgrep", "-x", "Unity"], capture_output=True)
    except OSError as error:
        parser.error(f"Cannot verify that Unity is closed: {error}")
    if unity.returncode != 1:
        parser.error("Unity is running or process inspection failed; close Unity before exporting.")

    with tempfile.TemporaryDirectory(prefix="hexlive-bug351-legacy64-") as temporary:
        work = Path(temporary)
        archive = work / "source.tar"
        with archive.open("wb") as stream:
            run("git", "-C", str(REPO), "archive", COMMIT,
                "Assets/HexLive/Simulation", "HexLive.Simulation.Standalone.csproj",
                "SimData/simdata.json", stdout=stream)
        historical = work / "historical"
        historical.mkdir()
        with tarfile.open(archive) as source_archive:
            source_archive.extractall(historical, filter="data")

        # Only disable unrelated SDK default asset scans. The historical Compile
        # include and every archived simulation/content file remain unchanged.
        props = work / "fast-evaluation.props"
        props.write_text('''<Project><PropertyGroup Condition="'$(MSBuildProjectName)' == 'HexLive.Simulation.Standalone'">
<EnableDefaultNoneItems>false</EnableDefaultNoneItems>
<EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>
</PropertyGroup></Project>''')
        run("dotnet", "build", str(historical / "HexLive.Simulation.Standalone.csproj"),
            "--configuration", "Release", "--nologo", "--disable-build-servers", "-m:1",
            f"-p:CustomAfterMicrosoftCommonProps={props}")

        exporter = work / "exporter"
        exporter.mkdir()
        shutil.copyfile(REPO / "Tools/bug351_legacy_export/Program.cs", exporter / "Program.cs")
        dll = historical / "Build/dotnet/bin/HexLive.Simulation/Release/net9.0/HexLive.Simulation.dll"
        simdata = historical / "SimData/simdata.json"
        serializer = historical / "Assets/HexLive/Simulation/Persistence/WorldSaveSerializer.cs"
        project = exporter / "exporter.csproj"
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><Reference Include="HexLive.Simulation"><HintPath>{escape(str(dll))}</HintPath></Reference>
<None Include="{escape(str(simdata))}" Link="simdata.json" CopyToOutputDirectory="PreserveNewest" />
<None Include="{escape(str(serializer))}" Link="WorldSaveSerializer.cs" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup></Project>''')
        run("dotnet", "run", "--project", str(project), "--configuration", "Release", "--disable-build-servers",
            "--", str(source), str(output))
    print(f"Verified three snapshots and provenance: {output / 'manifest.json'}")


if __name__ == "__main__":
    main()
