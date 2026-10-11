"""Install a reviewed staging export into the existing People source tree only."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--stage',type=Path,required=True)
    parser.add_argument('--project',type=Path,required=True)
    args=parser.parse_args()
    project=args.project.resolve(); stage=args.stage.resolve()
    root=project/'Assets/HexLiveContent/People/Source'
    path=root/'source-manifest.json'
    original=path.read_bytes(); manifest=json.loads(original)
    export=json.loads((stage/'hair-source-manifest.json').read_text())
    rows=export['models']; assert len(rows)==32 and all(m['kind']=='hair' for m in rows)
    assert {m['id'] for m in rows}=={m['id'] for m in manifest['models'] if m['kind']=='hair'}
    replacements={m['id']:m for m in rows}
    files={m['path'] for m in rows}|{m['authoredBindings'] for m in rows}|{t['path'] for t in export['textures'].values()}
    expected={m['path']:m['sha256'] for m in rows}
    expected.update({t['path']:t['sha256'] for t in export['textures'].values()})
    for relative in files:
        assert (root/relative).resolve().is_relative_to(root.resolve())
        assert (stage/relative).resolve().is_relative_to(stage)
        data=(stage/relative).read_bytes()
        if relative in expected: assert hashlib.sha256(data).hexdigest()==expected[relative],relative
    # Fail rather than absorb concurrent edits to any existing source file.
    check=['git','status','--porcelain','--',str(path.relative_to(project))]
    check.extend(str((root/f).relative_to(project)) for f in sorted(files) if (root/f).exists())
    dirty=subprocess.run(check,cwd=project,check=True,capture_output=True,text=True).stdout
    if dirty: raise SystemExit('Source files have local edits; review before installation:\n'+dirty)
    old_hair_keys={k for m in manifest['models'] if m['kind']=='hair' for mesh in m['meshes'] for k in mesh['materials']}
    other_keys={k for m in manifest['models'] if m['kind']!='hair' for mesh in m['meshes'] for k in mesh['materials']}
    manifest['models']=[replacements[m['id']] if m['kind']=='hair' else m for m in manifest['models']]
    for key in old_hair_keys-other_keys: manifest['materials'].pop(key,None)
    manifest['materials'].update(export['materials'])
    manifest['textures'].update(export['textures'])
    manifest['hairSource']={'sourceBlendSha256':export['sourceBlendSha256'],'sourceUV':True,'authoredWeights':True,'unityValidated':False}
    report=[]
    for relative in sorted(files):
        target=root/relative; target.parent.mkdir(parents=True,exist_ok=True)
        data=(stage/relative).read_bytes(); target.write_bytes(data)
        report.append({'path':str(target.relative_to(project)).replace('\\','/'),'sha256':hashlib.sha256(data).hexdigest()})
    assert path.read_bytes()==original,'Manifest changed concurrently'
    path.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (stage/'installed-files.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({'models':len(rows),'filesInstalled':len(report),'manifest':str(path)}))


if __name__=='__main__':main()
