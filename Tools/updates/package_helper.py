#!/usr/bin/env python3
"""§166: package updater into a completed Player BEFORE its final signature."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]

def package(player: Path, platform: str, architecture: str):
    if platform == 'windows' and architecture != 'x64': raise ValueError('Windows requires x64')
    config = json.loads((ROOT / 'Tools/updates/trust.json').read_text())
    config.update(platform=platform, architecture=architecture)
    config['endpoint'] += f'/{platform}/{architecture}/stable'
    destination = player / ('Contents/Resources/Data/StreamingAssets/HexLiveUpdate' if platform == 'macos' else 'HexLive_Data/StreamingAssets/HexLiveUpdate')
    destination.mkdir(parents=True,exist_ok=True)
    for arch in (['arm64','x64'] if architecture == 'universal' else [architecture]):
        rid = ('osx-' if platform == 'macos' else 'win-') + arch
        helper_config = dict(config, architecture=arch, endpoint=config['endpoint'].replace('/universal/',f'/{arch}/'))
        with tempfile.TemporaryDirectory(prefix='hexlive-updater-') as temp:
            temp = Path(temp); cfg = temp/'update-config.json'; cfg.write_text(json.dumps(helper_config))
            subprocess.run(['dotnet','publish',str(ROOT/'Launcher/HexLive.Updater'),'-c','Release','-r',rid,
                '--self-contained','true','-p:UpdateConfigPath='+str(cfg),'-o',str(temp/'publish'),'--nologo'],check=True)
            name = 'HexLive.Updater.exe' if platform == 'windows' else 'HexLive.Updater'
            target_name = name + '-' + arch if architecture == 'universal' else name
            shutil.copy2(temp/'publish'/name,destination/target_name)
            if platform == 'macos':
                # The shared publisher supplies the same mandatory identity to nested executables.
                sys.path.insert(0,str(ROOT/'Tools'))
                from macos_signing import require_identity
                subprocess.run(['/usr/bin/codesign','--force','--sign',require_identity(),
                    '--timestamp=none',str(destination/target_name)],check=True)
    (destination/'update-config.json').write_text(json.dumps(config))

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('player',type=Path);parser.add_argument('--platform',choices=['macos','windows'],required=True)
    parser.add_argument('--architecture',choices=['arm64','x64','universal'],required=True)
    args=parser.parse_args(); package(args.player,args.platform,args.architecture)
