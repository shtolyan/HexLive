#!/usr/bin/env python3
"""Compile updater integration through the generated Unity project (Editor must be closed)."""
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='hexlive-update-compile-') as temp:
    targets=Path(temp)/'updates.targets'
    project=ET.Element('Project')
    # Unity-generated projects can predate newly added source files. Supplement only
    # missing files from these two single-asmdef trees; never rewrite Unity's projects.
    for assembly, folder in [('HexLive.Simulation','Simulation'), ('HexLive.UnityPresentation','UnityPresentation')]:
        source=ET.parse(ROOT/(assembly+'.csproj'))
        included={(ROOT/node.attrib['Include']).resolve() for node in source.iter()
                  if node.tag.rsplit('}',1)[-1]=='Compile' and 'Include' in node.attrib}
        group=ET.SubElement(project,'ItemGroup',Condition="'$(MSBuildProjectName)' == '"+assembly+"'")
        for path in sorted((ROOT/'Assets/HexLive'/folder).rglob('*.cs')):
            if path.resolve() not in included: ET.SubElement(group,'Compile',Include=str(path))
    ET.ElementTree(project).write(targets,encoding='unicode')
    subprocess.run(['dotnet','build',str(ROOT/'HexLive.UnityPresentation.csproj'),'--no-restore','--nologo','--disable-build-servers',
        '-p:CustomAfterMicrosoftCommonTargets='+str(targets)],check=True)
